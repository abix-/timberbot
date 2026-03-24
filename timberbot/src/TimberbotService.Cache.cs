using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.BuilderPrioritySystem;
using Timberborn.Buildings;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockObjectTools;
using Timberborn.Coordinates;
using Timberborn.Cutting;
using Timberborn.TemplateInstantiation;
using Timberborn.MapIndexSystem;
using Timberborn.TerrainSystem;
using Timberborn.WaterSystem;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.Planting;
using Timberborn.Gathering;
using Timberborn.GameCycleSystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.PrioritySystem;
using Timberborn.ResourceCountingSystem;
using Timberborn.SingletonSystem;
using Timberborn.Stockpiles;
using Timberborn.TimeSystem;
using Timberborn.WaterBuildings;
using Timberborn.WeatherSystem;
using Timberborn.WorkSystem;
using Timberborn.NeedSystem;
using Timberborn.LifeSystem;
using Timberborn.Wellbeing;
using Timberborn.BuildingsReachability;
using Timberborn.ConstructionSites;
using Timberborn.MechanicalSystem;
using Timberborn.ScienceSystem;
using Timberborn.BeaverContaminationSystem;
using Timberborn.Bots;
using Timberborn.Carrying;
using Timberborn.DeteriorationSystem;
using Timberborn.Wonders;
using Timberborn.NotificationSystem;
using Timberborn.StatusSystem;
using Timberborn.DwellingSystem;
using Timberborn.PowerManagement;
using Timberborn.SoilContaminationSystem;
using Timberborn.Hauling;
using Timberborn.Workshops;
using Timberborn.Reproduction;
using Timberborn.Fields;
using Timberborn.GameDistrictsMigration;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;
using Timberborn.PlantingUI;
using Timberborn.BuildingsNavigation;
using Timberborn.SoilMoistureSystem;
using Timberborn.NeedSpecs;
using Timberborn.GameFactionSystem;
using Timberborn.RangedEffectSystem;
using UnityEngine;

