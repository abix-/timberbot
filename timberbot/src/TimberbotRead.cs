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
        // ================================================================

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
                var jj = _cache.Jw.Reset().OpenObj();
                jj.Key("time").OpenObj()
                    .Key("dayNumber").Int(_dayNightCycle.DayNumber)
                    .Key("dayProgress").Float((float)_dayNightCycle.DayProgress)
                    .Key("partialDayNumber").Float((float)_dayNightCycle.PartialDayNumber)
                    .CloseObj();
                jj.Key("weather").OpenObj()
                    .Key("cycle").Int(_gameCycleService.Cycle)
                    .Key("cycleDay").Int(_gameCycleService.CycleDay)
                    .Key("isHazardous").Bool(_weatherService.IsHazardousWeather)
                    .Key("temperateWeatherDuration").Int(_weatherService.TemperateWeatherDuration)
                    .Key("hazardousWeatherDuration").Int(_weatherService.HazardousWeatherDuration)
                    .Key("cycleLengthInDays").Int(_weatherService.CycleLengthInDays)
                    .CloseObj();
                jj.Key("districts").Raw(CollectDistricts("json") as string);
                jj.Key("trees").OpenObj()
                    .Key("markedGrown").Int(treeMarkedGrown)
                    .Key("markedSeedling").Int(treeMarkedSeedling)
                    .Key("unmarkedGrown").Int(treeUnmarkedGrown)
                    .CloseObj();
                jj.Key("crops").OpenObj()
                    .Key("ready").Int(cropReady)
                    .Key("growing").Int(cropGrowing)
                    .CloseObj();
                jj.Key("housing").OpenObj()
                    .Key("occupiedBeds").Int(occupiedBeds)
                    .Key("totalBeds").Int(totalBeds)
                    .Key("homeless").Int(homeless)
                    .CloseObj();
                jj.Key("employment").OpenObj()
                    .Key("assigned").Int(assignedWorkers)
                    .Key("vacancies").Int(totalVacancies)
                    .Key("unemployed").Int(unemployed)
                    .CloseObj();
                jj.Key("wellbeing").OpenObj()
                    .Key("average").Float((float)avgWellbeing, "F1")
                    .Key("miserable").Int(miserable)
                    .Key("critical").Int(critical)
                    .CloseObj();
                jj.Key("science").Int(_scienceService.SciencePoints);
                jj.Key("alerts").OpenObj()
                    .Key("unstaffed").Int(alertUnstaffed)
                    .Key("unpowered").Int(alertUnpowered)
                    .Key("unreachable").Int(alertUnreachable)
                    .CloseObj();
                jj.CloseObj();
                return jj.ToString();
            }

            // build flat summary matching TOON output format
            var jw = _cache.Jw.Reset().OpenObj();

            // time
            jw.Key("day").Int(_dayNightCycle.DayNumber);
            jw.Key("dayProgress").Float((float)_dayNightCycle.DayProgress);

            // weather
            jw.Key("cycle").Int(_gameCycleService.Cycle);
            jw.Key("cycleDay").Int(_gameCycleService.CycleDay);
            jw.Key("isHazardous").Bool(_weatherService.IsHazardousWeather);
            jw.Key("tempDays").Int(_weatherService.TemperateWeatherDuration);
            jw.Key("hazardDays").Int(_weatherService.HazardousWeatherDuration);

            // trees (actual trees only, not crops)
            jw.Key("markedGrown").Int(treeMarkedGrown);
            jw.Key("markedSeedling").Int(treeMarkedSeedling);
            jw.Key("unmarkedGrown").Int(treeUnmarkedGrown);
            // crops
            jw.Key("cropReady").Int(cropReady);
            jw.Key("cropGrowing").Int(cropGrowing);

            // population + resources (from cached district snapshot)
            int totalFood = 0, totalWater = 0, logStock = 0, plankStock = 0, gearStock = 0;
            foreach (var dc in _cache.Districts)
            {
                jw.Key("adults").Int(dc.Adults);
                jw.Key("children").Int(dc.Children);
                jw.Key("bots").Int(dc.Bots);
                if (dc.Resources != null)
                {
                    foreach (var kvp in dc.Resources)
                    {
                        jw.Key(kvp.Key).Int(kvp.Value);
                        if (kvp.Key == "Water") totalWater += kvp.Value;
                        else if (kvp.Key == "Berries" || kvp.Key == "Kohlrabi" || kvp.Key == "Carrot" || kvp.Key == "Potato"
                              || kvp.Key == "Wheat" || kvp.Key == "Bread" || kvp.Key == "Cassava" || kvp.Key == "Corn"
                              || kvp.Key == "Eggplant" || kvp.Key == "Soybean" || kvp.Key == "MapleSyrup")
                            totalFood += kvp.Value;
                        else if (kvp.Key == "Log") logStock = kvp.Value;
                        else if (kvp.Key == "Plank") plankStock = kvp.Value;
                        else if (kvp.Key == "Gear") gearStock = kvp.Value;
                    }
                }
            }

            // Resource projection: how many days of each resource at current consumption.
            // Beavers eat ~1 food/day and drink ~2 water/day. These projections help
            // the AI decide when to build more farms/pumps/tanks before a drought.
            int totalPop = beaverCount;
            if (totalPop > 0)
            {
                jw.Key("foodDays").Float((float)((double)totalFood / totalPop), "F1");
                jw.Key("waterDays").Float((float)((double)totalWater / (totalPop * 2.0)), "F1"); // 2x because beavers drink twice/day
                jw.Key("logDays").Float((float)((double)logStock / totalPop), "F1");
                jw.Key("plankDays").Float((float)((double)plankStock / totalPop), "F1");
                jw.Key("gearDays").Float((float)((double)gearStock / totalPop), "F1");
            }

            // housing
            jw.Key("beds").Str($"{occupiedBeds}/{totalBeds}");
            jw.Key("homeless").Int(homeless);

            // employment
            jw.Key("workers").Str($"{assignedWorkers}/{totalVacancies}");
            jw.Key("unemployed").Int(unemployed);

            // wellbeing
            jw.Key("wellbeing").Float((float)avgWellbeing, "F1");
            jw.Key("miserable").Int(miserable);
            jw.Key("critical").Int(critical);

            // science
            jw.Key("science").Int(_scienceService.SciencePoints);

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
            jw.Key("alerts").Str(alertStr);

            jw.CloseObj();
            return jw.ToString();
        }

        // PERF: iterates _cache.Buildings.Read instead of all entities.
        public object CollectAlerts()
        {
            var jw = _cache.Jw.Reset().OpenArr();
            foreach (var c in _cache.Buildings.Read)
            {
                if (c.Workplace != null && c.DesiredWorkers > 0 && c.AssignedWorkers < c.DesiredWorkers)
                    jw.OpenObj().Key("type").Str("unstaffed").Key("id").Int(c.Id).Key("name").Str(c.Name).Key("workers").Str($"{c.AssignedWorkers}/{c.DesiredWorkers}").CloseObj();

                if (c.IsConsumer && !c.Powered)
                    jw.OpenObj().Key("type").Str("unpowered").Key("id").Int(c.Id).Key("name").Str(c.Name).CloseObj();

                if (c.Unreachable)
                    jw.OpenObj().Key("type").Str("unreachable").Key("id").Int(c.Id).Key("name").Str(c.Name).CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
        }

        // PERF: O(n) entity scan + grid bucketing. Called occasionally for tree management.
        public object CollectTreeClusters(int cellSize = 10, int top = 5)
        {
            var cells = new Dictionary<long, int[]>(); // key -> [grown, total, centerX, centerY, z]
            foreach (var nr in _cache.NaturalResources.Read)
            {
                if (nr.Cuttable == null) continue;
                if (nr.Living == null || nr.Living.IsDead) continue;
                if (nr.BlockObject == null) continue;

                var c = nr.BlockObject.Coordinates;
                int cx = c.x / cellSize * cellSize + cellSize / 2;
                int cy = c.y / cellSize * cellSize + cellSize / 2;
                long key = (long)cx * 100000 + cy;

                if (!cells.ContainsKey(key))
                    cells[key] = new int[] { 0, 0, cx, cy, c.z };

                cells[key][1]++;
                if (nr.Growable != null && nr.Growable.IsGrown)
                    cells[key][0]++;
            }

            var sorted = new List<int[]>(cells.Values);
            sorted.Sort((a, b) => b[0].CompareTo(a[0]));
            var jw = _cache.Jw.Reset().OpenArr();
            for (int i = 0; i < System.Math.Min(top, sorted.Count); i++)
            {
                var s = sorted[i];
                jw.OpenObj().Key("x").Int(s[2]).Key("y").Int(s[3]).Key("z").Int(s[4]).Key("grown").Int(s[0]).Key("total").Int(s[1]).CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
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
            var goods = _goodService.Goods;
            var jw = _cache.Jw.Reset().OpenArr();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var counter = dc.GetComponent<DistrictResourceCounter>();
                var pop = dc.DistrictPopulation;
                jw.OpenObj().Key("name").Str(dc.DistrictName);
                if (format == "toon")
                {
                    jw.Key("adults").Int(pop != null ? pop.NumberOfAdults : 0)
                      .Key("children").Int(pop != null ? pop.NumberOfChildren : 0)
                      .Key("bots").Int(pop != null ? pop.NumberOfBots : 0);
                    if (counter != null)
                        foreach (var goodId in goods)
                        {
                            var rc = counter.GetResourceCount(goodId);
                            if (rc.AllStock > 0) jw.Key(goodId).Int(rc.AvailableStock);
                        }
                }
                else
                {
                    jw.Key("population").OpenObj()
                        .Key("adults").Int(pop != null ? pop.NumberOfAdults : 0)
                        .Key("children").Int(pop != null ? pop.NumberOfChildren : 0)
                        .Key("bots").Int(pop != null ? pop.NumberOfBots : 0)
                        .CloseObj();
                    jw.Key("resources").OpenObj();
                    if (counter != null)
                        foreach (var goodId in goods)
                        {
                            var rc = counter.GetResourceCount(goodId);
                            if (rc.AllStock > 0) jw.Key(goodId).OpenObj().Key("available").Int(rc.AvailableStock).Key("all").Int(rc.AllStock).CloseObj();
                        }
                    jw.CloseObj();
                }
                jw.CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
        }

        public object CollectResources(string format = "toon")
        {
            var goods = _goodService.Goods;
            var jw = _cache.Jw.Reset();
            if (format == "toon")
            {
                jw.OpenArr();
                foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    var counter = dc.GetComponent<DistrictResourceCounter>();
                    if (counter == null) continue;
                    foreach (var goodId in goods)
                    {
                        var rc = counter.GetResourceCount(goodId);
                        if (rc.AllStock > 0)
                            jw.OpenObj().Key("district").Str(dc.DistrictName).Key("good").Str(goodId).Key("available").Int(rc.AvailableStock).Key("all").Int(rc.AllStock).CloseObj();
                    }
                }
                jw.CloseArr();
            }
            else
            {
                jw.OpenObj();
                foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    var counter = dc.GetComponent<DistrictResourceCounter>();
                    if (counter == null) continue;
                    jw.Key(dc.DistrictName).OpenObj();
                    foreach (var goodId in goods)
                    {
                        var rc = counter.GetResourceCount(goodId);
                        if (rc.AllStock > 0)
                            jw.Key(goodId).OpenObj().Key("available").Int(rc.AvailableStock).Key("all").Int(rc.AllStock).CloseObj();
                    }
                    jw.CloseObj();
                }
                jw.CloseObj();
            }
            return jw.ToString();
        }

        public object CollectPopulation()
        {
            var jw = _cache.Jw.Reset().OpenArr();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var pop = dc.DistrictPopulation;
                jw.OpenObj()
                    .Key("district").Str(dc.DistrictName)
                    .Key("adults").Int(pop != null ? pop.NumberOfAdults : 0)
                    .Key("children").Int(pop != null ? pop.NumberOfChildren : 0)
                    .Key("bots").Int(pop != null ? pop.NumberOfBots : 0)
                    .CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
        }

        // List all buildings. Three modes:
        //   "basic" (default) -- compact: id, name, coords, finished, paused, priority, workers
        //   "full" -- all fields: inventory, recipes, power, floodgate, effect radius, etc
        //   "id:N" -- single building with full detail (for inspecting one building)
        //
        // Uses JwWriter to build JSON directly in a pre-allocated StringBuilder.
        // No Newtonsoft, no Dictionary, no anonymous objects. Just string appends.
        public object CollectBuildings(string format = "toon", string detail = "basic")
        {
            // parse "id:-12345" to filter to a single building
            int? singleId = null;
            if (detail != null && detail.StartsWith("id:"))
            {
                if (int.TryParse(detail.Substring(3), out int parsed))
                    singleId = parsed;
            }
            bool fullDetail = detail == "full" || singleId.HasValue;

            var jw = _cache.Jw.Reset().OpenArr();
            foreach (var c in _cache.Buildings.Read)
            {
                if (singleId.HasValue && c.Id != singleId.Value)
                    continue;

                // every building gets these base fields
                jw.OpenObj()
                    .Key("id").Int(c.Id)
                    .Key("name").Str(c.Name)
                    .Key("x").Int(c.X).Key("y").Int(c.Y).Key("z").Int(c.Z)
                    .Key("orientation").Str(c.Orientation ?? "")
                    .Key("finished").Bool(c.Finished)
                    .Key("paused").Bool(c.Paused);

                // basic mode: just priority + workers, then close
                if (!fullDetail)
                {
                    jw.Key("priority").Str(c.ConstructionPriority ?? "")
                        .Key("workers").Str(c.Workplace != null ? $"{c.AssignedWorkers}/{c.DesiredWorkers}" : "")
                        .CloseObj();
                    continue;
                }

                // full detail: conditional fields (only present if building has that component)
                if (c.Pausable != null) jw.Key("pausable").Bool(true);
                if (c.HasFloodgate) jw.Key("floodgate").Bool(true).Key("height").Float(c.FloodgateHeight, "F1").Key("maxHeight").Float(c.FloodgateMaxHeight, "F1");
                if (c.ConstructionPriority != null) jw.Key("constructionPriority").Str(c.ConstructionPriority);
                if (c.WorkplacePriorityStr != null) jw.Key("workplacePriority").Str(c.WorkplacePriorityStr);
                if (c.Workplace != null) jw.Key("maxWorkers").Int(c.MaxWorkers).Key("desiredWorkers").Int(c.DesiredWorkers).Key("assignedWorkers").Int(c.AssignedWorkers);
                if (c.Reachability != null) jw.Key("reachable").Bool(!c.Unreachable);
                if (c.Mechanical != null) jw.Key("powered").Bool(c.Powered);
                if (c.PowerNode != null)
                {
                    jw.Key("isGenerator").Bool(c.IsGenerator).Key("isConsumer").Bool(c.IsConsumer)
                        .Key("nominalPowerInput").Int(c.NominalPowerInput).Key("nominalPowerOutput").Int(c.NominalPowerOutput);
                    if (c.PowerDemand > 0 || c.PowerSupply > 0) jw.Key("powerDemand").Int(c.PowerDemand).Key("powerSupply").Int(c.PowerSupply);
                }
                if (c.Site != null) jw.Key("buildProgress").Float(c.BuildProgress).Key("materialProgress").Float(c.MaterialProgress).Key("hasMaterials").Bool(c.HasMaterials);
                if (c.Capacity > 0)
                {
                    jw.Key("stock").Int(c.Stock).Key("capacity").Int(c.Capacity);
                    if (c.Inventory != null && c.Inventory.Count > 0)
                    {
                        jw.Key("inventory").OpenObj();
                        foreach (var kvp in c.Inventory)
                            jw.Key(kvp.Key).Int(kvp.Value);
                        jw.CloseObj();
                    }
                }
                if (c.HasWonder) jw.Key("isWonder").Bool(true).Key("wonderActive").Bool(c.WonderActive);
                if (c.Dwelling != null) jw.Key("dwellers").Int(c.Dwellers).Key("maxDwellers").Int(c.MaxDwellers);
                if (c.HasClutch) jw.Key("isClutch").Bool(true).Key("clutchEngaged").Bool(c.ClutchEngaged);
                if (c.Manufactory != null)
                {
                    if (c.Recipes != null && c.Recipes.Count > 0)
                    {
                        jw.Key("recipes").OpenArr();
                        for (int ri = 0; ri < c.Recipes.Count; ri++)
                            jw.Str(c.Recipes[ri]);
                        jw.CloseArr();
                    }
                    jw.Key("currentRecipe").Str(c.CurrentRecipe ?? "")
                        .Key("productionProgress").Float(c.ProductionProgress)
                        .Key("readyToProduce").Bool(c.ReadyToProduce);
                }
                if (c.BreedingPod != null)
                {
                    jw.Key("needsNutrients").Bool(c.NeedsNutrients);
                    if (c.NutrientStock != null && c.NutrientStock.Count > 0)
                    {
                        jw.Key("nutrients").OpenObj();
                        foreach (var kvp in c.NutrientStock)
                            jw.Key(kvp.Key).Int(kvp.Value);
                        jw.CloseObj();
                    }
                }
                if (c.EffectRadius > 0) jw.Key("effectRadius").Int(c.EffectRadius);
                jw.CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
        }

        // PERF: cached component refs -- zero GetComponent per item.
        // PERF: TimberbotJw serialization -- 2ms for 3000 trees. Zero Newtonsoft.
        private object CollectNaturalResourcesJw(TimberbotJw jw, System.Collections.Generic.HashSet<string> species)
        {
            jw.Reset().OpenArr();
            foreach (var c in _cache.NaturalResources.Read)
            {
                if (c.Cuttable == null) continue;
                if (!species.Contains(c.Name)) continue;
                jw.OpenObj()
                    .Key("id").Int(c.Id)
                    .Key("name").Str(c.Name)
                    .Key("x").Int(c.X).Key("y").Int(c.Y).Key("z").Int(c.Z)
                    .Key("marked").Bool(c.Marked)
                    .Key("alive").Bool(c.Alive)
                    .Key("grown").Bool(c.Grown)
                    .Key("growth").Float(c.Growth)
                    .CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
        }

        public object CollectTrees() => CollectNaturalResourcesJw(_cache.Jw, TimberbotEntityCache.TreeSpecies);
        public object CollectCrops() => CollectNaturalResourcesJw(_cache.Jw, TimberbotEntityCache.CropSpecies);

        public object CollectGatherables()
        {
            var jw = _cache.Jw.Reset().OpenArr();
            foreach (var c in _cache.NaturalResources.Read)
            {
                if (c.Gatherable == null) continue;
                jw.OpenObj()
                    .Key("id").Int(c.Id)
                    .Key("name").Str(c.Name)
                    .Key("x").Int(c.X).Key("y").Int(c.Y).Key("z").Int(c.Z)
                    .Key("alive").Bool(c.Alive)
                    .CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
        }

        // PERF: reads cached beaver data only. Zero GetComponent from background thread.
        public object CollectBeavers(string format = "toon", string detail = "basic")
        {
            int? singleId = null;
            if (detail != null && detail.StartsWith("id:"))
            {
                if (int.TryParse(detail.Substring(3), out int parsed))
                    singleId = parsed;
            }
            bool fullDetail = detail == "full" || singleId.HasValue;

            var jw = _cache.Jw.Reset().OpenArr();
            foreach (var c in _cache.Beavers.Read)
            {
                if (singleId.HasValue && c.Id != singleId.Value)
                    continue;

                jw.OpenObj()
                    .Key("id").Int(c.Id)
                    .Key("name").Str(c.Name)
                    .Key("x").Int(c.X).Key("y").Int(c.Y).Key("z").Int(c.Z)
                    .Key("wellbeing").Float(c.Wellbeing, "F1")
                    .Key("isBot").Bool(c.IsBot);

                if (!fullDetail)
                {
                    float wb = c.Wellbeing;
                    string tier = wb >= 16 ? "ecstatic" : wb >= 12 ? "happy" : wb >= 8 ? "okay" : wb >= 4 ? "unhappy" : "miserable";
                    jw.Key("tier").Str(tier).Key("workplace").Str(c.Workplace ?? "");

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
                    jw.Key("critical").Str(critical).Key("unmet").Str(unmet).CloseObj();
                    continue;
                }

                // full detail
                jw.Key("anyCritical").Bool(c.AnyCritical);
                if (c.Workplace != null) jw.Key("workplace").Str(c.Workplace);
                if (c.District != null) jw.Key("district").Str(c.District);
                jw.Key("hasHome").Bool(c.HasHome).Key("contaminated").Bool(c.Contaminated);
                if (c.Life != null) jw.Key("lifeProgress").Float(c.LifeProgress);
                if (c.Deteriorable != null) jw.Key("deterioration").Float(c.DeteriorationProgress, "F3");
                if (c.Carrier != null) { jw.Key("liftingCapacity").Int(c.LiftingCapacity); if (c.Overburdened) jw.Key("overburdened").Bool(true); }
                if (c.IsCarrying) jw.Key("carrying").Str(c.CarryingGood).Key("carryAmount").Int(c.CarryAmount);

                // needs array
                jw.Key("needs").OpenArr();
                if (c.Needs != null)
                {
                    foreach (var n in c.Needs)
                    {
                        if (!fullDetail && !c.IsBot && !n.Active) continue;
                        jw.OpenObj()
                            .Key("id").Str(n.Id)
                            .Key("points").Float(n.Points)
                            .Key("wellbeing").Int(n.Wellbeing)
                            .Key("favorable").Bool(n.Favorable)
                            .Key("critical").Bool(n.Critical)
                            .Key("group").Str(n.Group)
                            .CloseObj();
                    }
                }
                jw.CloseArr().CloseObj();
            }
            jw.CloseArr();
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
            var jw = _cache.Jw.Reset().OpenArr();
            foreach (var net in networks.Values)
            {
                jw.OpenObj().Key("id").Int(net.Id).Key("supply").Int(net.Supply).Key("demand").Int(net.Demand);
                jw.Key("buildings").OpenArr();
                foreach (var idx in net.BuildingIndices)
                {
                    var c = buildings[idx];
                    jw.OpenObj().Key("name").Str(c.Name).Key("id").Int(c.Id).Key("isGenerator").Bool(c.IsGenerator).Key("nominalOutput").Int(c.NominalPowerOutput).Key("nominalInput").Int(c.NominalPowerInput).CloseObj();
                }
                jw.CloseArr().CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
        }

        public static readonly int[] SpeedScale = { 0, 1, 3, 7 };

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

        // Science points and unlockable buildings with costs and status
        public object CollectScience()
        {
            var jw = _cache.Jw.Reset().OpenObj().Key("points").Int(_scienceService.SciencePoints);
            jw.Key("unlockables").OpenArr();
            foreach (var building in _buildingService.Buildings)
            {
                var bs = building.GetSpec<BuildingSpec>();
                if (bs == null || bs.ScienceCost <= 0) continue;
                var templateSpec = building.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                var name = templateSpec?.TemplateName ?? "unknown";
                jw.OpenObj().Key("name").Str(name).Key("cost").Int(bs.ScienceCost).Key("unlocked").Bool(_buildingUnlockingService.Unlocked(bs)).CloseObj();
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
                var jw = _cache.Jw.Reset().OpenObj().Key("beavers").Int(beaverCount).Key("categories").OpenArr();
                foreach (var kvp in groupNeeds)
                {
                    var groupId = kvp.Key;
                    float avgCurrent = beaverCount > 0 ? groupTotals.GetValueOrDefault(groupId) / beaverCount : 0;
                    float avgMax = beaverCount > 0 ? groupMaxTotals.GetValueOrDefault(groupId) / beaverCount : 0;
                    jw.OpenObj().Key("group").Str(groupId).Key("current").Float((float)System.Math.Round(avgCurrent, 1), "F1").Key("max").Float((float)System.Math.Round(avgMax, 1), "F1");
                    jw.Key("needs").OpenArr();
                    foreach (var ns in kvp.Value)
                        jw.OpenObj().Key("id").Str(ns.Id).Key("favorableWellbeing").Float(ns.FavorableWellbeing, "F1").Key("unfavorableWellbeing").Float(ns.UnfavorableWellbeing, "F1").CloseObj();
                    jw.CloseArr().CloseObj();
                }
                jw.CloseArr().CloseObj();
                return jw.ToString();
            }
            catch (System.Exception ex) { TimberbotLog.Error("wellbeing", ex); return new { error = ex.Message }; }
        }

        // Game event history (droughts, deaths, etc)
        public object CollectNotifications()
        {
            var jw = _cache.Jw.Reset().OpenArr();
            try
            {
                foreach (var n in _notificationSaver.Notifications)
                    jw.OpenObj().Key("subject").Str(n.Subject.ToString()).Key("description").Str(n.Description.ToString()).Key("cycle").Int(n.Cycle).Key("cycleDay").Int(n.CycleDay).CloseObj();
            }
            catch (System.Exception _ex) { TimberbotLog.Error("notifications", _ex); }
            jw.CloseArr();
            return jw.ToString();
        }

        // Import/export settings per good per district
        public object CollectDistribution()
        {
            var jw = _cache.Jw.Reset().OpenArr();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var distSetting = dc.GetComponent<Timberborn.DistributionSystem.DistrictDistributionSetting>();
                if (distSetting == null) continue;
                jw.OpenObj().Key("district").Str(dc.DistrictName).Key("goods").OpenArr();
                try
                {
                    foreach (var gs in distSetting.GoodDistributionSettings)
                        jw.OpenObj().Key("good").Str(gs.GoodId).Key("importOption").Str(gs.ImportOption.ToString()).Key("exportThreshold").Float(gs.ExportThreshold, "F0").CloseObj();
                }
                catch (System.Exception _ex) { TimberbotLog.Error("distribution", _ex); }
                jw.CloseArr().CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
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
                return new { mapSize = new { x = size.x, y = size.y, z = size.z } };

            x1 = Mathf.Clamp(x1, 0, size.x - 1);
            y1 = Mathf.Clamp(y1, 0, size.y - 1);
            x2 = Mathf.Clamp(x2, 0, size.x - 1);
            y2 = Mathf.Clamp(y2, 0, size.y - 1);

            // build occupancy from cached indexes (zero GetComponent, thread-safe)
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

            var jw = _cache.Jw.Reset().OpenObj();
            jw.Key("mapSize").OpenObj().Key("x").Int(size.x).Key("y").Int(size.y).Key("z").Int(size.z).CloseObj();
            jw.Key("region").OpenObj().Key("x1").Int(x1).Key("y1").Int(y1).Key("x2").Int(x2).Key("y2").Int(y2).CloseObj();
            jw.Key("tiles").OpenArr();
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

                    jw.OpenObj().Key("x").Int(x).Key("y").Int(y).Key("terrain").Int(terrainHeight).Key("water").Float(waterHeight, "F1");
                    if (waterContamination > 0) jw.Key("badwater").Float((float)System.Math.Round(waterContamination, 2));
                    if (occList != null)
                    {
                        if (occList.Count == 1)
                            jw.Key("occupant").Str(occList[0].name);
                        else
                        {
                            jw.Key("occupants").OpenArr();
                            foreach (var o in occList) jw.OpenObj().Key("name").Str(o.name).Key("z").Int(o.z).CloseObj();
                            jw.CloseArr();
                        }
                    }
                    if (entrances.Contains(key)) jw.Key("entrance").Bool(true);
                    if (seedlings.Contains(key)) jw.Key("seedling").Bool(true);
                    if (deadTiles.Contains(key)) jw.Key("dead").Bool(true);
                    try { if (_soilContaminationService.SoilIsContaminated(new Vector3Int(x, y, terrainHeight))) jw.Key("contaminated").Bool(true); } catch (System.Exception _ex) { TimberbotLog.Error("map.soil", _ex); }
                    try { if (_soilMoistureService.SoilIsMoist(new Vector3Int(x, y, terrainHeight))) jw.Key("moist").Bool(true); } catch (System.Exception _ex) { TimberbotLog.Error("map.moisture", _ex); }
                    jw.CloseObj();
                }
            }
            jw.CloseArr().CloseObj();
            return jw.ToString();
        }
    }
}
