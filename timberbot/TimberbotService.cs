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
            IThreadSafeColumnTerrainMap terrainMap)
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

        private EntityComponent FindEntity(int id)
        {
            foreach (var ec in _entityRegistry.Entities)
            {
                if (ec.GameObject.GetInstanceID() == id)
                    return ec;
            }
            return null;
        }

        // ================================================================
        // READ ENDPOINTS
        // ================================================================

        public object CollectSummary()
        {
            return new
            {
                time = CollectTime(),
                weather = CollectWeather(),
                districts = CollectDistricts()
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
                {
                    entry["priority"] = prio.Priority.ToString();
                }

                if (workplace != null)
                {
                    entry["maxWorkers"] = workplace.MaxWorkers;
                    entry["desiredWorkers"] = workplace.DesiredWorkers;
                    entry["assignedWorkers"] = workplace.NumberOfAssignedWorkers;
                }

                results.Add(entry);
            }
            return results;
        }

        public object CollectTrees()
        {
            var results = new List<object>();
            foreach (var ec in _entityRegistry.Entities)
            {
                var cuttable = ec.GetComponent<Cuttable>();
                if (cuttable == null) continue;

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
                    entry["marked"] = _treeCuttingArea.IsInCuttingArea(coords);
                }

                if (living != null)
                {
                    entry["alive"] = !living.IsDead;
                }

                results.Add(entry);
            }
            return results;
        }

        public object CollectGatherables()
        {
            var results = new List<object>();
            foreach (var ec in _entityRegistry.Entities)
            {
                var gatherable = ec.GetComponent<Gatherable>();
                if (gatherable == null) continue;

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
                {
                    entry["alive"] = !living.IsDead;
                }

                results.Add(entry);
            }
            return results;
        }

        public object CollectSpeed()
        {
            return new { speed = _speedManager.CurrentSpeed };
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
            foreach (var ec in _entityRegistry.Entities)
            {
                var bo = ec.GetComponent<BlockObject>();
                if (bo == null) continue;
                var name = ec.GameObject.name;

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
                    try
                    {
                        waterHeight = _waterMap.CeiledWaterHeight(new Vector3Int(x, y, terrainHeight));
                    }
                    catch { }

                    long key = (long)x * 100000 + y;
                    occupants.TryGetValue(key, out var occupant);
                    bool isEntrance = entrances.Contains(key);

                    if (occupant != null)
                    {
                        if (isEntrance)
                            tiles.Add(new { x, y, terrain = terrainHeight, water = waterHeight, occupant, entrance = true });
                        else
                            tiles.Add(new { x, y, terrain = terrainHeight, water = waterHeight, occupant });
                    }
                    else if (isEntrance)
                    {
                        tiles.Add(new { x, y, terrain = terrainHeight, water = waterHeight, entrance = true });
                    }
                    else
                    {
                        tiles.Add(new { x, y, terrain = terrainHeight, water = waterHeight });
                    }
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

        public object SetSpeed(int speed)
        {
            if (speed < 0 || speed > 3)
                return new { error = "speed must be 0-3" };

            var previous = _speedManager.CurrentSpeed;
            _speedManager.ChangeSpeed(speed);
            return new { speed = _speedManager.CurrentSpeed, previous };
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

        public object SetBuildingPriority(int buildingId, string priorityStr)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var prio = ec.GetComponent<BuilderPrioritizable>();
            if (prio == null)
                return new { error = "building has no priority", id = buildingId };

            if (!Enum.TryParse<Priority>(priorityStr, true, out var parsed))
                return new { error = "invalid priority, use: VeryLow, Normal, VeryHigh", value = priorityStr };

            prio.SetPriority(parsed);
            return new { id = buildingId, name = ec.GameObject.name, priority = prio.Priority.ToString() };
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

            foreach (var c in coords)
            {
                _plantingService.SetPlantingCoordinates(c, crop);
            }

            return new
            {
                x1 = minX, y1 = minY, x2 = maxX, y2 = maxY, z,
                crop,
                tiles = coords.Count
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

        public object PlaceBuilding(string prefabName, int x, int y, int z, int orientation)
        {
            var buildingSpec = _buildingService.GetBuildingTemplate(prefabName);
            if (buildingSpec == null)
                return new { error = "unknown prefab", prefab = prefabName };

            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            if (blockObjectSpec == null)
                return new { error = "no block object spec", prefab = prefabName };

            var orient = (Timberborn.Coordinates.Orientation)orientation;
            var placement = new Placement(new Vector3Int(x, y, z), orient,
                FlipMode.Unflipped);

            // Place() is the ground truth -- callback fires only for valid placements
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
                var size = blockObjectSpec.Size;
                return new
                {
                    error = "invalid placement",
                    prefab = prefabName,
                    x, y, z, orientation,
                    sizeX = size.x, sizeY = size.y, sizeZ = size.z,
                    hint = "check terrain height, water, existing buildings, and building size"
                };
            }

            return new { id = placedId, name = placedName, x, y, z, orientation };
        }
    }
}
