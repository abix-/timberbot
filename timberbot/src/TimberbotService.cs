// TimberbotService.cs -- Core service: DI constructor, fields, lifecycle, settings.
//
// This is the main entry point for the Timberbot API mod. Timberborn's Bindito DI
// system injects ~35 game services into the constructor. The service runs as a game
// singleton: Load() starts the HTTP server, UpdateSingleton() refreshes cached state
// every N seconds and drains queued POST requests on the Unity main thread.
//
// The actual API logic is split across partial class files:
//   TimberbotService.Cache.cs      -- Double-buffered entity caching, structs, indexes
//   TimberbotService.Collect.cs    -- All GET read methods (CollectBuildings, CollectBeavers, etc.)
//   TimberbotService.Write.cs      -- All POST write methods (SetSpeed, PlaceBuilding, etc.)
//   TimberbotService.Placement.cs  -- Building placement validation, path routing, terrain queries
//   TimberbotService.Webhooks.cs   -- Push event notifications to registered URLs
//   TimberbotService.Debug.cs      -- Reflection inspector and benchmark endpoint

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
    public partial class TimberbotService : ILoadableSingleton, IUpdatableSingleton, IUnloadableSingleton
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
        public readonly TimberbotWebhook WebhookMgr;                      // batched webhook push notifications
        private TimberbotHttpServer _server;

        // settings (loaded from settings.json in mod folder)
        private float _refreshInterval = 1.0f;   // seconds between cache refreshes (default: 1s)
        private bool _debugEnabled = false;       // enable /api/debug endpoint (default: off)
        private int _httpPort = 8085;             // HTTP server port
        // webhook settings applied to WebhookMgr in Load()
        private bool _webhooksEnabled = true;
        private float _webhookBatchSeconds = 0.2f;
        private int _webhookCircuitBreaker = 30;

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
            EventBus eventBus,
            TimberbotWebhook webhookMgr)
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
            WebhookMgr = webhookMgr;
        }

        public void Load()
        {
            LoadSettings();
            var modDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                "Timberborn", "Mods", "Timberbot");
            TimberbotLog.Init(modDir);
            WebhookMgr.Enabled = _webhooksEnabled;
            WebhookMgr.BatchSeconds = _webhookBatchSeconds;
            WebhookMgr.CircuitBreakerThreshold = _webhookCircuitBreaker;
            TimberbotLog.Info($"v0.7.0 port={_httpPort} refresh={_refreshInterval}s debug={_debugEnabled} webhooks={_webhooksEnabled} batchMs={_webhookBatchSeconds * 1000:F0}");
            _eventBus.Register(this);
            WebhookMgr.Register();
            BuildAllIndexes();
            _server = new TimberbotHttpServer(_httpPort, this, _debugEnabled);
            TimberbotLog.Info($"HTTP server started on port {_httpPort}");
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
                    if (json["webhooksEnabled"] != null)
                        _webhooksEnabled = json.Value<bool>("webhooksEnabled");
                    if (json["webhookBatchMs"] != null)
                    {
                        int batchMs = json.Value<int>("webhookBatchMs");
                        _webhookBatchSeconds = batchMs >= 0 ? batchMs / 1000f : 0.2f;
                    }
                    if (json["webhookCircuitBreaker"] != null)
                    {
                        int cb = json.Value<int>("webhookCircuitBreaker");
                        _webhookCircuitBreaker = cb > 0 ? cb : 30;
                    }
                }
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error("settings.json load failed, using defaults", ex);
            }
        }

        public void Unload()
        {
            WebhookMgr.Unregister();
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
            WebhookMgr.FlushWebhooks(now);
        }
    }
}
