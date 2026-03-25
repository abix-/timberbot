// TimberbotEntityCache.cs -- Double-buffered entity caching system.
//
// Main thread (RefreshCachedState, every 1s):
//   1. Walk all cached entities, read their mutable state into the Write buffer
//   2. Swap Write and Read buffers atomically
//
// Background thread (HTTP GET handlers):
//   Read from the Read buffer. Never modified during reads. Zero contention.
//
// Entity lifecycle:
//   EntityInitializedEvent -> AddToIndexes() caches the entity + immutable data
//   EntityDeletedEvent     -> RemoveFromIndexes() removes from both buffers

using System;
using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.Bots;
using Timberborn.BuilderPrioritySystem;
using Timberborn.Buildings;
using Timberborn.BuildingsReachability;
using Timberborn.Carrying;
using Timberborn.ConstructionSites;
using Timberborn.Cutting;
using Timberborn.DeteriorationSystem;
using Timberborn.DwellingSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.ResourceCountingSystem;
using Timberborn.Forestry;
using Timberborn.Gathering;
using Timberborn.InventorySystem;
using Timberborn.LifeSystem;
using Timberborn.MechanicalSystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.NeedSystem;
using Timberborn.PrioritySystem;
using Timberborn.PowerManagement;
using Timberborn.RangedEffectSystem;
using Timberborn.Reproduction;
using Timberborn.SingletonSystem;
using Timberborn.StatusSystem;
using Timberborn.WaterBuildings;
using Timberborn.Wellbeing;
using Timberborn.Wonders;
using Timberborn.WorkSystem;
using Timberborn.Workshops;
using UnityEngine;

namespace Timberbot
{
    // The entity cache is the backbone of the mod. It holds every building, beaver, and
    // tree in the game as cached structs with pre-resolved component references.
    //
    // Why cache? Timberborn entities are Unity GameObjects with components attached.
    // Reading a component property requires GetComponent<T>() which is expensive.
    // We resolve components ONCE when the entity is created (AddToIndexes), then
    // read their properties every 1 second in RefreshCachedState.
    //
    // The double buffer (Buildings, NaturalResources, Beavers) lets the HTTP background
    // thread read cached data without locks. See TimberbotDoubleBuffer for details.
    public class TimberbotEntityCache
    {
        // game services injected via constructor
        private readonly EntityRegistry _entityRegistry;
        private readonly TreeCuttingArea _treeCuttingArea;
        private readonly EventBus _eventBus;
        public readonly DistrictCenterRegistry DistrictRegistry;
        private readonly IGoodService _goodService;

        // set by TimberbotService in Load() before use
        public TimberbotWebhook WebhookMgr;

        // double-buffered entity lists: main thread writes, background thread reads
        public readonly TimberbotDoubleBuffer<CachedBuilding> Buildings = new TimberbotDoubleBuffer<CachedBuilding>();
        public readonly TimberbotDoubleBuffer<CachedNaturalResource> NaturalResources = new TimberbotDoubleBuffer<CachedNaturalResource>();
        public readonly TimberbotDoubleBuffer<CachedBeaver> Beavers = new TimberbotDoubleBuffer<CachedBeaver>();

        // district snapshot (refreshed at 1Hz, not double-buffered -- tiny list, 1-3 items)
        public readonly List<CachedDistrict> Districts = new List<CachedDistrict>();

        // O(1) entity lookup by Unity instance ID (for write commands that target specific entities)
        private readonly Dictionary<int, EntityComponent> _entityCache = new Dictionary<int, EntityComponent>();

        // shared JSON writer instance: 300KB pre-allocated StringBuilder, reused via Reset()
        public readonly TimberbotJw Jw = new TimberbotJw(300000);
        // small JwWriter for district pre-serialization (main thread only, during RefreshCachedState)
        private readonly TimberbotJw _districtJw = new TimberbotJw(4096);

        public static readonly HashSet<string> TreeSpecies = new HashSet<string>
            { "Pine", "Birch", "Oak", "Maple", "Chestnut", "Mangrove" };
        public static readonly HashSet<string> CropSpecies = new HashSet<string>
            { "Kohlrabi", "Soybean", "Corn", "Sunflower", "Eggplant", "Algae", "Cassava", "Mushroom", "Potato", "Wheat", "Carrot" };

