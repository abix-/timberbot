// TimberbotRead.cs -- All read-only API endpoints.
//
// Every GET endpoint is a CollectX() method that reads from the double-buffered
// cache (background thread safe) and returns either a plain object (serialized to
// JSON by TimberbotHttpServer) or a StringBuilder of pre-built TOON output.
//
// These methods never touch game services directly -- they only read from
// _cache.Buildings.Read, _cache.Beavers.Read, _cache.NaturalResources.Read, and the thread-safe
// water/terrain maps.
//
// format param: "toon" = compact tabular (default, token-efficient for AI)
//               "json" = full nested objects (for programmatic access)
// detail param: "basic" = compact fields, "full" = all fields, "id:N" = single entity

using System.Collections.Generic;
using Timberborn.GameCycleSystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.MapIndexSystem;
using Timberborn.ResourceCountingSystem;
using Timberborn.SoilContaminationSystem;
using Timberborn.SoilMoistureSystem;
using Timberborn.TerrainSystem;
using Timberborn.TimeSystem;
using Timberborn.WaterSystem;
using Timberborn.WeatherSystem;
using Timberborn.WorkSystem;
using Timberborn.Buildings;
using Timberborn.NeedSpecs;
using Timberborn.NeedSystem;
using Timberborn.NotificationSystem;
using Timberborn.ScienceSystem;
using Timberborn.GameFactionSystem;
using UnityEngine;

namespace Timberbot
{
    // All GET endpoint handlers. Each method reads from the double-buffered cache
    // (zero main-thread cost) and writes JSON via the shared JwWriter.
    //
    // These run on the background HTTP listener thread. They NEVER call Unity
    // component properties directly -- only cached primitives from TimberbotEntityCache.
    //
    // format: "toon" = compact tabular for AI, "json" = full nested for scripts
    // detail: "basic" = compact, "full" = all fields, "id:N" = single entity
    public class TimberbotRead
    {
        // game services for data that isn't cached (district resources, weather, science)
        private readonly IGoodService _goodService;
        private readonly DistrictCenterRegistry _districtCenterRegistry;
        private readonly GameCycleService _gameCycleService;
        private readonly WeatherService _weatherService;
        private readonly IDayNightCycle _dayNightCycle;
        private readonly SpeedManager _speedManager;
        private readonly ScienceService _scienceService;
        private readonly WorkingHoursManager _workingHoursManager;
        private readonly BuildingService _buildingService;
        private readonly BuildingUnlockingService _buildingUnlockingService;
        private readonly FactionNeedService _factionNeedService;
        private readonly NotificationSaver _notificationSaver;
        private readonly TimberbotEntityCache _cache;
        // terrain/water services for CollectTiles (thread-safe by design)
        private readonly ITerrainService _terrainService;
        private readonly IThreadSafeWaterMap _waterMap;
        private readonly MapIndexService _mapIndexService;
        private readonly IThreadSafeColumnTerrainMap _terrainMap;
        private readonly ISoilContaminationService _soilContaminationService;
        private readonly ISoilMoistureService _soilMoistureService;

        public TimberbotRead(
            IGoodService goodService,
            DistrictCenterRegistry districtCenterRegistry,
            GameCycleService gameCycleService,
            WeatherService weatherService,
            IDayNightCycle dayNightCycle,
            SpeedManager speedManager,
            ScienceService scienceService,
            WorkingHoursManager workingHoursManager,
            TimberbotEntityCache cache,
            ITerrainService terrainService,
            IThreadSafeWaterMap waterMap,
            MapIndexService mapIndexService,
            IThreadSafeColumnTerrainMap terrainMap,
            ISoilContaminationService soilContaminationService,
            ISoilMoistureService soilMoistureService,
            BuildingService buildingService,
            BuildingUnlockingService buildingUnlockingService,
            FactionNeedService factionNeedService,
            NotificationSaver notificationSaver)
        {
            _goodService = goodService;
            _districtCenterRegistry = districtCenterRegistry;
            _gameCycleService = gameCycleService;
            _weatherService = weatherService;
            _dayNightCycle = dayNightCycle;
            _speedManager = speedManager;
            _scienceService = scienceService;
            _workingHoursManager = workingHoursManager;
            _cache = cache;
            _terrainService = terrainService;
            _waterMap = waterMap;
            _mapIndexService = mapIndexService;
            _terrainMap = terrainMap;
            _soilContaminationService = soilContaminationService;
            _soilMoistureService = soilMoistureService;
            _buildingService = buildingService;
            _buildingUnlockingService = buildingUnlockingService;
            _factionNeedService = factionNeedService;
            _notificationSaver = notificationSaver;
        }

        // ================================================================
        // READ ENDPOINTS
        // Each returns an object serialized to JSON. The "format" param controls shape:
        //   toon: flat dicts/lists for tabular TOON display (default for CLI)
        //   json: full nested data for programmatic access (--json flag)
        //
        // Server-side filtering:
        //   name: case-insensitive substring match on entity name
        //   x, y, radius: Manhattan distance proximity filter
        // Filters apply BEFORE pagination (limit/offset).
        // ================================================================

