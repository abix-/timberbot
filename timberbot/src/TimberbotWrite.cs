// TimberbotWrite.cs -- All state-modifying API endpoints.
//
// POST requests that change game state: speed, workers, priorities, crops, trees,
// stockpiles, floodgates, recipes, science, distribution, migration, work hours.
// Also includes CollectTiles (the map/tiles endpoint) which reads live water state
// and must run on the main thread.
//
// All write methods run on the Unity main thread (queued via DrainRequests).
// They call game services directly (not cached data) and return result objects
// that TimberbotHttpServer serializes to JSON.
//
// Pattern: each method takes primitive params, finds the entity, calls the game
// service, returns {id, name, field: newValue} on success or {error} on failure.

using System;
using System.Collections.Generic;
using Timberborn.BuilderPrioritySystem;
using Timberborn.Buildings;
using Timberborn.BlockObjectTools;
using Timberborn.MapIndexSystem;
using Timberborn.TerrainSystem;
using Timberborn.WaterSystem;
using Timberborn.Forestry;
using Timberborn.Planting;
using Timberborn.GameDistricts;
using Timberborn.InventorySystem;
using Timberborn.PrioritySystem;
using Timberborn.TimeSystem;
using Timberborn.WaterBuildings;
using Timberborn.WorkSystem;
using Timberborn.ScienceSystem;
using Timberborn.NotificationSystem;
using Timberborn.PowerManagement;
using Timberborn.SoilContaminationSystem;
using Timberborn.Hauling;
using Timberborn.Workshops;
using Timberborn.Fields;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;
using Timberborn.SoilMoistureSystem;
using Timberborn.NeedSpecs;
using Timberborn.GameFactionSystem;
using UnityEngine;

namespace Timberbot
{
    // All POST endpoint handlers that modify game state.
    //
    // These run on the Unity main thread (queued via DrainRequests in TimberbotHttpServer).
    // Each method takes primitive params from the HTTP body, finds the target entity
    // via FindEntity(), calls the game service to make the change, and returns a result
    // object that gets serialized to JSON.
    //
    // Pattern: every write method returns {id, name, field: newValue} on success
    // or {error: "message"} on failure. The HTTP server serializes either to JSON.
    public class TimberbotWrite
    {
        // game services for terrain, water, soil (used by tiles endpoint)
        private readonly ITerrainService _terrainService;
        private readonly IThreadSafeWaterMap _waterMap;
        private readonly MapIndexService _mapIndexService;
        private readonly IThreadSafeColumnTerrainMap _terrainMap;
        private readonly ISoilContaminationService _soilContaminationService;
        private readonly ISoilMoistureService _soilMoistureService;
        // game services for write operations
        private readonly SpeedManager _speedManager;
        private readonly RecipeSpecService _recipeSpecService;
        private readonly TreeCuttingArea _treeCuttingArea;
        private readonly PlantingService _plantingService;
        private readonly PlantingAreaValidator _plantingAreaValidator;
        private readonly ScienceService _scienceService;
        private readonly BuildingService _buildingService;
        private readonly BuildingUnlockingService _buildingUnlockingService;
        private readonly ToolButtonService _toolButtonService;
        private readonly ToolUnlockingService _toolUnlockingService;
        private readonly FactionNeedService _factionNeedService;
        private readonly NotificationSaver _notificationSaver;
        private readonly DistrictCenterRegistry _districtCenterRegistry;
        private readonly WorkingHoursManager _workingHoursManager;
        private readonly PopulationDistributorRetriever _populationDistributorRetriever;
        private readonly TimberbotEntityCache _cache;

