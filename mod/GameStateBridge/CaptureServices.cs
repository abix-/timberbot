using HarmonyLib;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.ResourceCountingSystem;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.WeatherSystem;

namespace GameStateBridge
{
    /// <summary>
    /// Harmony patches to capture game service instances as they initialize.
    /// Same pattern as VeVantZeData -- patch PostLoad/Load/Initialize to grab singletons.
    /// </summary>
    static class CaptureServices
    {
        [HarmonyPatch(typeof(DayNightCycle), "Load")]
        public static class CaptureDayNightCycle
        {
            private static void Postfix(DayNightCycle __instance)
            {
                GameState.DayNightCycle = __instance;
                Plugin.Log.LogDebug("Captured DayNightCycle");
            }
        }

        [HarmonyPatch(typeof(WeatherService), "Load")]
        public static class CaptureWeatherService
        {
            private static void Postfix(WeatherService __instance)
            {
                GameState.WeatherService = __instance;
                Plugin.Log.LogDebug("Captured WeatherService");
            }
        }

        [HarmonyPatch(typeof(GoodService), "Initialize")]
        public static class CaptureGoodService
        {
            private static void Postfix(GoodService __instance)
            {
                GameState.GoodService = __instance;
                Plugin.Log.LogDebug("Captured GoodService");
            }
        }

        [HarmonyPatch(typeof(ResourceCountingService), "PostLoad")]
        public static class CaptureResourceCountingService
        {
            private static void Postfix(ResourceCountingService __instance)
            {
                GameState.ResourceCountingService = __instance;
                Plugin.Log.LogDebug("Captured ResourceCountingService");
            }
        }

        [HarmonyPatch(typeof(EventBus), MethodType.Constructor)]
        public static class CaptureEventBus
        {
            private static void Postfix(EventBus __instance)
            {
                GameState.EventBus = __instance;
                Plugin.Log.LogDebug("Captured EventBus");
            }
        }
    }
}