        public static string FactionSuffix = "";  // set by TimberbotPlacement.DetectFaction()
        public static readonly string[] OrientNames = { "south", "west", "north", "east" };
        public static readonly string[] PriorityNames = { "VeryLow", "Low", "Normal", "High", "VeryHigh" };

        public TimberbotEntityCache(
            EntityRegistry entityRegistry,
            TreeCuttingArea treeCuttingArea,
            EventBus eventBus,
            DistrictCenterRegistry districtCenterRegistry,
            IGoodService goodService)
        {
            _entityRegistry = entityRegistry;
            _treeCuttingArea = treeCuttingArea;
            _eventBus = eventBus;
            DistrictRegistry = districtCenterRegistry;
            _goodService = goodService;
        }

        public void Register() => _eventBus.Register(this);
        public void Unregister() => _eventBus.Unregister(this);

        public static string GetPriorityName(Priority p)
        {
            int i = (int)p;
            return (i >= 0 && i < PriorityNames.Length) ? PriorityNames[i] : "Normal";
        }

        // Strip Unity/faction suffixes so API output has clean names.
        // Unity appends "(Clone)" to instantiated objects. FactionSuffix (e.g. ".Folktails")
        // is detected once at startup via FactionService -- no hardcoded faction names.
        public static string CleanName(string name)
        {
            var clean = name.Replace("(Clone)", "");
            if (FactionSuffix.Length > 0) clean = clean.Replace(FactionSuffix, "");
            return clean.Trim();
        }

        // Optimization: avoid calling CleanName() every refresh by comparing object references.
        // If the Workplace or District reference hasn't changed since last refresh,
        // the cached string is still valid. Only derive a new string when the ref changes.
        // This saves ~50 string allocations/sec for employed beavers.
        public static bool RefChanged(ref object cached, object current)
        {
            if (ReferenceEquals(cached, current)) return false;
            cached = current;
            return true;
        }

        // O(1) entity lookup by Unity instance ID. Used by write commands that
        // target a specific building/beaver (e.g. set_workers building_id:-12345).
        public EntityComponent FindEntity(int id)
        {
            _entityCache.TryGetValue(id, out var result);
            return result;
        }

