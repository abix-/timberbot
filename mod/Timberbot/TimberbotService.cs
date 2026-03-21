using System;
using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.BuilderPrioritySystem;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.GameCycleSystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.PrioritySystem;
using Timberborn.ResourceCountingSystem;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.WaterBuildings;
using Timberborn.WeatherSystem;
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
        private TimberbotHttpServer _server;

        public TimberbotService(
            IGoodService goodService,
            DistrictCenterRegistry districtCenterRegistry,
            GameCycleService gameCycleService,
            WeatherService weatherService,
            IDayNightCycle dayNightCycle,
            SpeedManager speedManager,
            EntityRegistry entityRegistry)
        {
            _goodService = goodService;
            _districtCenterRegistry = districtCenterRegistry;
            _gameCycleService = gameCycleService;
            _weatherService = weatherService;
            _dayNightCycle = dayNightCycle;
            _speedManager = speedManager;
            _entityRegistry = entityRegistry;
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

        // -- helper: find entity's GameObject by instance ID --

        private GameObject FindEntity(int id)
        {
            foreach (var ec in _entityRegistry.Entities)
            {
                if (ec.GameObject.GetInstanceID() == id)
                    return ec.GameObject;
            }
            return null;
        }

        // -- read endpoints (called on main thread) --

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
                var go = ec.GameObject;
                var building = go.GetComponent<Building>();
                if (building == null) continue;

                var bo = go.GetComponent<BlockObject>();
                var pausable = go.GetComponent<PausableBuilding>();
                var floodgate = go.GetComponent<Floodgate>();
                var prio = go.GetComponent<BuilderPrioritizable>();

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

                results.Add(entry);
            }
            return results;
        }

        // -- write endpoints (called on main thread) --

        public object CollectSpeed()
        {
            return new { speed = _speedManager.CurrentSpeed };
        }

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
            var go = FindEntity(buildingId);
            if (go == null)
                return new { error = "building not found", id = buildingId };

            var pausable = go.GetComponent<PausableBuilding>();
            if (pausable == null)
                return new { error = "building is not pausable", id = buildingId };

            pausable.Paused = paused;
            return new { id = buildingId, name = go.name, paused = pausable.Paused };
        }

        public object SetFloodgateHeight(int buildingId, float height)
        {
            var go = FindEntity(buildingId);
            if (go == null)
                return new { error = "building not found", id = buildingId };

            var floodgate = go.GetComponent<Floodgate>();
            if (floodgate == null)
                return new { error = "not a floodgate", id = buildingId };

            var clamped = Mathf.Clamp(height, 0f, floodgate.MaxHeight);
            floodgate.SetHeightAndSynchronize(clamped);
            return new
            {
                id = buildingId,
                name = go.name,
                height = floodgate.Height,
                maxHeight = floodgate.MaxHeight
            };
        }

        public object SetBuildingPriority(int buildingId, string priorityStr)
        {
            var go = FindEntity(buildingId);
            if (go == null)
                return new { error = "building not found", id = buildingId };

            var prio = go.GetComponent<BuilderPrioritizable>();
            if (prio == null)
                return new { error = "building has no priority", id = buildingId };

            if (!Enum.TryParse<Priority>(priorityStr, true, out var parsed))
                return new { error = "invalid priority, use: VeryLow, Normal, VeryHigh", value = priorityStr };

            prio.SetPriority(parsed);
            return new { id = buildingId, name = go.name, priority = prio.Priority.ToString() };
        }
    }
}