        public TimberbotWrite(
            ITerrainService terrainService,
            IThreadSafeWaterMap waterMap,
            MapIndexService mapIndexService,
            IThreadSafeColumnTerrainMap terrainMap,
            ISoilContaminationService soilContaminationService,
            ISoilMoistureService soilMoistureService,
            SpeedManager speedManager,
            RecipeSpecService recipeSpecService,
            TreeCuttingArea treeCuttingArea,
            PlantingService plantingService,
            PlantingAreaValidator plantingAreaValidator,
            ScienceService scienceService,
            BuildingService buildingService,
            BuildingUnlockingService buildingUnlockingService,
            ToolButtonService toolButtonService,
            ToolUnlockingService toolUnlockingService,
            FactionNeedService factionNeedService,
            NotificationSaver notificationSaver,
            DistrictCenterRegistry districtCenterRegistry,
            TimberbotEntityCache cache)
        {
            _terrainService = terrainService;
            _waterMap = waterMap;
            _mapIndexService = mapIndexService;
            _terrainMap = terrainMap;
            _soilContaminationService = soilContaminationService;
            _soilMoistureService = soilMoistureService;
            _speedManager = speedManager;
            _recipeSpecService = recipeSpecService;
            _treeCuttingArea = treeCuttingArea;
            _plantingService = plantingService;
            _plantingAreaValidator = plantingAreaValidator;
            _scienceService = scienceService;
            _buildingService = buildingService;
            _buildingUnlockingService = buildingUnlockingService;
            _toolButtonService = toolButtonService;
            _toolUnlockingService = toolUnlockingService;
            _factionNeedService = factionNeedService;
            _notificationSaver = notificationSaver;
            _districtCenterRegistry = districtCenterRegistry;
            _cache = cache;
        }

        private static readonly int[] SpeedScale = TimberbotRead.SpeedScale;

        // ================================================================
        // WRITE ENDPOINTS -- Tier 1
        // ================================================================

        // game speed 0-3, mapped to internal values 0,1,3,7
        public object SetSpeed(int speed)
        {
            if (speed < 0 || speed > 3)
                return new { error = "speed must be 0-3 (0=pause, 1=normal, 2=fast, 3=fastest)" };

            _speedManager.ChangeSpeed(SpeedScale[speed]);
            return new { speed };
        }

        // pause/unpause a building
        public object PauseBuilding(int buildingId, bool paused)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var pausable = ec.GetComponent<PausableBuilding>();
            if (pausable == null)
                return new { error = "building is not pausable", id = buildingId };