        // Called every 1 second on the main thread. Reads mutable properties from
        // live game components into the Write buffer. After all three loops complete,
        // Swap() makes the updated data available to the background HTTP thread.
        //
        // Why per-field reads instead of re-caching the whole entity?
        // Most fields don't change between refreshes. Reading individual properties
        // (~20 per building) is cheaper than calling GetComponent<T>() to resolve
        // all 18 component references again. Components are resolved ONCE in AddToIndexes.
        public void RefreshCachedState()
        {
            // --- BUILDINGS: read mutable state from each building's cached components ---
            for (int i = 0; i < Buildings.Write.Count; i++)
            {
                var c = Buildings.Write[i];
                try
                {
                    // IsFinished changes once (false->true when construction completes)
                    if (c.BlockObject != null)
                        c.Finished = c.BlockObject.IsFinished;
                    c.Paused = c.Pausable != null && c.Pausable.Paused;
                    c.Unreachable = c.Reachability != null && c.Reachability.IsAnyUnreachable();
                    c.Powered = c.Mechanical != null && c.Mechanical.ActiveAndPowered;
                    if (c.Workplace != null)
                    {
                        c.AssignedWorkers = c.Workplace.NumberOfAssignedWorkers;
                        c.DesiredWorkers = c.Workplace.DesiredWorkers;
                        c.MaxWorkers = c.Workplace.MaxWorkers;
                    }
                    if (c.Dwelling != null)
                    {
                        c.Dwellers = c.Dwelling.NumberOfDwellers;
                        c.MaxDwellers = c.Dwelling.MaxBeavers;
                    }
                    if (c.Floodgate != null)
                        c.FloodgateHeight = c.Floodgate.Height;
                    c.ConstructionPriority = c.BuilderPrio != null ? GetPriorityName(c.BuilderPrio.Priority) : null;
                    c.WorkplacePriorityStr = c.WorkplacePrio != null ? GetPriorityName(c.WorkplacePrio.Priority) : null;
                    if (c.Site != null)
                    {
                        c.BuildProgress = c.Site.BuildTimeProgress;
                        c.MaterialProgress = c.Site.MaterialProgress;
                        c.HasMaterials = c.Site.HasMaterialsToResumeBuilding;
                    }
                    if (c.Clutch != null) c.ClutchEngaged = c.Clutch.IsEngaged;
                    if (c.Wonder != null) c.WonderActive = c.Wonder.IsActive;
                    // Power network: each building can belong to one power graph.
                    // RuntimeHelpers.GetHashCode gives us a stable identity for the graph
                    // object so we can group buildings by network in the /api/power endpoint.
                    if (c.PowerNode != null)
                    {
                        try { var g = c.PowerNode.Graph; if (g != null) { c.PowerDemand = (int)g.PowerDemand; c.PowerSupply = (int)g.PowerSupply; c.PowerNetworkId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g); } } catch (Exception _ex) { TimberbotLog.Error("cache.power", _ex); }
                    }
                    // Manufactory: production buildings (lumber mill, gear workshop, etc).
                    // Recipes list is populated once (doesn't change for a building).
                    if (c.Manufactory != null)
                    {
                        c.CurrentRecipe = c.Manufactory.HasCurrentRecipe ? c.Manufactory.CurrentRecipe.Id : "";
                        c.ProductionProgress = c.Manufactory.ProductionProgress;
                        c.ReadyToProduce = c.Manufactory.IsReadyToProduce;
                        if (c.Recipes == null) // recipes don't change -- populate once
                        {
                            c.Recipes = new List<string>();
                            foreach (var r in c.Manufactory.ProductionRecipes)
                                c.Recipes.Add(r.Id);
                        }
                    }
                    // Breeding pods need berries + water to produce beaver children.
                    // NutrientStock tracks what's currently in the pod.
                    if (c.BreedingPod != null)
                    {
                        c.NeedsNutrients = c.BreedingPod.NeedsNutrients;
                        try
                        {
                            if (c.NutrientStock == null) c.NutrientStock = new Dictionary<string, int>();
                            c.NutrientStock.Clear(); // reuse dict, don't reallocate
                            foreach (var ga in c.BreedingPod.Nutrients)
                                if (ga.Amount > 0) c.NutrientStock[ga.GoodId] = ga.Amount;
                        }
                        catch (Exception _ex) { TimberbotLog.Error("cache.nutrients", _ex); }
                    }
                    // Inventory: aggregate stock across all a building's inventories.
                    // Buildings can have multiple inventories (input, output, construction).
                    // We skip the construction inventory and sum the rest into one dict.
                    // Uses indexed for-loop (not foreach) to avoid enumerator boxing.
                    if (c.Inventories != null)
                    {
                        int totalStock = 0, totalCapacity = 0;
                        if (c.Inventory == null) c.Inventory = new Dictionary<string, int>();
                        c.Inventory.Clear(); // reuse dict, don't reallocate
                        try
                        {
                            var allInv = c.Inventories.AllInventories;
                            for (int ii = 0; ii < allInv.Count; ii++)
                            {
                                var inv = allInv[ii];
                                if (inv.ComponentName == ConstructionSiteInventoryInitializer.InventoryComponentName) continue;
                                totalStock += inv.TotalAmountInStock;
                                totalCapacity += inv.Capacity;
                                var stock = inv.Stock;
                                for (int si = 0; si < stock.Count; si++)
                                {
                                    var ga = stock[si];
                                    if (ga.Amount > 0)
                                    {
                                        if (c.Inventory.ContainsKey(ga.GoodId))
                                            c.Inventory[ga.GoodId] += ga.Amount;
                                        else
                                            c.Inventory[ga.GoodId] = ga.Amount;
                                    }
                                }
                            }
                        }
                        catch (Exception _ex) { TimberbotLog.Error("cache.inventory", _ex); }
                        c.Stock = totalStock;
                        c.Capacity = totalCapacity;
                    }
                }
                catch (Exception _ex) { TimberbotLog.Error("cache.building", _ex); }
            }
            // --- TREES/CROPS: read mutable state (alive, grown, marked) ---
            // Unlike buildings, tree coords CAN change (they grow from seedling to full size)
            for (int i = 0; i < NaturalResources.Write.Count; i++)
            {
                var c = NaturalResources.Write[i];
                try
                {
                    if (c.BlockObject != null)
                    {
                        var coords = c.BlockObject.Coordinates;
                        c.X = coords.x; c.Y = coords.y; c.Z = coords.z;
                        // TreeCuttingArea is a game singleton that tracks which tiles are marked
                        c.Marked = c.Cuttable != null && _treeCuttingArea.IsInCuttingArea(coords);
                    }
                    c.Alive = c.Living != null && !c.Living.IsDead;
                    c.Grown = c.Growable != null && c.Growable.IsGrown;
                    c.Growth = c.Growable != null ? c.Growable.GrowthProgress : 0f;
                }
                catch (Exception _ex) { TimberbotLog.Error("cache.natural_resource", _ex); }
            }

