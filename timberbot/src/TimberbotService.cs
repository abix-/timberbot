using System;
using System.Collections.Generic;
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
using Timberborn.Wonders;
using Timberborn.NotificationSystem;
using Timberborn.StatusSystem;
using Timberborn.DwellingSystem;
using Timberborn.PowerManagement;
using Timberborn.SoilContaminationSystem;
using Timberborn.Hauling;
using Timberborn.Workshops;
using Timberborn.Fields;
using Timberborn.GameDistrictsMigration;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;
using Timberborn.PlantingUI;
using UnityEngine;

namespace Timberbot
{
    public class TimberbotService : ILoadableSingleton, IUpdatableSingleton
    {
        private readonly IGoodService _goodService;
        private readonly DistrictCenterRegistry _districtCenterRegistry;
        private readonly GameCycleService _gameCycleService;
        private readonly WeatherService _weatherService;
        private readonly IDayNightCycle _dayNightCycle;
        private readonly SpeedManager _speedManager;
        private readonly EntityRegistry _entityRegistry;
        private readonly TreeCuttingArea _treeCuttingArea;
        private readonly PlantingService _plantingService;
        private readonly BuildingService _buildingService;
        private readonly BlockObjectPlacerService _blockObjectPlacerService;
        private readonly EntityService _entityService;
        private readonly ITerrainService _terrainService;
        private readonly IThreadSafeWaterMap _waterMap;
        private readonly MapIndexService _mapIndexService;
        private readonly IThreadSafeColumnTerrainMap _terrainMap;
        private readonly ScienceService _scienceService;
        private readonly BuildingUnlockingService _buildingUnlockingService;
        private readonly NotificationSaver _notificationSaver;
        private readonly WorkingHoursManager _workingHoursManager;
        private readonly ISoilContaminationService _soilContaminationService;
        private readonly PopulationDistributorRetriever _populationDistributorRetriever;
        private readonly ToolButtonService _toolButtonService;
        private readonly UnlockedPlantableGroupsRegistry _unlockedPlantableGroupsRegistry;
        private readonly RecipeSpecService _recipeSpecService;
        private TimberbotHttpServer _server;

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
            UnlockedPlantableGroupsRegistry unlockedPlantableGroupsRegistry,
            RecipeSpecService recipeSpecService)
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
            _unlockedPlantableGroupsRegistry = unlockedPlantableGroupsRegistry;
            _recipeSpecService = recipeSpecService;
        }

        public void Load()
        {
            _server = new TimberbotHttpServer(8085, this);
            Debug.Log("[Timberbot] HTTP server started on port 8085");
        }

        public void UpdateSingleton()
        {
            _server?.DrainRequests();
        }

        private Dictionary<int, EntityComponent> _entityCache;
        private int _entityCacheFrame = -1;

        private EntityComponent FindEntity(int id)
        {
            int frame = Time.frameCount;
            if (_entityCache == null || _entityCacheFrame != frame)
            {
                _entityCache = new Dictionary<int, EntityComponent>();
                foreach (var ec in _entityRegistry.Entities)
                    _entityCache[ec.GameObject.GetInstanceID()] = ec;
                _entityCacheFrame = frame;
            }
            _entityCache.TryGetValue(id, out var result);
            return result;
        }

        // ================================================================
        // READ ENDPOINTS
        // ================================================================

        public object CollectSummary()
        {
            // single pass over all entities
            int markedGrown = 0, markedSeedling = 0, unmarkedGrown = 0;
            int occupiedBeds = 0, totalBeds = 0;
            int totalVacancies = 0, assignedWorkers = 0;
            float totalWellbeing = 0f;
            int beaverCount = 0;
            int alertUnstaffed = 0, alertUnpowered = 0, alertUnreachable = 0;
            int miserable = 0, critical = 0;

            foreach (var ec in _entityRegistry.Entities)
            {
                // trees
                var cuttable = ec.GetComponent<Cuttable>();
                if (cuttable != null)
                {
                    var living = ec.GetComponent<LivingNaturalResource>();
                    var growable = ec.GetComponent<Timberborn.Growing.Growable>();
                    var bo = ec.GetComponent<BlockObject>();
                    if (living != null && !living.IsDead && bo != null)
                    {
                        bool marked = _treeCuttingArea.IsInCuttingArea(bo.Coordinates);
                        bool grown = growable != null && growable.IsGrown;
                        if (marked && grown) markedGrown++;
                        else if (marked && !grown) markedSeedling++;
                        else if (!marked && grown) unmarkedGrown++;
                    }
                }

                // housing
                var dwelling = ec.GetComponent<Dwelling>();
                if (dwelling != null)
                {
                    occupiedBeds += dwelling.NumberOfDwellers;
                    totalBeds += dwelling.MaxBeavers;
                }

                // employment + unstaffed alert
                var wp = ec.GetComponent<Timberborn.WorkSystem.Workplace>();
                if (wp != null)
                {
                    assignedWorkers += wp.AssignedWorkers.Count;
                    totalVacancies += wp.DesiredWorkers;
                    if (wp.DesiredWorkers > 0 && wp.AssignedWorkers.Count < wp.DesiredWorkers)
                        alertUnstaffed++;
                }

                // wellbeing + critical needs
                var wb = ec.GetComponent<WellbeingTracker>();
                if (wb != null)
                {
                    totalWellbeing += wb.Wellbeing;
                    beaverCount++;
                    if (wb.Wellbeing < 4) miserable++;
                    try
                    {
                        var needMgr = ec.GetComponent<NeedManager>();
                        if (needMgr != null)
                        {
                            foreach (var needSpec in needMgr.GetNeeds())
                            {
                                var need = needMgr.GetNeed(needSpec.Id);
                                if (need.IsActive && need.IsCritical) { critical++; break; }
                            }
                        }
                    }
                    catch { }
                }

                // power alert
                var mech = ec.GetComponent<MechanicalNode>();
                if (mech != null && mech.IsConsumer && !mech.Active)
                    alertUnpowered++;

                // reachability alert
                var reach = ec.GetComponent<EntityReachabilityStatus>();
                if (reach != null && reach.IsAnyUnreachable())
                    alertUnreachable++;
            }
            int homeless = System.Math.Max(0, beaverCount - occupiedBeds);
            int unemployed = System.Math.Max(0, beaverCount - assignedWorkers);
            float avgWellbeing = beaverCount > 0 ? totalWellbeing / beaverCount : 0;

            return new
            {
                time = CollectTime(),
                weather = CollectWeather(),
                districts = CollectDistricts(),
                trees = new { markedGrown, markedSeedling, unmarkedGrown },
                housing = new { occupiedBeds, totalBeds, homeless },
                employment = new { assigned = assignedWorkers, vacancies = totalVacancies, unemployed },
                wellbeing = new { average = System.Math.Round(avgWellbeing, 1), miserable, critical },
                science = _scienceService.SciencePoints,
                alerts = new { unstaffed = alertUnstaffed, unpowered = alertUnpowered, unreachable = alertUnreachable }
            };
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

        public object CollectDistricts()
        {
            var goods = _goodService.Goods;
            var results = new List<object>();

            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var counter = dc.GetComponent<DistrictResourceCounter>();
                var pop = dc.DistrictPopulation;

                var resources = new Dictionary<string, object>();
                if (counter != null)
                {
                    foreach (var goodId in goods)
                    {
                        var rc = counter.GetResourceCount(goodId);
                        if (rc.AllStock > 0)
                        {
                            resources[goodId] = new
                            {
                                available = rc.AvailableStock,
                                all = rc.AllStock
                            };
                        }
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

            return results;
        }

        public object CollectResources()
        {
            var goods = _goodService.Goods;
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
                    {
                        distResources[goodId] = new
                        {
                            available = rc.AvailableStock,
                            all = rc.AllStock
                        };
                    }
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

        public object CollectBuildings()
        {
            var results = new List<object>();
            foreach (var ec in _entityRegistry.Entities)
            {
                var building = ec.GetComponent<Building>();
                if (building == null) continue;

                var go = ec.GameObject;
                var bo = ec.GetComponent<BlockObject>();
                var pausable = ec.GetComponent<PausableBuilding>();
                var floodgate = ec.GetComponent<Floodgate>();
                var prio = ec.GetComponent<BuilderPrioritizable>();
                var workplace = ec.GetComponent<Workplace>();

                var entry = new Dictionary<string, object>
                {
                    ["id"] = go.GetInstanceID(),
                    ["name"] = go.name
                };

                if (bo != null)
                {
                    entry["finished"] = bo.IsFinished;
                    var coords = bo.Coordinates;
                    entry["x"] = coords.x;
                    entry["y"] = coords.y;
                    entry["z"] = coords.z;
                    entry["orientation"] = (int)bo.Orientation;

                    if (bo.HasEntrance)
                    {
                        var entrance = bo.PositionedEntrance;
                        entry["entranceX"] = entrance.DoorstepCoordinates.x;
                        entry["entranceY"] = entrance.DoorstepCoordinates.y;
                        entry["entranceZ"] = entrance.DoorstepCoordinates.z;
                    }
                }

                if (pausable != null)
                {
                    entry["pausable"] = true;
                    entry["paused"] = pausable.Paused;
                }

                if (floodgate != null)
                {
                    entry["floodgate"] = true;
                    entry["height"] = floodgate.Height;
                    entry["maxHeight"] = floodgate.MaxHeight;
                }

                if (prio != null)
                    entry["constructionPriority"] = prio.Priority.ToString();

                var wpPrio = ec.GetComponent<WorkplacePriority>();
                if (wpPrio != null)
                    entry["workplacePriority"] = wpPrio.Priority.ToString();

                if (workplace != null)
                {
                    entry["maxWorkers"] = workplace.MaxWorkers;
                    entry["desiredWorkers"] = workplace.DesiredWorkers;
                    entry["assignedWorkers"] = workplace.NumberOfAssignedWorkers;
                }

                var reachability = ec.GetComponent<EntityReachabilityStatus>();
                if (reachability != null)
                    entry["reachable"] = !reachability.IsAnyUnreachable();

                var mechanical = ec.GetComponent<MechanicalBuilding>();
                if (mechanical != null)
                    entry["powered"] = mechanical.ActiveAndPowered;

                var statusSubject = ec.GetComponent<StatusSubject>();
                if (statusSubject != null)
                {
                    var statuses = new List<string>();
                    try
                    {
                        foreach (var s in statusSubject.ActiveStatuses)
                            statuses.Add(s.StatusDescription);
                    }
                    catch { }
                    if (statuses.Count > 0)
                        entry["statuses"] = statuses;
                }

                var node = ec.GetComponent<MechanicalNode>();
                if (node != null)
                {
                    entry["isGenerator"] = node.IsGenerator;
                    entry["isConsumer"] = node.IsConsumer;
                    entry["nominalPowerInput"] = node._nominalPowerInput;
                    entry["nominalPowerOutput"] = node._nominalPowerOutput;
                    try
                    {
                        var graph = node.Graph;
                        if (graph != null)
                        {
                            entry["powerDemand"] = graph.PowerDemand;
                            entry["powerSupply"] = graph.PowerSupply;
                        }
                    }
                    catch { }
                }

                // construction progress
                var site = ec.GetComponent<ConstructionSite>();
                if (site != null)
                {
                    entry["buildProgress"] = site.BuildTimeProgress;
                    entry["materialProgress"] = site.MaterialProgress;
                    entry["hasMaterials"] = site.HasMaterialsToResumeBuilding;
                }

                // inventory contents
                var inventories = ec.GetComponent<Inventories>();
                if (inventories != null)
                {
                    var goods = new Dictionary<string, int>();
                    try
                    {
                        foreach (var inv in inventories.AllInventories)
                        {
                            if (inv.ComponentName == ConstructionSiteInventoryInitializer.InventoryComponentName) continue;
                            foreach (var ga in inv.Stock)
                            {
                                if (ga.Amount > 0)
                                {
                                    var gid = ga.GoodId;
                                    if (goods.ContainsKey(gid))
                                        goods[gid] += ga.Amount;
                                    else
                                        goods[gid] = ga.Amount;
                                }
                            }
                        }
                    }
                    catch { }
                    if (goods.Count > 0)
                        entry["inventory"] = goods;
                }

                var wonder = ec.GetComponent<Wonder>();
                if (wonder != null)
                {
                    entry["isWonder"] = true;
                    entry["wonderActive"] = wonder.IsActive;
                }

                var dwelling = ec.GetComponent<Dwelling>();
                if (dwelling != null)
                {
                    entry["dwellers"] = dwelling.NumberOfDwellers;
                    entry["maxDwellers"] = dwelling.MaxBeavers;
                }

                var clutch = ec.GetComponent<Clutch>();
                if (clutch != null)
                {
                    entry["isClutch"] = true;
                    entry["clutchEngaged"] = clutch.IsEngaged;
                }

                results.Add(entry);
            }
            return results;
        }

        private List<object> CollectNaturalResources<T>(System.Action<EntityComponent, Dictionary<string, object>> enrich = null) where T : class
        {
            var results = new List<object>();
            foreach (var ec in _entityRegistry.Entities)
            {
                if (ec.GetComponent<T>() == null) continue;

                var go = ec.GameObject;
                var bo = ec.GetComponent<BlockObject>();
                var living = ec.GetComponent<LivingNaturalResource>();

                var entry = new Dictionary<string, object>
                {
                    ["id"] = go.GetInstanceID(),
                    ["name"] = go.name
                };

                if (bo != null)
                {
                    var coords = bo.Coordinates;
                    entry["x"] = coords.x;
                    entry["y"] = coords.y;
                    entry["z"] = coords.z;
                }

                if (living != null)
                    entry["alive"] = !living.IsDead;

                enrich?.Invoke(ec, entry);
                results.Add(entry);
            }
            return results;
        }

        public object CollectTrees()
        {
            return CollectNaturalResources<Cuttable>((ec, entry) =>
            {
                var bo = ec.GetComponent<BlockObject>();
                if (bo != null)
                    entry["marked"] = _treeCuttingArea.IsInCuttingArea(bo.Coordinates);
                var growable = ec.GetComponent<Timberborn.Growing.Growable>();
                if (growable != null)
                {
                    entry["grown"] = growable.IsGrown;
                    entry["growth"] = growable.GrowthProgress;
                }
            });
        }

        public object CollectGatherables()
        {
            return CollectNaturalResources<Gatherable>();
        }

        public object CollectBeavers()
        {
            var results = new List<object>();
            foreach (var ec in _entityRegistry.Entities)
            {
                var needMgr = ec.GetComponent<NeedManager>();
                if (needMgr == null) continue;

                var go = ec.GameObject;
                var entry = new Dictionary<string, object>
                {
                    ["id"] = go.GetInstanceID(),
                    ["name"] = go.name
                };

                // overall wellbeing
                var tracker = ec.GetComponent<WellbeingTracker>();
                if (tracker != null)
                    entry["wellbeing"] = tracker.Wellbeing;

                // per-need breakdown using NeedManager internals
                var needs = new Dictionary<string, object>();
                bool anyCritical = false;
                try
                {
                    foreach (var needSpec in needMgr.GetNeeds())
                    {
                        var id = needSpec.Id;
                        var need = needMgr.GetNeed(id);
                        if (!need.IsActive) continue;
                        needs[id] = new
                        {
                            points = need.Points,
                            isCritical = need.IsCritical,
                            isBelowWarning = need.IsBelowWarningThreshold
                        };
                        if (need.IsCritical) anyCritical = true;
                    }
                }
                catch { }
                entry["needs"] = needs;
                entry["anyCritical"] = anyCritical;

                // life progress
                var life = ec.GetComponent<LifeProgressor>();
                if (life != null)
                    entry["lifeProgress"] = life.LifeProgress;

                var worker = ec.GetComponent<Worker>();
                if (worker != null && worker.Workplace != null)
                    entry["workplace"] = worker.Workplace.GameObject.name;

                var bot = ec.GetComponent<Bot>();
                entry["isBot"] = bot != null;

                var contaminable = ec.GetComponent<Contaminable>();
                if (contaminable != null)
                    entry["contaminated"] = contaminable.IsContaminated;

                var dweller = ec.GetComponent<Dweller>();
                if (dweller != null)
                    entry["hasHome"] = dweller.HasHome;

                results.Add(entry);
            }
            return results;
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

        public object SetWorkHours(int endHours)
        {
            if (endHours < 1 || endHours > 24)
                return new { error = "endHours must be 1-24" };
            _workingHoursManager.EndHours = endHours;
            return new { endHours = _workingHoursManager.EndHours };
        }

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

            // build occupancy map from all entities -- use ALL occupied blocks, not just origin
            var occupants = new Dictionary<long, string>();
            var entrances = new HashSet<long>();
            var seedlings = new HashSet<long>();
            var deadTiles = new HashSet<long>();
            foreach (var ec in _entityRegistry.Entities)
            {
                var bo = ec.GetComponent<BlockObject>();
                if (bo == null) continue;
                var name = ec.GameObject.name;

                // track seedlings vs grown trees
                var growable = ec.GetComponent<Timberborn.Growing.Growable>();
                if (growable != null && !growable.IsGrown)
                {
                    var c = bo.Coordinates;
                    seedlings.Add((long)c.x * 100000 + c.y);
                }

                // track dead trees/plants (stumps -- buildable)
                var lnr = ec.GetComponent<LivingNaturalResource>();
                if (lnr != null && lnr.IsDead)
                {
                    var c = bo.Coordinates;
                    deadTiles.Add((long)c.x * 100000 + c.y);
                }

                // record entrance tile
                if (bo.HasEntrance)
                {
                    try
                    {
                        var ent = bo.PositionedEntrance.DoorstepCoordinates;
                        entrances.Add((long)ent.x * 100000 + ent.y);
                    }
                    catch { }
                }
                try
                {
                    var positioned = bo.PositionedBlocks;
                    foreach (var block in positioned.GetAllBlocks())
                    {
                        var c = block.Coordinates;
                        if (c.x >= x1 && c.x <= x2 && c.y >= y1 && c.y <= y2)
                        {
                            long key = (long)c.x * 100000 + c.y;
                            occupants[key] = name;
                        }
                    }
                }
                catch
                {
                    // fallback to origin coordinate
                    var c = bo.Coordinates;
                    if (c.x >= x1 && c.x <= x2 && c.y >= y1 && c.y <= y2)
                    {
                        long key = (long)c.x * 100000 + c.y;
                        occupants[key] = name;
                    }
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
                    occupants.TryGetValue(key, out var occupant);
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
                    if (occupant != null) tile["occupant"] = occupant;
                    if (isEntrance) tile["entrance"] = true;
                    if (isSeedling) tile["seedling"] = true;
                    if (deadTiles.Contains(key)) tile["dead"] = true;
                    try
                    {
                        if (_soilContaminationService.SoilIsContaminated(new Vector3Int(x, y, terrainHeight)))
                            tile["contaminated"] = true;
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

        public object SetSpeed(int speed)
        {
            if (speed < 0 || speed > 3)
                return new { error = "speed must be 0-3 (0=pause, 1=normal, 2=fast, 3=fastest)" };

            _speedManager.ChangeSpeed(SpeedScale[speed]);
            return new { speed };
        }

        public object PauseBuilding(int buildingId, bool paused)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var pausable = ec.GetComponent<PausableBuilding>();
            if (pausable == null)
                return new { error = "building is not pausable", id = buildingId };

            pausable.Paused = paused;
            return new { id = buildingId, name = ec.GameObject.name, paused = pausable.Paused };
        }

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
                name = ec.GameObject.name,
                height = floodgate.Height,
                maxHeight = floodgate.MaxHeight
            };
        }

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
                    return new { id = buildingId, name = ec.GameObject.name, constructionPriority = prio.Priority.ToString() };
                }
            }

            if (type == "workplace" || string.IsNullOrEmpty(type))
            {
                var wpPrio = ec.GetComponent<WorkplacePriority>();
                if (wpPrio != null)
                {
                    wpPrio.SetPriority(parsed);
                    return new { id = buildingId, name = ec.GameObject.name, workplacePriority = wpPrio.Priority.ToString() };
                }
            }

            return new { error = "building has no priority of that type", id = buildingId, type };
        }

        public object SetHaulPriority(int buildingId, bool prioritized)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var hp = ec.GetComponent<HaulPrioritizable>();
            if (hp == null)
                return new { error = "building has no haul priority", id = buildingId };

            hp.Prioritized = prioritized;
            return new { id = buildingId, name = ec.GameObject.name, haulPrioritized = hp.Prioritized };
        }

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
                return new { id = buildingId, name = ec.GameObject.name, recipe = "none" };
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
            return new { id = buildingId, name = ec.GameObject.name, recipe = recipe.Id };
        }

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
                return new { id = buildingId, name = ec.GameObject.name, action = "planting" };
            }
            else if (action == "harvesting" || action == "none")
            {
                farmhouse.UnprioritizePlanting();
                return new { id = buildingId, name = ec.GameObject.name, action = "default" };
            }

            return new { error = "invalid action, use: planting or harvesting", action };
        }

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
                return new { id = buildingId, name = ec.GameObject.name, prioritized = "none" };
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
            return new { id = buildingId, name = ec.GameObject.name, prioritized = match.TemplateName };
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 2
        // ================================================================

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
                name = ec.GameObject.name,
                desiredWorkers = workplace.DesiredWorkers,
                maxWorkers = workplace.MaxWorkers,
                assignedWorkers = workplace.NumberOfAssignedWorkers
            };
        }

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
                name = ec.GameObject.name,
                capacity
            };
        }

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
                name = ec.GameObject.name,
                good = sga.AllowedGood
            };
        }

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

            var occupied = GetOccupiedTiles();
            int planted = 0, skipped = 0;
            foreach (var c in coords)
            {
                long key = (long)c.x * 1000000 + (long)c.y * 1000 + c.z;
                if (occupied.Contains(key))
                {
                    skipped++;
                    continue;
                }
                int th = GetTerrainHeight(c.x, c.y);
                if (th == 0 || th < c.z)
                {
                    skipped++;
                    continue;
                }
                float wh = 0f;
                try { wh = _waterMap.CeiledWaterHeight(new Vector3Int(c.x, c.y, th)); }
                catch { }
                if (wh > 0)
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

        public object UnlockBuilding(string buildingName)
        {
            try
            {
                // find the tool button by template name and unlock via its BuildingSpec
                foreach (var toolButton in _toolButtonService.ToolButtons)
                {
                    var blockObjectTool = toolButton.Tool as BlockObjectTool;
                    if (blockObjectTool == null) continue;
                    var toolBuilding = blockObjectTool.Template.GetSpec<BuildingSpec>();
                    if (toolBuilding == null) continue;
                    var templateSpec = blockObjectTool.Template.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                    if (templateSpec != null && templateSpec.TemplateName == buildingName)
                    {
                        _buildingUnlockingService.Unlock(toolBuilding);
                        _unlockedPlantableGroupsRegistry.AddUnlockedPlantableGroups(toolBuilding);
                        // clear the tool lock via reflection if the property exists
                        var lockerProp = blockObjectTool.GetType().GetProperty("Locker",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (lockerProp != null && lockerProp.CanWrite)
                            lockerProp.SetValue(blockObjectTool, null);
                        toolButton.OnToolUnlocked(new ToolUnlockedEvent(toolButton.Tool));
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

        // PLACEMENT VALIDATION
        // ================================================================

        private HashSet<long> GetOccupiedTiles()
        {
            var occupied = new HashSet<long>();
            foreach (var ec in _entityRegistry.Entities)
            {
                var bo = ec.GetComponent<BlockObject>();
                if (bo == null) continue;
                // skip temporary debris from demolished buildings
                var name = ec.GameObject.name;
                if (name.Contains("RecoveredGoodStack") || name.Contains("GoodStack")) continue;
                // skip dead trees/plants -- game allows building on them
                var living = ec.GetComponent<LivingNaturalResource>();
                if (living != null && living.IsDead) continue;
                try
                {
                    foreach (var block in bo.PositionedBlocks.GetAllBlocks())
                    {
                        var c = block.Coordinates;
                        occupied.Add((long)c.x * 1000000 + (long)c.y * 1000 + c.z);
                    }
                }
                catch
                {
                    var c = bo.Coordinates;
                    occupied.Add((long)c.x * 1000000 + (long)c.y * 1000 + c.z);
                }
            }
            return occupied;
        }

        private struct FootprintTile
        {
            public Vector3Int coords;
            public bool isGroundFloor;
        }

        private List<FootprintTile> ComputeFootprint(BlockObjectSpec spec, int x, int y, int z, int orientation)
        {
            var size = spec.Size;
            var tiles = new List<FootprintTile>();
            for (int lx = 0; lx < size.x; lx++)
            {
                for (int ly = 0; ly < size.y; ly++)
                {
                    for (int lz = 0; lz < size.z; lz++)
                    {
                        // rotate local (lx, ly) by orientation
                        int rx, ry;
                        switch (orientation)
                        {
                            case 1: // Cw90
                                rx = ly; ry = -lx;
                                break;
                            case 2: // Cw180
                                rx = -lx; ry = -ly;
                                break;
                            case 3: // Cw270
                                rx = -ly; ry = lx;
                                break;
                            default: // Cw0
                                rx = lx; ry = ly;
                                break;
                        }
                        tiles.Add(new FootprintTile
                        {
                            coords = new Vector3Int(x + rx, y + ry, z + lz),
                            isGroundFloor = lz == 0
                        });
                    }
                }
            }
            return tiles;
        }

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

                results.Add(entry);
            }
            return results;
        }

        public object DemolishBuilding(int buildingId)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "entity not found", id = buildingId };

            var name = ec.GameObject.name;
            _entityService.Delete(ec);
            return new { id = buildingId, name, demolished = true };
        }

        private static readonly HashSet<string> WaterBuildingNames = new HashSet<string>
        {
            "Pump", "Floodgate", "Dam", "Levee", "Sluice", "WaterWheel"
        };

        public object PlaceBuilding(string prefabName, int x, int y, int z, int orientation)
        {
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

            // pre-validate: check all tiles the building would occupy
            var footprint = ComputeFootprint(blockObjectSpec, gx, gy, z, orientation);
            var occupied = GetOccupiedTiles();
            bool isWaterBuilding = false;
            foreach (var w in WaterBuildingNames)
            {
                if (prefabName.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    isWaterBuilding = true;
                    break;
                }
            }

            foreach (var ft in footprint)
            {
                var tile = ft.coords;

                // only check terrain/water for ground floor tiles
                if (ft.isGroundFloor)
                {
                    int terrainHeight = GetTerrainHeight(tile.x, tile.y);

                    // check water
                    if (!isWaterBuilding)
                    {
                        float waterHeight = 0f;
                        try { waterHeight = _waterMap.CeiledWaterHeight(new Vector3Int(tile.x, tile.y, terrainHeight)); }
                        catch { }
                        if (waterHeight > 0)
                            return new { error = $"tile ({tile.x},{tile.y}) is water", prefab = prefabName, x, y, z, orientation };
                    }

                    // check terrain supports the building
                    if (terrainHeight == 0 && !isWaterBuilding)
                        return new { error = $"no terrain at ({tile.x},{tile.y})", prefab = prefabName, x, y, z, orientation };

                    if (terrainHeight < tile.z && !isWaterBuilding)
                        return new { error = $"terrain too low at ({tile.x},{tile.y}): height {terrainHeight} < {tile.z}", prefab = prefabName, x, y, z, orientation };

                    if (terrainHeight > tile.z && !isWaterBuilding)
                        return new { error = $"terrain too high at ({tile.x},{tile.y}): height {terrainHeight} > {tile.z} (building would clip underground)", prefab = prefabName, x, y, z, orientation };
                }

                // check occupancy (all floors)
                long key = (long)tile.x * 1000000 + (long)tile.y * 1000 + tile.z;
                if (occupied.Contains(key))
                    return new { error = $"tile ({tile.x},{tile.y},{tile.z}) already occupied", prefab = prefabName, x, y, z, orientation };
            }

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
                placedName = entity.GameObject.name;
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

            return new { id = placedId, name = placedName, x, y, z, orientation };
        }
    }
}
