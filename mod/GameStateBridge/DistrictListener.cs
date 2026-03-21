using System.Linq;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;

namespace GameStateBridge
{
    /// <summary>
    /// Tracks DistrictCenter creation/destruction via EntityComponent events.
    /// Registered via Harmony patch on EntityService.
    /// </summary>
    static class DistrictListener
    {
        internal static void OnEntityCreated(EntityComponent ec)
        {
            var dc = ec.RegisteredComponents
                .FirstOrDefault(c => c.GetType() == typeof(DistrictCenter)) as DistrictCenter;
            if (dc != null)
                GameState.AddDistrict(dc);
        }

        internal static void OnEntityDeleted(EntityComponent ec)
        {
            var dc = ec.RegisteredComponents
                .FirstOrDefault(c => c.GetType() == typeof(DistrictCenter)) as DistrictCenter;
            if (dc != null)
                GameState.RemoveDistrict(dc);
        }
    }
}