            // --- BEAVERS: read mutable state (wellbeing, position, needs, carrying) ---
            // Beavers move constantly, so position is re-read every refresh.
            // Workplace and district use RefChanged to avoid CleanName string alloc
            // unless the beaver actually changed jobs or moved districts.
            for (int i = 0; i < Beavers.Write.Count; i++)
            {
                var c = Beavers.Write[i];
                try
                {
                    if (c.WbTracker != null)
                        c.Wellbeing = c.WbTracker.Wellbeing;
                    // position: Unity uses (x, y=height, z=depth) but Timberborn maps use
                    // (x, y=depth, z=height). We convert to the game's coordinate convention.
                    var go = c.Go;
                    if (go != null)
                    {
                        var pos = go.transform.position;
                        c.X = Mathf.FloorToInt(pos.x);
                        c.Y = Mathf.FloorToInt(pos.z);  // Unity Z -> game Y (depth)
                        c.Z = Mathf.FloorToInt(pos.y);  // Unity Y -> game Z (height)
                    }
                    // workplace: only derive the name string when the reference changes
                    var wp = c.Worker?.Workplace;
                    if (RefChanged(ref c.LastWorkplaceRef, wp))
                        c.Workplace = wp != null ? CleanName(wp.GameObject.name) : null;
                    // district: same RefChanged optimization
                    var dc = c.Citizen?.AssignedDistrict;
                    if (RefChanged(ref c.LastDistrictRef, dc))
                        c.District = dc?.DistrictName;
                    c.HasHome = c.Dweller != null && c.Dweller.HasHome;
                    c.Contaminated = c.Contaminable != null && c.Contaminable.IsContaminated;
                    if (c.Life != null) c.LifeProgress = c.Life.LifeProgress;
                    if (c.Deteriorable != null) c.DeteriorationProgress = (float)Math.Round(c.Deteriorable.DeteriorationProgress, 3);
                    if (c.Carrier != null)
                    {
                        c.LiftingCapacity = c.Carrier.LiftingCapacity;
                        c.Overburdened = c.Carrier.IsMovementSlowed;
                        if (c.Carrier.IsCarrying)
                        {
                            c.IsCarrying = true;
                            var ga = c.Carrier.CarriedGoods;
                            c.CarryingGood = ga.GoodId;
                            c.CarryAmount = ga.Amount;
                        }
                        else
                            c.IsCarrying = false;
                    }
                    // Needs: each beaver has ~30 needs (Hunger, Thirst, Sleep, etc).
                    // GetNeeds() returns a cached collection (confirmed zero-alloc via benchmark).
                    // CachedNeed is a struct -- Add() copies it to the list with no heap alloc.
                    // The List itself is allocated once and reused via Clear().
                    if (c.Needs == null) c.Needs = new List<CachedNeed>();
                    c.Needs.Clear();
                    c.AnyCritical = false;
                    if (c.NeedMgr != null)
                    {
                        foreach (var ns in c.NeedMgr.GetNeeds())
                        {
                            var need = c.NeedMgr.GetNeed(ns.Id);
                            c.Needs.Add(new CachedNeed
                            {
                                Id = ns.Id,
                                Points = (float)Math.Round(need.Points, 2),
                                Wellbeing = c.NeedMgr.GetNeedWellbeing(ns.Id),
                                Favorable = need.IsFavorable,
                                Critical = need.IsCritical,
                                Active = need.IsActive,
                                Group = ns.NeedGroupId ?? "" // which category (SocialLife, Fun, etc)
                            });
                            if (need.IsBelowWarningThreshold) c.AnyCritical = true;
                        }
                    }
                }
                catch (Exception _ex) { TimberbotLog.Error("cache.beaver", _ex); }
            }