        // Helper: check if an entity passes name and proximity filters.
        // Returns false if the entity should be skipped.
        private static bool PassesFilter(string entityName, int entityX, int entityY,
            string filterName, int filterX, int filterY, int filterRadius)
        {
            if (filterName != null && entityName.IndexOf(filterName, System.StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (filterRadius > 0 && (System.Math.Abs(entityX - filterX) + System.Math.Abs(entityY - filterY)) > filterRadius)
                return false;
            return true;
        }

        // PERF: uses typed indexes instead of scanning all entities.
        // Three passes over subsets (buildings, natural resources, beavers) instead of one pass over everything.
        // The summary endpoint is the most-called endpoint. AI bots call it every turn
        // to get a complete colony snapshot in one request. It aggregates data from all
        // three cached indexes (buildings, natural resources, beavers) plus district data.
        //
        // Everything here reads from cached data -- zero Unity calls, zero main thread cost.
        public object CollectSummary(string format = "toon")
        {
            // --- aggregate counters (built from cached data) ---
            int treeMarkedGrown = 0, treeMarkedSeedling = 0, treeUnmarkedGrown = 0;
            int cropReady = 0, cropGrowing = 0;
            int occupiedBeds = 0, totalBeds = 0;
            int totalVacancies = 0, assignedWorkers = 0;
            float totalWellbeing = 0f;
            int beaverCount = 0;
            int alertUnstaffed = 0, alertUnpowered = 0, alertUnreachable = 0;
            int miserable = 0, critical = 0;

            // --- TREES vs CROPS ---
            // Both are "natural resources" in the game. We split them by name to give
            // separate counts. Trees are tracked by marked/unmarked/grown status (for
            // the lumberjack). Crops by ready/growing (for food planning).
            var _cropNames = new System.Collections.Generic.HashSet<string>
                { "Kohlrabi", "Soybean", "Corn", "Sunflower", "Eggplant", "Algae", "Cassava", "Mushroom", "Potato", "Wheat", "Carrot" };
            foreach (var c in _cache.NaturalResources.Read)
            {
                if (c.Cuttable == null) continue; // skip non-cuttable resources
                if (!c.Alive) continue;            // dead stumps don't count
                if (_cropNames.Contains(c.Name))
                {
                    if (c.Grown) cropReady++;      // harvestable now
                    else cropGrowing++;            // still growing
                }
                else
                {
                    // trees: markedGrown = ready to chop, markedSeedling = marked but too young
                    if (c.Marked && c.Grown) treeMarkedGrown++;
                    else if (c.Marked && !c.Grown) treeMarkedSeedling++;
                    else if (!c.Marked && c.Grown) treeUnmarkedGrown++;
                }
            }

            // --- BUILDINGS ---
            // Aggregate housing, employment, and alert data from cached building state.
            // All fields were snapshotted in RefreshCachedState -- we just sum them here.
            foreach (var c in _cache.Buildings.Read)
            {
                if (c.Dwelling != null) // housing (barracks, rowhouses, lodges)
                {
                    occupiedBeds += c.Dwellers;
                    totalBeds += c.MaxDwellers;
                }
                if (c.Workplace != null) // any building with workers
                {
                    assignedWorkers += c.AssignedWorkers;
                    totalVacancies += c.DesiredWorkers;
                    // unstaffed = wants workers but doesn't have enough
                    if (c.DesiredWorkers > 0 && c.AssignedWorkers < c.DesiredWorkers)
                        alertUnstaffed++;
                }
                // unpowered = consumes power but isn't getting any
                if (c.IsConsumer && !c.Powered)
                    alertUnpowered++;
                if (c.Unreachable)
                    alertUnreachable++;
            }

            // --- BEAVERS ---
            // miserable = wellbeing below 4 (struggling, may die soon)
            // critical = any need below warning threshold (immediate danger)
            foreach (var c in _cache.Beavers.Read)
            {
                totalWellbeing += c.Wellbeing;
                beaverCount++;
                if (c.Wellbeing < 4) miserable++;
                if (c.AnyCritical) critical++;
            }

            // --- DERIVED STATS ---
            int totalAdults = 0;
            foreach (var dc in _cache.Districts)
                totalAdults += dc.Adults;
            // homeless = beavers with no bed (children count, adults count)
            int homeless = System.Math.Max(0, beaverCount - occupiedBeds);
            // unemployed = adults not assigned to any workplace (available for hauling)
            int unemployed = System.Math.Max(0, totalAdults - assignedWorkers);
            float avgWellbeing = beaverCount > 0 ? totalWellbeing / beaverCount : 0;

            if (format == "json")
            {
                // capture districts BEFORE using _cache.Jw -- CollectDistricts uses the same JwWriter
                var districtsJson = CollectDistricts("json") as string;
                var jj = _cache.Jw.BeginObj();
                jj.Obj("time")
                    .Prop("dayNumber", _dayNightCycle.DayNumber)
                    .Prop("dayProgress", (float)_dayNightCycle.DayProgress)
                    .Prop("partialDayNumber", (float)_dayNightCycle.PartialDayNumber)
                    .CloseObj();
                jj.Obj("weather")
                    .Prop("cycle", _gameCycleService.Cycle)
                    .Prop("cycleDay", _gameCycleService.CycleDay)
                    .Prop("isHazardous", _weatherService.IsHazardousWeather)
                    .Prop("temperateWeatherDuration", _weatherService.TemperateWeatherDuration)
                    .Prop("hazardousWeatherDuration", _weatherService.HazardousWeatherDuration)
                    .Prop("cycleLengthInDays", _weatherService.CycleLengthInDays)
                    .CloseObj();
                jj.RawProp("districts", districtsJson);
                jj.Obj("trees")
                    .Prop("markedGrown", treeMarkedGrown)
                    .Prop("markedSeedling", treeMarkedSeedling)
                    .Prop("unmarkedGrown", treeUnmarkedGrown)
                    .CloseObj();
                jj.Obj("crops")
                    .Prop("ready", cropReady)
                    .Prop("growing", cropGrowing)
                    .CloseObj();
                jj.Obj("housing")
                    .Prop("occupiedBeds", occupiedBeds)
                    .Prop("totalBeds", totalBeds)
                    .Prop("homeless", homeless)
                    .CloseObj();
                jj.Obj("employment")
                    .Prop("assigned", assignedWorkers)
                    .Prop("vacancies", totalVacancies)
                    .Prop("unemployed", unemployed)
                    .CloseObj();
                jj.Obj("wellbeing")
                    .Prop("average", (float)avgWellbeing, "F1")
                    .Prop("miserable", miserable)
                    .Prop("critical", critical)
                    .CloseObj();
                jj.Prop("science", _scienceService.SciencePoints);
                jj.Obj("alerts")
                    .Prop("unstaffed", alertUnstaffed)
                    .Prop("unpowered", alertUnpowered)
                    .Prop("unreachable", alertUnreachable)
                    .CloseObj();
                return jj.End();
            }

            // build flat summary matching TOON output format
            var jw = _cache.Jw.BeginObj();

            // time
            jw.Prop("day", _dayNightCycle.DayNumber);
            jw.Prop("dayProgress", (float)_dayNightCycle.DayProgress);

            // weather
            jw.Prop("cycle", _gameCycleService.Cycle);
            jw.Prop("cycleDay", _gameCycleService.CycleDay);
            jw.Prop("isHazardous", _weatherService.IsHazardousWeather);
            jw.Prop("tempDays", _weatherService.TemperateWeatherDuration);
            jw.Prop("hazardDays", _weatherService.HazardousWeatherDuration);

            // trees (actual trees only, not crops)
            jw.Prop("markedGrown", treeMarkedGrown);
            jw.Prop("markedSeedling", treeMarkedSeedling);
            jw.Prop("unmarkedGrown", treeUnmarkedGrown);
            // crops
            jw.Prop("cropReady", cropReady);
            jw.Prop("cropGrowing", cropGrowing);

            // population + resources (from cached district snapshot)
            int totalFood = 0, totalWater = 0, logStock = 0, plankStock = 0, gearStock = 0;
            foreach (var dc in _cache.Districts)
            {
                jw.Prop("adults", dc.Adults);
                jw.Prop("children", dc.Children);
                jw.Prop("bots", dc.Bots);
                if (dc.Resources != null)
                {
                    foreach (var kvp in dc.Resources)
                    {
                        int avail = kvp.Value.available;
                        jw.Key(kvp.Key).Int(avail);
                        if (kvp.Key == "Water") totalWater += avail;
                        else if (kvp.Key == "Berries" || kvp.Key == "Kohlrabi" || kvp.Key == "Carrot" || kvp.Key == "Potato"
                              || kvp.Key == "Wheat" || kvp.Key == "Bread" || kvp.Key == "Cassava" || kvp.Key == "Corn"
                              || kvp.Key == "Eggplant" || kvp.Key == "Soybean" || kvp.Key == "MapleSyrup")
                            totalFood += avail;
                        else if (kvp.Key == "Log") logStock = avail;
                        else if (kvp.Key == "Plank") plankStock = avail;
                        else if (kvp.Key == "Gear") gearStock = avail;
                    }
                }
            }

            // Resource projection: how many days of each resource at current consumption.
            // Beavers eat ~1 food/day and drink ~2 water/day. These projections help
            // the AI decide when to build more farms/pumps/tanks before a drought.
            int totalPop = beaverCount;
            if (totalPop > 0)
            {
                jw.Prop("foodDays", (float)((double)totalFood / totalPop), "F1");
                jw.Prop("waterDays", (float)((double)totalWater / (totalPop * 2.0)), "F1"); // 2x because beavers drink twice/day
                jw.Prop("logDays", (float)((double)logStock / totalPop), "F1");
                jw.Prop("plankDays", (float)((double)plankStock / totalPop), "F1");
                jw.Prop("gearDays", (float)((double)gearStock / totalPop), "F1");
            }

            // housing
            jw.Prop("beds", $"{occupiedBeds}/{totalBeds}");
            jw.Prop("homeless", homeless);

            // employment
            jw.Prop("workers", $"{assignedWorkers}/{totalVacancies}");
            jw.Prop("unemployed", unemployed);

            // wellbeing
            jw.Prop("wellbeing", (float)avgWellbeing, "F1");
            jw.Prop("miserable", miserable);
            jw.Prop("critical", critical);

            // science
            jw.Prop("science", _scienceService.SciencePoints);

            // alerts
            string alertStr = "none";
            if (alertUnstaffed > 0 || alertUnpowered > 0 || alertUnreachable > 0)
            {
                var parts = new List<string>();
                if (alertUnstaffed > 0) parts.Add($"{alertUnstaffed} unstaffed");
                if (alertUnpowered > 0) parts.Add($"{alertUnpowered} unpowered");
                if (alertUnreachable > 0) parts.Add($"{alertUnreachable} unreachable");
                alertStr = string.Join(", ", parts);
            }
            jw.Prop("alerts", alertStr);

            return jw.End();
        }

        // PERF: iterates _cache.Buildings.Read instead of all entities.
        public object CollectAlerts(int limit = 100, int offset = 0)
        {
            bool paginated = limit > 0;
            int skipped = 0, emitted = 0;
            var jw = _cache.Jw.BeginArr();
            foreach (var c in _cache.Buildings.Read)
            {
                if (c.Workplace != null && c.DesiredWorkers > 0 && c.AssignedWorkers < c.DesiredWorkers)
                {
                    if (offset > 0 && skipped < offset) { skipped++; }
                    else if (!paginated || emitted < limit)
                    {
                        jw.OpenObj().Prop("type", "unstaffed").Prop("id", c.Id).Prop("name", c.Name).Prop("workers", $"{c.AssignedWorkers}/{c.DesiredWorkers}").CloseObj();
                        emitted++;
                    }
                }
                if (c.IsConsumer && !c.Powered)
                {
                    if (offset > 0 && skipped < offset) { skipped++; }
                    else if (!paginated || emitted < limit)
                    {
                        jw.OpenObj().Prop("type", "unpowered").Prop("id", c.Id).Prop("name", c.Name).CloseObj();
                        emitted++;
                    }
                }
                if (c.Unreachable)
                {
                    if (offset > 0 && skipped < offset) { skipped++; }
                    else if (!paginated || emitted < limit)
                    {
                        jw.OpenObj().Prop("type", "unreachable").Prop("id", c.Id).Prop("name", c.Name).CloseObj();
                        emitted++;
                    }
                }
            }
            return jw.End();
        }

        // Find the densest tree clusters on the map.
        // Algorithm: divide the map into cellSize x cellSize grid cells (default 10x10).
        // Each tree's (x,y) is snapped to its cell center. Count grown + total trees per cell.
        // Sort by grown count descending, return top N clusters.
        //
        // Used by the AI to decide where to send lumberjacks. A cluster with many grown
        // trees is the best place to mark for cutting (high yield per lumberjack trip).
        public object CollectTreeClusters(int cellSize = 10, int top = 5)
        {
            // key = cell center encoded as long, value = [grownCount, totalCount, centerX, centerY, z]
            var cells = new Dictionary<long, int[]>();
            foreach (var nr in _cache.NaturalResources.Read)
            {
                if (nr.Cuttable == null) continue;       // not a tree/cuttable
                if (nr.Living == null || nr.Living.IsDead) continue;  // dead stump
                if (nr.BlockObject == null) continue;

                var c = nr.BlockObject.Coordinates;
                // snap to grid cell center: (127, 143) with cellSize=10 -> center=(125, 145)
                int cx = c.x / cellSize * cellSize + cellSize / 2;
                int cy = c.y / cellSize * cellSize + cellSize / 2;
                long key = (long)cx * 100000 + cy;

                if (!cells.ContainsKey(key))
                    cells[key] = new int[] { 0, 0, cx, cy, c.z };

                cells[key][1]++;  // total
                if (nr.Growable != null && nr.Growable.IsGrown)
                    cells[key][0]++;  // grown (ready to chop)
            }

            // sort by grown count descending -- densest harvestable clusters first
            var sorted = new List<int[]>(cells.Values);
            sorted.Sort((a, b) => b[0].CompareTo(a[0]));
            var jw = _cache.Jw.BeginArr();
            for (int i = 0; i < System.Math.Min(top, sorted.Count); i++)
            {
                var s = sorted[i];
                jw.OpenObj().Prop("x", s[2]).Prop("y", s[3]).Prop("z", s[4]).Prop("grown", s[0]).Prop("total", s[1]).CloseObj();
            }
            return jw.End();
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
            var jw = _cache.Jw.BeginArr();
            foreach (var dc in _cache.Districts)
            {
                jw.OpenObj().Prop("name", dc.Name);
                if (format == "toon")
                {
                    jw.Prop("adults", dc.Adults).Prop("children", dc.Children).Prop("bots", dc.Bots);
                    if (dc.ResourcesToon != null) jw.Raw(",").Raw(dc.ResourcesToon);
                }
                else
                {
                    jw.Obj("population")
                        .Prop("adults", dc.Adults).Prop("children", dc.Children).Prop("bots", dc.Bots)
                        .CloseObj();
                    jw.Obj("resources");
                    if (dc.ResourcesJson != null) jw.Raw(dc.ResourcesJson);
                    jw.CloseObj();
                }
                jw.CloseObj();
            }
            return jw.End();
        }

        public object CollectResources(string format = "toon")
        {
            var jw = _cache.Jw.Reset();
            if (format == "toon")
            {
                // flat list: [{district, good, available, all}, ...]
                // toon pre-serialized string only has available -- need json string for all
                // fall back to Resources dict for toon flat format
                jw.OpenArr();
                foreach (var dc in _cache.Districts)
                {
                    if (dc.Resources == null) continue;
                    foreach (var kvp in dc.Resources)
                        jw.OpenObj().Prop("district", dc.Name).Prop("good", kvp.Key).Prop("available", kvp.Value.available).Prop("all", kvp.Value.all).CloseObj();
                }
                jw.CloseArr();
            }
            else
            {
                // nested: {"District 1": {"Water": {"available": N, "all": N}}}
                jw.OpenObj();
                foreach (var dc in _cache.Districts)
                {
                    jw.Key(dc.Name).OpenObj();
                    if (dc.ResourcesJson != null) jw.Raw(dc.ResourcesJson);
                    jw.CloseObj();
                }
                jw.CloseObj();
            }
            return jw.ToString();
        }

        public object CollectPopulation()
        {
            var jw = _cache.Jw.BeginArr();
            foreach (var dc in _cache.Districts)
            {
                jw.OpenObj()
                    .Prop("district", dc.Name)
                    .Prop("adults", dc.Adults)
                    .Prop("children", dc.Children)
                    .Prop("bots", dc.Bots)
                    .CloseObj();
            }
            return jw.End();
        }

        // List all buildings. Three modes:
        //   "basic" (default) -- compact: id, name, coords, finished, paused, priority, workers
        //   "full" -- all fields: inventory, recipes, power, floodgate, effect radius, etc
        //   "id:N" -- single building with full detail (for inspecting one building)
        //
        // Uses JwWriter to build JSON directly in a pre-allocated StringBuilder.
        // No Newtonsoft, no Dictionary, no anonymous objects. Just string appends.
        // Server-side pagination: limit=100 default, limit=0 means unlimited.
        // When limit/offset are used, response wraps in {total, offset, limit, items:[...]}.
        // When unlimited (limit=0), returns flat array for backward compatibility.
        public object CollectBuildings(string format = "toon", string detail = "basic", int limit = 100, int offset = 0,
            string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
        {
            // parse "id:-12345" to filter to a single building
            int? singleId = null;
            if (detail != null && detail.StartsWith("id:"))
            {
                if (int.TryParse(detail.Substring(3), out int parsed))
                    singleId = parsed;
            }
            bool fullDetail = detail == "full" || singleId.HasValue;
            bool hasFilter = filterName != null || filterRadius > 0;
            bool paginated = limit > 0 && !singleId.HasValue;
            int total = _cache.Buildings.Read.Count;
            if (paginated && hasFilter)
            {
                total = 0;
                foreach (var b in _cache.Buildings.Read)
                    if (PassesFilter(b.Name, b.X, b.Y, filterName, filterX, filterY, filterRadius)) total++;
            }
            int skipped = 0, emitted = 0;

            var jw = _cache.Jw.Reset();
            if (paginated) jw.OpenObj().Prop("total", total).Prop("offset", offset).Prop("limit", limit).Key("items");
            jw.OpenArr();
            foreach (var c in _cache.Buildings.Read)
            {
                if (singleId.HasValue && c.Id != singleId.Value)
                    continue;
                if (hasFilter && !PassesFilter(c.Name, c.X, c.Y, filterName, filterX, filterY, filterRadius))
                    continue;
                if (offset > 0 && skipped < offset) { skipped++; continue; }
                if (paginated && emitted >= limit) break;
                emitted++;

                // every building gets these base fields
                jw.OpenObj()
                    .Prop("id", c.Id)
                    .Prop("name", c.Name)
                    .Prop("x", c.X).Prop("y", c.Y).Prop("z", c.Z)
                    .Prop("orientation", c.Orientation ?? "")
                    .Prop("finished", c.Finished)
                    .Prop("paused", c.Paused);

                // basic mode: just priority + workers, then close
                if (!fullDetail)
                {
                    jw.Prop("priority", c.ConstructionPriority ?? "")
                        .Prop("workers", c.Workplace != null ? $"{c.AssignedWorkers}/{c.DesiredWorkers}" : "")
                        .CloseObj();
                    continue;
                }

                // full detail: conditional fields (only present if building has that component)
                if (c.Pausable != null) jw.Prop("pausable", true);
                if (c.HasFloodgate) jw.Prop("floodgate", true).Prop("height", c.FloodgateHeight, "F1").Prop("maxHeight", c.FloodgateMaxHeight, "F1");
                if (c.ConstructionPriority != null) jw.Prop("constructionPriority", c.ConstructionPriority);
                if (c.WorkplacePriorityStr != null) jw.Prop("workplacePriority", c.WorkplacePriorityStr);
                if (c.Workplace != null) jw.Prop("maxWorkers", c.MaxWorkers).Prop("desiredWorkers", c.DesiredWorkers).Prop("assignedWorkers", c.AssignedWorkers);
                if (c.Reachability != null) jw.Prop("reachable", !c.Unreachable);
                if (c.Mechanical != null) jw.Prop("powered", c.Powered);
                if (c.PowerNode != null)
                {
                    jw.Prop("isGenerator", c.IsGenerator).Prop("isConsumer", c.IsConsumer)
                        .Prop("nominalPowerInput", c.NominalPowerInput).Prop("nominalPowerOutput", c.NominalPowerOutput);
                    if (c.PowerDemand > 0 || c.PowerSupply > 0) jw.Prop("powerDemand", c.PowerDemand).Prop("powerSupply", c.PowerSupply);
                }
                if (c.Site != null) jw.Prop("buildProgress", c.BuildProgress).Prop("materialProgress", c.MaterialProgress).Prop("hasMaterials", c.HasMaterials);
                if (c.Capacity > 0)
                {
                    jw.Prop("stock", c.Stock).Prop("capacity", c.Capacity);
                    if (c.Inventory != null && c.Inventory.Count > 0)
                    {
                        jw.Obj("inventory");
                        foreach (var kvp in c.Inventory)
                            jw.Key(kvp.Key).Int(kvp.Value);
                        jw.CloseObj();
                    }
                }
                if (c.HasWonder) jw.Prop("isWonder", true).Prop("wonderActive", c.WonderActive);
                if (c.Dwelling != null) jw.Prop("dwellers", c.Dwellers).Prop("maxDwellers", c.MaxDwellers);
                if (c.HasClutch) jw.Prop("isClutch", true).Prop("clutchEngaged", c.ClutchEngaged);
                if (c.Manufactory != null)
                {
                    if (c.Recipes != null && c.Recipes.Count > 0)
                    {
                        jw.Arr("recipes");
                        for (int ri = 0; ri < c.Recipes.Count; ri++)
                            jw.Str(c.Recipes[ri]);
                        jw.CloseArr();
                    }
                    jw.Prop("currentRecipe", c.CurrentRecipe ?? "")
                        .Prop("productionProgress", c.ProductionProgress)
                        .Prop("readyToProduce", c.ReadyToProduce);
                }
                if (c.BreedingPod != null)
                {
                    jw.Prop("needsNutrients", c.NeedsNutrients);
                    if (c.NutrientStock != null && c.NutrientStock.Count > 0)
                    {
                        jw.Obj("nutrients");
                        foreach (var kvp in c.NutrientStock)
                            jw.Key(kvp.Key).Int(kvp.Value);
                        jw.CloseObj();
                    }
                }
                if (c.EffectRadius > 0) jw.Prop("effectRadius", c.EffectRadius);
                jw.CloseObj();
            }
            jw.CloseArr();
            if (paginated) jw.CloseObj();
            return jw.ToString();
        }

        // Shared implementation for trees and crops endpoints.
        // Filters the NaturalResources cache by species name (TreeSpecies or CropSpecies HashSet).
        // Using JwWriter instead of Newtonsoft: ~2ms for 3000 trees vs ~15ms with serialization.
        private object CollectNaturalResourcesJw(TimberbotJw jw, System.Collections.Generic.HashSet<string> species, int limit = 100, int offset = 0,
            string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
        {
            bool paginated = limit > 0;
            bool hasFilter = filterName != null || filterRadius > 0;
            int skipped = 0, emitted = 0;
            // count matching items for total (needed for pagination metadata)
            int total = 0;
            if (paginated)
                foreach (var c in _cache.NaturalResources.Read)
                    if (c.Cuttable != null && species.Contains(c.Name) && (!hasFilter || PassesFilter(c.Name, c.X, c.Y, filterName, filterX, filterY, filterRadius))) total++;

            jw.Reset();
            if (paginated) jw.OpenObj().Prop("total", total).Prop("offset", offset).Prop("limit", limit).Key("items");
            jw.OpenArr();
            foreach (var c in _cache.NaturalResources.Read)
            {
                if (c.Cuttable == null) continue;
                if (!species.Contains(c.Name)) continue;
                if (hasFilter && !PassesFilter(c.Name, c.X, c.Y, filterName, filterX, filterY, filterRadius)) continue;
                if (offset > 0 && skipped < offset) { skipped++; continue; }
                if (paginated && emitted >= limit) break;
                emitted++;
                jw.OpenObj()
                    .Prop("id", c.Id)
                    .Prop("name", c.Name)
                    .Prop("x", c.X).Prop("y", c.Y).Prop("z", c.Z)
                    .Prop("marked", c.Marked)
                    .Prop("alive", c.Alive)
                    .Prop("grown", c.Grown)
                    .Prop("growth", c.Growth)
                    .CloseObj();
            }
            jw.CloseArr();
            if (paginated) jw.CloseObj();
            return jw.ToString();
        }

        public object CollectTrees(int limit = 100, int offset = 0, string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
            => CollectNaturalResourcesJw(_cache.Jw, TimberbotEntityCache.TreeSpecies, limit, offset, filterName, filterX, filterY, filterRadius);
        public object CollectCrops(int limit = 100, int offset = 0, string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
            => CollectNaturalResourcesJw(_cache.Jw, TimberbotEntityCache.CropSpecies, limit, offset, filterName, filterX, filterY, filterRadius);

        public object CollectGatherables(int limit = 100, int offset = 0,
            string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
        {
            bool paginated = limit > 0;
            bool hasFilter = filterName != null || filterRadius > 0;
            int skipped = 0, emitted = 0, total = 0;
            if (paginated)
                foreach (var c in _cache.NaturalResources.Read)
                    if (c.Gatherable != null && (!hasFilter || PassesFilter(c.Name, c.X, c.Y, filterName, filterX, filterY, filterRadius))) total++;

            var jw = _cache.Jw.Reset();
            if (paginated) jw.OpenObj().Prop("total", total).Prop("offset", offset).Prop("limit", limit).Key("items");
            jw.OpenArr();
            foreach (var c in _cache.NaturalResources.Read)
            {
                if (c.Gatherable == null) continue;
                if (hasFilter && !PassesFilter(c.Name, c.X, c.Y, filterName, filterX, filterY, filterRadius)) continue;
                if (offset > 0 && skipped < offset) { skipped++; continue; }
                if (paginated && emitted >= limit) break;
                emitted++;
                jw.OpenObj()
                    .Prop("id", c.Id)
                    .Prop("name", c.Name)
                    .Prop("x", c.X).Prop("y", c.Y).Prop("z", c.Z)
                    .Prop("alive", c.Alive)
                    .CloseObj();
            }
            jw.CloseArr();
            if (paginated) jw.CloseObj();
            return jw.ToString();
        }

        // List all beavers and bots. Same three modes as buildings (basic/full/id:N).
        // Basic mode shows a wellbeing tier (ecstatic/happy/okay/unhappy/miserable) plus
        // critical and unmet need summaries as "+"-separated strings for compact display.
        // Full mode includes all ~30 individual needs with points, wellbeing contribution,
        // favorable/critical flags, and need group (SocialLife, Fun, Nutrition, etc).
        public object CollectBeavers(string format = "toon", string detail = "basic", int limit = 100, int offset = 0,
            string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
        {
            int? singleId = null;
            if (detail != null && detail.StartsWith("id:"))
            {
                if (int.TryParse(detail.Substring(3), out int parsed))
                    singleId = parsed;
            }
            bool fullDetail = detail == "full" || singleId.HasValue;
            bool hasFilter = filterName != null || filterRadius > 0;
            bool paginated = limit > 0 && !singleId.HasValue;
            int total = _cache.Beavers.Read.Count;
            if (paginated && hasFilter)
            {
                total = 0;
                foreach (var b in _cache.Beavers.Read)
                    if (PassesFilter(b.Name, b.X, b.Y, filterName, filterX, filterY, filterRadius)) total++;
            }
            int skipped = 0, emitted = 0;

            var jw = _cache.Jw.Reset();
            if (paginated) jw.OpenObj().Prop("total", total).Prop("offset", offset).Prop("limit", limit).Key("items");
            jw.OpenArr();
            foreach (var c in _cache.Beavers.Read)
            {
                if (singleId.HasValue && c.Id != singleId.Value)
                    continue;
                if (hasFilter && !PassesFilter(c.Name, c.X, c.Y, filterName, filterX, filterY, filterRadius))
                    continue;
                if (offset > 0 && skipped < offset) { skipped++; continue; }
                if (paginated && emitted >= limit) break;
                emitted++;

                jw.OpenObj()
                    .Prop("id", c.Id)
                    .Prop("name", c.Name)
                    .Prop("x", c.X).Prop("y", c.Y).Prop("z", c.Z)
                    .Prop("wellbeing", c.Wellbeing, "F1")
                    .Prop("isBot", c.IsBot);

                if (!fullDetail)
                {
                    float wb = c.Wellbeing;
                    string tier = wb >= 16 ? "ecstatic" : wb >= 12 ? "happy" : wb >= 8 ? "okay" : wb >= 4 ? "unhappy" : "miserable";
                    jw.Prop("tier", tier).Prop("workplace", c.Workplace ?? "");

                    // critical + unmet need summaries
                    string critical = "", unmet = "";
                    if (c.Needs != null)
                    {
                        foreach (var n in c.Needs)
                        {
                            if (n.Critical) critical = critical.Length > 0 ? critical + "+" + n.Id : n.Id;
                            else if (!n.Favorable && n.Active) unmet = unmet.Length > 0 ? unmet + "+" + n.Id : n.Id;
                        }
                    }
                    jw.Prop("critical", critical).Prop("unmet", unmet).CloseObj();
                    continue;
                }

                // full detail
                jw.Prop("anyCritical", c.AnyCritical);
                if (c.Workplace != null) jw.Prop("workplace", c.Workplace);
                if (c.District != null) jw.Prop("district", c.District);
                jw.Prop("hasHome", c.HasHome).Prop("contaminated", c.Contaminated);
                if (c.Life != null) jw.Prop("lifeProgress", c.LifeProgress);
                if (c.Deteriorable != null) jw.Prop("deterioration", c.DeteriorationProgress, "F3");
                if (c.Carrier != null) { jw.Prop("liftingCapacity", c.LiftingCapacity); if (c.Overburdened) jw.Prop("overburdened", true); }
                if (c.IsCarrying) jw.Prop("carrying", c.CarryingGood).Prop("carryAmount", c.CarryAmount);

                // needs array
                jw.Arr("needs");
                if (c.Needs != null)
                {
                    foreach (var n in c.Needs)
                    {
                        if (!fullDetail && !c.IsBot && !n.Active) continue;
                        jw.OpenObj()
                            .Prop("id", n.Id)
                            .Prop("points", n.Points)
                            .Prop("wellbeing", n.Wellbeing)
                            .Prop("favorable", n.Favorable)
                            .Prop("critical", n.Critical)
                            .Prop("group", n.Group)
                            .CloseObj();
                    }
                }
                jw.CloseArr().CloseObj();
            }
            jw.CloseArr();
            if (paginated) jw.CloseObj();
            return jw.ToString();
        }

        private struct PowerNetwork { public int Id, Supply, Demand; public List<int> BuildingIndices; }

        // Power networks: groups of buildings connected by adjacent power-conducting buildings.
        // In Timberborn, power transfers through ADJACENT buildings only (paths don't conduct).
        // Each building on a power network shares the same Graph object in the game engine.
        // We use the Graph's hash code (cached as PowerNetworkId) to group buildings by network.
        //
        // Supply = total generator output, Demand = total consumer input.
        // If demand > supply, buildings brownout (cycle power between consumers).
        public object CollectPowerNetworks()
        {
            // pass 1: group buildings by their power network ID
            var networks = new Dictionary<int, PowerNetwork>();
            var buildings = _cache.Buildings.Read;
            for (int i = 0; i < buildings.Count; i++)
            {
                var c = buildings[i];
                if (c.PowerNode == null || c.PowerNetworkId == 0) continue;
                int netId = c.PowerNetworkId; // RuntimeHelpers.GetHashCode of the Graph object
                if (!networks.ContainsKey(netId))
                    networks[netId] = new PowerNetwork { Id = netId, Supply = c.PowerSupply, Demand = c.PowerDemand, BuildingIndices = new List<int>() };
                networks[netId].BuildingIndices.Add(i);
            }
            var jw = _cache.Jw.BeginArr();
            foreach (var net in networks.Values)
            {
                jw.OpenObj().Prop("id", net.Id).Prop("supply", net.Supply).Prop("demand", net.Demand);
                jw.Arr("buildings");
                foreach (var idx in net.BuildingIndices)
                {
                    var c = buildings[idx];
                    jw.OpenObj().Prop("name", c.Name).Prop("id", c.Id).Prop("isGenerator", c.IsGenerator).Prop("nominalOutput", c.NominalPowerOutput).Prop("nominalInput", c.NominalPowerInput).CloseObj();
                }
                jw.CloseArr().CloseObj();
            }
            return jw.End();
        }

        // Timberborn's internal speed values are 0, 1, 3, 7 (not 0-3).
        // We map them to user-friendly 0-3 (pause, normal, fast, fastest).
        public static readonly int[] SpeedScale = { 0, 1, 3, 7 };

        public object CollectSpeed()
        {
            var raw = _speedManager.CurrentSpeed;
            int level = System.Array.IndexOf(SpeedScale, raw);
            if (level < 0) level = 0;  // unknown internal speed -> treat as paused
            return _cache.Jw.Result(("speed", level));
        }

        public object CollectWorkHours()
        {
            return new
            {
                endHours = _workingHoursManager.EndHours,
                areWorkingHours = _workingHoursManager.AreWorkingHours
            };
        }

        // Science points and unlockable buildings with costs and status
        public object CollectScience()
        {
            var jw = _cache.Jw.BeginObj().Prop("points", _scienceService.SciencePoints);
            jw.Arr("unlockables");
            foreach (var building in _buildingService.Buildings)
            {
                var bs = building.GetSpec<BuildingSpec>();
                if (bs == null || bs.ScienceCost <= 0) continue;
                var templateSpec = building.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                var name = templateSpec?.TemplateName ?? "unknown";
                jw.OpenObj().Prop("name", name).Prop("cost", bs.ScienceCost).Prop("unlocked", _buildingUnlockingService.Unlocked(bs)).CloseObj();
            }
            jw.CloseArr().CloseObj();
            return jw.ToString();
        }

        // Population wellbeing breakdown by need group (SocialLife, Fun, Nutrition, etc).
        // Aggregates across all beavers from cached need data.
        public object CollectWellbeing()
        {
            try
            {
                var beaverNeeds = _factionNeedService.GetBeaverNeeds();
                var groupNeeds = new Dictionary<string, List<NeedSpec>>();
                foreach (var ns in beaverNeeds)
                {
                    var groupId = ns.NeedGroupId;
                    if (string.IsNullOrEmpty(groupId)) continue;
                    if (!groupNeeds.ContainsKey(groupId))
                        groupNeeds[groupId] = new List<NeedSpec>();
                    groupNeeds[groupId].Add(ns);
                }
                int beaverCount = 0;
                var groupTotals = new Dictionary<string, float>();
                var groupMaxTotals = new Dictionary<string, float>();
                var needToGroup = new Dictionary<string, string>();
                foreach (var kvp in groupNeeds)
                    foreach (var ns in kvp.Value)
                        needToGroup[ns.Id] = kvp.Key;
                foreach (var c in _cache.Beavers.Read)
                {
                    if (c.Needs == null) continue;
                    beaverCount++;
                    foreach (var n in c.Needs)
                    {
                        if (!needToGroup.TryGetValue(n.Id, out var groupId)) continue;
                        if (!groupTotals.ContainsKey(groupId)) { groupTotals[groupId] = 0f; groupMaxTotals[groupId] = 0f; }
                        groupTotals[groupId] += n.Wellbeing;
                    }
                    foreach (var kvp in groupNeeds)
                    {
                        var groupId = kvp.Key;
                        float groupMax = 0f;
                        foreach (var ns in kvp.Value) groupMax += ns.FavorableWellbeing;
                        if (!groupMaxTotals.ContainsKey(groupId)) groupMaxTotals[groupId] = 0f;
                        groupMaxTotals[groupId] += groupMax;
                    }
                }
                var jw = _cache.Jw.BeginObj().Prop("beavers", beaverCount).Arr("categories");
                foreach (var kvp in groupNeeds)
                {
                    var groupId = kvp.Key;
                    float avgCurrent = beaverCount > 0 ? groupTotals.GetValueOrDefault(groupId) / beaverCount : 0;
                    float avgMax = beaverCount > 0 ? groupMaxTotals.GetValueOrDefault(groupId) / beaverCount : 0;
                    jw.OpenObj().Prop("group", groupId).Prop("current", (float)System.Math.Round(avgCurrent, 1), "F1").Prop("max", (float)System.Math.Round(avgMax, 1), "F1");
                    jw.Arr("needs");
                    foreach (var ns in kvp.Value)
                        jw.OpenObj().Prop("id", ns.Id).Prop("favorableWellbeing", ns.FavorableWellbeing, "F1").Prop("unfavorableWellbeing", ns.UnfavorableWellbeing, "F1").CloseObj();
                    jw.CloseArr().CloseObj();
                }
                jw.CloseArr().CloseObj();
                return jw.ToString();
            }
            catch (System.Exception ex) { TimberbotLog.Error("wellbeing", ex); return _cache.Jw.BeginObj().Prop("error", ex.Message).CloseObj().ToString(); }
        }

        // Game event history (droughts, deaths, etc)
        public object CollectNotifications(int limit = 100, int offset = 0)
        {
            bool paginated = limit > 0;
            int skipped = 0, emitted = 0;
            var jw = _cache.Jw.BeginArr();
            try
            {
                foreach (var n in _notificationSaver.Notifications)
                {
                    if (offset > 0 && skipped < offset) { skipped++; continue; }
                    if (paginated && emitted >= limit) break;
                    emitted++;
                    jw.OpenObj().Prop("subject", n.Subject.ToString()).Prop("description", n.Description.ToString()).Prop("cycle", n.Cycle).Prop("cycleDay", n.CycleDay).CloseObj();
                }
            }
            catch (System.Exception _ex) { TimberbotLog.Error("notifications", _ex); }
            return jw.End();
        }

        // Import/export settings per good per district
        public object CollectDistribution()
        {
            var jw = _cache.Jw.BeginArr();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var distSetting = dc.GetComponent<Timberborn.DistributionSystem.DistrictDistributionSetting>();
                if (distSetting == null) continue;
                jw.OpenObj().Prop("district", dc.DistrictName).Arr("goods");
                try
                {
                    foreach (var gs in distSetting.GoodDistributionSettings)
                        jw.OpenObj().Prop("good", gs.GoodId).Prop("importOption", gs.ImportOption.ToString()).Prop("exportThreshold", gs.ExportThreshold, "F0").CloseObj();
                }
                catch (System.Exception _ex) { TimberbotLog.Error("distribution", _ex); }
                jw.CloseArr().CloseObj();
            }
            return jw.End();
        }

        // Tile data for a rectangular region. Returns terrain height, water depth,
        // badwater contamination, occupants (with vertical stacking), soil moisture,
        // and soil contamination per tile.
        //
        // Uses IThreadSafeWaterMap and IThreadSafeColumnTerrainMap which are designed
        // for background thread access. Occupancy built from cached entity indexes.
        // Soil services wrapped in try/catch as a safety net.
        public object CollectTiles(int x1, int y1, int x2, int y2)
        {
            var size = _terrainService.Size;
            var stride = _mapIndexService.VerticalStride;

            if (x1 == 0 && y1 == 0 && x2 == 0 && y2 == 0)
                return _cache.Jw.BeginObj().Obj("mapSize").Prop("x", size.x).Prop("y", size.y).Prop("z", size.z).CloseObj().CloseObj().ToString();

            x1 = Mathf.Clamp(x1, 0, size.x - 1);
            y1 = Mathf.Clamp(y1, 0, size.y - 1);
            x2 = Mathf.Clamp(x2, 0, size.x - 1);
            y2 = Mathf.Clamp(y2, 0, size.y - 1);

            // Build occupancy map from cached entity data.
            // Each tile can have multiple occupants at different z-levels (vertical stacking:
            // a path on z=2 with a building on z=3). We track occupant names by z-level.
            // Also track building entrances (for path connectivity analysis),
            // seedlings (trees not yet grown), and dead resources (stumps).
            var occupants = new Dictionary<long, List<(string name, int z)>>();
            var entrances = new HashSet<long>();
            var seedlings = new HashSet<long>();
            var deadTiles = new HashSet<long>();

            var buildings = _cache.Buildings.Read;
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

            var resources = _cache.NaturalResources.Read;
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

            var jw = _cache.Jw.BeginObj();
            jw.Obj("mapSize").Prop("x", size.x).Prop("y", size.y).Prop("z", size.z).CloseObj();
            jw.Obj("region").Prop("x1", x1).Prop("y1", y1).Prop("x2", x2).Prop("y2", y2).CloseObj();
            jw.Arr("tiles");
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
                    try { waterHeight = _waterMap.CeiledWaterHeight(waterCoord); } catch (System.Exception _ex) { TimberbotLog.Error("map.water", _ex); }
                    // Badwater (contaminated water) check: iterate water columns top-down.
                    // Water is stored in columns (like terrain) -- multiple water layers can exist
                    // at different heights. We check from top down and report the first contaminated one.
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
                    catch (System.Exception _ex) { TimberbotLog.Error("map.badwater", _ex); }

                    long key = (long)x * 100000 + y;
                    occupants.TryGetValue(key, out var occList);

                    jw.OpenObj().Prop("x", x).Prop("y", y).Prop("terrain", terrainHeight).Prop("water", waterHeight, "F1");
                    if (waterContamination > 0) jw.Prop("badwater", (float)System.Math.Round(waterContamination, 2));
                    if (occList != null)
                    {
                        if (occList.Count == 1)
                            jw.Prop("occupant", occList[0].name);
                        else
                        {
                            jw.Arr("occupants");
                            foreach (var o in occList) jw.OpenObj().Prop("name", o.name).Prop("z", o.z).CloseObj();
                            jw.CloseArr();
                        }
                    }
                    if (entrances.Contains(key)) jw.Prop("entrance", true);
                    if (seedlings.Contains(key)) jw.Prop("seedling", true);
                    if (deadTiles.Contains(key)) jw.Prop("dead", true);
                    try { if (_soilContaminationService.SoilIsContaminated(new Vector3Int(x, y, terrainHeight))) jw.Prop("contaminated", true); } catch (System.Exception _ex) { TimberbotLog.Error("map.soil", _ex); }
                    try { if (_soilMoistureService.SoilIsMoist(new Vector3Int(x, y, terrainHeight))) jw.Prop("moist", true); } catch (System.Exception _ex) { TimberbotLog.Error("map.moisture", _ex); }
                    jw.CloseObj();
                }
            }
            jw.CloseArr().CloseObj();
            return jw.ToString();
        }
    }
}