            if (paused)
                pausable.Pause();
            else
                pausable.Resume();
            return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), paused = pausable.Paused };
        }

        // engage/disengage clutch on a building
        public object SetClutch(int buildingId, bool engaged)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var clutch = ec.GetComponent<Clutch>();
            if (clutch == null)
                return new { error = "building has no clutch", id = buildingId };

            clutch.SetMode(engaged ? ClutchMode.Engaged : ClutchMode.Disengaged);
            return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), engaged = clutch.IsEngaged };
        }

        // adjust floodgate water gate height (clamped to max)
        public object SetFloodgateHeight(int buildingId, float height)
        {
            var ec = _cache.FindEntity(buildingId);
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
                name = TimberbotEntityCache.CleanName(ec.GameObject.name),
                height = floodgate.Height,
                maxHeight = floodgate.MaxHeight
            };
        }

        // set construction or workplace priority (VeryLow/Normal/VeryHigh)
        public object SetBuildingPriority(int buildingId, string priorityStr, string type)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            if (!Enum.TryParse<Priority>(priorityStr, true, out var parsed))
                return new { error = "invalid priority, use: VeryLow, Normal, VeryHigh", value = priorityStr };

            if (type == "construction" || string.IsNullOrEmpty(type))
            {
                var prio = ec.GetComponent<BuilderPrioritizable>();
                if (prio != null)
                {
                    prio.SetPriority(parsed);
                    return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), constructionPriority = prio.Priority.ToString() };
                }
            }

            if (type == "workplace" || string.IsNullOrEmpty(type))
            {
                var wpPrio = ec.GetComponent<WorkplacePriority>();
                if (wpPrio != null)
                {
                    wpPrio.SetPriority(parsed);
                    return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), workplacePriority = wpPrio.Priority.ToString() };
                }
            }

            return new { error = "building has no priority of that type", id = buildingId, type };
        }

        // haulers deliver goods to this building first
        public object SetHaulPriority(int buildingId, bool prioritized)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var hp = ec.GetComponent<HaulPrioritizable>();
            if (hp == null)
                return new { error = "building has no haul priority", id = buildingId };

            hp.Prioritized = prioritized;
            return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), haulPrioritized = hp.Prioritized };
        }

        // set which recipe a manufactory produces
        public object SetRecipe(int buildingId, string recipeId)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var manufactory = ec.GetComponent<Manufactory>();
            if (manufactory == null)
                return new { error = "building has no manufactory", id = buildingId };

            if (string.IsNullOrEmpty(recipeId) || recipeId == "none")
            {
                manufactory.SetRecipe(null);
                return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), recipe = "none" };
            }

            RecipeSpec recipe = null;
            try { recipe = _recipeSpecService.GetRecipe(recipeId); } catch (System.Exception _ex) { TimberbotLog.Error("write", _ex); }
            if (recipe == null)
            {
                var available = new List<string>();
                foreach (var r in manufactory.ProductionRecipes)
                    available.Add(r.Id);
                return new { error = "recipe not found", recipeId, available };
            }

            manufactory.SetRecipe(recipe);
            return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), recipe = recipe.Id };
        }

        // prioritize planting vs default (harvest when ready)
        public object SetFarmhouseAction(int buildingId, string action)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var farmhouse = ec.GetComponent<FarmHouse>();
            if (farmhouse == null)
                return new { error = "building is not a farmhouse", id = buildingId };

            if (action == "planting")
            {
                farmhouse.PrioritizePlanting();
                return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), action = "planting" };
            }
            else if (action == "harvesting" || action == "none")
            {
                farmhouse.UnprioritizePlanting();
                return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), action = "default" };
            }

            return new { error = "invalid action, use: planting or harvesting", action };
        }

        // forester/gatherer prioritizes this resource type
        public object SetPlantablePriority(int buildingId, string plantableName)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var prioritizer = ec.GetComponent<PlantablePrioritizer>();
            if (prioritizer == null)
                return new { error = "building has no plantable prioritizer", id = buildingId };

            if (string.IsNullOrEmpty(plantableName) || plantableName == "none")
            {
                prioritizer.PrioritizePlantable(null);
                return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), prioritized = "none" };
            }

            var planterBuilding = ec.GetComponent<PlanterBuilding>();
            if (planterBuilding == null)
                return new { error = "building has no planter", id = buildingId };

            PlantableSpec match = null;
            var available = new List<string>();
            foreach (var p in planterBuilding.AllowedPlantables)
            {
                available.Add(p.TemplateName);
                if (p.TemplateName == plantableName)
                    match = p;
            }

            if (match == null)
                return new { error = "plantable not found", plantableName, available };

            prioritizer.PrioritizePlantable(match);
            return new { id = buildingId, name = TimberbotEntityCache.CleanName(ec.GameObject.name), prioritized = match.TemplateName };
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 2
        // ================================================================

        // set desired worker count (0 to maxWorkers)
        public object SetWorkers(int buildingId, int count)
        {
            var ec = _cache.FindEntity(buildingId);
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
                name = TimberbotEntityCache.CleanName(ec.GameObject.name),
                desiredWorkers = workplace.DesiredWorkers,
                maxWorkers = workplace.MaxWorkers,
                assignedWorkers = workplace.NumberOfAssignedWorkers
            };
        }

        // mark/clear rectangular area for tree cutting
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
                x1 = minX,
                y1 = minY,
                x2 = maxX,
                y2 = maxY,
                z,
                marked,
                tiles = coords.Count
            };
        }

        // set max capacity on a stockpile building
        public object SetStockpileCapacity(int buildingId, int capacity)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var inventories = ec.GetComponent<Inventories>();
            if (inventories == null)
                return new { error = "no inventory", id = buildingId };

            // Set capacity on all inventories
            var capInv = inventories.AllInventories;
            for (int ci = 0; ci < capInv.Count; ci++)
            {
                capInv[ci].Capacity = capacity;
            }

            return new
            {
                id = buildingId,
                name = TimberbotEntityCache.CleanName(ec.GameObject.name),
                capacity
            };
        }

        // set which good a single-good stockpile accepts
        public object SetStockpileGood(int buildingId, string goodId)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var sga = ec.GetComponent<SingleGoodAllower>();
            if (sga == null)
                return new { error = "not a single-good stockpile", id = buildingId };

            sga.AllowedGood = goodId;
            return new
            {
                id = buildingId,
                name = TimberbotEntityCache.CleanName(ec.GameObject.name),
                good = sga.AllowedGood
            };
        }

        // mark area for crop planting (validates via PlantingAreaValidator.CanPlant)
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

            int planted = 0, skipped = 0;
            foreach (var c in coords)
            {
                // PlantingAreaValidator.CanPlant -- same check the player UI uses for green/red tiles
                if (!_plantingAreaValidator.CanPlant(c, crop))
                {
                    skipped++;
                    continue;
                }
                _plantingService.SetPlantingCoordinates(c, crop);
                planted++;
            }

            return new
            {
                x1 = minX,
                y1 = minY,
                x2 = maxX,
                y2 = maxY,
                z,
                crop,
                planted,
                skipped
            };
        }

        // find valid planting spots in an area or within a building's range
        public object FindPlantingSpots(string crop, int buildingId, int x1, int y1, int x2, int y2, int z)
        {
            if (buildingId != 0)
            {
                var ec = _cache.FindEntity(buildingId);
                if (ec == null) return new { error = "building not found", id = buildingId };
                var inRange = ec.GetComponent<Timberborn.Planting.InRangePlantingCoordinates>();
                if (inRange == null) return new { error = "building has no planting range", id = buildingId };

                var jw = _cache.Jw.Reset().OpenObj().Key("crop").Str(crop).Key("spots").OpenArr();
                foreach (var c in inRange.GetCoordinates())
                {
                    if (!_plantingAreaValidator.CanPlant(c, crop)) continue;
                    jw.OpenObj().Key("x").Int(c.x).Key("y").Int(c.y).Key("z").Int(c.z).Key("moist").Bool(_soilMoistureService.SoilIsMoist(c)).Key("planted").Bool(_plantingService.IsResourceAt(c)).CloseObj();
                }
                jw.CloseArr().CloseObj();
                return jw.ToString();
            }
            else
            {
                var jw = _cache.Jw.Reset().OpenObj().Key("crop").Str(crop).Key("spots").OpenArr();
                for (int x = Mathf.Min(x1, x2); x <= Mathf.Max(x1, x2); x++)
                    for (int y = Mathf.Min(y1, y2); y <= Mathf.Max(y1, y2); y++)
                    {
                        var c = new Vector3Int(x, y, z);
                        if (!_plantingAreaValidator.CanPlant(c, crop)) continue;
                        jw.OpenObj().Key("x").Int(x).Key("y").Int(y).Key("z").Int(z).Key("moist").Bool(_soilMoistureService.SoilIsMoist(c)).Key("planted").Bool(_plantingService.IsResourceAt(c)).CloseObj();
                    }
                jw.CloseArr().CloseObj();
                return jw.ToString();
            }
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
                x1 = minX,
                y1 = minY,
                x2 = maxX,
                y2 = maxY,
                z,
                cleared = true,
                tiles = coords.Count
            };
        }

        // ================================================================
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

        // unlock via ToolUnlockingService.TryToUnlock -- matches the exact UI flow
        // when a player clicks "Unlock" in the science panel (cost deduction + events + UI refresh)
        public object UnlockBuilding(string buildingName)
        {
            try
            {
                foreach (var toolButton in _toolButtonService.ToolButtons)
                {
                    var blockObjectTool = toolButton.Tool as BlockObjectTool;
                    if (blockObjectTool == null) continue;
                    var templateSpec = blockObjectTool.Template.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                    if (templateSpec != null && templateSpec.TemplateName == buildingName)
                    {
                        var buildingSpec = blockObjectTool.Template.GetSpec<BuildingSpec>();
                        if (buildingSpec != null && _buildingUnlockingService.Unlocked(buildingSpec))
                            return new
                            {
                                building = buildingName,
                                unlocked = true,
                                remaining = _scienceService.SciencePoints,
                                note = "already unlocked"
                            };
                        var cost = buildingSpec?.ScienceCost ?? 0;
                        if (cost > _scienceService.SciencePoints)
                            return new
                            {
                                error = "not enough science",
                                building = buildingName,
                                scienceCost = cost,
                                currentPoints = _scienceService.SciencePoints
                            };
                        _buildingUnlockingService.Unlock(buildingSpec);
                        _toolUnlockingService.UnlockInternal(blockObjectTool, () => { });
                        return new
                        {
                            building = buildingName,
                            unlocked = true,
                            remaining = _scienceService.SciencePoints
                        };
                    }
                }

                return new { error = "building not found in toolbar", building = buildingName };
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error("unlock", ex);
                return new { error = ex.Message, building = buildingName };
            }
        }

        // PERF: O(n) entity scan x O(needs) per beaver. Called occasionally for wellbeing analysis.
        // population wellbeing breakdown by need group (Social, Hygiene, etc)
        public object CollectWellbeing()
        {
            try
            {
                // get all need specs for beavers
                var beaverNeeds = _factionNeedService.GetBeaverNeeds();

                // build group -> need specs mapping
                var groupNeeds = new Dictionary<string, List<NeedSpec>>();
                foreach (var ns in beaverNeeds)
                {
                    var groupId = ns.NeedGroupId;
                    if (string.IsNullOrEmpty(groupId)) continue;
                    if (!groupNeeds.ContainsKey(groupId))
                        groupNeeds[groupId] = new List<NeedSpec>();
                    groupNeeds[groupId].Add(ns);
                }

                // aggregate per beaver
                int beaverCount = 0;
                var groupTotals = new Dictionary<string, float>();    // current wellbeing sum per group
                var groupMaxTotals = new Dictionary<string, float>(); // max wellbeing sum per group

                // build need->group lookup from specs
                var needToGroup = new Dictionary<string, string>();
                foreach (var kvp in groupNeeds)
                    foreach (var ns in kvp.Value)
                        needToGroup[ns.Id] = kvp.Key;

                foreach (var c in _cache.Beavers.Read)
                {
                    if (c.Needs == null) continue;
                    beaverCount++;

                    // accumulate from cached needs
                    foreach (var n in c.Needs)
                    {
                        if (!needToGroup.TryGetValue(n.Id, out var groupId)) continue;
                        if (!groupTotals.ContainsKey(groupId))
                        {
                            groupTotals[groupId] = 0f;
                            groupMaxTotals[groupId] = 0f;
                        }
                        groupTotals[groupId] += n.Wellbeing;
                    }
                    // max totals (from specs, same per beaver)
                    foreach (var kvp in groupNeeds)
                    {
                        var groupId = kvp.Key;
                        float groupMax = 0f;
                        foreach (var ns in kvp.Value)
                            groupMax += ns.FavorableWellbeing;
                        if (!groupMaxTotals.ContainsKey(groupId))
                            groupMaxTotals[groupId] = 0f;
                        groupMaxTotals[groupId] += groupMax;
                    }
                }

                // build output
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
            catch (System.Exception ex)
            {
                TimberbotLog.Error("wellbeing", ex);
                return new { error = ex.Message };
            }
        }

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

        // set import/export settings for a good in a district
        public object SetDistribution(string districtName, string goodId, string importOption, int exportThreshold)
        {
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                if (dc.DistrictName != districtName) continue;

                var distSetting = dc.GetComponent<Timberborn.DistributionSystem.DistrictDistributionSetting>();
                if (distSetting == null)
                    return new { error = "no distribution settings", district = districtName };

                try
                {
                    var gs = distSetting.GetGoodDistributionSetting(goodId);
                    if (gs != null)
                    {
                        if (!string.IsNullOrEmpty(importOption) &&
                            Enum.TryParse<Timberborn.DistributionSystem.ImportOption>(importOption, true, out var parsed))
                            gs.SetImportOption(parsed);
                        if (exportThreshold >= 0)
                            gs.SetExportThreshold(exportThreshold);
                    }
                }
                catch (System.Exception ex)
                {
                    TimberbotLog.Error("distribution", ex);
                    return new { error = ex.Message, district = districtName, good = goodId };
                }

                return new { district = districtName, good = goodId, importOption, exportThreshold };
            }
            return new { error = "district not found", district = districtName };
        }

        // building work range -- same green circle the player sees
        public object CollectBuildingRange(int buildingId)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var terrainRange = ec.GetComponent<Timberborn.BuildingsNavigation.BuildingTerrainRange>();
            if (terrainRange == null)
                return new
                {
                    error = "building has no work range",
                    id = buildingId,
                    name = TimberbotEntityCache.CleanName(ec.GameObject.name)
                };

            var range = terrainRange.GetRange();
            int moistCount = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var c in range)
            {
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.y < minY) minY = c.y;
                if (c.y > maxY) maxY = c.y;
                if (_soilMoistureService.SoilIsMoist(c)) moistCount++;
            }

            return new
            {
                id = buildingId,
                name = TimberbotEntityCache.CleanName(ec.GameObject.name),
                tiles = range.Count,
                moist = moistCount,
                bounds = range.Count > 0 ? new { x1 = minX, y1 = minY, x2 = maxX, y2 = maxY } : null
            };
        }

        // PLACEMENT VALIDATION
    }
}