            // SWAP: the Write buffers (just updated with fresh data) become the new Read
            // buffers. The background HTTP thread will now see the updated data.
            // The old Read buffers become the new Write targets for next refresh.
            // This is a pointer swap -- O(1), no data copying.
            // districts (not double-buffered -- tiny list, refreshed in place)
            Districts.Clear();
            try
            {
                var goods = _goodService.Goods;
                foreach (var dc in DistrictRegistry.FinishedDistrictCenters)
                {
                    var pop = dc.DistrictPopulation;
                    var cd = new CachedDistrict
                    {
                        Name = dc.DistrictName,
                        Adults = pop != null ? pop.NumberOfAdults : 0,
                        Children = pop != null ? pop.NumberOfChildren : 0,
                        Bots = pop != null ? pop.NumberOfBots : 0,
                    };
                    var counter = dc.GetComponent<DistrictResourceCounter>();
                    if (counter != null)
                    {
                        cd.Resources = new Dictionary<string, (int, int)>();
                        // pre-serialize toon format: "Water":50,"Log":236
                        var dj = _districtJw.Reset();
                        bool first = true;
                        foreach (var goodId in goods)
                        {
                            var rc = counter.GetResourceCount(goodId);
                            if (rc.AllStock > 0)
                            {
                                cd.Resources[goodId] = (rc.AvailableStock, rc.AllStock);
                                if (!first) dj.Raw(",");
                                first = false;
                                dj.Raw("\"").Raw(goodId).Raw("\":").Int(rc.AvailableStock);
                            }
                        }
                        cd.ResourcesToon = dj.ToString();

                        // pre-serialize json format: "Water":{"available":50,"all":54}
                        dj.Reset();
                        first = true;
                        foreach (var goodId in goods)
                        {
                            var rc = counter.GetResourceCount(goodId);
                            if (rc.AllStock > 0)
                            {
                                if (!first) dj.Raw(",");
                                first = false;
                                dj.Raw("\"").Raw(goodId).Raw("\":{\"available\":").Int(rc.AvailableStock).Raw(",\"all\":").Int(rc.AllStock).Raw("}");
                            }
                        }
                        cd.ResourcesJson = dj.ToString();
                    }
                    Districts.Add(cd);
                }
            }
            catch (System.Exception _ex) { TimberbotLog.Error("cache.districts", _ex); }

            Buildings.Swap();
            NaturalResources.Swap();
            Beavers.Swap();
        }

        // Called once at game load to populate all indexes from the entity registry.
        public void BuildAllIndexes()
        {
            Buildings.Clear();
            NaturalResources.Clear();
            Beavers.Clear();
            _entityCache.Clear();
            foreach (var ec in _entityRegistry.Entities)
                AddToIndexes(ec);
        }

