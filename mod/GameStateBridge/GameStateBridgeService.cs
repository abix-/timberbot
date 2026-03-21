using System.Collections.Generic;
using Timberborn.GameCycleSystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.ResourceCountingSystem;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.WeatherSystem;
using UnityEngine;

namespace GameStateBridge
{
    public class GameStateBridgeService : ILoadableSingleton, IUpdatableSingleton
    {
        private readonly IGoodService _goodService;
        private readonly DistrictCenterRegistry _districtCenterRegistry;
        private readonly GameCycleService _gameCycleService;
        private readonly WeatherService _weatherService;
        private readonly IDayNightCycle _dayNightCycle;
        private GameStateHttpServer _server;

        public GameStateBridgeService(
            IGoodService goodService,
            DistrictCenterRegistry districtCenterRegistry,
            GameCycleService gameCycleService,
            WeatherService weatherService,
            IDayNightCycle dayNightCycle)
        {
            _goodService = goodService;
            _districtCenterRegistry = districtCenterRegistry;
            _gameCycleService = gameCycleService;
            _weatherService = weatherService;
            _dayNightCycle = dayNightCycle;
        }

        public void Load()
        {
            _server = new GameStateHttpServer(8085, this);
            Debug.Log("[GameStateBridge] HTTP server started on port 8085");
        }

        public void UpdateSingleton()
        {
            _server?.DrainRequests();
        }

        // -- data collection (called on main thread) --

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
    }
}
