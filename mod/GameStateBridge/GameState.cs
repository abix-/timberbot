using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.ResourceCountingSystem;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.WeatherSystem;

namespace GameStateBridge
{
    /// <summary>
    /// Static holder for captured game service instances.
    /// Services are populated by Harmony patches in CaptureServices.cs.
    /// </summary>
    static class GameState
    {
        internal static DayNightCycle DayNightCycle;
        internal static WeatherService WeatherService;
        internal static GoodService GoodService;
        internal static ResourceCountingService ResourceCountingService;
        internal static EventBus EventBus;

        // District centers tracked via entity listener
        private static readonly HashSet<DistrictCenter> _districtCenters = new HashSet<DistrictCenter>();

        internal static IEnumerable<DistrictCenter> DistrictCenters => _districtCenters;

        internal static void AddDistrict(DistrictCenter dc)
        {
            _districtCenters.Add(dc);
            Plugin.Log.LogDebug($"District added: {dc.DistrictName}");
        }

        internal static void RemoveDistrict(DistrictCenter dc)
        {
            _districtCenters.Remove(dc);
            Plugin.Log.LogDebug($"District removed: {dc.DistrictName}");
        }

        internal static bool IsReady =>
            DayNightCycle != null &&
            WeatherService != null &&
            GoodService != null &&
            ResourceCountingService != null;

        internal static void Reset()
        {
            DayNightCycle = null;
            WeatherService = null;
            GoodService = null;
            ResourceCountingService = null;
            EventBus = null;
            _districtCenters.Clear();
        }

        // -- data collection (called on main thread) --

        internal static object CollectSummary()
        {
            if (!IsReady) return new { error = "game not ready" };

            var time = CollectTime();
            var weather = CollectWeather();
            var districts = CollectDistricts();

            return new
            {
                time,
                weather,
                districts
            };
        }

        internal static object CollectTime()
        {
            if (DayNightCycle == null) return new { error = "not ready" };

            return new
            {
                dayNumber = DayNightCycle.DayNumber,
                dayProgress = DayNightCycle.DayProgress,
                partialDayNumber = DayNightCycle.PartialDayNumber
            };
        }

        internal static object CollectWeather()
        {
            if (WeatherService == null) return new { error = "not ready" };

            return new
            {
                cycle = WeatherService.Cycle,
                cycleDay = WeatherService.CycleDay
            };
        }

        internal static object CollectDistricts()
        {
            if (!IsReady) return new { error = "not ready" };

            var results = new List<object>();

            // Save current district context so we can restore it
            var prevDC = GetPrivateField<ResourceCountingService, DistrictCenter>(
                ResourceCountingService, "_districtCenter");

            var goodSpecs = GoodService.GetGoodSpecifications().ToList();

            foreach (var dc in _districtCenters)
            {
                ResourceCountingService.SwitchDistrict(dc);

                var resources = new Dictionary<string, object>();
                foreach (var spec in goodSpecs)
                {
                    var amount = ResourceCountingService.GetDistrictAmount(spec);
                    // GetDistrictAmount returns an int (stock count)
                    resources[spec.Id] = amount;
                }

                var pop = dc.DistrictPopulation;

                results.Add(new
                {
                    name = dc.DistrictName,
                    population = new
                    {
                        adults = pop.NumberOfAdults,
                        children = pop.NumberOfChildren
                    },
                    resources
                });
            }

            // Restore original district context
            if (prevDC != null)
                ResourceCountingService.SwitchDistrict(prevDC);

            return results;
        }

        internal static object CollectResources()
        {
            if (!IsReady) return new { error = "not ready" };

            var prevDC = GetPrivateField<ResourceCountingService, DistrictCenter>(
                ResourceCountingService, "_districtCenter");

            var goodSpecs = GoodService.GetGoodSpecifications().ToList();
            var results = new Dictionary<string, object>();

            foreach (var dc in _districtCenters)
            {
                ResourceCountingService.SwitchDistrict(dc);

                var goods = new Dictionary<string, object>();
                foreach (var spec in goodSpecs)
                {
                    var amount = ResourceCountingService.GetDistrictAmount(spec);
                    goods[spec.Id] = amount;
                }

                results[dc.DistrictName] = goods;
            }

            if (prevDC != null)
                ResourceCountingService.SwitchDistrict(prevDC);

            return results;
        }

        internal static object CollectPopulation()
        {
            var results = new List<object>();
            foreach (var dc in _districtCenters)
            {
                var pop = dc.DistrictPopulation;
                results.Add(new
                {
                    district = dc.DistrictName,
                    adults = pop.NumberOfAdults,
                    children = pop.NumberOfChildren
                });
            }
            return results;
        }

        // Reflection helper (same as VeVantZeData)
        private static TF GetPrivateField<T, TF>(T instance, string fieldName)
        {
            if (instance == null) return default;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var field = typeof(T).GetField(fieldName, flags);
            if (field == null) return default;
            return (TF)field.GetValue(instance);
        }
    }
}