        // Called when ANY entity is created (building placed, beaver born, tree grown).
        // We determine what kind of entity it is by checking for key components:
        //   Building component    -> it's a building (farmhouse, path, power wheel, etc)
        //   LivingNaturalResource -> it's a tree or crop
        //   NeedManager           -> it's a beaver or bot (they have needs)
        //
        // GetComponent<T>() is expensive (~microseconds) but we only call it ONCE per
        // entity here. The results are stored in the cached struct so RefreshCachedState
        // never needs to resolve components again.
        private void AddToIndexes(EntityComponent ec)
        {
            // cache for O(1) lookup by ID in write commands
            _entityCache[ec.GameObject.GetInstanceID()] = ec;

            // === BUILDING ===
            // Resolve all 18 component refs at add-time. Most will be null (a Path has
            // no Workplace, a FarmHouse has no Floodgate). Null checks in RefreshCachedState
            // skip components that don't exist on this building type.
            if (ec.GetComponent<Building>() != null)
            {
                var cb = new CachedBuilding
                {
                    Entity = ec,
                    Id = ec.GameObject.GetInstanceID(),
                    Name = CleanName(ec.GameObject.name),
                    BlockObject = ec.GetComponent<BlockObject>(),
                    Pausable = ec.GetComponent<PausableBuilding>(),
                    Floodgate = ec.GetComponent<Floodgate>(),
                    BuilderPrio = ec.GetComponent<BuilderPrioritizable>(),
                    Workplace = ec.GetComponent<Workplace>(),
                    WorkplacePrio = ec.GetComponent<WorkplacePriority>(),
                    Reachability = ec.GetComponent<EntityReachabilityStatus>(),
                    Mechanical = ec.GetComponent<MechanicalBuilding>(),
                    Status = ec.GetComponent<StatusSubject>(),
                    PowerNode = ec.GetComponent<MechanicalNode>(),
                    Site = ec.GetComponent<ConstructionSite>(),
                    Inventories = ec.GetComponent<Inventories>(),
                    Wonder = ec.GetComponent<Wonder>(),
                    Dwelling = ec.GetComponent<Dwelling>(),
                    Clutch = ec.GetComponent<Clutch>(),
                    Manufactory = ec.GetComponent<Manufactory>(),
                    BreedingPod = ec.GetComponent<BreedingPod>(),
                    RangedEffect = ec.GetComponent<RangedEffectBuildingSpec>(),
                    HasFloodgate = ec.GetComponent<Floodgate>() != null,
                    FloodgateMaxHeight = ec.GetComponent<Floodgate>()?.MaxHeight ?? 0f,
                    HasClutch = ec.GetComponent<Clutch>() != null,
                    HasWonder = ec.GetComponent<Wonder>() != null,
                    IsGenerator = ec.GetComponent<MechanicalNode>()?.IsGenerator ?? false,
                    IsConsumer = ec.GetComponent<MechanicalNode>()?.IsConsumer ?? false,
                    NominalPowerInput = ec.GetComponent<MechanicalNode>()?._nominalPowerInput ?? 0,
                    NominalPowerOutput = ec.GetComponent<MechanicalNode>()?._nominalPowerOutput ?? 0,
                    EffectRadius = ec.GetComponent<RangedEffectBuildingSpec>()?.EffectRadius ?? 0
                };
                // Static values set at add-time: buildings don't move, so coords/orientation
                // are read once and never refreshed. This saves ~2000 property reads/sec.
                var bo = cb.BlockObject;
                if (bo != null)
                {
                    var coords = bo.Coordinates;
                    cb.X = coords.x; cb.Y = coords.y; cb.Z = coords.z;
                    cb.Orientation = OrientNames[(int)bo.Orientation];

                    // Cache the multi-tile footprint for map/tiles endpoint.
                    // A 3x3 building occupies 9 tiles. Caching them here means the
                    // map endpoint doesn't need to call BlockObject at request time.
                    cb.OccupiedTiles = new List<(int, int, int)>();
                    try
                    {
                        foreach (var block in bo.PositionedBlocks.GetAllBlocks())
                        {
                            var tc = block.Coordinates;
                            cb.OccupiedTiles.Add((tc.x, tc.y, tc.z));
                        }
                    }
                    catch { cb.OccupiedTiles.Add((cb.Id, 0, 0)); }

                    // Entrance: where beavers enter the building (for path connectivity checks)
                    if (bo.HasEntrance)
                    {
                        try
                        {
                            var ent = bo.PositionedEntrance.DoorstepCoordinates;
                            cb.HasEntrance = true;
                            cb.EntranceX = ent.x;
                            cb.EntranceY = ent.y;
                        }
                        catch (Exception _ex) { TimberbotLog.Error("cache.entrance", _ex); }
                    }
                }

                // Add to BOTH buffers with separate instances (Clone).
                // Why Clone? Both buffers need their own copy of reference-type fields
                // (Recipes list, Inventory dict). If they shared the same List/Dict instance,
                // one thread modifying it would corrupt the other's read.
                Buildings.Add(cb, cb.Clone());
            }
            // === TREE or CROP ===
            // Trees and crops are "natural resources" in Timberborn's entity system.
            // They have growth progress, alive/dead state, and can be marked for cutting.
            else if (ec.GetComponent<LivingNaturalResource>() != null)
            {
                var nr = new CachedNaturalResource
                {
                    Id = ec.GameObject.GetInstanceID(),
                    Name = CleanName(ec.GameObject.name),
                    BlockObject = ec.GetComponent<BlockObject>(),
                    Living = ec.GetComponent<LivingNaturalResource>(),
                    Cuttable = ec.GetComponent<Cuttable>(),
                    Gatherable = ec.GetComponent<Gatherable>(),
                    Growable = ec.GetComponent<Timberborn.Growing.Growable>()
                };
                NaturalResources.Add(nr, nr.Clone());
            }
            // === BEAVER or BOT ===
            // Beavers and bots both have NeedManager (they have needs like Hunger, Energy).
            // Bots are distinguished by having the Bot component (IsBot = true).
            // Bots don't eat/drink/sleep but DO have Energy, ControlTower, and Grease needs.
            else if (ec.GetComponent<NeedManager>() != null)
            {
                var cb = new CachedBeaver
                {
                    Id = ec.GameObject.GetInstanceID(),
                    Name = CleanName(ec.GameObject.name),
                    IsBot = ec.GetComponent<Bot>() != null, // bots have Bot component, beavers don't
                    Go = ec.GameObject,
                    NeedMgr = ec.GetComponent<NeedManager>(),
                    WbTracker = ec.GetComponent<WellbeingTracker>(),
                    Worker = ec.GetComponent<Worker>(),
                    Life = ec.GetComponent<LifeProgressor>(),
                    Carrier = ec.GetComponent<GoodCarrier>(),
                    Deteriorable = ec.GetComponent<Deteriorable>(),
                    Contaminable = ec.GetComponent<Timberborn.BeaverContaminationSystem.Contaminable>(),
                    Dweller = ec.GetComponent<Dweller>(),
                    Citizen = ec.GetComponent<Timberborn.GameDistricts.Citizen>(),
                    Needs = new List<CachedNeed>()
                };
                Beavers.Add(cb, cb.Clone());
            }
        }

