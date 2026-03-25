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
using Timberborn.GameDistrictsMigration;
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
        private readonly TimberbotJw _jw = new TimberbotJw(1024);
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
            WorkingHoursManager workingHoursManager,
            PopulationDistributorRetriever populationDistributorRetriever,
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
            _workingHoursManager = workingHoursManager;
            _populationDistributorRetriever = populationDistributorRetriever;
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
                return _jw.Reset().OpenObj().Prop("error", "speed must be 0-3 (0=pause, 1=normal, 2=fast, 3=fastest)").CloseObj().ToString();

            _speedManager.ChangeSpeed(SpeedScale[speed]);
            return _jw.Reset().OpenObj().Prop("speed", speed).CloseObj().ToString();
        }

        // set when beavers stop working (1-24, default 18 = 6pm)
        public object SetWorkHours(int endHours)
        {
            if (endHours < 1 || endHours > 24)
                return _jw.Reset().OpenObj().Prop("error", "endHours must be 1-24").CloseObj().ToString();
            _workingHoursManager.EndHours = endHours;
            return _jw.Reset().OpenObj().Prop("endHours", _workingHoursManager.EndHours).CloseObj().ToString();
        }

        // move beavers between districts. requires 2+ districts.
        public object MigratePopulation(string fromDistrict, string toDistrict, int count)
        {
            Timberborn.GameDistricts.DistrictCenter fromDc = null, toDc = null;
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                if (dc.DistrictName == fromDistrict) fromDc = dc;
                if (dc.DistrictName == toDistrict) toDc = dc;
            }
            if (fromDc == null) return _jw.Reset().OpenObj().Prop("error", "from district not found").Prop("from", fromDistrict).CloseObj().ToString();
            if (toDc == null) return _jw.Reset().OpenObj().Prop("error", "to district not found").Prop("to", toDistrict).CloseObj().ToString();
            try
            {
                var distributor = _populationDistributorRetriever.GetPopulationDistributor<AdultsDistributorTemplate>(fromDc);
                if (distributor == null)
                    return _jw.Reset().OpenObj().Prop("error", "no population distributor").Prop("from", fromDistrict).CloseObj().ToString();
                var available = distributor.Current;
                var toMove = System.Math.Min(count, available);
                if (toMove <= 0)
                    return _jw.Reset().OpenObj().Prop("error", "no population to migrate").Prop("from", fromDistrict).Prop("available", available).CloseObj().ToString();
                distributor.MigrateTo(toDc, toMove);
                return _jw.Reset().OpenObj().Prop("from", fromDistrict).Prop("to", toDistrict).Prop("migrated", toMove).CloseObj().ToString();
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error("migration", ex);
                return _jw.Reset().OpenObj().Prop("error", ex.Message).Prop("from", fromDistrict).Prop("to", toDistrict).CloseObj().ToString();
            }
        }

        // pause/unpause a building
        public object PauseBuilding(int buildingId, bool paused)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var pausable = ec.GetComponent<PausableBuilding>();
            if (pausable == null)
                return _jw.Reset().OpenObj().Prop("error", "building is not pausable").Prop("id", buildingId).CloseObj().ToString();

            if (paused)
                pausable.Pause();
            else
                pausable.Resume();
            return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("paused", pausable.Paused).CloseObj().ToString();
        }

        // engage/disengage clutch on a building
        public object SetClutch(int buildingId, bool engaged)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var clutch = ec.GetComponent<Clutch>();
            if (clutch == null)
                return _jw.Reset().OpenObj().Prop("error", "building has no clutch").Prop("id", buildingId).CloseObj().ToString();

            clutch.SetMode(engaged ? ClutchMode.Engaged : ClutchMode.Disengaged);
            return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("engaged", clutch.IsEngaged).CloseObj().ToString();
        }

        // adjust floodgate water gate height (clamped to max)
        public object SetFloodgateHeight(int buildingId, float height)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var floodgate = ec.GetComponent<Floodgate>();
            if (floodgate == null)
                return _jw.Reset().OpenObj().Prop("error", "not a floodgate").Prop("id", buildingId).CloseObj().ToString();

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
        // Buildings have TWO separate priority systems:
        //   "construction" -- how urgently builders deliver materials and construct it
        //   "workplace" -- how urgently workers are assigned to it vs other buildings
        // Both use the same VeryLow/Low/Normal/High/VeryHigh enum but are set independently.
        // If type is empty, tries construction first, then workplace.
        public object SetBuildingPriority(int buildingId, string priorityStr, string type)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            if (!Enum.TryParse<Priority>(priorityStr, true, out var parsed))
                return _jw.Reset().OpenObj().Prop("error", "invalid priority, use: VeryLow, Normal, VeryHigh").Prop("value", priorityStr).CloseObj().ToString();

            // construction priority: affects how fast builders deliver materials
            if (type == "construction" || string.IsNullOrEmpty(type))
            {
                var prio = ec.GetComponent<BuilderPrioritizable>();
                if (prio != null)
                {
                    prio.SetPriority(parsed);
                    return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("constructionPriority", prio.Priority.ToString()).CloseObj().ToString();
                }
            }

            if (type == "workplace" || string.IsNullOrEmpty(type))
            {
                var wpPrio = ec.GetComponent<WorkplacePriority>();
                if (wpPrio != null)
                {
                    wpPrio.SetPriority(parsed);
                    return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("workplacePriority", wpPrio.Priority.ToString()).CloseObj().ToString();
                }
            }

            return _jw.Reset().OpenObj().Prop("error", "building has no priority of that type").Prop("id", buildingId).Prop("type", type).CloseObj().ToString();
        }

        // haulers deliver goods to this building first
        public object SetHaulPriority(int buildingId, bool prioritized)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var hp = ec.GetComponent<HaulPrioritizable>();
            if (hp == null)
                return _jw.Reset().OpenObj().Prop("error", "building has no haul priority").Prop("id", buildingId).CloseObj().ToString();

            hp.Prioritized = prioritized;
            return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("haulPrioritized", hp.Prioritized).CloseObj().ToString();
        }

        // DANGEROUS: changing a recipe DESTROYS in-progress items and all consumed materials.
        // A BotPartFactory mid-way through a BotChassis will lose the planks, gears, and metal
        // blocks already consumed. Only call this on buildings with no recipe set (new buildings)
        // or when you're certain the current batch is complete.
        // Pass an invalid recipe name to get a list of available recipes in the error response.
        public object SetRecipe(int buildingId, string recipeId)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var manufactory = ec.GetComponent<Manufactory>();
            if (manufactory == null)
                return _jw.Reset().OpenObj().Prop("error", "building has no manufactory").Prop("id", buildingId).CloseObj().ToString();

            if (string.IsNullOrEmpty(recipeId) || recipeId == "none")
            {
                manufactory.SetRecipe(null);
                return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("recipe", "none").CloseObj().ToString();
            }

            RecipeSpec recipe = null;
            try { recipe = _recipeSpecService.GetRecipe(recipeId); } catch (System.Exception _ex) { TimberbotLog.Error("write", _ex); }
            if (recipe == null)
            {
                var available = new List<string>();
                foreach (var r in manufactory.ProductionRecipes)
                    available.Add(r.Id);
                return _jw.Reset().OpenObj().Prop("error", "recipe not found").Prop("recipeId", recipeId).Prop("available", available).CloseObj().ToString();
            }

            manufactory.SetRecipe(recipe);
            return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("recipe", recipe.Id).CloseObj().ToString();
        }

        // prioritize planting vs default (harvest when ready)
        public object SetFarmhouseAction(int buildingId, string action)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var farmhouse = ec.GetComponent<FarmHouse>();
            if (farmhouse == null)
                return _jw.Reset().OpenObj().Prop("error", "building is not a farmhouse").Prop("id", buildingId).CloseObj().ToString();

            if (action == "planting")
            {
                farmhouse.PrioritizePlanting();
                return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("action", "planting").CloseObj().ToString();
            }
            else if (action == "harvesting" || action == "none")
            {
                farmhouse.UnprioritizePlanting();
                return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("action", "default").CloseObj().ToString();
            }

            return _jw.Reset().OpenObj().Prop("error", "invalid action, use: planting or harvesting").Prop("action", action).CloseObj().ToString();
        }

        // forester/gatherer prioritizes this resource type
        public object SetPlantablePriority(int buildingId, string plantableName)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var prioritizer = ec.GetComponent<PlantablePrioritizer>();
            if (prioritizer == null)
                return _jw.Reset().OpenObj().Prop("error", "building has no plantable prioritizer").Prop("id", buildingId).CloseObj().ToString();

            if (string.IsNullOrEmpty(plantableName) || plantableName == "none")
            {
                prioritizer.PrioritizePlantable(null);
                return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("prioritized", "none").CloseObj().ToString();
            }

            var planterBuilding = ec.GetComponent<PlanterBuilding>();
            if (planterBuilding == null)
                return _jw.Reset().OpenObj().Prop("error", "building has no planter").Prop("id", buildingId).CloseObj().ToString();

            PlantableSpec match = null;
            var available = new List<string>();
            foreach (var p in planterBuilding.AllowedPlantables)
            {
                available.Add(p.TemplateName);
                if (p.TemplateName == plantableName)
                    match = p;
            }

            if (match == null)
                return _jw.Reset().OpenObj().Prop("error", "plantable not found").Prop("plantableName", plantableName).Prop("available", available).CloseObj().ToString();

            prioritizer.PrioritizePlantable(match);
            return _jw.Reset().OpenObj().Prop("id", buildingId).Prop("name", TimberbotEntityCache.CleanName(ec.GameObject.name)).Prop("prioritized", match.TemplateName).CloseObj().ToString();
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 2
        // ================================================================

        // set desired worker count (0 to maxWorkers)
        public object SetWorkers(int buildingId, int count)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var workplace = ec.GetComponent<Workplace>();
            if (workplace == null)
                return _jw.Reset().OpenObj().Prop("error", "not a workplace").Prop("id", buildingId).CloseObj().ToString();

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

        // Mark or unmark a rectangular area for tree cutting. Lumberjacks will chop
        // any marked trees within their work range. Uses TreeCuttingArea singleton
        // which is coordinate-based (not per-entity) -- same system as the player's UI.
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
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var inventories = ec.GetComponent<Inventories>();
            if (inventories == null)
                return _jw.Reset().OpenObj().Prop("error", "no inventory").Prop("id", buildingId).CloseObj().ToString();

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
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

            var sga = ec.GetComponent<SingleGoodAllower>();
            if (sga == null)
                return _jw.Reset().OpenObj().Prop("error", "not a single-good stockpile").Prop("id", buildingId).CloseObj().ToString();

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

        // Find valid planting spots for a crop. Two modes:
        //
        // 1. By building (building_id != 0): uses InRangePlantingCoordinates to get all
        //    tiles within the farmhouse's work range. Only farmhouses/foresters have this.
        //    The range is a circle around the building, same as the green overlay in-game.
        //
        // 2. By area (x1,y1,x2,y2,z): scans a rectangular region.
        //
        // Each candidate is validated with PlantingAreaValidator.CanPlant() -- the same
        // check the player UI uses (green/red tiles). Returns soil moisture and whether
        // a crop is already planted at that spot.
        //
        // Crops need moist soil to grow. During drought, only tiles near standing water
        // stay moist. The AI uses the "moist" field to choose where to plant.
        public object FindPlantingSpots(string crop, int buildingId, int x1, int y1, int x2, int y2, int z)
        {
            if (buildingId != 0)
            {
                var ec = _cache.FindEntity(buildingId);
                if (ec == null) return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();
                var inRange = ec.GetComponent<Timberborn.Planting.InRangePlantingCoordinates>();
                if (inRange == null) return _jw.Reset().OpenObj().Prop("error", "building has no planting range").Prop("id", buildingId).CloseObj().ToString();

                var jw = _cache.Jw.Reset().OpenObj().Prop("crop", crop).Arr("spots");
                foreach (var c in inRange.GetCoordinates())
                {
                    if (!_plantingAreaValidator.CanPlant(c, crop)) continue;
                    jw.OpenObj().Prop("x", c.x).Prop("y", c.y).Prop("z", c.z).Prop("moist", _soilMoistureService.SoilIsMoist(c)).Prop("planted", _plantingService.IsResourceAt(c)).CloseObj();
                }
                jw.CloseArr().CloseObj();
                return jw.ToString();
            }
            else
            {
                var jw = _cache.Jw.Reset().OpenObj().Prop("crop", crop).Arr("spots");
                for (int x = Mathf.Min(x1, x2); x <= Mathf.Max(x1, x2); x++)
                    for (int y = Mathf.Min(y1, y2); y <= Mathf.Max(y1, y2); y++)
                    {
                        var c = new Vector3Int(x, y, z);
                        if (!_plantingAreaValidator.CanPlant(c, crop)) continue;
                        jw.OpenObj().Prop("x", x).Prop("y", y).Prop("z", z).Prop("moist", _soilMoistureService.SoilIsMoist(c)).Prop("planted", _plantingService.IsResourceAt(c)).CloseObj();
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

        // Unlock a building using science points. Matches the exact UI flow when a
        // player clicks "Unlock" in the science panel: checks cost, deducts points,
        // fires events, and updates the UI toolbar.
        //
        // We iterate ToolButtons (the building toolbar) rather than BuildingService
        // because ToolUnlockingService.UnlockInternal requires the BlockObjectTool
        // reference to update the toolbar state. Without this, the building would be
        // unlocked internally but the toolbar button would still show as locked.
        public object UnlockBuilding(string buildingName)
        {
            try
            {
                foreach (var toolButton in _toolButtonService.ToolButtons)
                {
                    // only BlockObjectTool entries are buildings (others are path tool, demolish, etc)
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

                return _jw.Reset().OpenObj().Prop("error", "building not found in toolbar").Prop("building", buildingName).CloseObj().ToString();
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error("unlock", ex);
                return _jw.Reset().OpenObj().Prop("error", ex.Message).Prop("building", buildingName).CloseObj().ToString();
            }
        }

        // Set import/export settings for a good in a district.
        // Timberborn's distribution system controls how goods flow between districts:
        //   ImportOption: None, Normal, Forced (Forced = always import even if local stock is ok)
        //   ExportThreshold: export excess above this amount to other districts
        // -1 for exportThreshold means "don't change" (only update import option).
        public object SetDistribution(string districtName, string goodId, string importOption, int exportThreshold)
        {
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                if (dc.DistrictName != districtName) continue;

                var distSetting = dc.GetComponent<Timberborn.DistributionSystem.DistrictDistributionSetting>();
                if (distSetting == null)
                    return _jw.Reset().OpenObj().Prop("error", "no distribution settings").Prop("district", districtName).CloseObj().ToString();

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
                    return _jw.Reset().OpenObj().Prop("error", ex.Message).Prop("district", districtName).Prop("good", goodId).CloseObj().ToString();
                }

                return _jw.Reset().OpenObj().Prop("district", districtName).Prop("good", goodId).Prop("importOption", importOption).Prop("exportThreshold", exportThreshold).CloseObj().ToString();
            }
            return _jw.Reset().OpenObj().Prop("error", "district not found").Prop("district", districtName).CloseObj().ToString();
        }

        // Get the work range for a building (farmhouse, lumberjack, forester, gatherer).
        // Returns the list of tiles this building's workers can reach -- same green circle
        // the player sees in the UI when selecting the building. Also counts how many
        // tiles have moist soil (important for crop placement near water).
        public object CollectBuildingRange(int buildingId)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Reset().OpenObj().Prop("error", "building not found").Prop("id", buildingId).CloseObj().ToString();

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
