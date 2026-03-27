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
        internal readonly FactionNeedService _factionNeedService;
        private readonly NotificationSaver _notificationSaver;
        private readonly TimberbotEntityCache _cache;
        // terrain/water services for CollectTiles (thread-safe by design)
        private readonly ITerrainService _terrainService;
        private readonly IThreadSafeWaterMap _waterMap;
        private readonly MapIndexService _mapIndexService;
        private readonly IThreadSafeColumnTerrainMap _terrainMap;
        private readonly ISoilContaminationService _soilContaminationService;
        private readonly ISoilMoistureService _soilMoistureService;

        // reusable collections -- allocated once, cleared per call to avoid GC pressure
        private readonly Dictionary<string, int[]> _treeSpecies = new Dictionary<string, int[]>();
        private readonly Dictionary<string, int[]> _cropSpecies = new Dictionary<string, int[]>();
        private readonly Dictionary<string, int> _roleCounts = new Dictionary<string, int>();
        private readonly Dictionary<string, int[]> _districtStats = new Dictionary<string, int[]>();
        private readonly Dictionary<string, (int x, int y, int z, string orientation, int entranceX, int entranceY)> _districtDCs = new Dictionary<string, (int, int, int, string, int, int)>();
        private readonly Dictionary<string, string> _needToGroup = new Dictionary<string, string>();
        private readonly Dictionary<string, float> _groupMaxPerBeaver = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _wbGroupTotals = new Dictionary<string, float>();
        private readonly Dictionary<string, float[]> _districtWb = new Dictionary<string, float[]>();
        private readonly Dictionary<string, int> _resourceTotals = new Dictionary<string, int>();
        private static readonly HashSet<string> _cropNames = new HashSet<string>
            { "Kohlrabi", "Soybean", "Corn", "Sunflower", "Eggplant", "Algae", "Cassava", "Mushroom", "Potato", "Wheat", "Carrot" };
        private static readonly Dictionary<string, string[]> _roleMap = new Dictionary<string, string[]> {
            {"water", new[]{"Pump","Tank","FluidDump","AquiferDrill"}},
            {"food", new[]{"FarmHouse","AquaticFarmhouse","EfficientFarmHouse","Gatherer","Grill","Gristmill","Bakery","FoodFactory","Fermenter","HydroponicGarden"}},
            {"housing", new[]{"Lodge","MiniLodge","DoubleLodge","TripleLodge","Rowhouse","Barrack"}},
            {"wood", new[]{"Lumberjack","LumberMill","IndustrialLumberMill","Forester"}},
            {"storage", new[]{"Warehouse","Pile","ReservePile","ReserveWarehouse","ReserveTank"}},
            {"power", new[]{"PowerWheel","LargePowerWheel","WaterWheel","WindTurbine","LargeWindTurbine","SteamEngine","PowerShaft","Clutch","GravityBattery"}},
            {"science", new[]{"Inventor","Numbercruncher","Observatory"}},
            {"production", new[]{"GearWorkshop","Smelter","Metalsmith","Scavenger","Mine","BotAssembler","BotPartFactory","PaperMill","PrintingPress","WoodWorkshop","Centrifuge","ExplosivesFactory","Refinery"}},
            {"leisure", new[]{"Campfire","Scratcher","Shower","DoubleShower","SwimmingPool","Carousel","MudPit","MudBath","ExercisePlaza","WindTunnel","Motivatorium","Lido","Detailer","ContemplationSpot","Agora","DanceHall","RooftopTerrace","MedicalBed","TeethGrindstone","Herbalist","DecontaminationPod","ChargingStation"}},
        };
        // reusable SBs for CollectBuildings full toon (inventory/recipes per building)
        private readonly System.Text.StringBuilder _invSb = new System.Text.StringBuilder(256);
        private readonly System.Text.StringBuilder _recSb = new System.Text.StringBuilder(128);
        // reusable collections for cluster methods
        private readonly Dictionary<long, int[]> _clusterCells = new Dictionary<long, int[]>();
        private readonly Dictionary<long, Dictionary<string, int>> _clusterSpecies = new Dictionary<long, Dictionary<string, int>>();
        private readonly List<long> _clusterSorted = new List<long>();
        // reusable collections for CollectTiles
        private readonly Dictionary<long, List<(string name, int z)>> _tileOccupants = new Dictionary<long, List<(string, int)>>();
        private readonly HashSet<long> _tileEntrances = new HashSet<long>();
        private readonly HashSet<long> _tileSeedlings = new HashSet<long>();
        private readonly HashSet<long> _tileDeadTiles = new HashSet<long>();
        private readonly System.Text.StringBuilder _tileSb = new System.Text.StringBuilder(256);
        // cached science data (refreshed lazily, avoids GetSpec on background thread)
        private string _cachedScienceJson;
        private int _cachedSciencePoints = -1;
        // cached distribution data (avoids GetComponent on background thread)
        private string _cachedDistributionJson;

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

        // Called from main thread (RefreshCachedState) to pre-build data that requires
        // Unity component access (GetComponent, GetSpec). Background thread reads the cached strings.
        public void RefreshMainThreadData()
        {
            try
            {
                // science: iterate BuildingService.Buildings + GetSpec on main thread
                var sjw = new TimberbotJw(4096);
                sjw.Reset().OpenObj().Prop("points", _scienceService.SciencePoints);
                sjw.Arr("unlockables");
                foreach (var building in _buildingService.Buildings)
                {
                    var bs = building.GetSpec<BuildingSpec>();
                    if (bs == null || bs.ScienceCost <= 0) continue;
                    var templateSpec = building.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                    var name = templateSpec?.TemplateName ?? "unknown";
                    sjw.OpenObj().Prop("name", name).Prop("cost", bs.ScienceCost).Prop("unlocked", _buildingUnlockingService.Unlocked(bs)).CloseObj();
                }
                sjw.CloseArr().CloseObj();
                _cachedScienceJson = sjw.ToString();
                _cachedSciencePoints = _scienceService.SciencePoints;
            }
            catch (System.Exception _ex) { TimberbotLog.Error("cache.science", _ex); }

            try
            {
                // distribution: iterate DistrictCenterRegistry + GetComponent on main thread
                var djw = new TimberbotJw(4096);
                djw.BeginArr();
                foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    var distSetting = dc.GetComponent<Timberborn.DistributionSystem.DistrictDistributionSetting>();
                    if (distSetting == null) continue;
                    djw.OpenObj().Prop("district", dc.DistrictName).Arr("goods");
                    foreach (var gs in distSetting.GoodDistributionSettings)
                        djw.OpenObj().Prop("good", gs.GoodId).Prop("importOption", gs.ImportOption.ToString()).Prop("exportThreshold", gs.ExportThreshold, "F0").CloseObj();
                    djw.CloseArr().CloseObj();
                }
                _cachedDistributionJson = djw.End();
            }
            catch (System.Exception _ex) { TimberbotLog.Error("cache.distribution", _ex); }
        }

        public string GetSettlementName()
        {
            try
            {
                var obj = (object)_gameCycleService;
                foreach (var field in new[] { "_singletonLoader", "_serializedWorldSupplier", "_sceneLoader", "_sceneParameters" })
                {
                    var fi = obj.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (fi == null) return "unknown";
                    obj = fi.GetValue(obj);
                    if (obj == null) return "unknown";
                }
                var saveRef = obj.GetType().GetProperty("SaveReference")?.GetValue(obj);
                if (saveRef == null) return "unknown";
                var settRef = saveRef.GetType().GetProperty("SettlementReference")?.GetValue(saveRef);
                if (settRef == null) return "unknown";
                var name = settRef.GetType().GetProperty("SettlementName")?.GetValue(settRef);
                return name?.ToString() ?? "unknown";
            }
            catch { return "unknown"; }
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
            // per-species breakdowns -- reuse field-level dicts
            _treeSpecies.Clear();
            _cropSpecies.Clear();
            var treeSpecies = _treeSpecies;
            var cropSpecies = _cropSpecies;
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
            // _cropNames is a static field, no per-call alloc
            foreach (var c in _cache.NaturalResources.Read)
            {
                if (c.Cuttable == null) continue; // skip non-cuttable resources
                if (c.Alive == 0) continue;            // dead stumps don't count
                if (_cropNames.Contains(c.Name))
                {
                    if (c.Grown != 0) cropReady++;      // harvestable now
                    else cropGrowing++;            // still growing
                    if (!cropSpecies.TryGetValue(c.Name, out var cs)) { cs = new int[2]; cropSpecies[c.Name] = cs; }
                    if (c.Grown != 0) cs[0]++; else cs[1]++;
                }
                else
                {
                    // trees: markedGrown = ready to chop, markedSeedling = marked but too young
                    if (c.Marked != 0 && c.Grown != 0) treeMarkedGrown++;
                    else if (c.Marked != 0 && c.Grown == 0) treeMarkedSeedling++;
                    else if (c.Marked == 0 && c.Grown != 0) treeUnmarkedGrown++;
                    if (!treeSpecies.TryGetValue(c.Name, out var ts)) { ts = new int[3]; treeSpecies[c.Name] = ts; }
                    if (c.Marked != 0 && c.Grown != 0) ts[0]++;
                    else if (c.Marked == 0 && c.Grown != 0) ts[1]++;
                    else if (c.Marked != 0 && c.Grown == 0) ts[2]++;
                }
            }

            // --- BUILDINGS ---
            // Aggregate housing, employment, alerts, DC location, and role counts -- PER DISTRICT.
            int dcX = 0, dcY = 0, dcZ = 0;
            bool foundDC = false;
            _roleCounts.Clear();
            _districtStats.Clear();
            _districtDCs.Clear();
            var roleCounts = _roleCounts;
            var districtStats = _districtStats;
            var districtDCs = _districtDCs;
            var roleMap = _roleMap;

            foreach (var c in _cache.Buildings.Read)
            {
                var dname = c.District ?? "_unknown";
                if (!districtStats.TryGetValue(dname, out var ds)) { ds = new int[7]; districtStats[dname] = ds; }
                if (c.Dwelling != null)
                {
                    occupiedBeds += c.Dwellers;
                    totalBeds += c.MaxDwellers;
                    ds[0] += c.Dwellers;
                    ds[1] += c.MaxDwellers;
                }
                if (c.Workplace != null)
                {
                    assignedWorkers += c.AssignedWorkers;
                    totalVacancies += c.DesiredWorkers;
                    ds[2] += c.AssignedWorkers;
                    ds[3] += c.DesiredWorkers;
                    if (c.DesiredWorkers > 0 && c.AssignedWorkers < c.DesiredWorkers)
                    { alertUnstaffed++; ds[4]++; }
                }
                if (c.IsConsumer != 0 && c.Powered == 0)
                { alertUnpowered++; ds[5]++; }
                if (c.Unreachable != 0)
                { alertUnreachable++; ds[6]++; }

                // DC detection -- per district
                if (c.Name != null && c.Name.Contains("DistrictCenter"))
                {
                    var dcOri = c.Orientation ?? "south";
                    int eX = c.X + 1, eY = c.Y + 1;
                    if (dcOri == "south") { eX = c.X + 1; eY = c.Y - 1; }
                    else if (dcOri == "north") { eX = c.X + 1; eY = c.Y + 3; }
                    else if (dcOri == "east") { eX = c.X + 3; eY = c.Y + 1; }
                    else if (dcOri == "west") { eX = c.X - 1; eY = c.Y + 1; }
                    districtDCs[dname] = (c.X, c.Y, c.Z, dcOri, eX, eY);
                    if (!foundDC) { foundDC = true; dcX = c.X; dcY = c.Y; dcZ = c.Z; }
                }

                // role counting
                string name = c.Name ?? "";
                if (name == "Path") { roleCounts["paths"] = roleCounts.GetValueOrDefault("paths") + 1; continue; }
                bool matched = false;
                foreach (var kv in roleMap)
                {
                    foreach (var keyword in kv.Value)
                    {
                        if (name.Contains(keyword)) { roleCounts[kv.Key] = roleCounts.GetValueOrDefault(kv.Key) + 1; matched = true; break; }
                    }
                    if (matched) break;
                }
                if (!matched) roleCounts["other"] = roleCounts.GetValueOrDefault("other") + 1;
            }

            // faction
            string faction = TimberbotEntityCache.FactionSuffix == ".Folktails" ? "Folktails" : TimberbotEntityCache.FactionSuffix == ".IronTeeth" ? "IronTeeth" : "unknown";

            // --- BEAVERS + WELLBEING CATEGORIES ---
            // miserable = wellbeing below 4 (struggling, may die soon)
            // critical = any need below warning threshold (immediate danger)
            // Also accumulate per-category wellbeing (avoids client needing separate /api/wellbeing call)
            var beaverNeeds = _factionNeedService.GetBeaverNeeds();
            _needToGroup.Clear();
            _groupMaxPerBeaver.Clear();
            var needToGroup = _needToGroup;
            var groupMaxPerBeaver = _groupMaxPerBeaver;
            foreach (var ns in beaverNeeds)
            {
                if (string.IsNullOrEmpty(ns.NeedGroupId)) continue;
                needToGroup[ns.Id] = ns.NeedGroupId;
                groupMaxPerBeaver[ns.NeedGroupId] = groupMaxPerBeaver.GetValueOrDefault(ns.NeedGroupId) + ns.FavorableWellbeing;
            }
            _wbGroupTotals.Clear();
            _districtWb.Clear();
            var wbGroupTotals = _wbGroupTotals;
            var districtWb = _districtWb;
            foreach (var c in _cache.Beavers.Read)
            {
                totalWellbeing += c.Wellbeing;
                beaverCount++;
                if (c.Wellbeing < 4) miserable++;
                if (c.AnyCritical != 0) critical++;
                var bDist = c.District ?? "_unknown";
                if (!districtWb.TryGetValue(bDist, out var dw)) { dw = new float[4]; districtWb[bDist] = dw; }
                dw[0] += c.Wellbeing; dw[1]++; if (c.Wellbeing < 4) dw[2]++; if (c.AnyCritical != 0) dw[3]++;
                if (c.Needs != null)
                    foreach (var n in c.Needs)
                        if (needToGroup.ContainsKey(n.Id))
                            wbGroupTotals[needToGroup[n.Id]] = wbGroupTotals.GetValueOrDefault(needToGroup[n.Id]) + n.Wellbeing;
            }

            // --- DERIVED STATS ---
            int totalAdults = 0, totalChildren = 0, totalBots = 0;
            foreach (var dc in _cache.Districts)
            { totalAdults += dc.Adults; totalChildren += dc.Children; totalBots += dc.Bots; }
            // homeless = beavers with no bed (children count, adults count)
            int homeless = System.Math.Max(0, beaverCount - occupiedBeds);
            // unemployed = adults not assigned to any workplace (available for hauling)
            int unemployed = System.Math.Max(0, totalAdults - assignedWorkers);
            float avgWellbeing = beaverCount > 0 ? totalWellbeing / beaverCount : 0;
            int currentSpeed = System.Array.IndexOf(SpeedScale, _speedManager.CurrentSpeed);
            if (currentSpeed < 0) currentSpeed = 0;

            if (format == "json")
            {
                var jj = _cache.Jw.BeginObj();
                jj.Prop("settlement", GetSettlementName());
                jj.Prop("faction", faction);
                jj.Obj("time")
                    .Prop("dayNumber", _dayNightCycle.DayNumber)
                    .Prop("dayProgress", (float)_dayNightCycle.DayProgress)
                    .Prop("partialDayNumber", (float)_dayNightCycle.PartialDayNumber)
                    .Prop("speed", currentSpeed)
                    .CloseObj();
                jj.Obj("weather")
                    .Prop("cycle", _gameCycleService.Cycle)
                    .Prop("cycleDay", _gameCycleService.CycleDay)
                    .Prop("isHazardous", _weatherService.IsHazardousWeather)
                    .Prop("temperateWeatherDuration", _weatherService.TemperateWeatherDuration)
                    .Prop("hazardousWeatherDuration", _weatherService.HazardousWeatherDuration)
                    .Prop("cycleLengthInDays", _weatherService.CycleLengthInDays)
                    .CloseObj();
                // districts with population + resources + housing + employment inline
                jj.Arr("districts");
                foreach (var dc in _cache.Districts)
                {
                    jj.OpenObj().Prop("name", dc.Name);
                    jj.Obj("population")
                        .Prop("adults", dc.Adults).Prop("children", dc.Children).Prop("bots", dc.Bots)
                        .CloseObj();
                    jj.Obj("resources");
                    if (dc.Resources != null)
                        foreach (var kvp in dc.Resources) jj.Prop(kvp.Key, kvp.Value.all);
                    jj.CloseObj();
                    var ds = districtStats.GetValueOrDefault(dc.Name);
                    int dBeds = ds != null ? ds[1] : 0;
                    int dOccBeds = ds != null ? ds[0] : 0;
                    int dPop = dc.Adults + dc.Children + dc.Bots;
                    jj.Obj("housing")
                        .Prop("occupiedBeds", dOccBeds).Prop("totalBeds", dBeds)
                        .Prop("homeless", System.Math.Max(0, dPop - dOccBeds))
                        .CloseObj();
                    int dAssigned = ds != null ? ds[2] : 0;
                    int dVacancies = ds != null ? ds[3] : 0;
                    jj.Obj("employment")
                        .Prop("assigned", dAssigned).Prop("vacancies", dVacancies)
                        .Prop("unemployed", System.Math.Max(0, dc.Adults - dAssigned))
                        .CloseObj();
                    var dwb = districtWb.GetValueOrDefault(dc.Name);
                    float dAvgWb = dwb != null && dwb[1] > 0 ? dwb[0] / dwb[1] : 0;
                    jj.Obj("wellbeing")
                        .Prop("average", (float)System.Math.Round(dAvgWb, 1), "F1")
                        .Prop("miserable", (int)(dwb != null ? dwb[2] : 0))
                        .Prop("critical", (int)(dwb != null ? dwb[3] : 0))
                        .CloseObj();
                    if (districtDCs.TryGetValue(dc.Name, out var ddc))
                        jj.Obj("dc").Prop("x", ddc.x).Prop("y", ddc.y).Prop("z", ddc.z).Prop("orientation", ddc.orientation).Prop("entranceX", ddc.entranceX).Prop("entranceY", ddc.entranceY).CloseObj();
                    jj.CloseObj();
                }
                jj.CloseArr();
                jj.Obj("trees")
                    .Prop("markedGrown", treeMarkedGrown)
                    .Prop("markedSeedling", treeMarkedSeedling)
                    .Prop("unmarkedGrown", treeUnmarkedGrown);
                jj.Arr("species");
                foreach (var kv in treeSpecies)
                    jj.OpenObj().Prop("name", kv.Key).Prop("markedGrown", kv.Value[0]).Prop("unmarkedGrown", kv.Value[1]).Prop("seedling", kv.Value[2]).CloseObj();
                jj.CloseArr().CloseObj();
                jj.Obj("crops")
                    .Prop("ready", cropReady)
                    .Prop("growing", cropGrowing);
                jj.Arr("species");
                foreach (var kv in cropSpecies)
                    jj.OpenObj().Prop("name", kv.Key).Prop("ready", kv.Value[0]).Prop("growing", kv.Value[1]).CloseObj();
                jj.CloseArr().CloseObj();
                jj.Obj("wellbeing")
                    .Prop("average", (float)avgWellbeing, "F1")
                    .Prop("miserable", miserable)
                    .Prop("critical", critical);
                jj.Arr("categories");
                foreach (var kv in groupMaxPerBeaver)
                {
                    float avg = beaverCount > 0 ? wbGroupTotals.GetValueOrDefault(kv.Key) / beaverCount : 0;
                    float max = kv.Value;
                    jj.OpenObj().Prop("group", kv.Key).Prop("current", (float)System.Math.Round(avg, 1), "F1").Prop("max", (float)System.Math.Round(max, 1), "F1").CloseObj();
                }
                jj.CloseArr().CloseObj();
                jj.Prop("science", _scienceService.SciencePoints);
                jj.Obj("alerts")
                    .Prop("unstaffed", alertUnstaffed)
                    .Prop("unpowered", alertUnpowered)
                    .Prop("unreachable", alertUnreachable)
                    .CloseObj();
                jj.Obj("buildings");
                foreach (var kv in roleCounts) jj.Prop(kv.Key, kv.Value);
                jj.CloseObj();
                // nearby clusters -- built inline from cached data, no Newtonsoft
                WriteClustersFiltered(jj, "treeClusters", _cache.NaturalResources.Read, null, dcX, dcY, dcZ, 40, 10, 5);
                WriteClustersFiltered(jj, "foodClusters", _cache.NaturalResources.Read, TimberbotEntityCache.TreeSpecies, dcX, dcY, dcZ, 40, 10, 5);
                return jj.End();
            }

            // build flat summary matching TOON output format
            var jw = _cache.Jw.BeginObj();
            jw.Prop("settlement", GetSettlementName());
            jw.Prop("faction", faction);

            // time
            jw.Prop("day", _dayNightCycle.DayNumber);
            jw.Prop("dayProgress", (float)_dayNightCycle.DayProgress);
            jw.Prop("speed", currentSpeed);

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

            // population + resources -- aggregate across all districts before emitting
            int totalFood = 0, totalWater = 0, logStock = 0, plankStock = 0, gearStock = 0;
            _resourceTotals.Clear();
            var resourceTotals = _resourceTotals;
            foreach (var dc in _cache.Districts)
            {
                if (dc.Resources != null)
                {
                    foreach (var kvp in dc.Resources)
                    {
                        int avail = kvp.Value.available;
                        resourceTotals[kvp.Key] = resourceTotals.GetValueOrDefault(kvp.Key) + avail;
                        if (kvp.Key == "Water") totalWater += avail;
                        else if (kvp.Key == "Berries" || kvp.Key == "Kohlrabi" || kvp.Key == "Carrot" || kvp.Key == "Potato"
                              || kvp.Key == "Wheat" || kvp.Key == "Bread" || kvp.Key == "Cassava" || kvp.Key == "Corn"
                              || kvp.Key == "Eggplant" || kvp.Key == "Soybean" || kvp.Key == "MapleSyrup")
                            totalFood += avail;
                        else if (kvp.Key == "Log") logStock += avail;
                        else if (kvp.Key == "Plank") plankStock += avail;
                        else if (kvp.Key == "Gear") gearStock += avail;
                    }
                }
            }
            jw.Prop("adults", totalAdults);
            jw.Prop("children", totalChildren);
            jw.Prop("bots", totalBots);
            foreach (var kvp in resourceTotals)
                jw.Key(kvp.Key).Int(kvp.Value);

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

            // brain fields
            jw.Obj("buildings");
            foreach (var kv in roleCounts) jw.Prop(kv.Key, kv.Value);
            jw.CloseObj();

            return jw.End();
        }

        // PERF: iterates _cache.Buildings.Read instead of all entities.
        public object CollectAlerts(string format = "toon", int limit = 100, int offset = 0)
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
                if (c.IsConsumer != 0 && c.Powered == 0)
                {
                    if (offset > 0 && skipped < offset) { skipped++; }
                    else if (!paginated || emitted < limit)
                    {
                        jw.OpenObj().Prop("type", "unpowered").Prop("id", c.Id).Prop("name", c.Name).CloseObj();
                        emitted++;
                    }
                }
                if (c.Unreachable != 0)
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
        public object CollectTreeClusters(string format = "toon", int cellSize = 10, int top = 5)
        {
            _clusterCells.Clear(); _clusterSpecies.Clear();
            var cells = _clusterCells;
            var cellSpecies = _clusterSpecies;
            foreach (var nr in _cache.NaturalResources.Read)
            {
                if (nr.Cuttable == null) continue;
                if (nr.Alive == 0) continue;

                int cx = nr.X / cellSize * cellSize + cellSize / 2;
                int cy = nr.Y / cellSize * cellSize + cellSize / 2;
                long key = (long)cx * 100000 + cy;

                if (!cells.ContainsKey(key))
                { cells[key] = new int[] { 0, 0, cx, cy, nr.Z }; cellSpecies[key] = new Dictionary<string, int>(); }

                cells[key][1]++;
                cellSpecies[key][nr.Name] = cellSpecies[key].GetValueOrDefault(nr.Name) + 1;
                if (nr.Grown != 0)
                    cells[key][0]++;
            }

            _clusterSorted.Clear(); _clusterSorted.AddRange(cells.Keys);
            _clusterSorted.Sort((a, b) => cells[b][0].CompareTo(cells[a][0]));
            var jw = _cache.Jw.BeginArr();
            for (int i = 0; i < System.Math.Min(top, _clusterSorted.Count); i++)
            {
                var s = cells[_clusterSorted[i]];
                jw.OpenObj().Prop("x", s[2]).Prop("y", s[3]).Prop("z", s[4]).Prop("grown", s[0]).Prop("total", s[1]);
                var sp = cellSpecies[_clusterSorted[i]];
                jw.Obj("species");
                foreach (var kv in sp) jw.Prop(kv.Key, kv.Value);
                jw.CloseObj().CloseObj();
            }
            return jw.End();
        }

        public object CollectFoodClusters(string format = "toon", int cellSize = 10, int top = 5)
        {
            _clusterCells.Clear(); _clusterSpecies.Clear();
            var cells = _clusterCells;
            var cellSpecies = _clusterSpecies;
            foreach (var nr in _cache.NaturalResources.Read)
            {
                if (nr.Gatherable == null) continue;
                if (TimberbotEntityCache.TreeSpecies.Contains(nr.Name)) continue;
                if (nr.Alive == 0) continue;

                int cx = nr.X / cellSize * cellSize + cellSize / 2;
                int cy = nr.Y / cellSize * cellSize + cellSize / 2;
                long key = (long)cx * 100000 + cy;

                if (!cells.ContainsKey(key))
                { cells[key] = new int[] { 0, 0, cx, cy, nr.Z }; cellSpecies[key] = new Dictionary<string, int>(); }

                cells[key][1]++;
                cellSpecies[key][nr.Name] = cellSpecies[key].GetValueOrDefault(nr.Name) + 1;
                if (nr.Grown != 0)
                    cells[key][0]++;
            }

            _clusterSorted.Clear(); _clusterSorted.AddRange(cells.Keys);
            _clusterSorted.Sort((a, b) => cells[b][0].CompareTo(cells[a][0]));
            var jw = _cache.Jw.BeginArr();
            for (int i = 0; i < System.Math.Min(top, _clusterSorted.Count); i++)
            {
                var s = cells[_clusterSorted[i]];
                jw.OpenObj().Prop("x", s[2]).Prop("y", s[3]).Prop("z", s[4]).Prop("grown", s[0]).Prop("total", s[1]);
                var sp = cellSpecies[_clusterSorted[i]];
                jw.Obj("species");
                foreach (var kv in sp) jw.Prop(kv.Key, kv.Value);
                jw.CloseObj().CloseObj();
            }
            return jw.End();
        }

        // Write cluster data directly into an existing JW, filtered by proximity to DC.
        // Used by CollectSummary json to avoid serialize-then-deserialize via Newtonsoft.
        // excludeSpecies=null for tree clusters, excludeSpecies=TreeSpecies for food clusters.
        private void WriteClustersFiltered(TimberbotJw jw, string key,
            List<TimberbotEntityCache.CachedNaturalResource> resources,
            System.Collections.Generic.HashSet<string> excludeSpecies,
            int dcX, int dcY, int dcZ, int maxDist, int cellSize, int top)
        {
            _clusterCells.Clear(); _clusterSpecies.Clear();
            foreach (var nr in resources)
            {
                if (excludeSpecies == null) { if (nr.Cuttable == null) continue; }
                else { if (nr.Gatherable == null || excludeSpecies.Contains(nr.Name)) continue; }
                if (nr.Alive == 0) continue;

                int cx = nr.X / cellSize * cellSize + cellSize / 2;
                int cy = nr.Y / cellSize * cellSize + cellSize / 2;
                if (nr.Z != dcZ || System.Math.Abs(cx - dcX) + System.Math.Abs(cy - dcY) > maxDist) continue;
                long k = (long)cx * 100000 + cy;

                if (!_clusterCells.ContainsKey(k))
                { _clusterCells[k] = new int[] { 0, 0, cx, cy, nr.Z }; _clusterSpecies[k] = new Dictionary<string, int>(); }
                _clusterCells[k][1]++;
                _clusterSpecies[k][nr.Name] = _clusterSpecies[k].GetValueOrDefault(nr.Name) + 1;
                if (nr.Grown != 0) _clusterCells[k][0]++;
            }
            _clusterSorted.Clear(); _clusterSorted.AddRange(_clusterCells.Keys);
            _clusterSorted.Sort((a, b) => _clusterCells[b][0].CompareTo(_clusterCells[a][0]));
            jw.Arr(key);
            for (int i = 0; i < System.Math.Min(top, _clusterSorted.Count); i++)
            {
                var s = _clusterCells[_clusterSorted[i]];
                jw.OpenObj().Prop("x", s[2]).Prop("y", s[3]).Prop("z", s[4]).Prop("grown", s[0]).Prop("total", s[1]);
                var sp = _clusterSpecies[_clusterSorted[i]];
                jw.Obj("species");
                foreach (var kv in sp) jw.Prop(kv.Key, kv.Value);
                jw.CloseObj().CloseObj();
            }
            jw.CloseArr();
        }

        public object CollectTime()
        {
            return _cache.Jw.Result(("dayNumber", _dayNightCycle.DayNumber), ("dayProgress", _dayNightCycle.DayProgress), ("partialDayNumber", _dayNightCycle.PartialDayNumber));
        }

        public object CollectWeather()
        {
            return _cache.Jw.Result(("cycle", _gameCycleService.Cycle), ("cycleDay", _gameCycleService.CycleDay), ("isHazardous", _weatherService.IsHazardousWeather), ("temperateWeatherDuration", _weatherService.TemperateWeatherDuration), ("hazardousWeatherDuration", _weatherService.HazardousWeatherDuration), ("cycleLengthInDays", _weatherService.CycleLengthInDays));
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
                    if (!string.IsNullOrEmpty(dc.ResourcesToon)) jw.Raw(dc.ResourcesToon);
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

                // full detail: uniform schema -- always emit all fields with defaults
                jw.Prop("constructionPriority", c.ConstructionPriority ?? "")
                    .Prop("workplacePriority", c.WorkplacePriorityStr ?? "")
                    .Prop("maxWorkers", c.Workplace != null ? c.MaxWorkers : 0)
                    .Prop("desiredWorkers", c.Workplace != null ? c.DesiredWorkers : 0)
                    .Prop("assignedWorkers", c.Workplace != null ? c.AssignedWorkers : 0)
                    .Prop("reachable", c.Reachability != null ? (c.Unreachable == 0 ? 1 : 0) : 0)
                    .Prop("powered", c.Powered)
                    .Prop("isGenerator", c.IsGenerator)
                    .Prop("isConsumer", c.IsConsumer)
                    .Prop("nominalPowerInput", c.PowerNode != null ? c.NominalPowerInput : 0)
                    .Prop("nominalPowerOutput", c.PowerNode != null ? c.NominalPowerOutput : 0)
                    .Prop("powerDemand", c.PowerNode != null ? c.PowerDemand : 0)
                    .Prop("powerSupply", c.PowerNode != null ? c.PowerSupply : 0)
                    .Prop("buildProgress", c.Site != null ? c.BuildProgress : 0f)
                    .Prop("materialProgress", c.Site != null ? c.MaterialProgress : 0f)
                    .Prop("hasMaterials", c.HasMaterials)
                    .Prop("stock", c.Capacity > 0 ? c.Stock : 0)
                    .Prop("capacity", c.Capacity)
                    .Prop("dwellers", c.Dwelling != null ? c.Dwellers : 0)
                    .Prop("maxDwellers", c.Dwelling != null ? c.MaxDwellers : 0)
                    .Prop("floodgate", c.HasFloodgate)
                    .Prop("height", c.HasFloodgate != 0 ? c.FloodgateHeight : 0f, "F1")
                    .Prop("maxHeight", c.HasFloodgate != 0 ? c.FloodgateMaxHeight : 0f, "F1")
                    .Prop("isClutch", c.HasClutch)
                    .Prop("clutchEngaged", c.ClutchEngaged)
                    .Prop("currentRecipe", c.Manufactory != null ? (c.CurrentRecipe ?? "") : "")
                    .Prop("productionProgress", c.Manufactory != null ? c.ProductionProgress : 0f)
                    .Prop("readyToProduce", c.ReadyToProduce)
                    .Prop("effectRadius", c.EffectRadius)
                    .Prop("isWonder", c.HasWonder)
                    .Prop("wonderActive", c.WonderActive);

                // inventory: flat string for toon, object for json
                if (format == "toon")
                {
                    _invSb.Clear();
                    if (c.Inventory != null)
                        foreach (var kvp in c.Inventory) { if (_invSb.Length > 0) _invSb.Append('/'); _invSb.Append(kvp.Key).Append(':').Append(kvp.Value); }
                    jw.Prop("inventory", _invSb.ToString());

                    _recSb.Clear();
                    if (c.Recipes != null)
                        for (int ri = 0; ri < c.Recipes.Count; ri++) { if (_recSb.Length > 0) _recSb.Append('/'); _recSb.Append(c.Recipes[ri]); }
                    jw.Prop("recipes", _recSb.ToString());
                }
                else
                {
                    jw.Obj("inventory");
                    if (c.Inventory != null) foreach (var kvp in c.Inventory) jw.Key(kvp.Key).Int(kvp.Value);
                    jw.CloseObj();
                    jw.Arr("recipes");
                    if (c.Recipes != null) for (int ri = 0; ri < c.Recipes.Count; ri++) jw.Str(c.Recipes[ri]);
                    jw.CloseArr();
                }
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

        public object CollectTrees(string format = "toon", int limit = 100, int offset = 0, string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
            => CollectNaturalResourcesJw(_cache.Jw, TimberbotEntityCache.TreeSpecies, limit, offset, filterName, filterX, filterY, filterRadius);
        public object CollectCrops(string format = "toon", int limit = 100, int offset = 0, string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
            => CollectNaturalResourcesJw(_cache.Jw, TimberbotEntityCache.CropSpecies, limit, offset, filterName, filterX, filterY, filterRadius);

        public object CollectGatherables(string format = "toon", int limit = 100, int offset = 0,
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
                            if (n.Critical != 0) critical = critical.Length > 0 ? critical + "+" + n.Id : n.Id;
                            else if (n.Favorable == 0 && n.Active != 0) unmet = unmet.Length > 0 ? unmet + "+" + n.Id : n.Id;
                        }
                    }
                    jw.Prop("critical", critical).Prop("unmet", unmet).CloseObj();
                    continue;
                }

                // full detail: uniform schema -- always emit all fields with defaults
                jw.Prop("anyCritical", c.AnyCritical)
                    .Prop("workplace", c.Workplace ?? "")
                    .Prop("district", c.District ?? "")
                    .Prop("hasHome", c.HasHome)
                    .Prop("contaminated", c.Contaminated)
                    .Prop("lifeProgress", c.Life != null ? c.LifeProgress : 0f)
                    .Prop("deterioration", c.Deteriorable != null ? c.DeteriorationProgress : 0f, "F3")
                    .Prop("liftingCapacity", c.Carrier != null ? c.LiftingCapacity : 0)
                    .Prop("overburdened", c.Overburdened)
                    .Prop("carrying", c.IsCarrying != 0 ? c.CarryingGood : "")
                    .Prop("carryAmount", c.IsCarrying != 0 ? c.CarryAmount : 0);

                // needs array
                jw.Arr("needs");
                if (c.Needs != null)
                {
                    foreach (var n in c.Needs)
                    {
                        if (!fullDetail && c.IsBot == 0 && n.Active == 0) continue;
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
        public object CollectPowerNetworks(string format = "toon")
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
            return _cache.Jw.Result(("endHours", _workingHoursManager.EndHours), ("areWorkingHours", _workingHoursManager.AreWorkingHours));
        }

        // Science points and unlockable buildings with costs and status.
        // Data pre-built on main thread by RefreshMainThreadData() to avoid GetSpec on background thread.
        public object CollectScience(string format = "toon")
        {
            return _cachedScienceJson ?? _cache.Jw.Error("not_ready");
        }

        // Population wellbeing breakdown by need group (SocialLife, Fun, Nutrition, etc).
        // Aggregates across all beavers from cached need data.
        public object CollectWellbeing(string format = "toon")
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
            catch (System.Exception ex) { TimberbotLog.Error("wellbeing", ex); return _cache.Jw.Error("operation_failed: " + ex.Message); }
        }

        // Game event history (droughts, deaths, etc)
        public object CollectNotifications(string format = "toon", int limit = 100, int offset = 0)
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

        // Import/export settings per good per district.
        // Data pre-built on main thread by RefreshMainThreadData() to avoid GetComponent on background thread.
        public object CollectDistribution(string format = "toon")
        {
            return _cachedDistributionJson ?? _cache.Jw.Error("not_ready");
        }

        // Tile data for a rectangular region. Returns terrain height, water depth,
        // badwater contamination, occupants (with vertical stacking), soil moisture,
        // and soil contamination per tile.
        //
        // Uses IThreadSafeWaterMap and IThreadSafeColumnTerrainMap which are designed
        // for background thread access. Occupancy built from cached entity indexes.
        // Soil services wrapped in try/catch as a safety net.
        public object CollectTiles(string format = "toon", int x1 = 0, int y1 = 0, int x2 = 0, int y2 = 0)
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
            _tileOccupants.Clear();
            _tileEntrances.Clear();
            _tileSeedlings.Clear();
            _tileDeadTiles.Clear();
            var occupants = _tileOccupants;
            var entrances = _tileEntrances;
            var seedlings = _tileSeedlings;
            var deadTiles = _tileDeadTiles;

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
                if (c.HasEntrance != 0)
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
                    if (r.Grown == 0) seedlings.Add(key);
                    if (r.Alive == 0) deadTiles.Add(key);
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
                    // Water check: iterate water columns top-down for depth and contamination.
                    float waterDepth = 0f;
                    float waterContamination = 0f;
                    try
                    {
                        int wIdx2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
                        int wColCount = _waterMap.ColumnCount(wIdx2D);
                        for (int ci = wColCount - 1; ci >= 0; ci--)
                        {
                            int wIdx3D = ci * _mapIndexService.VerticalStride + wIdx2D;
                            var col = _waterMap.WaterColumns[wIdx3D];
                            if (col.WaterDepth > 0)
                            {
                                if (waterDepth == 0f) waterDepth = col.WaterDepth;
                                if (col.Contamination > 0) { waterContamination = col.Contamination; break; }
                            }
                        }
                    }
                    catch (System.Exception _ex) { TimberbotLog.Error("map.water", _ex); }

                    long key = (long)x * 100000 + y;
                    occupants.TryGetValue(key, out var occList);

                    jw.OpenObj().Prop("x", x).Prop("y", y).Prop("terrain", terrainHeight);
                    jw.Prop("water", waterDepth, "F1");

                    // uniform schema -- always emit all fields
                    jw.Prop("badwater", (float)System.Math.Round(waterContamination, 2));
                    jw.Prop("entrance", entrances.Contains(key) ? 1 : 0);
                    jw.Prop("seedling", seedlings.Contains(key) ? 1 : 0);
                    jw.Prop("dead", deadTiles.Contains(key) ? 1 : 0);
                    int contaminated = 0;
                    try { contaminated = _soilContaminationService.SoilIsContaminated(new Vector3Int(x, y, terrainHeight)) ? 1 : 0; } catch (System.Exception _ex) { TimberbotLog.Error("map.soil", _ex); }
                    jw.Prop("contaminated", contaminated);
                    int moist = 0;
                    try { moist = _soilMoistureService.SoilIsMoist(new Vector3Int(x, y, terrainHeight)) ? 1 : 0; } catch (System.Exception _ex) { TimberbotLog.Error("map.moisture", _ex); }
                    jw.Prop("moist", moist);

                    // occupants last for readability (variable-length string)
                    if (format == "toon")
                    {
                        string occStr = "";
                        if (occList != null)
                        {
                            occList.Sort((a, b) => a.name != b.name ? string.Compare(a.name, b.name) : a.z - b.z);
                            _tileSb.Clear();
                            var sb = _tileSb;
                            int si = 0;
                            while (si < occList.Count)
                            {
                                if (sb.Length > 0) sb.Append('/');
                                string n = occList[si].name;
                                int zlo = occList[si].z, zhi = zlo;
                                while (si + 1 < occList.Count && occList[si + 1].name == n && occList[si + 1].z == zhi + 1)
                                {
                                    zhi = occList[++si].z;
                                }
                                sb.Append(n).Append(":z").Append(zlo);
                                if (zhi > zlo) sb.Append('-').Append(zhi);
                                si++;
                            }
                            occStr = sb.ToString();
                        }
                        jw.Prop("occupants", occStr);
                    }
                    else
                    {
                        jw.Arr("occupants");
                        if (occList != null) foreach (var o in occList) jw.OpenObj().Prop("name", o.name).Prop("z", o.z).CloseObj();
                        jw.CloseArr();
                    }
                    jw.CloseObj();
                }
            }
            jw.CloseArr().CloseObj();
            return jw.ToString();
        }
    }
}