        // Remove from all indexes when an entity is destroyed.
        // RemoveAll scans both Write and Read buffers so both stay in sync.
        private void RemoveFromIndexes(EntityComponent ec)
        {
            int id = ec.GameObject.GetInstanceID();
            _entityCache.Remove(id);
            Buildings.RemoveAll(b => b.Id == id);
            NaturalResources.RemoveAll(n => n.Id == id);
            Beavers.RemoveAll(b => b.Id == id);
        }

        // Timberborn fires EntityInitializedEvent when ANY entity is created in the world.
        // This is how we learn about new buildings/beavers without polling.
        [OnEvent]
        public void OnEntityInitialized(EntityInitializedEvent e)
        {
            AddToIndexes(e.Entity);
            // push webhook event if anyone is listening (guard avoids alloc with no subscribers)
            if (WebhookMgr != null && WebhookMgr.Count > 0)
            {
                var ec = e.Entity;
                if (ec.GetComponent<Building>() != null)
                    WebhookMgr.PushEvent("building.placed", WebhookMgr.DataEntity(ec.GameObject.GetInstanceID(), CleanName(ec.GameObject.name)));
                else if (ec.GetComponent<NeedManager>() != null)
                    WebhookMgr.PushEvent("beaver.born", WebhookMgr.DataEntityBot(ec.GameObject.GetInstanceID(), CleanName(ec.GameObject.name), ec.GetComponent<Bot>() != null));
            }
        }

        // Timberborn fires EntityDeletedEvent when an entity is removed (demolished, died, etc).
        // Webhook fires BEFORE RemoveFromIndexes so the entity data is still available.
        [OnEvent]
        public void OnEntityDeleted(EntityDeletedEvent e)
        {
            if (WebhookMgr != null && WebhookMgr.Count > 0)
            {
                var ec = e.Entity;
                if (ec.GetComponent<Building>() != null)
                    WebhookMgr.PushEvent("building.demolished", WebhookMgr.DataEntity(ec.GameObject.GetInstanceID(), CleanName(ec.GameObject.name)));
                else if (ec.GetComponent<NeedManager>() != null)
                    WebhookMgr.PushEvent("beaver.died", WebhookMgr.DataEntity(ec.GameObject.GetInstanceID(), CleanName(ec.GameObject.name)));
            }
            RemoveFromIndexes(e.Entity);
        }

        // ================================================================
        // CACHED CLASSES
        //
        // Each class holds two types of fields:
        //   1. Component references (set once in AddToIndexes, never change)
        //      These are the "resolved refs" that eliminate GetComponent calls.
        //   2. Mutable state (refreshed every 1s in RefreshCachedState)
        //      These are the actual values served by API endpoints.
        //
        // Clone() creates a shallow copy for the second buffer. Reference-type
        // fields (Lists, Dicts) get null in the clone and are allocated on first use.
        // This prevents two threads from sharing the same List/Dict instance.
        // ================================================================

