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
// service, returns {id, name, field: newValue} on success or {error: "code"} on failure.
// Error codes: "code: detail" format (e.g. "not_found", "invalid_type: not a floodgate").
// AI parses the prefix before the colon. Codes: not_found, invalid_type, invalid_param,
// insufficient_science, no_population, operation_failed.

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
        private readonly TimberbotEntityRegistry _cache;

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
            TimberbotEntityRegistry cache)
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

        private static readonly int[] SpeedScale = TimberbotReadV2.SpeedScale;

        // ================================================================
        // WRITE ENDPOINTS -- Tier 1
        // ================================================================

        // game speed 0-3, mapped to internal values 0,1,3,7
        public object SetSpeed(int speed)
        {
            if (speed < 0 || speed > 3)
                return _jw.Error("invalid_param: speed must be 0-3");

            _speedManager.ChangeSpeed(SpeedScale[speed]);
            return _jw.Result(("speed", speed));
        }

        // set when beavers stop working (1-24, default 18 = 6pm)
        public object SetWorkHours(int endHours)
        {
            if (endHours < 1 || endHours > 24)
                return _jw.Error("invalid_param: endHours must be 1-24");
            _workingHoursManager.EndHours = endHours;
            return _jw.Result(("endHours", (_workingHoursManager.EndHours)));
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
            if (fromDc == null) return _jw.Error("not_found", ("from", fromDistrict));
            if (toDc == null) return _jw.Error("not_found", ("to", toDistrict));
            try
            {
                var distributor = _populationDistributorRetriever.GetPopulationDistributor<AdultsDistributorTemplate>(fromDc);
                if (distributor == null)
                    return _jw.Error("not_found: no population distributor", ("from", fromDistrict));
                var available = distributor.Current;
                var toMove = System.Math.Min(count, available);
                if (toMove <= 0)
                    return _jw.Error("no_population", ("from", fromDistrict), ("available", available));
                distributor.MigrateTo(toDc, toMove);
                return _jw.Result(("from", fromDistrict), ("to", toDistrict), ("migrated", toMove));
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error("migration", ex);
                return _jw.Error("operation_failed: " + ex.Message, ("from", fromDistrict), ("to", toDistrict));
            }
        }

        // pause/unpause a building
        public object PauseBuilding(int buildingId, bool paused)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Error("not_found", ("id", buildingId));

            var pausable = ec.GetComponent<PausableBuilding>();
            if (pausable == null)
                return _jw.Error("invalid_type: not pausable", ("id", buildingId));

            if (paused)
                pausable.Pause();
            else
                pausable.Resume();
            return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("paused", pausable.Paused).CloseObj().ToString();
        }

        // engage/disengage clutch on a building
        public object SetClutch(int buildingId, bool engaged)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Error("not_found", ("id", buildingId));

            var clutch = ec.GetComponent<Clutch>();
            if (clutch == null)
                return _jw.Error("invalid_type: no clutch", ("id", buildingId));

            clutch.SetMode(engaged ? ClutchMode.Engaged : ClutchMode.Disengaged);
            return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("engaged", clutch.IsEngaged).CloseObj().ToString();
        }

        // adjust floodgate water gate height (clamped to max)
        public object SetFloodgateHeight(int buildingId, float height)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Error("not_found", ("id", buildingId));

            var floodgate = ec.GetComponent<Floodgate>();
            if (floodgate == null)
                return _jw.Error("invalid_type: not a floodgate", ("id", buildingId));

            var clamped = Mathf.Clamp(height, 0f, floodgate.MaxHeight);
            floodgate.SetHeightAndSynchronize(clamped);
            return _jw.Result(("id", buildingId), ("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)), ("height", floodgate.Height), ("maxHeight", floodgate.MaxHeight));
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
                return _jw.Error("not_found", ("id", buildingId));

            if (!Enum.TryParse<Priority>(priorityStr, true, out var parsed))
                return _jw.Error("invalid_param: use VeryLow, Normal, VeryHigh", ("value", priorityStr));

            // construction priority: affects how fast builders deliver materials
            if (type == "construction" || string.IsNullOrEmpty(type))
            {
                var prio = ec.GetComponent<BuilderPrioritizable>();
                if (prio != null)
                {
                    prio.SetPriority(parsed);
                    return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("constructionPriority", prio.Priority.ToString()).CloseObj().ToString();
                }
            }

            if (type == "workplace" || string.IsNullOrEmpty(type))
            {
                var wpPrio = ec.GetComponent<WorkplacePriority>();
                if (wpPrio != null)
                {
                    wpPrio.SetPriority(parsed);
                    return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("workplacePriority", wpPrio.Priority.ToString()).CloseObj().ToString();
                }
            }

            return _jw.Error("invalid_type: no priority of that type", ("id", buildingId), ("type", type));
        }

        // haulers deliver goods to this building first
        public object SetHaulPriority(int buildingId, bool prioritized)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Error("not_found", ("id", buildingId));

            var hp = ec.GetComponent<HaulPrioritizable>();
            if (hp == null)
                return _jw.Error("invalid_type: no haul priority", ("id", buildingId));

            hp.Prioritized = prioritized;
            return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("haulPrioritized", hp.Prioritized).CloseObj().ToString();
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
                return _jw.Error("not_found", ("id", buildingId));

            var manufactory = ec.GetComponent<Manufactory>();
            if (manufactory == null)
                return _jw.Error("invalid_type: no manufactory", ("id", buildingId));

            if (string.IsNullOrEmpty(recipeId) || recipeId == "none")
            {
                if (manufactory.ProductionRecipes == null || manufactory.ProductionRecipes.Length <= 1)
                    return _jw.Error("invalid_type: recipe cannot be cleared", ("id", buildingId));
                try
                {
                    manufactory.SetRecipe(null);
                    return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("recipe", "none").CloseObj().ToString();
                }
                catch (System.Exception ex)
                {
                    TimberbotLog.Error("write.recipe.clear", ex);
                    return _jw.Error("operation_failed: " + ex.Message, ("id", buildingId));
                }
            }

            RecipeSpec recipe = null;
            try { recipe = _recipeSpecService.GetRecipe(recipeId); } catch (System.Exception _ex) { TimberbotLog.Error("write", _ex); }
            if (recipe == null)
            {
                var available = new List<string>();
                foreach (var r in manufactory.ProductionRecipes)
                    available.Add(r.Id);
                return _jw.Error("not_found", ("recipeId", recipeId), ("available", available));
            }

            try
            {
                manufactory.SetRecipe(recipe);
                return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("recipe", recipe.Id).CloseObj().ToString();
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error("write.recipe.set", ex);
                return _jw.Error("operation_failed: " + ex.Message, ("id", buildingId), ("recipeId", recipeId));
            }
        }

        // prioritize planting vs default (harvest when ready)
        public object SetFarmhouseAction(int buildingId, string action)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Error("not_found", ("id", buildingId));

            var farmhouse = ec.GetComponent<FarmHouse>();
            if (farmhouse == null)
                return _jw.Error("invalid_type: not a farmhouse", ("id", buildingId));

            if (action == "planting")
            {
                farmhouse.PrioritizePlanting();
                return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("action", "planting").CloseObj().ToString();
            }
            else if (action == "harvesting" || action == "none")
            {
                farmhouse.UnprioritizePlanting();
                return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("action", "default").CloseObj().ToString();
            }

            return _jw.Error("invalid_param: use planting or harvesting", ("action", action));
        }

        // forester/gatherer prioritizes this resource type
        public object SetPlantablePriority(int buildingId, string plantableName)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Error("not_found", ("id", buildingId));

            var prioritizer = ec.GetComponent<PlantablePrioritizer>();
            if (prioritizer == null)
                return _jw.Error("invalid_type: no plantable prioritizer", ("id", buildingId));

            if (string.IsNullOrEmpty(plantableName) || plantableName == "none")
            {
                prioritizer.PrioritizePlantable(null);
                return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("prioritized", "none").CloseObj().ToString();
            }

            var planterBuilding = ec.GetComponent<PlanterBuilding>();
            if (planterBuilding == null)
                return _jw.Error("invalid_type: no planter", ("id", buildingId));

            PlantableSpec match = null;
            var available = new List<string>();
            foreach (var p in planterBuilding.AllowedPlantables)
            {
                available.Add(p.TemplateName);
                if (p.TemplateName == plantableName)
                    match = p;
            }

            if (match == null)
                return _jw.Error("not_found", ("plantableName", plantableName), ("available", available));

            prioritizer.PrioritizePlantable(match);
            return _jw.BeginObj().Prop("id", buildingId).Prop("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)).Prop("prioritized", match.TemplateName).CloseObj().ToString();
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 2
        // ================================================================

        // set desired worker count (0 to maxWorkers)
        public object SetWorkers(int buildingId, int count)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Error("not_found", ("id", buildingId));

            var workplace = ec.GetComponent<Workplace>();
            if (workplace == null)
                return _jw.Error("invalid_type: not a workplace", ("id", buildingId));

            var clamped = Mathf.Clamp(count, 0, workplace.MaxWorkers);
            workplace.DesiredWorkers = clamped;
            return _jw.Result(("id", buildingId), ("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)), ("desiredWorkers", workplace.DesiredWorkers), ("maxWorkers", workplace.MaxWorkers), ("assignedWorkers", workplace.NumberOfAssignedWorkers));
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
                return _jw.Error("not_found", ("id", buildingId));

            var inventories = ec.GetComponent<Inventories>();
            if (inventories == null)
                return _jw.Error("invalid_type: no inventory", ("id", buildingId));

            // Set capacity on all inventories
            var capInv = inventories.AllInventories;
            for (int ci = 0; ci < capInv.Count; ci++)
            {
                capInv[ci].Capacity = capacity;
            }

            return new
            {
                id = buildingId,
                name = TimberbotEntityRegistry.CanonicalName(ec.GameObject.name),
                capacity
            };
        }

        // set which good a single-good stockpile accepts
        public object SetStockpileGood(int buildingId, string goodId)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Error("not_found", ("id", buildingId));

            var sga = ec.GetComponent<SingleGoodAllower>();
            if (sga == null)
                return _jw.Error("invalid_type: not a single-good stockpile", ("id", buildingId));

            sga.AllowedGood = goodId;
            return _jw.Result(("id", buildingId), ("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)), ("good", sga.AllowedGood));
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
        // 1. By building (id != 0): uses InRangePlantingCoordinates to get all
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
                if (ec == null) return _jw.Error("not_found", ("id", buildingId));
                var inRange = ec.GetComponent<Timberborn.Planting.InRangePlantingCoordinates>();
                if (inRange == null) return _jw.Error("invalid_type: no planting range", ("id", buildingId));

                var jw = _jw.Reset().BeginObj().Prop("crop", crop).Arr("spots");
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
                var jw = _jw.Reset().BeginObj().Prop("crop", crop).Arr("spots");
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

        internal ITimberbotWriteJob CreateFindPlantingSpotsJob(string crop, int buildingId, int x1, int y1, int x2, int y2, int z)
            => new FindPlantingSpotsJob(this, crop, buildingId, x1, y1, x2, y2, z);

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
                            return _jw.Result(("building", buildingName), ("unlocked", true), ("remaining", _scienceService.SciencePoints), ("note", "already unlocked"));
                        var cost = buildingSpec?.ScienceCost ?? 0;
                        if (cost > _scienceService.SciencePoints)
                            return _jw.Error("insufficient_science", ("building", buildingName), ("scienceCost", cost), ("currentPoints", _scienceService.SciencePoints));
                        _buildingUnlockingService.Unlock(buildingSpec);
                        _toolUnlockingService.UnlockInternal(blockObjectTool, () => { });
                        return _jw.Result(("building", buildingName), ("unlocked", true), ("remaining", _scienceService.SciencePoints));
                    }
                }

                return _jw.Error("not_found", ("building", buildingName));
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error("unlock", ex);
                return _jw.Error("operation_failed: " + ex.Message, ("building", buildingName));
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
                    return _jw.Error("invalid_type: no distribution settings", ("district", districtName));

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
                    return _jw.Error("operation_failed: " + ex.Message, ("district", districtName), ("good", goodId));
                }

                return _jw.Result(("district", districtName), ("good", goodId), ("importOption", importOption), ("exportThreshold", exportThreshold));
            }
            return _jw.Error("not_found", ("district", districtName));
        }

        // Get the work range for a building (farmhouse, lumberjack, forester, gatherer).
        // Returns the list of tiles this building's workers can reach -- same green circle
        // the player sees in the UI when selecting the building. Also counts how many
        // tiles have moist soil (important for crop placement near water).
        public object CollectBuildingRange(int buildingId)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return _jw.Error("not_found", ("id", buildingId));

            var terrainRange = ec.GetComponent<Timberborn.BuildingsNavigation.BuildingTerrainRange>();
            if (terrainRange == null)
                return _jw.Error("invalid_type: no work range", ("id", buildingId), ("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)));

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
                name = TimberbotEntityRegistry.CanonicalName(ec.GameObject.name),
                tiles = range.Count,
                moist = moistCount,
                bounds = range.Count > 0 ? new { x1 = minX, y1 = minY, x2 = maxX, y2 = maxY } : null
            };
        }

        internal ITimberbotWriteJob CreateCollectBuildingRangeJob(int buildingId)
            => new CollectBuildingRangeJob(this, buildingId);

        private sealed class FindPlantingSpotsJob : ITimberbotWriteJob
        {
            private readonly TimberbotWrite _owner;
            private readonly string _crop;
            private readonly int _buildingId;
            private readonly int _x1;
            private readonly int _y1;
            private readonly int _x2;
            private readonly int _y2;
            private readonly int _z;
            private readonly List<Spot> _spots = new List<Spot>();
            private List<Vector3Int> _coords;
            private int _index;
            private bool _initialized;
            private bool _completed;
            private int _statusCode = 200;
            private object _result;

            private struct Spot
            {
                public int X;
                public int Y;
                public int Z;
                public bool Moist;
                public bool Planted;
            }

            public FindPlantingSpotsJob(TimberbotWrite owner, string crop, int buildingId, int x1, int y1, int x2, int y2, int z)
            {
                _owner = owner;
                _crop = crop;
                _buildingId = buildingId;
                _x1 = x1;
                _y1 = y1;
                _x2 = x2;
                _y2 = y2;
                _z = z;
            }

            public string Name => "/api/planting/find";
            public bool IsCompleted => _completed;
            public int StatusCode => _statusCode;
            public object Result => _result;

            public void Step(float now, double budgetMs)
            {
                if (_completed) return;
                if (!_initialized && !Initialize()) return;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_index < _coords.Count)
                {
                    var c = _coords[_index++];
                    if (_owner._plantingAreaValidator.CanPlant(c, _crop))
                    {
                        _spots.Add(new Spot
                        {
                            X = c.x,
                            Y = c.y,
                            Z = c.z,
                            Moist = _owner._soilMoistureService.SoilIsMoist(c),
                            Planted = _owner._plantingService.IsResourceAt(c)
                        });
                    }

                    if (sw.Elapsed.TotalMilliseconds >= budgetMs)
                        return;
                }

                var jw = _owner._jw.Reset().BeginObj().Prop("crop", _crop).Arr("spots");
                for (int i = 0; i < _spots.Count; i++)
                {
                    var s = _spots[i];
                    jw.OpenObj()
                        .Prop("x", s.X)
                        .Prop("y", s.Y)
                        .Prop("z", s.Z)
                        .Prop("moist", s.Moist)
                        .Prop("planted", s.Planted)
                        .CloseObj();
                }
                _result = jw.CloseArr().CloseObj().ToString();
                _completed = true;
            }

            public void Cancel(string error)
            {
                if (_completed) return;
                _statusCode = 500;
                _result = "{\"error\":\"" + error.Replace("\"", "'") + "\"}";
                _completed = true;
            }

            private bool Initialize()
            {
                _initialized = true;
                _coords = new List<Vector3Int>();

                if (_buildingId != 0)
                {
                    var ec = _owner._cache.FindEntity(_buildingId);
                    if (ec == null)
                    {
                        _result = _owner._jw.Error("not_found", ("id", _buildingId));
                        _completed = true;
                        return false;
                    }

                    var inRange = ec.GetComponent<Timberborn.Planting.InRangePlantingCoordinates>();
                    if (inRange == null)
                    {
                        _result = _owner._jw.Error("invalid_type: no planting range", ("id", _buildingId));
                        _completed = true;
                        return false;
                    }

                    foreach (var c in inRange.GetCoordinates())
                        _coords.Add(c);
                    return true;
                }

                for (int x = Mathf.Min(_x1, _x2); x <= Mathf.Max(_x1, _x2); x++)
                    for (int y = Mathf.Min(_y1, _y2); y <= Mathf.Max(_y1, _y2); y++)
                        _coords.Add(new Vector3Int(x, y, _z));
                return true;
            }
        }

        private sealed class CollectBuildingRangeJob : ITimberbotWriteJob
        {
            private readonly TimberbotWrite _owner;
            private readonly int _buildingId;
            private List<Vector3Int> _range;
            private string _name;
            private int _index;
            private int _moistCount;
            private int _minX = int.MaxValue;
            private int _minY = int.MaxValue;
            private int _maxX = int.MinValue;
            private int _maxY = int.MinValue;
            private bool _initialized;
            private bool _completed;
            private int _statusCode = 200;
            private object _result;

            public CollectBuildingRangeJob(TimberbotWrite owner, int buildingId)
            {
                _owner = owner;
                _buildingId = buildingId;
            }

            public string Name => "/api/building/range";
            public bool IsCompleted => _completed;
            public int StatusCode => _statusCode;
            public object Result => _result;

            public void Step(float now, double budgetMs)
            {
                if (_completed) return;
                if (!_initialized && !Initialize()) return;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_index < _range.Count)
                {
                    var c = _range[_index++];
                    if (c.x < _minX) _minX = c.x;
                    if (c.x > _maxX) _maxX = c.x;
                    if (c.y < _minY) _minY = c.y;
                    if (c.y > _maxY) _maxY = c.y;
                    if (_owner._soilMoistureService.SoilIsMoist(c)) _moistCount++;

                    if (sw.Elapsed.TotalMilliseconds >= budgetMs)
                        return;
                }

                _result = new
                {
                    id = _buildingId,
                    name = _name,
                    tiles = _range.Count,
                    moist = _moistCount,
                    bounds = _range.Count > 0 ? new { x1 = _minX, y1 = _minY, x2 = _maxX, y2 = _maxY } : null
                };
                _completed = true;
            }

            public void Cancel(string error)
            {
                if (_completed) return;
                _statusCode = 500;
                _result = "{\"error\":\"" + error.Replace("\"", "'") + "\"}";
                _completed = true;
            }

            private bool Initialize()
            {
                _initialized = true;
                var ec = _owner._cache.FindEntity(_buildingId);
                if (ec == null)
                {
                    _result = _owner._jw.Error("not_found", ("id", _buildingId));
                    _completed = true;
                    return false;
                }

                var terrainRange = ec.GetComponent<Timberborn.BuildingsNavigation.BuildingTerrainRange>();
                if (terrainRange == null)
                {
                    _result = _owner._jw.Error("invalid_type: no work range", ("id", _buildingId), ("name", TimberbotEntityRegistry.CanonicalName(ec.GameObject.name)));
                    _completed = true;
                    return false;
                }

                _range = new List<Vector3Int>();
                foreach (var c in terrainRange.GetRange())
                    _range.Add(c);
                _name = TimberbotEntityRegistry.CanonicalName(ec.GameObject.name);
                return true;
            }
        }

        // PLACEMENT VALIDATION
    }
}

