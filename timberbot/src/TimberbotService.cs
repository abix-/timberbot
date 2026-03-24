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
    // HTTP API service. Injected via Bindito DI, runs as game singleton.
    // Returns plain objects serialized to JSON by TimberbotHttpServer.
    //
    // format param: "toon" (default) = flat for tabular display, "json" = full nested data
    // entity access: no typed queries in Timberborn, so we iterate _entityRegistry.Entities + GetComponent<T>()
    // names: CleanName() strips "(Clone)", ".IronTeeth", ".Folktails" from all output
    // entity lookup: FindEntity() uses per-frame dictionary cache for O(1) writes
    public class TimberbotService : ILoadableSingleton, IUpdatableSingleton, IUnloadableSingleton
    {
        // -- game services (injected via Bindito constructor) --
        private readonly IGoodService _goodService;                         // list of all good types (Water, Log, Plank, etc)
        private readonly DistrictCenterRegistry _districtCenterRegistry;    // all district centers -> population + resources
        private readonly GameCycleService _gameCycleService;                // cycle number, day within cycle
        private readonly WeatherService _weatherService;                    // drought/temperate durations
        private readonly IDayNightCycle _dayNightCycle;                     // day number, progress (0-1)
        private readonly SpeedManager _speedManager;                        // game speed (raw values: 0,1,3,7 mapped to levels 0-3)
        private readonly EntityRegistry _entityRegistry;                    // ALL entities in game -- buildings, beavers, trees, everything
        private readonly TreeCuttingArea _treeCuttingArea;                  // which tiles are marked for tree cutting
        private readonly PlantingService _plantingService;                  // mark/clear crop planting areas
        private readonly BuildingService _buildingService;                  // building templates/specs (not placed buildings)
        private readonly BlockObjectPlacerService _blockObjectPlacerService;// Place() to create buildings in world
        private readonly EntityService _entityService;                      // Delete() to remove entities
        private readonly ITerrainService _terrainService;                   // terrain height queries
        private readonly IThreadSafeWaterMap _waterMap;                     // water height + column contamination
        private readonly MapIndexService _mapIndexService;                  // 2D/3D index math for water columns
        private readonly IThreadSafeColumnTerrainMap _terrainMap;           // column terrain heights
        private readonly ScienceService _scienceService;                    // science points
        private readonly BuildingUnlockingService _buildingUnlockingService;// unlock buildings with science
        private readonly NotificationSaver _notificationSaver;              // game event history
        private readonly WorkingHoursManager _workingHoursManager;          // work schedule (end hours)
        private readonly ISoilContaminationService _soilContaminationService;// soil contamination from badwater
        private readonly PopulationDistributorRetriever _populationDistributorRetriever; // migrate beavers between districts
        private readonly ToolButtonService _toolButtonService;              // UI toolbar buttons (for unlock UI update)
        private readonly ToolUnlockingService _toolUnlockingService;      // full unlock flow (cost + UI + events)
        private readonly UnlockedPlantableGroupsRegistry _unlockedPlantableGroupsRegistry; // plantable groups (for unlock UI)
        private readonly RecipeSpecService _recipeSpecService;              // manufactory recipe lookup
        private readonly StackableBlockService _stackableBlockService;    // stackable block checks (platforms)
        private readonly DistrictPathNavRangeDrawerRegistrar _districtPathNavRegistrar; // district road connectivity
        private readonly Timberborn.Navigation.INavMeshService _navMeshService;   // road/terrain nav mesh connectivity
        private readonly ISoilMoistureService _soilMoistureService;       // soil moisture/irrigation
        private readonly PlantingAreaValidator _plantingAreaValidator;     // planting spot validation (same as player UI green/red)
        private readonly PlantablePreviewFactory _plantablePreviewFactory; // crop preview for placement validation
        private readonly FactionNeedService _factionNeedService;           // need specs per faction (beaver/bot)
        private readonly NeedGroupSpecService _needGroupSpecService;       // need group categories (Social, Hygiene, etc)
        private readonly PreviewFactory _previewFactory;                       // create preview entities for placement validation
        private readonly EventBus _eventBus;                                    // game event bus for entity lifecycle events
        private TimberbotHttpServer _server;

        // settings (loaded from settings.json in mod folder)
        private float _refreshInterval = 1.0f;   // seconds between cache refreshes (default: 1s)
        private bool _debugEnabled = false;       // enable /api/debug endpoint (default: off)
        private int _httpPort = 8085;             // HTTP server port

        public TimberbotService(
            IGoodService goodService,
            DistrictCenterRegistry districtCenterRegistry,
            GameCycleService gameCycleService,
            WeatherService weatherService,
            IDayNightCycle dayNightCycle,
            SpeedManager speedManager,
            EntityRegistry entityRegistry,
            TreeCuttingArea treeCuttingArea,
            PlantingService plantingService,
            BuildingService buildingService,
            BlockObjectPlacerService blockObjectPlacerService,
            EntityService entityService,
            ITerrainService terrainService,
            IThreadSafeWaterMap waterMap,
            MapIndexService mapIndexService,
            IThreadSafeColumnTerrainMap terrainMap,
            ScienceService scienceService,
            BuildingUnlockingService buildingUnlockingService,
            NotificationSaver notificationSaver,
            WorkingHoursManager workingHoursManager,
            ISoilContaminationService soilContaminationService,
            PopulationDistributorRetriever populationDistributorRetriever,
            ToolButtonService toolButtonService,
            ToolUnlockingService toolUnlockingService,
            UnlockedPlantableGroupsRegistry unlockedPlantableGroupsRegistry,
            RecipeSpecService recipeSpecService,
            DistrictPathNavRangeDrawerRegistrar districtPathNavRegistrar,
            Timberborn.Navigation.INavMeshService navMeshService,
            ISoilMoistureService soilMoistureService,
            StackableBlockService stackableBlockService,
            PlantingAreaValidator plantingAreaValidator,
            PlantablePreviewFactory plantablePreviewFactory,
            FactionNeedService factionNeedService,
            NeedGroupSpecService needGroupSpecService,
            PreviewFactory previewFactory,
            EventBus eventBus)
        {
            _goodService = goodService;
            _districtCenterRegistry = districtCenterRegistry;
            _gameCycleService = gameCycleService;
            _weatherService = weatherService;
            _dayNightCycle = dayNightCycle;
            _speedManager = speedManager;
            _entityRegistry = entityRegistry;
            _treeCuttingArea = treeCuttingArea;
            _plantingService = plantingService;
            _buildingService = buildingService;
            _blockObjectPlacerService = blockObjectPlacerService;
            _entityService = entityService;
            _terrainService = terrainService;
            _waterMap = waterMap;
            _mapIndexService = mapIndexService;
            _terrainMap = terrainMap;
            _scienceService = scienceService;
            _buildingUnlockingService = buildingUnlockingService;
            _notificationSaver = notificationSaver;
            _workingHoursManager = workingHoursManager;
            _soilContaminationService = soilContaminationService;
            _populationDistributorRetriever = populationDistributorRetriever;
            _toolButtonService = toolButtonService;
            _toolUnlockingService = toolUnlockingService;
            _unlockedPlantableGroupsRegistry = unlockedPlantableGroupsRegistry;
            _recipeSpecService = recipeSpecService;
            _districtPathNavRegistrar = districtPathNavRegistrar;
            _navMeshService = navMeshService;
            _soilMoistureService = soilMoistureService;
            _stackableBlockService = stackableBlockService;
            _plantingAreaValidator = plantingAreaValidator;
            _plantablePreviewFactory = plantablePreviewFactory;
            _factionNeedService = factionNeedService;
            _needGroupSpecService = needGroupSpecService;
            _previewFactory = previewFactory;
            _eventBus = eventBus;
        }

        public void Load()
        {
            LoadSettings();
            _eventBus.Register(this);
            BuildAllIndexes();
            _server = new TimberbotHttpServer(_httpPort, this, _debugEnabled);
            Debug.Log($"[Timberbot] HTTP server started on port {_httpPort} (refresh={_refreshInterval}s, debug={_debugEnabled})");
        }

        private void LoadSettings()
        {
            try
            {
                var modDir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                    "Timberborn", "Mods", "Timberbot");
                var path = System.IO.Path.Combine(modDir, "settings.json");
                if (System.IO.File.Exists(path))
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path));
                    _refreshInterval = json.Value<float>("refreshIntervalSeconds");
                    if (_refreshInterval <= 0) _refreshInterval = 1.0f;
                    _debugEnabled = json.Value<bool>("debugEndpointEnabled");
                    _httpPort = json.Value<int>("httpPort");
                    if (_httpPort <= 0) _httpPort = 8085;
                }
            }
            catch (System.Exception ex)
            {
                Debug.Log($"[Timberbot] settings.json load failed, using defaults: {ex.Message}");
            }
        }

        public void Unload()
        {
            _eventBus.Unregister(this);
            _server?.Stop();
            _server = null;
            Debug.Log("[Timberbot] HTTP server stopped");
        }

        private float _lastRefreshTime = 0f;

        public void UpdateSingleton()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastRefreshTime >= _refreshInterval)
            {
                _lastRefreshTime = now;
                RefreshCachedState();
            }
            _server?.DrainRequests();
        }

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
                            foreach (var inv in c.Inventories.AllInventories)
                            {
                                if (inv.ComponentName == ConstructionSiteInventoryInitializer.InventoryComponentName) continue;
                                totalStock += inv.TotalAmountInStock;
                                totalCapacity += inv.Capacity;
                                foreach (var ga in inv.Stock)
                                {
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
        private readonly System.Text.StringBuilder _sbTrees = new System.Text.StringBuilder(400000);

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
        }

        [OnEvent]
        public void OnEntityDeleted(EntityDeletedEvent e)
        {
            RemoveFromIndexes(e.Entity);
        }

        // strip Unity/faction suffixes so API returns clean names
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

        // ================================================================
        // READ ENDPOINTS
        // Each returns an object serialized to JSON. The "format" param controls shape:
        //   toon: flat dicts/lists for tabular TOON display (default for CLI)
        //   json: full nested data for programmatic access (--json flag)
        // ================================================================

        // PERF: uses typed indexes instead of scanning all entities.
        // Three passes over subsets (buildings, natural resources, beavers) instead of one pass over everything.
        public object CollectSummary(string format = "toon")
        {
            int markedGrown = 0, markedSeedling = 0, unmarkedGrown = 0;
            int occupiedBeds = 0, totalBeds = 0;
            int totalVacancies = 0, assignedWorkers = 0;
            float totalWellbeing = 0f;
            int beaverCount = 0;
            int alertUnstaffed = 0, alertUnpowered = 0, alertUnreachable = 0;
            int miserable = 0, critical = 0;

            // trees (read cached primitives only -- zero Unity calls)
            foreach (var c in _naturalResources.Read)
            {
                if (c.Cuttable == null) continue;
                if (c.Alive)
                {
                    if (c.Marked && c.Grown) markedGrown++;
                    else if (c.Marked && !c.Grown) markedSeedling++;
                    else if (!c.Marked && c.Grown) unmarkedGrown++;
                }
            }

            // buildings (read cached primitives only -- zero Unity calls)
            foreach (var c in _buildings.Read)
            {
                if (c.Dwelling != null)
                {
                    occupiedBeds += c.Dwellers;
                    totalBeds += c.MaxDwellers;
                }
                if (c.Workplace != null)
                {
                    assignedWorkers += c.AssignedWorkers;
                    totalVacancies += c.DesiredWorkers;
                    if (c.DesiredWorkers > 0 && c.AssignedWorkers < c.DesiredWorkers)
                        alertUnstaffed++;
                }
                if (c.IsConsumer && !c.Powered)
                    alertUnpowered++;
                if (c.Unreachable)
                    alertUnreachable++;
            }

            // beavers: cached wellbeing + critical needs
            foreach (var c in _beavers.Read)
            {
                totalWellbeing += c.Wellbeing;
                beaverCount++;
                if (c.Wellbeing < 4) miserable++;
                if (c.AnyCritical) critical++;
            }
            // count adults only (children can't work, shouldn't count as idle haulers)
            int totalAdults = 0;
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var pop = dc.GetComponent<DistrictPopulation>();
                if (pop != null) totalAdults += pop.NumberOfAdults;
            }
            int homeless = System.Math.Max(0, beaverCount - occupiedBeds);
            int unemployed = System.Math.Max(0, totalAdults - assignedWorkers);
            float avgWellbeing = beaverCount > 0 ? totalWellbeing / beaverCount : 0;

            if (format == "json")
            {
                return new
                {
                    time = CollectTime(),
                    weather = CollectWeather(),
                    districts = CollectDistricts("json"),
                    trees = new { markedGrown, markedSeedling, unmarkedGrown },
                    housing = new { occupiedBeds, totalBeds, homeless },
                    employment = new { assigned = assignedWorkers, vacancies = totalVacancies, unemployed },
                    wellbeing = new { average = System.Math.Round(avgWellbeing, 1), miserable, critical },
                    science = _scienceService.SciencePoints,
                    alerts = new { unstaffed = alertUnstaffed, unpowered = alertUnpowered, unreachable = alertUnreachable }
                };
            }

            // build flat summary matching TOON output format
            var flat = new Dictionary<string, object>();

            // time
            flat["day"] = _dayNightCycle.DayNumber;
            flat["dayProgress"] = System.Math.Round(_dayNightCycle.DayProgress, 2);

            // weather
            flat["cycle"] = _gameCycleService.Cycle;
            flat["cycleDay"] = _gameCycleService.CycleDay;
            flat["isHazardous"] = _weatherService.IsHazardousWeather;
            flat["tempDays"] = _weatherService.TemperateWeatherDuration;
            flat["hazardDays"] = _weatherService.HazardousWeatherDuration;

            // trees
            flat["markedGrown"] = markedGrown;
            flat["markedSeedling"] = markedSeedling;
            flat["unmarkedGrown"] = unmarkedGrown;

            // population + resources (first district)
            var goods = _goodService.Goods;
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var pop = dc.DistrictPopulation;
                flat["adults"] = pop.NumberOfAdults;
                flat["children"] = pop.NumberOfChildren;
                flat["bots"] = pop.NumberOfBots;
                var counter = dc.GetComponent<DistrictResourceCounter>();
                if (counter != null)
                {
                    foreach (var goodId in goods)
                    {
                        var rc = counter.GetResourceCount(goodId);
                        if (rc.AllStock > 0)
                            flat[goodId] = rc.AvailableStock;
                    }
                }
            }

            // resource projection
            int totalPop = beaverCount;
            if (totalPop > 0)
            {
                int totalFood = 0;
                int totalWater = 0;
                foreach (var kv in flat)
                {
                    if (kv.Value is int stock && stock > 0)
                    {
                        var g = kv.Key;
                        if (g == "Water") totalWater += stock;
                        else if (g == "Berries" || g == "Kohlrabi" || g == "Carrot" || g == "Potato"
                              || g == "Wheat" || g == "Bread" || g == "Cassava" || g == "Corn"
                              || g == "Eggplant" || g == "Soybean" || g == "MapleSyrup")
                            totalFood += stock;
                    }
                }
                flat["foodDays"] = System.Math.Round((double)totalFood / totalPop, 1);
                flat["waterDays"] = System.Math.Round((double)totalWater / (totalPop * 2.0), 1);

                // material projection -- stock / pop, same rough estimate as food/water
                int logs = flat.ContainsKey("Log") && flat["Log"] is int ls ? ls : 0;
                int planks = flat.ContainsKey("Plank") && flat["Plank"] is int ps ? ps : 0;
                int gears = flat.ContainsKey("Gear") && flat["Gear"] is int gs ? gs : 0;
                flat["logDays"] = System.Math.Round((double)logs / totalPop, 1);
                flat["plankDays"] = System.Math.Round((double)planks / totalPop, 1);
                flat["gearDays"] = System.Math.Round((double)gears / totalPop, 1);
            }

            // housing
            flat["beds"] = $"{occupiedBeds}/{totalBeds}";
            flat["homeless"] = homeless;

            // employment
            flat["workers"] = $"{assignedWorkers}/{totalVacancies}";
            flat["unemployed"] = unemployed;

            // wellbeing
            flat["wellbeing"] = System.Math.Round(avgWellbeing, 1);
            flat["miserable"] = miserable;
            flat["critical"] = critical;

            // science
            flat["science"] = _scienceService.SciencePoints;

            // alerts
            var alertParts = new List<string>();
            if (alertUnstaffed > 0) alertParts.Add($"{alertUnstaffed} unstaffed");
            if (alertUnpowered > 0) alertParts.Add($"{alertUnpowered} unpowered");
            if (alertUnreachable > 0) alertParts.Add($"{alertUnreachable} unreachable");
            flat["alerts"] = alertParts.Count > 0 ? string.Join(", ", alertParts) : "none";

            return flat;
        }

        // PERF: iterates _buildings.Read instead of all entities.
        public object CollectAlerts()
        {
            var alerts = new List<object>();
            foreach (var c in _buildings.Read)
            {
                if (c.Workplace != null && c.DesiredWorkers > 0 && c.AssignedWorkers < c.DesiredWorkers)
                    alerts.Add(new { type = "unstaffed", id = c.Id, name = c.Name, workers = $"{c.AssignedWorkers}/{c.DesiredWorkers}" });

                if (c.IsConsumer && !c.Powered)
                    alerts.Add(new { type = "unpowered", id = c.Id, name = c.Name });

                if (c.Unreachable)
                    alerts.Add(new { type = "unreachable", id = c.Id, name = c.Name });
            }
            return alerts;
        }

        // PERF: O(n) entity scan + grid bucketing. Called occasionally for tree management.
        public object CollectTreeClusters(int cellSize = 10, int top = 5)
        {
            var cells = new Dictionary<long, int[]>(); // key -> [grown, total, centerX, centerY, z]
            foreach (var nr in _naturalResources.Read)
            {
                if (nr.Cuttable == null) continue;
                if (nr.Living == null || nr.Living.IsDead) continue;
                if (nr.BlockObject == null) continue;

                var c = nr.BlockObject.Coordinates;
                int cx = c.x / cellSize * cellSize + cellSize / 2;
                int cy = c.y / cellSize * cellSize + cellSize / 2;
                long key = (long)cx * 100000 + cy;

                if (!cells.ContainsKey(key))
                    cells[key] = new int[] { 0, 0, cx, cy, c.z };

                cells[key][1]++;
                if (nr.Growable != null && nr.Growable.IsGrown)
                    cells[key][0]++;
            }

            var sorted = new List<int[]>(cells.Values);
            sorted.Sort((a, b) => b[0].CompareTo(a[0]));
            var results = new List<object>();
            for (int i = 0; i < System.Math.Min(top, sorted.Count); i++)
            {
                var s = sorted[i];
                results.Add(new { x = s[2], y = s[3], z = s[4], grown = s[0], total = s[1] });
            }
            return results;
        }

        // PERF: O(n) entity scan filtered by radius. Rarely called.
        public object CollectScan(int cx, int cy, int radius)
        {
            int x1 = cx - radius, y1 = cy - radius, x2 = cx + radius, y2 = cy + radius;
            var occupied = new List<object>();
            var water = new List<object>();

            // build occupancy from cached indexes -- zero GetComponent, fully thread-safe
            var occupants = new Dictionary<long, string>();
            var entrances = new HashSet<long>();
            var seedlings = new HashSet<long>();
            var deadTiles = new HashSet<long>();

            // buildings (multi-tile footprints cached at add-time)
            var buildings = _buildings.Read;
            for (int bi = 0; bi < buildings.Count; bi++)
            {
                var b = buildings[bi];
                if (b.OccupiedTiles == null) continue;
                if (b.Name.Contains("RecoveredGoodStack") || b.Name.Contains("GoodStack")) continue;
                foreach (var tile in b.OccupiedTiles)
                {
                    if (tile.x >= x1 && tile.x <= x2 && tile.y >= y1 && tile.y <= y2)
                        occupants[(long)tile.x * 100000 + tile.y] = b.Name;
                }
                if (b.HasEntrance)
                    entrances.Add((long)b.EntranceX * 100000 + b.EntranceY);
            }

            // natural resources (1x1, all data cached)
            var resources = _naturalResources.Read;
            for (int ri = 0; ri < resources.Count; ri++)
            {
                var r = resources[ri];
                if (r.X >= x1 && r.X <= x2 && r.Y >= y1 && r.Y <= y2)
                {
                    long key = (long)r.X * 100000 + r.Y;
                    occupants[key] = r.Name;
                    if (!r.Grown) seedlings.Add(key);
                    if (!r.Alive) deadTiles.Add(key);
                }
            }

            for (int x = x1; x <= x2; x++)
            {
                for (int y = y1; y <= y2; y++)
                {
                    long key = (long)x * 100000 + y;
                    occupants.TryGetValue(key, out var occ);
                    bool isEntrance = entrances.Contains(key);
                    bool isSeedling = seedlings.Contains(key);
                    bool isDead = deadTiles.Contains(key);

                    int terrainHeight = GetTerrainHeight(x, y);
                    float waterHeight = 0f;
                    float waterContamination = 0f;
                    try { waterHeight = _waterMap.CeiledWaterHeight(new Vector3Int(x, y, terrainHeight)); } catch { }
                    try
                    {
                        int wIdx2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
                        int wColCount = _waterMap.ColumnCount(wIdx2D);
                        for (int ci = wColCount - 1; ci >= 0; ci--)
                        {
                            int wIdx3D = ci * _mapIndexService.VerticalStride + wIdx2D;
                            var col = _waterMap.WaterColumns[wIdx3D];
                            if (col.WaterDepth > 0 && col.Contamination > 0) { waterContamination = col.Contamination; break; }
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(occ))
                    {
                        string suffix = isDead ? ".dead" : isSeedling ? ".seedling" : "";
                        if (isEntrance) suffix += ".entrance";
                        occupied.Add(new { x, y, what = occ + suffix });
                    }
                    else if (isEntrance)
                    {
                        occupied.Add(new { x, y, what = "entrance" });
                    }

                    if (waterHeight > 0 && string.IsNullOrEmpty(occ))
                    {
                        if (waterContamination > 0)
                            water.Add(new { x, y, badwater = System.Math.Round(waterContamination, 2) });
                        else
                            water.Add(new { x, y });
                    }
                }
            }

            return new { center = $"{cx},{cy}", radius, @default = "ground", occupied, water };
        }

        public object CollectTime()
        {
            return new
            {
                dayNumber = _dayNightCycle.DayNumber,
                dayProgress = _dayNightCycle.DayProgress,
                partialDayNumber = _dayNightCycle.PartialDayNumber
            };
        }

        public object CollectWeather()
        {
            return new
            {
                cycle = _gameCycleService.Cycle,
                cycleDay = _gameCycleService.CycleDay,
                isHazardous = _weatherService.IsHazardousWeather,
                temperateWeatherDuration = _weatherService.TemperateWeatherDuration,
                hazardousWeatherDuration = _weatherService.HazardousWeatherDuration,
                cycleLengthInDays = _weatherService.CycleLengthInDays
            };
        }

        public object CollectDistricts(string format = "toon")
        {
            var goods = _goodService.Goods;
            var results = new List<object>();

            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var counter = dc.GetComponent<DistrictResourceCounter>();
                var pop = dc.DistrictPopulation;

                if (format == "toon")
                {
                    var row = new Dictionary<string, object>
                    {
                        ["name"] = dc.DistrictName,
                        ["adults"] = pop != null ? pop.NumberOfAdults : 0,
                        ["children"] = pop != null ? pop.NumberOfChildren : 0,
                        ["bots"] = pop != null ? pop.NumberOfBots : 0
                    };
                    if (counter != null)
                    {
                        foreach (var goodId in goods)
                        {
                            var rc = counter.GetResourceCount(goodId);
                            if (rc.AllStock > 0)
                                row[goodId] = rc.AvailableStock;
                        }
                    }
                    results.Add(row);
                }
                else
                {
                    var resources = new Dictionary<string, object>();
                    if (counter != null)
                    {
                        foreach (var goodId in goods)
                        {
                            var rc = counter.GetResourceCount(goodId);
                            if (rc.AllStock > 0)
                                resources[goodId] = new { available = rc.AvailableStock, all = rc.AllStock };
                        }
                    }
                    results.Add(new
                    {
                        name = dc.DistrictName,
                        population = new
                        {
                            adults = pop != null ? pop.NumberOfAdults : 0,
                            children = pop != null ? pop.NumberOfChildren : 0,
                            bots = pop != null ? pop.NumberOfBots : 0
                        },
                        resources
                    });
                }
            }

            return results;
        }

        public object CollectResources(string format = "toon")
        {
            var goods = _goodService.Goods;

            if (format == "toon")
            {
                var flat = new List<object>();
                foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    var counter = dc.GetComponent<DistrictResourceCounter>();
                    if (counter == null) continue;
                    foreach (var goodId in goods)
                    {
                        var rc = counter.GetResourceCount(goodId);
                        if (rc.AllStock > 0)
                            flat.Add(new { district = dc.DistrictName, good = goodId, available = rc.AvailableStock, all = rc.AllStock });
                    }
                }
                return flat;
            }

            var results = new Dictionary<string, object>();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var counter = dc.GetComponent<DistrictResourceCounter>();
                if (counter == null) continue;
                var distResources = new Dictionary<string, object>();
                foreach (var goodId in goods)
                {
                    var rc = counter.GetResourceCount(goodId);
                    if (rc.AllStock > 0)
                        distResources[goodId] = new { available = rc.AvailableStock, all = rc.AllStock };
                }
                results[dc.DistrictName] = distResources;
            }
            return results;
        }

        public object CollectPopulation()
        {
            var results = new List<object>();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var pop = dc.DistrictPopulation;
                results.Add(new
                {
                    district = dc.DistrictName,
                    adults = pop != null ? pop.NumberOfAdults : 0,
                    children = pop != null ? pop.NumberOfChildren : 0,
                    bots = pop != null ? pop.NumberOfBots : 0
                });
            }
            return results;
        }

        // PERF: StringBuilder serialization for buildings. Zero Dictionary alloc.
        public object CollectBuildings(string format = "toon", string detail = "basic")
        {
            int? singleId = null;
            if (detail != null && detail.StartsWith("id:"))
            {
                if (int.TryParse(detail.Substring(3), out int parsed))
                    singleId = parsed;
            }
            bool fullDetail = detail == "full" || singleId.HasValue;

            var sb = _sbBuildings;
            sb.Clear();
            Jw.OpenArr(sb);
            bool first = true;
            foreach (var c in _buildings.Read)
            {
                if (singleId.HasValue && c.Id != singleId.Value)
                    continue;
                if (!first) Jw.Sep(sb);
                first = false;

                Jw.Open(sb);
                Jw.KeyFirst(sb, "id"); Jw.Int(sb, c.Id);
                Jw.Key(sb, "name"); Jw.Str(sb, c.Name);
                Jw.Key(sb, "x"); Jw.Int(sb, c.X);
                Jw.Key(sb, "y"); Jw.Int(sb, c.Y);
                Jw.Key(sb, "z"); Jw.Int(sb, c.Z);
                Jw.Key(sb, "orientation"); Jw.Str(sb, c.Orientation ?? "");
                Jw.Key(sb, "finished"); Jw.Bool(sb, c.Finished);
                Jw.Key(sb, "paused"); Jw.Bool(sb, c.Paused);

                if (!fullDetail)
                {
                    Jw.Key(sb, "priority"); Jw.Str(sb, c.ConstructionPriority ?? "");
                    Jw.Key(sb, "workers"); Jw.Str(sb, c.Workplace != null ? $"{c.AssignedWorkers}/{c.DesiredWorkers}" : "");
                    Jw.Close(sb);
                    continue;
                }

                // full detail
                if (c.Pausable != null) { Jw.Key(sb, "pausable"); Jw.Bool(sb, true); }
                if (c.HasFloodgate) { Jw.Key(sb, "floodgate"); Jw.Bool(sb, true); Jw.Key(sb, "height"); Jw.Float(sb, c.FloodgateHeight, "F1"); Jw.Key(sb, "maxHeight"); Jw.Float(sb, c.FloodgateMaxHeight, "F1"); }
                if (c.ConstructionPriority != null) { Jw.Key(sb, "constructionPriority"); Jw.Str(sb, c.ConstructionPriority); }
                if (c.WorkplacePriorityStr != null) { Jw.Key(sb, "workplacePriority"); Jw.Str(sb, c.WorkplacePriorityStr); }
                if (c.Workplace != null) { Jw.Key(sb, "maxWorkers"); Jw.Int(sb, c.MaxWorkers); Jw.Key(sb, "desiredWorkers"); Jw.Int(sb, c.DesiredWorkers); Jw.Key(sb, "assignedWorkers"); Jw.Int(sb, c.AssignedWorkers); }
                if (c.Reachability != null) { Jw.Key(sb, "reachable"); Jw.Bool(sb, !c.Unreachable); }
                if (c.Mechanical != null) { Jw.Key(sb, "powered"); Jw.Bool(sb, c.Powered); }
                if (c.PowerNode != null)
                {
                    Jw.Key(sb, "isGenerator"); Jw.Bool(sb, c.IsGenerator);
                    Jw.Key(sb, "isConsumer"); Jw.Bool(sb, c.IsConsumer);
                    Jw.Key(sb, "nominalPowerInput"); Jw.Int(sb, c.NominalPowerInput);
                    Jw.Key(sb, "nominalPowerOutput"); Jw.Int(sb, c.NominalPowerOutput);
                    if (c.PowerDemand > 0 || c.PowerSupply > 0) { Jw.Key(sb, "powerDemand"); Jw.Int(sb, c.PowerDemand); Jw.Key(sb, "powerSupply"); Jw.Int(sb, c.PowerSupply); }
                }
                if (c.Site != null) { Jw.Key(sb, "buildProgress"); Jw.Float(sb, c.BuildProgress); Jw.Key(sb, "materialProgress"); Jw.Float(sb, c.MaterialProgress); Jw.Key(sb, "hasMaterials"); Jw.Bool(sb, c.HasMaterials); }
                if (c.Capacity > 0)
                {
                    Jw.Key(sb, "stock"); Jw.Int(sb, c.Stock);
                    Jw.Key(sb, "capacity"); Jw.Int(sb, c.Capacity);
                    if (c.Inventory != null && c.Inventory.Count > 0)
                    {
                        Jw.Key(sb, "inventory"); Jw.Open(sb);
                        bool ifirst = true;
                        foreach (var kvp in c.Inventory)
                        {
                            if (!ifirst) Jw.Sep(sb);
                            else { Jw.KeyFirst(sb, kvp.Key); ifirst = false; Jw.Int(sb, kvp.Value); continue; }
                            Jw.KeyFirst(sb, kvp.Key); Jw.Int(sb, kvp.Value);
                        }
                        Jw.Close(sb);
                    }
                }
                if (c.HasWonder) { Jw.Key(sb, "isWonder"); Jw.Bool(sb, true); Jw.Key(sb, "wonderActive"); Jw.Bool(sb, c.WonderActive); }
                if (c.Dwelling != null) { Jw.Key(sb, "dwellers"); Jw.Int(sb, c.Dwellers); Jw.Key(sb, "maxDwellers"); Jw.Int(sb, c.MaxDwellers); }
                if (c.HasClutch) { Jw.Key(sb, "isClutch"); Jw.Bool(sb, true); Jw.Key(sb, "clutchEngaged"); Jw.Bool(sb, c.ClutchEngaged); }
                if (c.Manufactory != null)
                {
                    if (c.Recipes != null && c.Recipes.Count > 0)
                    {
                        Jw.Key(sb, "recipes"); Jw.OpenArr(sb);
                        for (int ri = 0; ri < c.Recipes.Count; ri++)
                        {
                            if (ri > 0) Jw.Sep(sb);
                            Jw.Str(sb, c.Recipes[ri]);
                        }
                        Jw.CloseArr(sb);
                    }
                    Jw.Key(sb, "currentRecipe"); Jw.Str(sb, c.CurrentRecipe ?? "");
                    Jw.Key(sb, "productionProgress"); Jw.Float(sb, c.ProductionProgress);
                    Jw.Key(sb, "readyToProduce"); Jw.Bool(sb, c.ReadyToProduce);
                }
                if (c.BreedingPod != null)
                {
                    Jw.Key(sb, "needsNutrients"); Jw.Bool(sb, c.NeedsNutrients);
                    if (c.NutrientStock != null && c.NutrientStock.Count > 0)
                    {
                        Jw.Key(sb, "nutrients"); Jw.Open(sb);
                        bool nfirst = true;
                        foreach (var kvp in c.NutrientStock)
                        {
                            if (!nfirst) Jw.Sep(sb);
                            else { Jw.KeyFirst(sb, kvp.Key); nfirst = false; Jw.Int(sb, kvp.Value); continue; }
                            Jw.KeyFirst(sb, kvp.Key); Jw.Int(sb, kvp.Value);
                        }
                        Jw.Close(sb);
                    }
                }
                if (c.EffectRadius > 0) { Jw.Key(sb, "effectRadius"); Jw.Int(sb, c.EffectRadius); }
                Jw.Close(sb);
            }
            Jw.CloseArr(sb);
            return sb.ToString();
        }

        // PERF: cached component refs -- zero GetComponent per item.
        // serial param: dict (default), anon, sb -- for A/B testing serialization methods
        // PERF: StringBuilder serialization -- 2ms for 3000 trees. No Dictionary, no Newtonsoft.
        public object CollectTrees()
        {
            var sb = _sbTrees;
            sb.Clear();
            Jw.OpenArr(sb);
            bool first = true;
            foreach (var c in _naturalResources.Read)
            {
                if (c.Cuttable == null) continue;
                if (!first) Jw.Sep(sb);
                first = false;
                Jw.Open(sb);
                Jw.KeyFirst(sb, "id"); Jw.Int(sb, c.Id);
                Jw.Key(sb, "name"); Jw.Str(sb, c.Name);
                Jw.Key(sb, "x"); Jw.Int(sb, c.X);
                Jw.Key(sb, "y"); Jw.Int(sb, c.Y);
                Jw.Key(sb, "z"); Jw.Int(sb, c.Z);
                Jw.Key(sb, "marked"); Jw.Bool(sb, c.Marked);
                Jw.Key(sb, "alive"); Jw.Bool(sb, c.Alive);
                Jw.Key(sb, "grown"); Jw.Bool(sb, c.Grown);
                Jw.Key(sb, "growth"); Jw.Float(sb, c.Growth);
                Jw.Close(sb);
            }
            Jw.CloseArr(sb);
            return sb.ToString();
        }

        public object CollectGatherables()
        {
            var results = new List<object>();
            foreach (var c in _naturalResources.Read)
            {
                if (c.Gatherable == null) continue;
                results.Add(new Dictionary<string, object>
                {
                    ["id"] = c.Id, ["name"] = c.Name,
                    ["x"] = c.X, ["y"] = c.Y, ["z"] = c.Z,
                    ["alive"] = c.Alive
                });
            }
            return results;
        }

        // PERF: reads cached beaver data only. Zero GetComponent from background thread.
        private readonly System.Text.StringBuilder _sbBeavers = new System.Text.StringBuilder(50000);

        public object CollectBeavers(string format = "toon", string detail = "basic")
        {
            int? singleId = null;
            if (detail != null && detail.StartsWith("id:"))
            {
                if (int.TryParse(detail.Substring(3), out int parsed))
                    singleId = parsed;
            }
            bool fullDetail = detail == "full" || singleId.HasValue;

            var sb = _sbBeavers;
            sb.Clear();
            Jw.OpenArr(sb);
            bool first = true;
            foreach (var c in _beavers.Read)
            {
                if (singleId.HasValue && c.Id != singleId.Value)
                    continue;
                if (!first) Jw.Sep(sb);
                first = false;

                Jw.Open(sb);
                Jw.KeyFirst(sb, "id"); Jw.Int(sb, c.Id);
                Jw.Key(sb, "name"); Jw.Str(sb, c.Name);
                Jw.Key(sb, "x"); Jw.Int(sb, c.X);
                Jw.Key(sb, "y"); Jw.Int(sb, c.Y);
                Jw.Key(sb, "z"); Jw.Int(sb, c.Z);
                Jw.Key(sb, "wellbeing"); Jw.Float(sb, c.Wellbeing, "F1");
                Jw.Key(sb, "isBot"); Jw.Bool(sb, c.IsBot);

                if (!fullDetail)
                {
                    float wb = c.Wellbeing;
                    string tier = wb >= 16 ? "ecstatic" : wb >= 12 ? "happy" : wb >= 8 ? "okay" : wb >= 4 ? "unhappy" : "miserable";
                    Jw.Key(sb, "tier"); Jw.Str(sb, tier);
                    Jw.Key(sb, "workplace"); Jw.Str(sb, c.Workplace ?? "");

                    // critical + unmet need summaries
                    sb.Append(",\"critical\":\"");
                    bool cfirst = true;
                    if (c.Needs != null)
                        foreach (var n in c.Needs)
                            if (n.Critical) { if (!cfirst) sb.Append('+'); cfirst = false; sb.Append(n.Id); }
                    sb.Append("\",\"unmet\":\"");
                    bool ufirst = true;
                    if (c.Needs != null)
                        foreach (var n in c.Needs)
                            if (!n.Favorable && !n.Critical && n.Active) { if (!ufirst) sb.Append('+'); ufirst = false; sb.Append(n.Id); }
                    sb.Append("\"}");
                    continue;
                }

                // full detail
                Jw.Key(sb, "anyCritical"); Jw.Bool(sb, c.AnyCritical);
                if (c.Workplace != null) { Jw.Key(sb, "workplace"); Jw.Str(sb, c.Workplace); }
                if (c.District != null) { Jw.Key(sb, "district"); Jw.Str(sb, c.District); }
                Jw.Key(sb, "hasHome"); Jw.Bool(sb, c.HasHome);
                Jw.Key(sb, "contaminated"); Jw.Bool(sb, c.Contaminated);
                if (c.Life != null) { Jw.Key(sb, "lifeProgress"); Jw.Float(sb, c.LifeProgress); }
                if (c.Deteriorable != null) { Jw.Key(sb, "deterioration"); Jw.Float(sb, c.DeteriorationProgress, "F3"); }
                if (c.Carrier != null) { Jw.Key(sb, "liftingCapacity"); Jw.Int(sb, c.LiftingCapacity); if (c.Overburdened) { Jw.Key(sb, "overburdened"); Jw.Bool(sb, true); } }
                if (c.IsCarrying) { Jw.Key(sb, "carrying"); Jw.Str(sb, c.CarryingGood); Jw.Key(sb, "carryAmount"); Jw.Int(sb, c.CarryAmount); }

                // needs array
                Jw.Key(sb, "needs"); Jw.OpenArr(sb);
                if (c.Needs != null)
                {
                    bool nfirst = true;
                    foreach (var n in c.Needs)
                    {
                        if (!fullDetail && !c.IsBot && !n.Active) continue;
                        if (!nfirst) Jw.Sep(sb);
                        nfirst = false;
                        Jw.Open(sb);
                        Jw.KeyFirst(sb, "id"); Jw.Str(sb, n.Id);
                        Jw.Key(sb, "points"); Jw.Float(sb, n.Points);
                        Jw.Key(sb, "wellbeing"); Jw.Int(sb, n.Wellbeing);
                        Jw.Key(sb, "favorable"); Jw.Bool(sb, n.Favorable);
                        Jw.Key(sb, "critical"); Jw.Bool(sb, n.Critical);
                        Jw.Key(sb, "group"); Jw.Str(sb, n.Group);
                        Jw.Close(sb);
                    }
                }
                Jw.CloseArr(sb);
                Jw.Close(sb);
            }
            Jw.CloseArr(sb);
            return sb.ToString();
        }

        public object CollectPowerNetworks()
        {
            // group buildings by power network using cached PowerNetworkId
            var networks = new Dictionary<int, Dictionary<string, object>>();
            var buildings = _buildings.Read;
            for (int i = 0; i < buildings.Count; i++)
            {
                var c = buildings[i];
                if (c.PowerNode == null || c.PowerNetworkId == 0) continue;
                int netId = c.PowerNetworkId;
                if (!networks.ContainsKey(netId))
                {
                    networks[netId] = new Dictionary<string, object>
                    {
                        ["id"] = netId,
                        ["supply"] = c.PowerSupply,
                        ["demand"] = c.PowerDemand,
                        ["buildings"] = new List<object>()
                    };
                }
                var list = (List<object>)networks[netId]["buildings"];
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = c.Name,
                    ["id"] = c.Id,
                    ["isGenerator"] = c.IsGenerator,
                    ["nominalOutput"] = c.NominalPowerOutput,
                    ["nominalInput"] = c.NominalPowerInput
                });
            }
            return networks.Values.ToList();
        }

        public object CollectSpeed()
        {
            var raw = _speedManager.CurrentSpeed;
            int level = System.Array.IndexOf(SpeedScale, raw);
            if (level < 0) level = 0;
            return new { speed = level };
        }

        public object CollectWorkHours()
        {
            return new
            {
                endHours = _workingHoursManager.EndHours,
                areWorkingHours = _workingHoursManager.AreWorkingHours
            };
        }

        // set when beavers stop working (1-24 hours)
        public object SetWorkHours(int endHours)
        {
            if (endHours < 1 || endHours > 24)
                return new { error = "endHours must be 1-24" };
            _workingHoursManager.EndHours = endHours;
            return new { endHours = _workingHoursManager.EndHours };
        }

        // move adult beavers between districts
        public object MigratePopulation(string fromDistrict, string toDistrict, int count)
        {
            Timberborn.GameDistricts.DistrictCenter fromDc = null, toDc = null;
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                if (dc.DistrictName == fromDistrict) fromDc = dc;
                if (dc.DistrictName == toDistrict) toDc = dc;
            }
            if (fromDc == null) return new { error = "from district not found", from = fromDistrict };
            if (toDc == null) return new { error = "to district not found", to = toDistrict };

            try
            {
                var distributor = _populationDistributorRetriever.GetPopulationDistributor<AdultsDistributorTemplate>(fromDc);
                if (distributor == null)
                    return new { error = "no population distributor", from = fromDistrict };

                var available = distributor.Current;
                var toMove = System.Math.Min(count, available);
                if (toMove <= 0)
                    return new { error = "no population to migrate", from = fromDistrict, available };

                distributor.MigrateTo(toDc, toMove);
                return new { from = fromDistrict, to = toDistrict, migrated = toMove };
            }
            catch (System.Exception ex)
            {
                return new { error = ex.Message, from = fromDistrict, to = toDistrict };
            }
        }

        // PERF: O(n) entity scan to build occupant lookup, then O(region) tile iteration.
        // Region-bounded so cost depends on area size, not map size. Called occasionally.
        public object CollectMap(int x1, int y1, int x2, int y2)
        {
            var size = _terrainService.Size;
            var stride = _mapIndexService.VerticalStride;

            // default to full map if no region specified
            if (x1 == 0 && y1 == 0 && x2 == 0 && y2 == 0)
            {
                return new
                {
                    mapSize = new { x = size.x, y = size.y, z = size.z }
                };
            }

            x1 = Mathf.Clamp(x1, 0, size.x - 1);
            y1 = Mathf.Clamp(y1, 0, size.y - 1);
            x2 = Mathf.Clamp(x2, 0, size.x - 1);
            y2 = Mathf.Clamp(y2, 0, size.y - 1);

            // build occupancy map from cached indexes -- zero GetComponent, fully thread-safe
            // key = x*100000+y, value = list of (name, z) for vertical stacking
            var occupants = new Dictionary<long, List<(string name, int z)>>();
            var entrances = new HashSet<long>();
            var seedlings = new HashSet<long>();
            var deadTiles = new HashSet<long>();

            // buildings (multi-tile footprints cached at add-time)
            var buildings = _buildings.Read;
            for (int i = 0; i < buildings.Count; i++)
            {
                var c = buildings[i];
                if (c.OccupiedTiles == null) continue;
                foreach (var tile in c.OccupiedTiles)
                {
                    if (tile.x >= x1 && tile.x <= x2 && tile.y >= y1 && tile.y <= y2)
                    {
                        long key = (long)tile.x * 100000 + tile.y;
                        if (!occupants.ContainsKey(key))
                            occupants[key] = new List<(string, int)>();
                        occupants[key].Add((c.Name, tile.z));
                    }
                }
                if (c.HasEntrance)
                    entrances.Add((long)c.EntranceX * 100000 + c.EntranceY);
            }

            // natural resources (1x1, all data cached)
            var resources = _naturalResources.Read;
            for (int i = 0; i < resources.Count; i++)
            {
                var r = resources[i];
                if (r.X >= x1 && r.X <= x2 && r.Y >= y1 && r.Y <= y2)
                {
                    long key = (long)r.X * 100000 + r.Y;
                    if (!occupants.ContainsKey(key))
                        occupants[key] = new List<(string, int)>();
                    occupants[key].Add((r.Name, r.Z));
                    if (!r.Grown) seedlings.Add(key);
                    if (!r.Alive) deadTiles.Add(key);
                }
            }

            var tiles = new List<object>();
            for (int x = x1; x <= x2; x++)
            {
                for (int y = y1; y <= y2; y++)
                {
                    var index2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
                    var columnCount = _terrainMap.ColumnCounts[index2D];

                    int terrainHeight = 0;
                    if (columnCount > 0)
                    {
                        var topIndex = index2D + (columnCount - 1) * stride;
                        terrainHeight = _terrainMap.GetColumnCeiling(topIndex);
                    }

                    float waterHeight = 0f;
                    float waterContamination = 0f;
                    var waterCoord = new Vector3Int(x, y, terrainHeight);
                    try { waterHeight = _waterMap.CeiledWaterHeight(waterCoord); } catch { }
                    try
                    {
                        int wIdx2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
                        int wColCount = _waterMap.ColumnCount(wIdx2D);
                        for (int ci = wColCount - 1; ci >= 0; ci--)
                        {
                            int wIdx3D = ci * _mapIndexService.VerticalStride + wIdx2D;
                            var col = _waterMap.WaterColumns[wIdx3D];
                            if (col.WaterDepth > 0 && col.Contamination > 0)
                            {
                                waterContamination = col.Contamination;
                                break;
                            }
                        }
                    }
                    catch { }

                    long key = (long)x * 100000 + y;
                    occupants.TryGetValue(key, out var occList);
                    bool isEntrance = entrances.Contains(key);
                    bool isSeedling = seedlings.Contains(key);

                    var tile = new Dictionary<string, object>
                    {
                        ["x"] = x, ["y"] = y,
                        ["terrain"] = terrainHeight,
                        ["water"] = waterHeight
                    };
                    if (waterContamination > 0)
                        tile["badwater"] = Math.Round(waterContamination, 2);
                    if (occList != null)
                    {
                        if (occList.Count == 1)
                            tile["occupant"] = occList[0].name;
                        else
                        {
                            var stacked = new List<object>(occList.Count);
                            foreach (var o in occList)
                                stacked.Add(new Dictionary<string, object> { ["name"] = o.name, ["z"] = o.z });
                            tile["occupants"] = stacked;
                        }
                    }
                    if (isEntrance) tile["entrance"] = true;
                    if (isSeedling) tile["seedling"] = true;
                    if (deadTiles.Contains(key)) tile["dead"] = true;
                    try
                    {
                        if (_soilContaminationService.SoilIsContaminated(new Vector3Int(x, y, terrainHeight)))
                            tile["contaminated"] = true;
                    }
                    catch { }
                    try
                    {
                        if (_soilMoistureService.SoilIsMoist(new Vector3Int(x, y, terrainHeight)))
                            tile["moist"] = true;
                    }
                    catch { }
                    tiles.Add(tile);
                }
            }

            return new
            {
                mapSize = new { x = size.x, y = size.y, z = size.z },
                region = new { x1, y1, x2, y2 },
                tiles
            };
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 1
        // ================================================================

        private static readonly int[] SpeedScale = { 0, 1, 3, 7 };

        // game speed 0-3, mapped to internal values 0,1,3,7
        public object SetSpeed(int speed)
        {
            if (speed < 0 || speed > 3)
                return new { error = "speed must be 0-3 (0=pause, 1=normal, 2=fast, 3=fastest)" };

            _speedManager.ChangeSpeed(SpeedScale[speed]);
            return new { speed };
        }

        // pause/unpause a building
        public object PauseBuilding(int buildingId, bool paused)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var pausable = ec.GetComponent<PausableBuilding>();
            if (pausable == null)
                return new { error = "building is not pausable", id = buildingId };

            if (paused)
                pausable.Pause();
            else
                pausable.Resume();
            return new { id = buildingId, name = CleanName(ec.GameObject.name), paused = pausable.Paused };
        }

        // engage/disengage clutch on a building
        public object SetClutch(int buildingId, bool engaged)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var clutch = ec.GetComponent<Clutch>();
            if (clutch == null)
                return new { error = "building has no clutch", id = buildingId };

            clutch.SetMode(engaged ? ClutchMode.Engaged : ClutchMode.Disengaged);
            return new { id = buildingId, name = CleanName(ec.GameObject.name), engaged = clutch.IsEngaged };
        }

        // adjust floodgate water gate height (clamped to max)
        public object SetFloodgateHeight(int buildingId, float height)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var floodgate = ec.GetComponent<Floodgate>();
            if (floodgate == null)
                return new { error = "not a floodgate", id = buildingId };

            var clamped = Mathf.Clamp(height, 0f, floodgate.MaxHeight);
            floodgate.SetHeightAndSynchronize(clamped);
            return new
            {
                id = buildingId,
                name = CleanName(ec.GameObject.name),
                height = floodgate.Height,
                maxHeight = floodgate.MaxHeight
            };
        }

        // set construction or workplace priority (VeryLow/Normal/VeryHigh)
        public object SetBuildingPriority(int buildingId, string priorityStr, string type)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            if (!Enum.TryParse<Priority>(priorityStr, true, out var parsed))
                return new { error = "invalid priority, use: VeryLow, Normal, VeryHigh", value = priorityStr };

            if (type == "construction" || string.IsNullOrEmpty(type))
            {
                var prio = ec.GetComponent<BuilderPrioritizable>();
                if (prio != null)
                {
                    prio.SetPriority(parsed);
                    return new { id = buildingId, name = CleanName(ec.GameObject.name), constructionPriority = prio.Priority.ToString() };
                }
            }

            if (type == "workplace" || string.IsNullOrEmpty(type))
            {
                var wpPrio = ec.GetComponent<WorkplacePriority>();
                if (wpPrio != null)
                {
                    wpPrio.SetPriority(parsed);
                    return new { id = buildingId, name = CleanName(ec.GameObject.name), workplacePriority = wpPrio.Priority.ToString() };
                }
            }

            return new { error = "building has no priority of that type", id = buildingId, type };
        }

        // haulers deliver goods to this building first
        public object SetHaulPriority(int buildingId, bool prioritized)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var hp = ec.GetComponent<HaulPrioritizable>();
            if (hp == null)
                return new { error = "building has no haul priority", id = buildingId };

            hp.Prioritized = prioritized;
            return new { id = buildingId, name = CleanName(ec.GameObject.name), haulPrioritized = hp.Prioritized };
        }

        // set which recipe a manufactory produces
        public object SetRecipe(int buildingId, string recipeId)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var manufactory = ec.GetComponent<Manufactory>();
            if (manufactory == null)
                return new { error = "building has no manufactory", id = buildingId };

            if (string.IsNullOrEmpty(recipeId) || recipeId == "none")
            {
                manufactory.SetRecipe(null);
                return new { id = buildingId, name = CleanName(ec.GameObject.name), recipe = "none" };
            }

            RecipeSpec recipe = null;
            try { recipe = _recipeSpecService.GetRecipe(recipeId); } catch { }
            if (recipe == null)
            {
                var available = new List<string>();
                foreach (var r in manufactory.ProductionRecipes)
                    available.Add(r.Id);
                return new { error = "recipe not found", recipeId, available };
            }

            manufactory.SetRecipe(recipe);
            return new { id = buildingId, name = CleanName(ec.GameObject.name), recipe = recipe.Id };
        }

        // prioritize planting vs default (harvest when ready)
        public object SetFarmhouseAction(int buildingId, string action)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var farmhouse = ec.GetComponent<FarmHouse>();
            if (farmhouse == null)
                return new { error = "building is not a farmhouse", id = buildingId };

            if (action == "planting")
            {
                farmhouse.PrioritizePlanting();
                return new { id = buildingId, name = CleanName(ec.GameObject.name), action = "planting" };
            }
            else if (action == "harvesting" || action == "none")
            {
                farmhouse.UnprioritizePlanting();
                return new { id = buildingId, name = CleanName(ec.GameObject.name), action = "default" };
            }

            return new { error = "invalid action, use: planting or harvesting", action };
        }

        // forester/gatherer prioritizes this resource type
        public object SetPlantablePriority(int buildingId, string plantableName)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var prioritizer = ec.GetComponent<PlantablePrioritizer>();
            if (prioritizer == null)
                return new { error = "building has no plantable prioritizer", id = buildingId };

            if (string.IsNullOrEmpty(plantableName) || plantableName == "none")
            {
                prioritizer.PrioritizePlantable(null);
                return new { id = buildingId, name = CleanName(ec.GameObject.name), prioritized = "none" };
            }

            var planterBuilding = ec.GetComponent<PlanterBuilding>();
            if (planterBuilding == null)
                return new { error = "building has no planter", id = buildingId };

            PlantableSpec match = null;
            var available = new List<string>();
            foreach (var p in planterBuilding.AllowedPlantables)
            {
                available.Add(p.TemplateName);
                if (p.TemplateName == plantableName)
                    match = p;
            }

            if (match == null)
                return new { error = "plantable not found", plantableName, available };

            prioritizer.PrioritizePlantable(match);
            return new { id = buildingId, name = CleanName(ec.GameObject.name), prioritized = match.TemplateName };
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 2
        // ================================================================

        // set desired worker count (0 to maxWorkers)
        public object SetWorkers(int buildingId, int count)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var workplace = ec.GetComponent<Workplace>();
            if (workplace == null)
                return new { error = "not a workplace", id = buildingId };

            var clamped = Mathf.Clamp(count, 0, workplace.MaxWorkers);
            workplace.DesiredWorkers = clamped;
            return new
            {
                id = buildingId,
                name = CleanName(ec.GameObject.name),
                desiredWorkers = workplace.DesiredWorkers,
                maxWorkers = workplace.MaxWorkers,
                assignedWorkers = workplace.NumberOfAssignedWorkers
            };
        }

        // mark/clear rectangular area for tree cutting
        public object MarkCuttingArea(int x1, int y1, int x2, int y2, int z, bool marked)
        {
            var minX = Mathf.Min(x1, x2);
            var maxX = Mathf.Max(x1, x2);
            var minY = Mathf.Min(y1, y2);
            var maxY = Mathf.Max(y1, y2);

            var coords = new List<Vector3Int>();
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    coords.Add(new Vector3Int(x, y, z));
                }
            }

            if (marked)
                _treeCuttingArea.AddCoordinates(coords);
            else
                _treeCuttingArea.RemoveCoordinates(coords);

            return new
            {
                x1 = minX, y1 = minY, x2 = maxX, y2 = maxY, z,
                marked,
                tiles = coords.Count
            };
        }

        // set max capacity on a stockpile building
        public object SetStockpileCapacity(int buildingId, int capacity)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var inventories = ec.GetComponent<Inventories>();
            if (inventories == null)
                return new { error = "no inventory", id = buildingId };

            // Set capacity on all inventories
            foreach (var inv in inventories.AllInventories)
            {
                inv.Capacity = capacity;
            }

            return new
            {
                id = buildingId,
                name = CleanName(ec.GameObject.name),
                capacity
            };
        }

        // set which good a single-good stockpile accepts
        public object SetStockpileGood(int buildingId, string goodId)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var sga = ec.GetComponent<SingleGoodAllower>();
            if (sga == null)
                return new { error = "not a single-good stockpile", id = buildingId };

            sga.AllowedGood = goodId;
            return new
            {
                id = buildingId,
                name = CleanName(ec.GameObject.name),
                good = sga.AllowedGood
            };
        }

        // mark area for crop planting (validates via PlantingAreaValidator.CanPlant)
        public object MarkPlanting(int x1, int y1, int x2, int y2, int z, string crop)
        {
            var minX = Mathf.Min(x1, x2);
            var maxX = Mathf.Max(x1, x2);
            var minY = Mathf.Min(y1, y2);
            var maxY = Mathf.Max(y1, y2);

            var coords = new List<Vector3Int>();
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    coords.Add(new Vector3Int(x, y, z));
                }
            }

            int planted = 0, skipped = 0;
            foreach (var c in coords)
            {
                // PlantingAreaValidator.CanPlant -- same check the player UI uses for green/red tiles
                if (!_plantingAreaValidator.CanPlant(c, crop))
                {
                    skipped++;
                    continue;
                }
                _plantingService.SetPlantingCoordinates(c, crop);
                planted++;
            }

            return new
            {
                x1 = minX, y1 = minY, x2 = maxX, y2 = maxY, z,
                crop,
                planted, skipped
            };
        }

        // find valid planting spots in an area or within a building's range
        public object FindPlantingSpots(string crop, int buildingId, int x1, int y1, int x2, int y2, int z)
        {
            var spots = new List<object>();

            if (buildingId != 0)
            {
                // building mode: get planting coords from building's InRangePlantingCoordinates
                var ec = FindEntity(buildingId);
                if (ec == null)
                    return new { error = "building not found", id = buildingId };

                var inRange = ec.GetComponent<Timberborn.Planting.InRangePlantingCoordinates>();
                if (inRange == null)
                    return new { error = "building has no planting range", id = buildingId };

                foreach (var c in inRange.GetCoordinates())
                {
                    if (!_plantingAreaValidator.CanPlant(c, crop)) continue;
                    bool moist = _soilMoistureService.SoilIsMoist(c);
                    bool planted = _plantingService.IsResourceAt(c);
                    spots.Add(new { x = c.x, y = c.y, z = c.z, moist, planted });
                }
            }
            else
            {
                // area mode: scan rectangle
                for (int x = Mathf.Min(x1, x2); x <= Mathf.Max(x1, x2); x++)
                {
                    for (int y = Mathf.Min(y1, y2); y <= Mathf.Max(y1, y2); y++)
                    {
                        var c = new Vector3Int(x, y, z);
                        if (!_plantingAreaValidator.CanPlant(c, crop)) continue;
                        bool moist = _soilMoistureService.SoilIsMoist(c);
                        bool planted = _plantingService.IsResourceAt(c);
                        spots.Add(new { x, y, z, moist, planted });
                    }
                }
            }

            return new { crop, spots };
        }

        public object UnmarkPlanting(int x1, int y1, int x2, int y2, int z)
        {
            var minX = Mathf.Min(x1, x2);
            var maxX = Mathf.Max(x1, x2);
            var minY = Mathf.Min(y1, y2);
            var maxY = Mathf.Max(y1, y2);

            var coords = new List<Vector3Int>();
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    coords.Add(new Vector3Int(x, y, z));
                }
            }

            foreach (var c in coords)
            {
                _plantingService.UnsetPlantingCoordinates(c);
            }

            return new
            {
                x1 = minX, y1 = minY, x2 = maxX, y2 = maxY, z,
                cleared = true,
                tiles = coords.Count
            };
        }

        // ================================================================
        public object CollectScience()
        {
            var unlockables = new List<object>();
            foreach (var building in _buildingService.Buildings)
            {
                var bs = building.GetSpec<BuildingSpec>();
                if (bs == null || bs.ScienceCost <= 0) continue;
                var templateSpec = building.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                var name = templateSpec?.TemplateName ?? "unknown";
                var unlocked = _buildingUnlockingService.Unlocked(bs);
                unlockables.Add(new { name, cost = bs.ScienceCost, unlocked });
            }

            return new
            {
                points = _scienceService.SciencePoints,
                unlockables
            };
        }

        // unlock via ToolUnlockingService.TryToUnlock -- matches the exact UI flow
        // when a player clicks "Unlock" in the science panel (cost deduction + events + UI refresh)
        public object UnlockBuilding(string buildingName)
        {
            try
            {
                foreach (var toolButton in _toolButtonService.ToolButtons)
                {
                    var blockObjectTool = toolButton.Tool as BlockObjectTool;
                    if (blockObjectTool == null) continue;
                    var templateSpec = blockObjectTool.Template.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                    if (templateSpec != null && templateSpec.TemplateName == buildingName)
                    {
                        var buildingSpec = blockObjectTool.Template.GetSpec<BuildingSpec>();
                        if (buildingSpec != null && _buildingUnlockingService.Unlocked(buildingSpec))
                            return new { building = buildingName, unlocked = true,
                                         remaining = _scienceService.SciencePoints,
                                         note = "already unlocked" };
                        var cost = buildingSpec?.ScienceCost ?? 0;
                        if (cost > _scienceService.SciencePoints)
                            return new { error = "not enough science",
                                         building = buildingName,
                                         scienceCost = cost,
                                         currentPoints = _scienceService.SciencePoints };
                        _buildingUnlockingService.Unlock(buildingSpec);
                        _toolUnlockingService.UnlockInternal(blockObjectTool, () => {});
                        return new { building = buildingName, unlocked = true,
                                     remaining = _scienceService.SciencePoints };
                    }
                }

                return new { error = "building not found in toolbar", building = buildingName };
            }
            catch (System.Exception ex)
            {
                return new { error = ex.Message, building = buildingName };
            }
        }

        // PERF: O(n) entity scan x O(needs) per beaver. Called occasionally for wellbeing analysis.
        // population wellbeing breakdown by need group (Social, Hygiene, etc)
        public object CollectWellbeing()
        {
            try
            {
                // get all need specs for beavers
                var beaverNeeds = _factionNeedService.GetBeaverNeeds();

                // build group -> need specs mapping
                var groupNeeds = new Dictionary<string, List<NeedSpec>>();
                foreach (var ns in beaverNeeds)
                {
                    var groupId = ns.NeedGroupId;
                    if (string.IsNullOrEmpty(groupId)) continue;
                    if (!groupNeeds.ContainsKey(groupId))
                        groupNeeds[groupId] = new List<NeedSpec>();
                    groupNeeds[groupId].Add(ns);
                }

                // aggregate per beaver
                int beaverCount = 0;
                var groupTotals = new Dictionary<string, float>();    // current wellbeing sum per group
                var groupMaxTotals = new Dictionary<string, float>(); // max wellbeing sum per group

                // build need->group lookup from specs
                var needToGroup = new Dictionary<string, string>();
                foreach (var kvp in groupNeeds)
                    foreach (var ns in kvp.Value)
                        needToGroup[ns.Id] = kvp.Key;

                foreach (var c in _beavers.Read)
                {
                    if (c.Needs == null) continue;
                    beaverCount++;

                    // accumulate from cached needs
                    foreach (var n in c.Needs)
                    {
                        if (!needToGroup.TryGetValue(n.Id, out var groupId)) continue;
                        if (!groupTotals.ContainsKey(groupId))
                        {
                            groupTotals[groupId] = 0f;
                            groupMaxTotals[groupId] = 0f;
                        }
                        groupTotals[groupId] += n.Wellbeing;
                    }
                    // max totals (from specs, same per beaver)
                    foreach (var kvp in groupNeeds)
                    {
                        var groupId = kvp.Key;
                        float groupMax = 0f;
                        foreach (var ns in kvp.Value)
                            groupMax += ns.FavorableWellbeing;
                        if (!groupMaxTotals.ContainsKey(groupId))
                            groupMaxTotals[groupId] = 0f;
                        groupMaxTotals[groupId] += groupMax;
                    }
                }

                // build output
                var categories = new List<object>();
                foreach (var kvp in groupNeeds)
                {
                    var groupId = kvp.Key;
                    float avgCurrent = beaverCount > 0 ? groupTotals.GetValueOrDefault(groupId) / beaverCount : 0;
                    float avgMax = beaverCount > 0 ? groupMaxTotals.GetValueOrDefault(groupId) / beaverCount : 0;

                    var needs = new List<object>();
                    foreach (var ns in kvp.Value)
                        needs.Add(new { id = ns.Id, favorableWellbeing = ns.FavorableWellbeing, unfavorableWellbeing = ns.UnfavorableWellbeing });

                    categories.Add(new
                    {
                        group = groupId,
                        current = System.Math.Round(avgCurrent, 1),
                        max = System.Math.Round(avgMax, 1),
                        needs
                    });
                }

                return new
                {
                    beavers = beaverCount,
                    categories
                };
            }
            catch (System.Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        public object CollectNotifications()
        {
            var results = new List<object>();
            try
            {
                foreach (var n in _notificationSaver.Notifications)
                {
                    results.Add(new
                    {
                        subject = n.Subject,
                        description = n.Description,
                        cycle = n.Cycle,
                        cycleDay = n.CycleDay
                    });
                }
            }
            catch { }
            return results;
        }

        public object CollectDistribution()
        {
            var results = new List<object>();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var distSetting = dc.GetComponent<Timberborn.DistributionSystem.DistrictDistributionSetting>();
                if (distSetting == null) continue;

                var goods = new List<object>();
                try
                {
                    foreach (var gs in distSetting.GoodDistributionSettings)
                    {
                        goods.Add(new
                        {
                            good = gs.GoodId,
                            importOption = gs.ImportOption.ToString(),
                            exportThreshold = gs.ExportThreshold
                        });
                    }
                }
                catch { }

                results.Add(new
                {
                    district = dc.DistrictName,
                    goods
                });
            }
            return results;
        }

        // set import/export settings for a good in a district
        public object SetDistribution(string districtName, string goodId, string importOption, int exportThreshold)
        {
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                if (dc.DistrictName != districtName) continue;

                var distSetting = dc.GetComponent<Timberborn.DistributionSystem.DistrictDistributionSetting>();
                if (distSetting == null)
                    return new { error = "no distribution settings", district = districtName };

                try
                {
                    var gs = distSetting.GetGoodDistributionSetting(goodId);
                    if (gs != null)
                    {
                        if (!string.IsNullOrEmpty(importOption) &&
                            Enum.TryParse<Timberborn.DistributionSystem.ImportOption>(importOption, true, out var parsed))
                            gs.SetImportOption(parsed);
                        if (exportThreshold >= 0)
                            gs.SetExportThreshold(exportThreshold);
                    }
                }
                catch (System.Exception ex)
                {
                    return new { error = ex.Message, district = districtName, good = goodId };
                }

                return new { district = districtName, good = goodId, importOption, exportThreshold };
            }
            return new { error = "district not found", district = districtName };
        }

        // building work range -- same green circle the player sees
        public object CollectBuildingRange(int buildingId)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var terrainRange = ec.GetComponent<Timberborn.BuildingsNavigation.BuildingTerrainRange>();
            if (terrainRange == null)
                return new { error = "building has no work range", id = buildingId,
                             name = CleanName(ec.GameObject.name) };

            var range = terrainRange.GetRange();
            int moistCount = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var c in range)
            {
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.y < minY) minY = c.y;
                if (c.y > maxY) maxY = c.y;
                if (_soilMoistureService.SoilIsMoist(c)) moistCount++;
            }

            return new
            {
                id = buildingId,
                name = CleanName(ec.GameObject.name),
                tiles = range.Count,
                moist = moistCount,
                bounds = range.Count > 0 ? new { x1 = minX, y1 = minY, x2 = maxX, y2 = maxY } : null
            };
        }

        // PLACEMENT VALIDATION
        // ================================================================

        private int GetTerrainHeight(int x, int y)
        {
            var size = _terrainService.Size;
            if (x < 0 || x >= size.x || y < 0 || y >= size.y) return 0;
            var index2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
            var stride = _mapIndexService.VerticalStride;
            var columnCount = _terrainMap.ColumnCounts[index2D];
            if (columnCount <= 0) return 0;
            var topIndex = index2D + (columnCount - 1) * stride;
            return _terrainMap.GetColumnCeiling(topIndex);
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 3
        // ================================================================

        public object CollectPrefabs()
        {
            var results = new List<object>();
            foreach (var building in _buildingService.Buildings)
            {
                var templateSpec = building.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                var blockSpec = building.GetSpec<BlockObjectSpec>();

                var entry = new Dictionary<string, object>
                {
                    ["name"] = templateSpec?.TemplateName ?? "unknown"
                };

                if (blockSpec != null)
                {
                    var size = blockSpec.Size;
                    entry["sizeX"] = size.x;
                    entry["sizeY"] = size.y;
                    entry["sizeZ"] = size.z;
                }

                var bs = building.GetSpec<BuildingSpec>();
                if (bs != null)
                {
                    if (bs.ScienceCost > 0)
                    {
                        entry["scienceCost"] = bs.ScienceCost;
                        entry["unlocked"] = _buildingUnlockingService.Unlocked(bs);
                    }
                    var costs = new List<object>();
                    try
                    {
                        foreach (var ga in bs.BuildingCost)
                        {
                            var goodProp = ga.GetType().GetProperty("GoodId") ?? ga.GetType().GetProperty("Id");
                            var amtProp = ga.GetType().GetProperty("Amount");
                            if (goodProp != null && amtProp != null)
                                costs.Add(new { good = goodProp.GetValue(ga)?.ToString(), amount = amtProp.GetValue(ga) });
                        }
                    }
                    catch { }
                    if (costs.Count > 0)
                        entry["cost"] = costs;
                }

                results.Add(entry);
            }
            return results;
        }

        // remove a building from the world
        public object DemolishBuilding(int buildingId)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "entity not found", id = buildingId };

            var name = CleanName(ec.GameObject.name);
            _entityService.Delete(ec);
            return new { id = buildingId, name, demolished = true };
        }

        // route a straight-line path from (x1,y1) to (x2,y2), auto-placing stairs at z-level changes
        public object RoutePath(int x1, int y1, int x2, int y2)
        {
            if (x1 != x2 && y1 != y2)
                return new { error = "path must be a straight line (x1==x2 or y1==y2)" };

            int dx = x2 > x1 ? 1 : x2 < x1 ? -1 : 0;
            int dy = y2 > y1 ? 1 : y2 < y1 ? -1 : 0;
            // stairs orientation: direction of travel when going uphill
            // south=0, west=1, north=2, east=3
            int stairsOrient = dx > 0 ? 3 : dx < 0 ? 1 : dy > 0 ? 2 : 0;

            int placed = 0, skipped = 0, stairs = 0;
            var errors = new List<string>();
            int cx = x1, cy = y1;
            int prevZ = GetTerrainHeight(cx, cy);

            while (true)
            {
                int tz = GetTerrainHeight(cx, cy);
                if (tz <= 0)
                {
                    errors.Add($"no terrain at ({cx},{cy})");
                    if (cx == x2 && cy == y2) break;
                    cx += dx; cy += dy;
                    continue;
                }

                int zDiff = tz - prevZ;

                if (zDiff != 0)
                {
                    int levels = System.Math.Abs(zDiff);
                    int baseZ = System.Math.Min(prevZ, tz);
                    bool goingUp = zDiff > 0;
                    int rampOrient = goingUp ? stairsOrient : (stairsOrient + 2) % 4;

                    // helper: demolish any path at a tile position
                    // O(n) scan but only called once per z-level change (max ~6 times per route)
                    void DemolishPathAt(int px, int py, int pz)
                    {
                        foreach (var cb in _buildings.Read)
                        {
                            if (cb.BlockObject == null) continue;
                            var c = cb.BlockObject.Coordinates;
                            if (c.x == px && c.y == py && c.z == pz && cb.Name.Contains("Path"))
                            {
                                DemolishBuilding(cb.Id);
                                placed--;
                                break;
                            }
                        }
                    }

                    // build ramp: N tiles, each with (tileIndex) platforms + 1 stair on top
                    // going up: ramp starts at previous tile, extends backward
                    // going down: ramp starts at current tile, extends forward
                    for (int step = 0; step < levels; step++)
                    {
                        int rampTileX, rampTileY;
                        if (goingUp)
                        {
                            // going up: first ramp tile is the previous tile, then go backward
                            rampTileX = cx - dx * (levels - step);
                            rampTileY = cy - dy * (levels - step);
                        }
                        else
                        {
                            // going down: ramp tiles go forward from current position
                            rampTileX = cx + dx * step;
                            rampTileY = cy + dy * step;
                        }

                        // demolish any path we placed on this ramp tile
                        DemolishPathAt(rampTileX, rampTileY, GetTerrainHeight(rampTileX, rampTileY));

                        // stack platforms: step count of them
                        for (int p = 0; p < step; p++)
                        {
                            var platResult = PlaceBuilding("Platform.IronTeeth", rampTileX, rampTileY, baseZ + p, "south");
                            if (platResult.GetType().GetProperty("id") == null)
                            {
                                var err = platResult.GetType().GetProperty("error")?.GetValue(platResult);
                                if (err != null && !err.ToString().Contains("occupied"))
                                    errors.Add($"platform at ({rampTileX},{rampTileY},z={baseZ + p}): {err}");
                            }
                        }

                        // place stair on top
                        int stairZ = baseZ + step;
                        var stairResult = PlaceBuilding("Stairs.IronTeeth", rampTileX, rampTileY, stairZ, OrientNames[rampOrient]);
                        if (stairResult.GetType().GetProperty("id") != null)
                            stairs++;
                        else
                        {
                            var err = stairResult.GetType().GetProperty("error")?.GetValue(stairResult);
                            if (err != null && !err.ToString().Contains("occupied"))
                                errors.Add($"stairs at ({rampTileX},{rampTileY},z={stairZ}): {err}");
                        }
                    }

                    if (!goingUp)
                    {
                        // skip past the ramp tiles we just built
                        for (int skip = 0; skip < levels - 1; skip++)
                        {
                            cx += dx; cy += dy;
                        }
                    }

                    prevZ = tz;
                    // fall through to place path at current tile (first tile at new z-level)
                }

                // place path at current tile
                var result = PlaceBuilding("Path", cx, cy, tz, "south");
                if (result.GetType().GetProperty("id") != null)
                    placed++;
                else
                {
                    var err = result.GetType().GetProperty("error")?.GetValue(result);
                    if (err != null && !err.ToString().Contains("occupied"))
                        errors.Add($"path at ({cx},{cy}): {err}");
                    else
                        skipped++;
                }

                prevZ = tz;
                if (cx == x2 && cy == y2) break;
                cx += dx; cy += dy;
            }

            var ret = new { placed, stairs, skipped,
                            errors = errors.Count > 0 ? errors.ToArray() : null };
            return ret;
        }

        // general purpose debug endpoint -- navigate, inspect, and call methods on any game object
        // chain through objects with dot-separated paths: "type._field1._field2.MethodName"
        // all params passed as args dict from POST body
        private static object _debugLastResult;

        public object DebugInspect(string target, Dictionary<string, string> args = null)
        {
            var info = new Dictionary<string, object>();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            args = args ?? new Dictionary<string, string>();

            string Arg(string key, string def = "") => args.ContainsKey(key) ? args[key] : def;

            // parse a string arg into any supported type
            object ParseArg(string argStr, System.Type pType)
            {
                if (argStr == "$") return _debugLastResult;
                if (pType == typeof(string)) return argStr;
                if (pType == typeof(int)) return int.Parse(argStr);
                if (pType == typeof(float)) return float.Parse(argStr);
                if (pType == typeof(double)) return double.Parse(argStr);
                if (pType == typeof(bool)) return bool.Parse(argStr);
                if (pType == typeof(long)) return long.Parse(argStr);
                if (pType == typeof(Vector3Int))
                {
                    var c = argStr.Split(',');
                    return new Vector3Int(int.Parse(c[0]), int.Parse(c[1]), int.Parse(c[2]));
                }
                if (pType == typeof(Vector3))
                {
                    var c = argStr.Split(',');
                    return new Vector3(float.Parse(c[0]), float.Parse(c[1]), float.Parse(c[2]));
                }
                if (pType == typeof(Vector2Int))
                {
                    var c = argStr.Split(',');
                    return new Vector2Int(int.Parse(c[0]), int.Parse(c[1]));
                }
                // try Convert.ChangeType as fallback
                try { return System.Convert.ChangeType(argStr, pType); } catch { }
                return null;
            }

            // resolve a dot-path from this service to a nested object
            // supports: fields, properties, parameterless methods, list indexing [N], GetComponent<T>
            // $ = last debug result (for chaining calls)
            // e.g. "_districtCenterRegistry.FinishedDistrictCenters.[0].AllComponents"
            // e.g. "$.HasNode" (call method on last result)
            object Resolve(string path)
            {
                var parts = path.Split('.');
                object current = parts[0] == "$" ? _debugLastResult : (object)this;
                if (parts[0] == "$") parts = parts.Skip(1).ToArray();
                foreach (var part in parts)
                {
                    if (current == null) return null;

                    // list/array indexing: [N]
                    if (part.StartsWith("[") && part.EndsWith("]"))
                    {
                        int idx = int.Parse(part.Substring(1, part.Length - 2));
                        if (current is System.Collections.IList list)
                        {
                            current = idx < list.Count ? list[idx] : null;
                        }
                        else if (current is System.Collections.IEnumerable enumerable)
                        {
                            int i = 0;
                            current = null;
                            foreach (var item in enumerable)
                            {
                                if (i == idx) { current = item; break; }
                                i++;
                            }
                        }
                        else return null;
                        continue;
                    }

                    // GetComponent<TypeName> syntax: ~TypeName
                    if (part.StartsWith("~"))
                    {
                        var typeName = part.Substring(1);
                        var getCompMethod = current.GetType().GetMethod("GetComponent",
                            System.Type.EmptyTypes);
                        // try finding the right generic overload by iterating AllComponents
                        var allCompsProp = current.GetType().GetProperty("AllComponents", flags);
                        if (allCompsProp != null)
                        {
                            var allComps = allCompsProp.GetValue(current) as System.Collections.IEnumerable;
                            if (allComps != null)
                            {
                                current = null;
                                foreach (var comp in allComps)
                                {
                                    if (comp.GetType().Name == typeName || comp.GetType().FullName.Contains(typeName))
                                    { current = comp; break; }
                                }
                            }
                        }
                        continue;
                    }

                    var t = current.GetType();
                    var field = t.GetField(part, flags);
                    if (field != null) { current = field.GetValue(current); continue; }
                    var prop = t.GetProperty(part, flags);
                    if (prop != null) { current = prop.GetValue(current); continue; }
                    // try as parameterless method
                    var method = t.GetMethod(part, flags, null, System.Type.EmptyTypes, null);
                    if (method != null) { current = method.Invoke(current, null); continue; }
                    return null;
                }
                return current;
            }

            // dump an object's fields and properties
            void DumpObject(object obj, Dictionary<string, object> into, int maxItems = 5)
            {
                if (obj == null) { into["value"] = "null"; return; }
                into["type"] = obj.GetType().FullName;
                if (obj is string s) { into["value"] = s; return; }
                if (obj is System.Collections.IEnumerable enumerable)
                {
                    int count = 0;
                    var samples = new List<string>();
                    foreach (var item in enumerable)
                    {
                        count++;
                        if (samples.Count < maxItems) samples.Add(item?.ToString() ?? "null");
                    }
                    into["count"] = count;
                    into["samples"] = samples;
                    return;
                }
                into["value"] = obj.ToString();
            }

            try
            {
                switch (target)
                {
                    case "help":
                        info["targets"] = new[]
                        {
                            "help -- this message",
                            "get -- navigate object chain. args: path (dot-separated from TimberbotService)",
                            "fields -- list members. args: path, filter",
                            "call -- call method. args: path (to object), method, arg0..argN (string args, Vector3Int as x,y,z)",
                        };
                        info["roots"] = new[]
                        {
                            "_buildingService", "_entityRegistry", "_districtCenterRegistry",
                            "_navMeshService", "_soilMoistureService", "_toolButtonService",
                            "_blockObjectPlacerService", "_scienceService", "_buildingUnlockingService",
                            "_districtPathNavRegistrar", "_toolUnlockingService"
                        };
                        info["examples"] = new[]
                        {
                            "debug target:fields path:_navMeshService filter:Road",
                            "debug target:get path:_scienceService.SciencePoints",
                            "debug target:call path:_navMeshService method:AreConnectedRoadInstant arg0:120,142,2 arg1:130,142,2",
                        };
                        break;

                    case "get":
                    {
                        var path = Arg("path", "");
                        if (string.IsNullOrEmpty(path)) { info["error"] = "pass path:_fieldName.nested.field"; break; }
                        var obj = Resolve(path);
                        _debugLastResult = obj;
                        info["path"] = path;
                        DumpObject(obj, info);
                        break;
                    }

                    case "fields":
                    {
                        var path = Arg("path", "");
                        object obj = string.IsNullOrEmpty(path) ? (object)this : Resolve(path);
                        if (obj == null) { info["error"] = $"could not resolve '{path}'"; break; }
                        info["type"] = obj.GetType().FullName;
                        var filter = Arg("filter", "");
                        var members = new List<string>();
                        foreach (var f in obj.GetType().GetFields(flags))
                            if (string.IsNullOrEmpty(filter) || f.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                                members.Add($"F {f.Name}:{f.FieldType.Name}");
                        foreach (var p in obj.GetType().GetProperties(flags))
                            if (string.IsNullOrEmpty(filter) || p.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                                members.Add($"P {p.Name}:{p.PropertyType.Name}");
                        foreach (var m in obj.GetType().GetMethods(flags))
                        {
                            if (m.DeclaringType == typeof(object) || m.IsSpecialName) continue;
                            if (!string.IsNullOrEmpty(filter) && m.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var parms = m.GetParameters();
                            members.Add($"M {m.Name}({string.Join(",", System.Linq.Enumerable.Select(parms, p => p.ParameterType.Name))})->{m.ReturnType.Name}");
                        }
                        info["members"] = members;
                        break;
                    }

                    case "call":
                    {
                        var path = Arg("path", "");
                        var methodName = Arg("method", "");
                        if (string.IsNullOrEmpty(methodName)) { info["error"] = "pass method:MethodName"; break; }
                        object obj = string.IsNullOrEmpty(path) ? (object)this : Resolve(path);
                        if (obj == null) { info["error"] = $"could not resolve '{path}'"; break; }
                        // find all overloads
                        var methods = obj.GetType().GetMethods(flags);
                        System.Reflection.MethodInfo bestMethod = null;
                        foreach (var m in methods)
                            if (m.Name == methodName) { bestMethod = m; break; }
                        if (bestMethod == null) { info["error"] = $"method {methodName} not found on {obj.GetType().Name}"; break; }
                        // build args from arg0, arg1, etc
                        var methodParams = bestMethod.GetParameters();
                        var callArgs = new object[methodParams.Length];
                        for (int i = 0; i < methodParams.Length; i++)
                        {
                            var argStr = Arg($"arg{i}", "");
                            callArgs[i] = ParseArg(argStr, methodParams[i].ParameterType);
                        }
                        var result = bestMethod.Invoke(obj, callArgs);
                        _debugLastResult = result;
                        DumpObject(result, info);
                        info["stored"] = "result stored in $ for chaining";
                        break;
                    }

                    default:
                        info["error"] = $"unknown target '{target}'. use: help, get, fields, call";
                        break;
                }
            }
            catch (System.Exception ex)
            {
                info["error"] = ex.ToString();
            }
            return info;
        }

        // validate placement using the game's own preview system (same as player UI)
        // PreviewFactory.Create() handles water buildings, terrain, occupancy -- all 9 validators
        private bool ValidatePlacement(BuildingSpec buildingSpec, BlockObjectSpec blockObjectSpec, int x, int y, int z, int orientation)
        {
            var size = blockObjectSpec.Size;
            int rx = size.x, ry = size.y;
            if (orientation == 1 || orientation == 3) { rx = size.y; ry = size.x; }
            int gx = x, gy = y;
            switch (orientation)
            {
                case 1: gy = y + ry - 1; break;
                case 2: gx = x + rx - 1; gy = y + ry - 1; break;
                case 3: gx = x + rx - 1; break;
            }

            var placeableSpec = buildingSpec.GetSpec<PlaceableBlockObjectSpec>();
            if (placeableSpec == null) return false;
            Preview preview = null;
            try
            {
                var placement = new Placement(new Vector3Int(gx, gy, z),
                    (Timberborn.Coordinates.Orientation)orientation, FlipMode.Unflipped);
                preview = _previewFactory.Create(placeableSpec);
                preview.Reposition(placement);
                return preview.BlockObject.IsValid();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Timberbot] ValidatePlacement error at ({x},{y},{z}): {ex.Message}\n{ex.StackTrace}");
                return false;
            }
            finally
            {
                if (preview != null)
                    UnityEngine.Object.Destroy(preview.GameObject);
            }
        }

        // PERF: O(n) entity scan for path/power tiles + O(area * 4) preview validation loop.
        // Cached preview reused via Reposition for each candidate. Called once per bot turn.
        public object FindPlacement(string prefabName, int x1, int y1, int x2, int y2)
        {
            var buildingSpec = _buildingService.GetBuildingTemplate(prefabName);
            if (buildingSpec == null)
                return new { error = "unknown prefab", prefab = prefabName };
            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            if (blockObjectSpec == null)
                return new { error = "no block object spec", prefab = prefabName };

            var size = blockObjectSpec.Size;

            // get all road nodes reachable from DC using the game's own range service
            // same method the game uses to draw the green-to-red path line
            var reachableRoadCoords = new HashSet<Vector3Int>();
            try
            {
                var reflFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var nodeIdSvc = _navMeshService.GetType().GetField("_nodeIdService", reflFlags)
                    ?.GetValue(_navMeshService) as Timberborn.Navigation.NodeIdService;

                foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    var cachingFF = dc.GetComponent<BuildingCachingFlowField>();
                    if (cachingFF == null || nodeIdSvc == null) continue;
                    var accessCoords = (Vector3Int)cachingFF.GetType().GetField("_accessCoordinates", reflFlags).GetValue(cachingFF);
                    int dcNodeId = nodeIdSvc.GridToId(accessCoords);
                    Vector3 dcWorldPos = nodeIdSvc.IdToWorld(dcNodeId);

                    var drawer = dc.GetComponent<DistrictPathNavRangeDrawer>();
                    if (drawer == null) continue;
                    var navRangeSvc = drawer.GetType().GetField("_navigationRangeService", reflFlags)?.GetValue(drawer);
                    if (navRangeSvc == null) continue;

                    var nodesInRange = navRangeSvc.GetType().GetMethod("GetRoadNodesInRange")
                        ?.Invoke(navRangeSvc, new object[] { dcWorldPos }) as System.Collections.IEnumerable;
                    if (nodesInRange == null) continue;

                    foreach (var wc in nodesInRange)
                    {
                        var coordsProp = wc.GetType().GetProperty("Coordinates");
                        if (coordsProp != null)
                            reachableRoadCoords.Add((Vector3Int)coordsProp.GetValue(wc));
                    }
                    break;
                }
            }
            catch { }

            // collect path and power tile positions for placement scoring
            var pathTiles = new HashSet<long>();
            var powerTiles = new HashSet<long>();
            foreach (var cb in _buildings.Read)
            {
                if (cb.BlockObject == null) continue;
                if (cb.Name.Contains("Path") || cb.Name.Contains("Stairs"))
                {
                    foreach (var block in cb.BlockObject.PositionedBlocks.GetAllBlocks())
                    {
                        var c = block.Coordinates;
                        pathTiles.Add((long)c.x * 1000000 + (long)c.y * 1000 + c.z);
                    }
                }
                if (cb.PowerNode != null)
                {
                    foreach (var block in cb.BlockObject.PositionedBlocks.GetAllBlocks())
                    {
                        var c = block.Coordinates;
                        powerTiles.Add((long)c.x * 1000000 + (long)c.y * 1000 + c.z);
                    }
                }
            }

            var orientNames = new[] { "south", "west", "north", "east" };
            var results = new List<object>();

            // PERF: create ONE preview, reuse with Reposition for each candidate
            var placeableSpec = buildingSpec.GetSpec<PlaceableBlockObjectSpec>();
            Preview cachedPreview = null;
            try { if (placeableSpec != null) cachedPreview = _previewFactory.Create(placeableSpec); } catch { }
            try
            {

            for (int ty = y1; ty <= y2; ty++)
            {
                for (int tx = x1; tx <= x2; tx++)
                {
                    int tz = GetTerrainHeight(tx, ty);
                    if (tz <= 0) continue;

                    // find best orientation (most path tiles adjacent to entrance side)
                    int bestOrient = -1;
                    int bestPathCount = -1;

                    for (int orient = 0; orient < 4; orient++)
                    {
                        // validate using cached preview
                        if (cachedPreview == null) continue;
                        int vrx = size.x, vry = size.y;
                        if (orient == 1 || orient == 3) { vrx = size.y; vry = size.x; }
                        int vgx = tx, vgy = ty;
                        switch (orient)
                        {
                            case 1: vgy = ty + vry - 1; break;
                            case 2: vgx = tx + vrx - 1; vgy = ty + vry - 1; break;
                            case 3: vgx = tx + vrx - 1; break;
                        }
                        var placement = new Placement(new Vector3Int(vgx, vgy, tz),
                            (Timberborn.Coordinates.Orientation)orient, FlipMode.Unflipped);
                        cachedPreview.Reposition(placement);
                        if (!cachedPreview.BlockObject.IsValid()) continue;

                        // count path tiles on entrance side
                        int rx = size.x, ry = size.y;
                        if (orient == 1 || orient == 3) { rx = size.y; ry = size.x; }

                        int pathCount = 0;
                        switch (orient)
                        {
                            case 0: // south: check y-1 row
                                for (int px = tx; px < tx + rx; px++)
                                    if (pathTiles.Contains((long)px * 1000000 + (long)(ty - 1) * 1000 + tz)) pathCount++;
                                break;
                            case 1: // west: check x-1 column
                                for (int py = ty; py < ty + ry; py++)
                                    if (pathTiles.Contains((long)(tx - 1) * 1000000 + (long)py * 1000 + tz)) pathCount++;
                                break;
                            case 2: // north: check y+ry row
                                for (int px = tx; px < tx + rx; px++)
                                    if (pathTiles.Contains((long)px * 1000000 + (long)(ty + ry) * 1000 + tz)) pathCount++;
                                break;
                            case 3: // east: check x+rx column
                                for (int py = ty; py < ty + ry; py++)
                                    if (pathTiles.Contains((long)(tx + rx) * 1000000 + (long)py * 1000 + tz)) pathCount++;
                                break;
                        }

                        if (pathCount > bestPathCount)
                        {
                            bestPathCount = pathCount;
                            bestOrient = orient;
                        }
                    }

                    if (bestOrient >= 0)
                    {
                        // check district road reachability on entrance-side path tiles
                        bool reachable = false;
                        if (bestPathCount > 0)
                        {
                            int erx = size.x, ery = size.y;
                            if (bestOrient == 1 || bestOrient == 3) { erx = size.y; ery = size.x; }
                            var checkCoords = new List<Vector3Int>();
                            switch (bestOrient)
                            {
                                case 0:
                                    for (int px = tx; px < tx + erx; px++)
                                        checkCoords.Add(new Vector3Int(px, ty - 1, tz));
                                    break;
                                case 1:
                                    for (int py = ty; py < ty + ery; py++)
                                        checkCoords.Add(new Vector3Int(tx - 1, py, tz));
                                    break;
                                case 2:
                                    for (int px = tx; px < tx + erx; px++)
                                        checkCoords.Add(new Vector3Int(px, ty + ery, tz));
                                    break;
                                case 3:
                                    for (int py = ty; py < ty + ery; py++)
                                        checkCoords.Add(new Vector3Int(tx + erx, py, tz));
                                    break;
                            }
                            foreach (var coord in checkCoords)
                            {
                                if (reachableRoadCoords.Contains(coord))
                                { reachable = true; break; }
                            }
                        }

                        // check power adjacency on all 4 sides of footprint
                        int brx = size.x, bry = size.y;
                        if (bestOrient == 1 || bestOrient == 3) { brx = size.y; bry = size.x; }
                        bool nearPower = false;
                        for (int px = tx - 1; px <= tx + brx && !nearPower; px++)
                            for (int py = ty - 1; py <= ty + bry && !nearPower; py++)
                            {
                                if (px >= tx && px < tx + brx && py >= ty && py < ty + bry) continue;
                                if (powerTiles.Contains((long)px * 1000000 + (long)py * 1000 + tz))
                                    nearPower = true;
                            }

                        results.Add(new { x = tx, y = ty, z = tz,
                                          orientation = orientNames[bestOrient],
                                          pathAccess = bestPathCount > 0,
                                          pathCount = bestPathCount,
                                          reachable,
                                          nearPower });
                    }
                }
            }

            // sort: reachable > path access > power > path count
            results.Sort((a, b) =>
            {
                var aType = a.GetType();
                var bType = b.GetType();
                bool ra = (bool)aType.GetProperty("reachable").GetValue(a);
                bool rb = (bool)bType.GetProperty("reachable").GetValue(b);
                if (ra != rb) return rb ? 1 : -1;
                bool pa = (bool)aType.GetProperty("pathAccess").GetValue(a);
                bool pb = (bool)bType.GetProperty("pathAccess").GetValue(b);
                if (pa != pb) return pb ? 1 : -1;
                bool pwa = (bool)aType.GetProperty("nearPower").GetValue(a);
                bool pwb = (bool)bType.GetProperty("nearPower").GetValue(b);
                if (pwa != pwb) return pwb ? 1 : -1;
                int ca = (int)aType.GetProperty("pathCount").GetValue(a);
                int cb = (int)bType.GetProperty("pathCount").GetValue(b);
                return cb - ca;
            });

            } // end try
            finally
            {
                if (cachedPreview != null)
                    UnityEngine.Object.Destroy(cachedPreview.GameObject);
            }

            if (results.Count > 10) results = results.GetRange(0, 10);

            return new { prefab = prefabName, sizeX = size.x, sizeY = size.y,
                         placements = results };
        }

        // place with full validation before calling Place():
        // 1. exists + unlocked
        // 2. origin correction (user coords = bottom-left regardless of orientation)
        // 3. per-tile: terrain height == z, no water (unless water building), no occupancy (dead trees ok), no underground clipping
        // 4. Place() only after all checks pass
        private static readonly string[] OrientNames = { "south", "west", "north", "east" };
        private static readonly string[] PriorityNames = { "VeryLow", "Low", "Normal", "High", "VeryHigh" };

        private static string GetPriorityName(Timberborn.PrioritySystem.Priority p)
        {
            int i = (int)p;
            return (i >= 0 && i < PriorityNames.Length) ? PriorityNames[i] : "Normal";
        }

        private static int ParseOrientation(string orient)
        {
            if (string.IsNullOrEmpty(orient)) return 0;
            var lower = orient.Trim().ToLowerInvariant();
            switch (lower)
            {
                case "south": return 0;
                case "west":  return 1;
                case "north": return 2;
                case "east":  return 3;
                default: return -1;
            }
        }

        public object PlaceBuilding(string prefabName, int x, int y, int z, string orientationStr)
        {
            int orientation = ParseOrientation(orientationStr);
            if (orientation < 0)
                return new { error = $"invalid orientation '{orientationStr}', use: south, west, north, east",
                             prefab = prefabName };

            var buildingSpec = _buildingService.GetBuildingTemplate(prefabName);
            if (buildingSpec == null)
                return new { error = "unknown prefab", prefab = prefabName };

            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            if (blockObjectSpec == null)
                return new { error = "no block object spec", prefab = prefabName };

            // check building is unlocked
            var bs = buildingSpec.GetSpec<BuildingSpec>();
            if (bs != null && bs.ScienceCost > 0 && !_buildingUnlockingService.Unlocked(bs))
                return new { error = "building not unlocked", prefab = prefabName,
                             scienceCost = bs.ScienceCost,
                             currentPoints = _scienceService.SciencePoints };

            // correct origin so user coords = bottom-left corner regardless of orientation
            // orientations 1,3 swap x/y dimensions
            var size = blockObjectSpec.Size;
            int rx = size.x, ry = size.y;
            if (orientation == 1 || orientation == 3) { rx = size.y; ry = size.x; }
            int gx = x, gy = y;
            switch (orientation)
            {
                case 1: gy = y + ry - 1; break;
                case 2: gx = x + rx - 1; gy = y + ry - 1; break;
                case 3: gx = x + rx - 1; break;
            }

            // validate using the game's own preview system (same as player UI)
            if (!ValidatePlacement(buildingSpec, blockObjectSpec, x, y, z, orientation))
                return new { error = $"Cannot place BlockObject {prefabName} at ({gx}, {gy}, {z}).",
                             prefab = prefabName, x, y, z, orientation = OrientNames[orientation] };

            // validation passed -- place the building
            var orient = (Timberborn.Coordinates.Orientation)orientation;
            var placement = new Placement(new Vector3Int(gx, gy, z), orient,
                FlipMode.Unflipped);

            var placer = _blockObjectPlacerService.GetMatchingPlacer(blockObjectSpec);
            int placedId = 0;
            string placedName = "";
            placer.Place(blockObjectSpec, placement, (entity) =>
            {
                placedId = entity.GameObject.GetInstanceID();
                placedName = CleanName(entity.GameObject.name);
            });

            if (placedId == 0)
            {
                return new
                {
                    error = "placement rejected by game engine",
                    prefab = prefabName,
                    x, y, z, orientation,
                    sizeX = size.x, sizeY = size.y, sizeZ = size.z,
                    hint = "passed pre-validation but game rejected it"
                };
            }

            return new { id = placedId, name = placedName, x, y, z, orientation = OrientNames[orientation] };
        }
    }
}
