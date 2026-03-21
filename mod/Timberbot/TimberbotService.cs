using System;
using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.BuilderPrioritySystem;
using Timberborn.Buildings;
using Timberborn.Cutting;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
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
        private TimberbotHttpServer _server;

        public TimberbotService(
            IGoodService goodService,
            DistrictCenterRegistry districtCenterRegistry,
            GameCycleService gameCycleService,
            WeatherService weatherService,
            IDayNightCycle dayNightCycle,
            SpeedManager speedManager,
            EntityRegistry entityRegistry,
            TreeCuttingArea treeCuttingArea)
        {
            _goodService = goodService;
            _districtCenterRegistry = districtCenterRegistry;
            _gameCycleService = gameCycleService;
            _weatherService = weatherService;
            _dayNightCycle = dayNightCycle;
            _speedManager = speedManager;
            _entityRegistry = entityRegistry;
            _treeCuttingArea = treeCuttingArea;
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

        public object CollectSpeed()
        {
            return new { speed = _speedManager.CurrentSpeed };
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

        public object MarkTree(int treeId, bool marked)
        {
            var ec = FindEntity(treeId);
            if (ec == null)
                return new { error = "tree not found", id = treeId };

            var bo = ec.GetComponent<BlockObject>();
            if (bo == null)
                return new { error = "no block object", id = treeId };

            var coords = bo.Coordinates;
            var coordList = new List<Vector3Int> { coords };

            if (marked)
                _treeCuttingArea.AddCoordinates(coordList);
            else
                _treeCuttingArea.RemoveCoordinates(coordList);

            return new
            {
                id = treeId,
                name = ec.GameObject.name,
                marked = _treeCuttingArea.IsInCuttingArea(coords),
                x = coords.x,
                y = coords.y,
                z = coords.z
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
    }
}