        public class CachedNaturalResource
        {
            public int Id;
            public string Name;
            public BlockObject BlockObject;
            public LivingNaturalResource Living;
            public Cuttable Cuttable;
            public Gatherable Gatherable;
            public Timberborn.Growing.Growable Growable;
            public int X, Y, Z;
            public bool Alive, Grown, Marked;
            public float Growth;
            public CachedNaturalResource Clone() => (CachedNaturalResource)MemberwiseClone();
        }

        public class CachedBuilding
        {
            public EntityComponent Entity;
            public int Id;
            public string Name;
            public BlockObject BlockObject;
            public PausableBuilding Pausable;
            public Floodgate Floodgate;
            public BuilderPrioritizable BuilderPrio;
            public Workplace Workplace;
            public WorkplacePriority WorkplacePrio;
            public EntityReachabilityStatus Reachability;
            public MechanicalBuilding Mechanical;
            public StatusSubject Status;
            public MechanicalNode PowerNode;
            public ConstructionSite Site;
            public Inventories Inventories;
            public Wonder Wonder;
            public Dwelling Dwelling;
            public Clutch Clutch;
            public Manufactory Manufactory;
            public BreedingPod BreedingPod;
            public RangedEffectBuildingSpec RangedEffect;
            public bool Finished, Paused, Unreachable, Powered;
            public int X, Y, Z;
            public string Orientation;
            public int AssignedWorkers, DesiredWorkers, MaxWorkers;
            public int Dwellers, MaxDwellers;
            public bool HasFloodgate;
            public float FloodgateHeight, FloodgateMaxHeight;
            public string ConstructionPriority, WorkplacePriorityStr;
            public float BuildProgress, MaterialProgress;
            public bool HasMaterials;
            public bool ClutchEngaged, HasClutch;
            public bool WonderActive, HasWonder;
            public bool IsGenerator, IsConsumer;
            public int NominalPowerInput, NominalPowerOutput;
            public int PowerDemand, PowerSupply, PowerNetworkId;
            public string CurrentRecipe;
            public List<string> Recipes;
            public float ProductionProgress;
            public bool ReadyToProduce;
            public bool NeedsNutrients;
            public Dictionary<string, int> NutrientStock;
            public Dictionary<string, int> Inventory;
            public int EffectRadius;
            public int Stock, Capacity;
            public List<(int x, int y, int z)> OccupiedTiles;
            public bool HasEntrance;
            public int EntranceX, EntranceY;
            public CachedBuilding Clone()
            {
                var c = (CachedBuilding)MemberwiseClone();
                c.Recipes = null;
                c.Inventory = null;
                c.NutrientStock = null;
                c.OccupiedTiles = OccupiedTiles;
                return c;
            }
        }

        public struct CachedNeed
        {
            public string Id, Group;
            public float Points;
            public int Wellbeing;
            public bool Favorable, Critical, Active;
        }

        public class CachedBeaver
        {
            public int Id;
            public string Name;
            public bool IsBot;
            public GameObject Go;
            public NeedManager NeedMgr;
            public WellbeingTracker WbTracker;
            public Worker Worker;
            public LifeProgressor Life;
            public GoodCarrier Carrier;
            public Deteriorable Deteriorable;
            public Timberborn.BeaverContaminationSystem.Contaminable Contaminable;
            public Dweller Dweller;
            public Timberborn.GameDistricts.Citizen Citizen;
            public float Wellbeing;
            public int X, Y, Z;
            public string Workplace, District;
            public object LastWorkplaceRef;
            public object LastDistrictRef;
            public bool HasHome, Contaminated;
            public float LifeProgress, DeteriorationProgress;
            public bool IsCarrying;
            public string CarryingGood;
            public int CarryAmount, LiftingCapacity;
            public bool Overburdened;
            public bool AnyCritical;
            public List<CachedNeed> Needs;
            public CachedBeaver Clone()
            {
                var c = (CachedBeaver)MemberwiseClone();
                c.Needs = new List<CachedNeed>();
                return c;
            }
        }

        public class CachedDistrict
        {
            public string Name;
            public int Adults, Children, Bots;
            public Dictionary<string, (int available, int all)> Resources;  // for projection math + toon resources
            public string ResourcesToon;  // pre-serialized: "Water":50,"Log":236,...
            public string ResourcesJson;  // pre-serialized: "Water":{"available":50,"all":54},...
        }
    }
}
