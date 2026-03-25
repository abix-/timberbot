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
        private readonly EntityRegistry _entityRegistry;   // all entities in the game
        private readonly TreeCuttingArea _treeCuttingArea;  // which tiles are marked for tree cutting
        private readonly EventBus _eventBus;                // entity lifecycle events

        // set by TimberbotService in Load() before use
        public TimberbotWebhook WebhookMgr;

        // double-buffered entity lists: main thread writes, background thread reads
        public readonly TimberbotDoubleBuffer<CachedBuilding> Buildings = new TimberbotDoubleBuffer<CachedBuilding>();
        public readonly TimberbotDoubleBuffer<CachedNaturalResource> NaturalResources = new TimberbotDoubleBuffer<CachedNaturalResource>();
        public readonly TimberbotDoubleBuffer<CachedBeaver> Beavers = new TimberbotDoubleBuffer<CachedBeaver>();

        // O(1) entity lookup by Unity instance ID (for write commands that target specific entities)
        private readonly Dictionary<int, EntityComponent> _entityCache = new Dictionary<int, EntityComponent>();

        // shared JSON writer instance: 300KB pre-allocated StringBuilder, reused via Reset()
        public readonly TimberbotJw Jw = new TimberbotJw(300000);

        public static readonly HashSet<string> TreeSpecies = new HashSet<string>
            { "Pine", "Birch", "Oak", "Maple", "Chestnut", "Mangrove" };
        public static readonly HashSet<string> CropSpecies = new HashSet<string>
            { "Kohlrabi", "Soybean", "Corn", "Sunflower", "Eggplant", "Algae", "Cassava", "Mushroom", "Potato", "Wheat", "Carrot" };

        public static readonly string[] OrientNames = { "south", "west", "north", "east" };
        public static readonly string[] PriorityNames = { "VeryLow", "Low", "Normal", "High", "VeryHigh" };

        public TimberbotEntityCache(
            EntityRegistry entityRegistry,
            TreeCuttingArea treeCuttingArea,
            EventBus eventBus)
        {
            _entityRegistry = entityRegistry;
            _treeCuttingArea = treeCuttingArea;
            _eventBus = eventBus;
        }

        public void Register() => _eventBus.Register(this);
        public void Unregister() => _eventBus.Unregister(this);

        public static string GetPriorityName(Priority p)
        {
            int i = (int)p;
            return (i >= 0 && i < PriorityNames.Length) ? PriorityNames[i] : "Normal";
        }

        public static string CleanName(string name) =>
            name.Replace("(Clone)", "").Replace(".IronTeeth", "").Replace(".Folktails", "").Trim();

        public static bool RefChanged(ref object cached, object current)
        {
            if (ReferenceEquals(cached, current)) return false;
            cached = current;
            return true;
        }

        public EntityComponent FindEntity(int id)
        {
            _entityCache.TryGetValue(id, out var result);
            return result;
        }

        public void RefreshCachedState()
        {
            for (int i = 0; i < Buildings.Write.Count; i++)
            {
                var c = Buildings.Write[i];
                try
                {
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
                    if (c.PowerNode != null)
                    {
                        try { var g = c.PowerNode.Graph; if (g != null) { c.PowerDemand = (int)g.PowerDemand; c.PowerSupply = (int)g.PowerSupply; c.PowerNetworkId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g); } } catch (Exception _ex) { TimberbotLog.Error("cache.power", _ex); }
                    }
                    if (c.Manufactory != null)
                    {
                        c.CurrentRecipe = c.Manufactory.HasCurrentRecipe ? c.Manufactory.CurrentRecipe.Id : "";
                        c.ProductionProgress = c.Manufactory.ProductionProgress;
                        c.ReadyToProduce = c.Manufactory.IsReadyToProduce;
                        if (c.Recipes == null)
                        {
                            c.Recipes = new List<string>();
                            foreach (var r in c.Manufactory.ProductionRecipes)
                                c.Recipes.Add(r.Id);
                        }
                    }
                    if (c.BreedingPod != null)
                    {
                        c.NeedsNutrients = c.BreedingPod.NeedsNutrients;
                        try
                        {
                            if (c.NutrientStock == null) c.NutrientStock = new Dictionary<string, int>();
                            c.NutrientStock.Clear();
                            foreach (var ga in c.BreedingPod.Nutrients)
                                if (ga.Amount > 0) c.NutrientStock[ga.GoodId] = ga.Amount;
                        }
                        catch (Exception _ex) { TimberbotLog.Error("cache.nutrients", _ex); }
                    }
                    if (c.Inventories != null)
                    {
                        int totalStock = 0, totalCapacity = 0;
                        if (c.Inventory == null) c.Inventory = new Dictionary<string, int>();
                        c.Inventory.Clear();
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
            for (int i = 0; i < NaturalResources.Write.Count; i++)
            {
                var c = NaturalResources.Write[i];
                try
                {
                    if (c.BlockObject != null)
                    {
                        var coords = c.BlockObject.Coordinates;
                        c.X = coords.x; c.Y = coords.y; c.Z = coords.z;
                        c.Marked = c.Cuttable != null && _treeCuttingArea.IsInCuttingArea(coords);
                    }
                    c.Alive = c.Living != null && !c.Living.IsDead;
                    c.Grown = c.Growable != null && c.Growable.IsGrown;
                    c.Growth = c.Growable != null ? c.Growable.GrowthProgress : 0f;
                }
                catch (Exception _ex) { TimberbotLog.Error("cache.natural_resource", _ex); }
            }
            for (int i = 0; i < Beavers.Write.Count; i++)
            {
                var c = Beavers.Write[i];
                try
                {
                    if (c.WbTracker != null)
                        c.Wellbeing = c.WbTracker.Wellbeing;
                    var go = c.Go;
                    if (go != null)
                    {
                        var pos = go.transform.position;
                        c.X = Mathf.FloorToInt(pos.x);
                        c.Y = Mathf.FloorToInt(pos.z);
                        c.Z = Mathf.FloorToInt(pos.y);
                    }
                    var wp = c.Worker?.Workplace;
                    if (RefChanged(ref c.LastWorkplaceRef, wp))
                        c.Workplace = wp != null ? CleanName(wp.GameObject.name) : null;
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
                                Group = ns.NeedGroupId ?? ""
                            });
                            if (need.IsBelowWarningThreshold) c.AnyCritical = true;
                        }
                    }
                }
                catch (Exception _ex) { TimberbotLog.Error("cache.beaver", _ex); }
            }
            Buildings.Swap();
            NaturalResources.Swap();
            Beavers.Swap();
        }

        public void BuildAllIndexes()
        {
            Buildings.Clear();
            NaturalResources.Clear();
            Beavers.Clear();
            _entityCache.Clear();
            foreach (var ec in _entityRegistry.Entities)
                AddToIndexes(ec);
        }

        private void AddToIndexes(EntityComponent ec)
        {
            _entityCache[ec.GameObject.GetInstanceID()] = ec;

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
                var bo = cb.BlockObject;
                if (bo != null)
                {
                    var coords = bo.Coordinates;
                    cb.X = coords.x; cb.Y = coords.y; cb.Z = coords.z;
                    cb.Orientation = OrientNames[(int)bo.Orientation];
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
                Buildings.Add(cb, cb.Clone());
            }
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
            else if (ec.GetComponent<NeedManager>() != null)
            {
                var cb = new CachedBeaver
                {
                    Id = ec.GameObject.GetInstanceID(),
                    Name = CleanName(ec.GameObject.name),
                    IsBot = ec.GetComponent<Bot>() != null,
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

        private void RemoveFromIndexes(EntityComponent ec)
        {
            int id = ec.GameObject.GetInstanceID();
            _entityCache.Remove(id);
            Buildings.RemoveAll(b => b.Id == id);
            NaturalResources.RemoveAll(n => n.Id == id);
            Beavers.RemoveAll(b => b.Id == id);
        }

        [OnEvent]
        public void OnEntityInitialized(EntityInitializedEvent e)
        {
            AddToIndexes(e.Entity);
            if (WebhookMgr != null && WebhookMgr.Count > 0)
            {
                var ec = e.Entity;
                if (ec.GetComponent<Building>() != null)
                    WebhookMgr.PushEvent("building.placed", WebhookMgr.DataEntity(ec.GameObject.GetInstanceID(), CleanName(ec.GameObject.name)));
                else if (ec.GetComponent<NeedManager>() != null)
                    WebhookMgr.PushEvent("beaver.born", WebhookMgr.DataEntityBot(ec.GameObject.GetInstanceID(), CleanName(ec.GameObject.name), ec.GetComponent<Bot>() != null));
            }
        }

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
    }
}