namespace Timberbot
{
    public partial class TimberbotService
    {
        // snapshot all mutable state on main thread, then swap buffers.
        // background thread reads from _read lists (never modified during read).
        private void RefreshCachedState()
        {
            for (int i = 0; i < _buildings.Write.Count; i++)
            {
                var c = _buildings.Write[i];
                try
                {
                    if (c.BlockObject != null)
                        c.Finished = c.BlockObject.IsFinished;
                    // X, Y, Z, Orientation set at add-time (immutable after placement)
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
                    {
                        c.FloodgateHeight = c.Floodgate.Height;
                    }
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
                        try { var g = c.PowerNode.Graph; if (g != null) { c.PowerDemand = (int)g.PowerDemand; c.PowerSupply = (int)g.PowerSupply; c.PowerNetworkId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g); } } catch { }
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
                        catch { }
                    }
                    // EffectRadius, IsGenerator, IsConsumer, NominalPower, HasFloodgate,
                    // HasClutch, HasWonder, FloodgateMaxHeight set at add-time (static values)
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
                        catch { }
                        c.Stock = totalStock;
                        c.Capacity = totalCapacity;
                    }
                    _buildings.Write[i] = c;
                }
                catch { }
            }
            for (int i = 0; i < _naturalResources.Write.Count; i++)
            {
                var c = _naturalResources.Write[i];
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
                    _naturalResources.Write[i] = c;
                }
                catch { }
            }
            // beavers
            for (int i = 0; i < _beavers.Write.Count; i++)
            {
                var c = _beavers.Write[i];
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
                    if (c.Deteriorable != null) c.DeteriorationProgress = (float)System.Math.Round(c.Deteriorable.DeteriorationProgress, 3);
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
                        {
                            c.IsCarrying = false;
                        }
                    }
                    // needs
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
                                Points = (float)System.Math.Round(need.Points, 2),
                                Wellbeing = c.NeedMgr.GetNeedWellbeing(ns.Id),
                                Favorable = need.IsFavorable,
                                Critical = need.IsCritical,
                                Active = need.IsActive,
                                Group = ns.NeedGroupId ?? ""
                            });
                            if (need.IsBelowWarningThreshold) c.AnyCritical = true;
                        }
                    }
                    _beavers.Write[i] = c;
                }
                catch { }
            }
            // swap: background thread gets the freshly updated buffer
            _buildings.Swap();
            _naturalResources.Swap();
            _beavers.Swap();
        }

        // PERF: event-driven entity indexes with cached component refs.
        // Components resolved once at add-time. Zero GetComponent calls per request.

        private struct CachedNaturalResource
        {
            // immutable refs (set at add-time)
            public int Id;
            public string Name;
            public BlockObject BlockObject;
            public LivingNaturalResource Living;
            public Cuttable Cuttable;
            public Gatherable Gatherable;
            public Timberborn.Growing.Growable Growable;
            // mutable state (refreshed on main thread)
            public int X, Y, Z;
            public bool Alive, Grown, Marked;
            public float Growth;
        }

        private struct CachedBuilding
        {
            // immutable refs (set at add-time)
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
            // mutable state (refreshed on main thread, safe to read from background)
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
            // spatial footprint (set once at add-time, immutable)
            public List<(int x, int y, int z)> OccupiedTiles;
            public bool HasEntrance;
            public int EntranceX, EntranceY;
        }

        private struct CachedNeed
        {
            public string Id, Group;
            public float Points;
            public int Wellbeing;
            public bool Favorable, Critical, Active;
        }

        private struct CachedBeaver
        {
            // immutable refs (add-time)
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
            public Contaminable Contaminable;
            public Dweller Dweller;
            public Timberborn.GameDistricts.Citizen Citizen;
            // mutable (refreshed on main thread)
            public float Wellbeing;
            public int X, Y, Z;
            public string Workplace, District;
            public object LastWorkplaceRef; // reference comparison to avoid CleanName per refresh
            public object LastDistrictRef; // reference comparison to avoid DistrictName per refresh
            public bool HasHome, Contaminated;
            public float LifeProgress, DeteriorationProgress;
            public bool IsCarrying;
            public string CarryingGood;
            public int CarryAmount, LiftingCapacity;
            public bool Overburdened;
            public bool AnyCritical;
            public List<CachedNeed> Needs;
        }

        private readonly DoubleBuffer<CachedBuilding> _buildings = new DoubleBuffer<CachedBuilding>();
        private readonly DoubleBuffer<CachedNaturalResource> _naturalResources = new DoubleBuffer<CachedNaturalResource>();
        private readonly DoubleBuffer<CachedBeaver> _beavers = new DoubleBuffer<CachedBeaver>();
        private readonly Dictionary<int, EntityComponent> _entityCache = new Dictionary<int, EntityComponent>();
        // separate StringBuilders per endpoint to avoid contention on background thread
        private readonly System.Text.StringBuilder _sbBuildings = new System.Text.StringBuilder(200000);
        private readonly System.Text.StringBuilder _sbTrees = new System.Text.StringBuilder(300000);
        private readonly System.Text.StringBuilder _sbCrops = new System.Text.StringBuilder(100000);
        private static readonly System.Collections.Generic.HashSet<string> _treeSpecies = new System.Collections.Generic.HashSet<string>
            { "Pine", "Birch", "Oak", "Maple", "Chestnut", "Mangrove" };
        private static readonly System.Collections.Generic.HashSet<string> _cropSpecies = new System.Collections.Generic.HashSet<string>
            { "Kohlrabi", "Soybean", "Corn", "Sunflower", "Eggplant", "Algae", "Cassava", "Mushroom", "Potato", "Wheat", "Carrot" };

        private void BuildAllIndexes()
        {
            _buildings.Clear();
            _naturalResources.Clear();
            _beavers.Clear();
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
                    // static values -- set once at add-time, never refreshed
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
                // spatial footprint (immutable after placement)
                var bo = cb.BlockObject;
                if (bo != null)
                {
                    // immutable after placement -- set once at add-time
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
                    catch { cb.OccupiedTiles.Add((cb.Id, 0, 0)); } // fallback shouldn't happen
                    if (bo.HasEntrance)
                    {
                        try
                        {
                            var ent = bo.PositionedEntrance.DoorstepCoordinates;
                            cb.HasEntrance = true;
                            cb.EntranceX = ent.x;
                            cb.EntranceY = ent.y;
                        }
                        catch { }
                    }
                }
                // separate reference-type instances per buffer to avoid shared mutation
                var cbRead = cb;
                cbRead.Recipes = null; // populated on first refresh
                cbRead.Inventory = null;
                cbRead.NutrientStock = null;
                _buildings.Add(cb, cbRead);
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
                _naturalResources.Add(nr);
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
                    Contaminable = ec.GetComponent<Contaminable>(),
                    Dweller = ec.GetComponent<Dweller>(),
                    Citizen = ec.GetComponent<Timberborn.GameDistricts.Citizen>(),
                    Needs = new List<CachedNeed>()
                };
                var cbRead = cb;
                cbRead.Needs = new List<CachedNeed>();
                _beavers.Add(cb, cbRead);
            }
        }

        private void RemoveFromIndexes(EntityComponent ec)
        {
            int id = ec.GameObject.GetInstanceID();
            _entityCache.Remove(id);
            _buildings.RemoveAll(b => b.Id == id);
            _naturalResources.RemoveAll(n => n.Id == id);
            _beavers.RemoveAll(b => b.Id == id);
        }

        [OnEvent]
        public void OnEntityInitialized(EntityInitializedEvent e)
        {
            AddToIndexes(e.Entity);
            // webhooks
            var ec = e.Entity;
            if (ec.GetComponent<Building>() != null)
                PushEvent("building.placed", new { id = ec.GameObject.GetInstanceID(), name = CleanName(ec.GameObject.name) });
            else if (ec.GetComponent<NeedManager>() != null)
                PushEvent("beaver.born", new { id = ec.GameObject.GetInstanceID(), name = CleanName(ec.GameObject.name), isBot = ec.GetComponent<Bot>() != null });
        }

        [OnEvent]
        public void OnEntityDeleted(EntityDeletedEvent e)
        {
            // webhooks (before removing from indexes)
            var ec = e.Entity;
            if (ec.GetComponent<Building>() != null)
                PushEvent("building.demolished", new { id = ec.GameObject.GetInstanceID(), name = CleanName(ec.GameObject.name) });
            else if (ec.GetComponent<NeedManager>() != null)
                PushEvent("beaver.died", new { id = ec.GameObject.GetInstanceID(), name = CleanName(ec.GameObject.name) });
            RemoveFromIndexes(e.Entity);
        }

        private static string CleanName(string name) =>
            name.Replace("(Clone)", "").Replace(".IronTeeth", "").Replace(".Folktails", "").Trim();

        // ref-compare helper: returns true (and updates cached ref) only when the reference changes.
        // use to skip expensive string derivation (CleanName, DistrictName) when the source object hasn't changed.
        private static bool RefChanged(ref object cached, object current)
        {
            if (ReferenceEquals(cached, current)) return false;
            cached = current;
            return true;
        }

        private EntityComponent FindEntity(int id)
        {
            _entityCache.TryGetValue(id, out var result);
            return result;
        }

    }
}
