// TimberbotService.Write.cs -- All state-modifying API endpoints.
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
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.BuilderPrioritySystem;
using Timberborn.Buildings;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockObjectTools;
using Timberborn.Coordinates;
using Timberborn.Cutting;
using Timberborn.TemplateInstantiation;
using Timberborn.MapIndexSystem;
using Timberborn.TerrainSystem;
using Timberborn.WaterSystem;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.Planting;
using Timberborn.Gathering;
using Timberborn.GameCycleSystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.PrioritySystem;
using Timberborn.ResourceCountingSystem;
using Timberborn.SingletonSystem;
using Timberborn.Stockpiles;
using Timberborn.TimeSystem;
using Timberborn.WaterBuildings;
using Timberborn.WeatherSystem;
using Timberborn.WorkSystem;
using Timberborn.NeedSystem;
using Timberborn.LifeSystem;
using Timberborn.Wellbeing;
using Timberborn.BuildingsReachability;
using Timberborn.ConstructionSites;
using Timberborn.MechanicalSystem;
using Timberborn.ScienceSystem;
using Timberborn.BeaverContaminationSystem;
using Timberborn.Bots;
using Timberborn.Carrying;
using Timberborn.DeteriorationSystem;
using Timberborn.Wonders;
using Timberborn.NotificationSystem;
using Timberborn.StatusSystem;
using Timberborn.DwellingSystem;
using Timberborn.PowerManagement;
using Timberborn.SoilContaminationSystem;
using Timberborn.Hauling;
using Timberborn.Workshops;
using Timberborn.Reproduction;
using Timberborn.Fields;
using Timberborn.GameDistrictsMigration;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;
using Timberborn.PlantingUI;
using Timberborn.BuildingsNavigation;
using Timberborn.SoilMoistureSystem;
using Timberborn.NeedSpecs;
using Timberborn.GameFactionSystem;
using Timberborn.RangedEffectSystem;
using UnityEngine;

namespace Timberbot
{
    public partial class TimberbotService
    {
        public object CollectTiles(int x1, int y1, int x2, int y2)
        {
            var size = _terrainService.Size;
            var stride = _mapIndexService.VerticalStride;

            // default to full map if no region specified
            if (x1 == 0 && y1 == 0 && x2 == 0 && y2 == 0)
            {
                return new
                {
                    mapSize = new { x = size.x, y = size.y, z = size.z }
                };
            }

            x1 = Mathf.Clamp(x1, 0, size.x - 1);
            y1 = Mathf.Clamp(y1, 0, size.y - 1);
            x2 = Mathf.Clamp(x2, 0, size.x - 1);
            y2 = Mathf.Clamp(y2, 0, size.y - 1);

            // build occupancy map from cached indexes -- zero GetComponent, fully thread-safe
            // key = x*100000+y, value = list of (name, z) for vertical stacking
            var occupants = new Dictionary<long, List<(string name, int z)>>();
            var entrances = new HashSet<long>();
            var seedlings = new HashSet<long>();
            var deadTiles = new HashSet<long>();

            // buildings (multi-tile footprints cached at add-time)
            var buildings = _buildings.Read;
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

            // natural resources (1x1, all data cached)
            var resources = _naturalResources.Read;
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

            var tiles = new List<object>();
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
                    try { waterHeight = _waterMap.CeiledWaterHeight(waterCoord); } catch (System.Exception _ex) { TimberbotLog.Error("write", _ex); }
                    try
                    {
                        int wIdx2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
                        int wColCount = _waterMap.ColumnCount(wIdx2D);
                        for (int ci = wColCount - 1; ci >= 0; ci--)
                        {
                            int wIdx3D = ci * _mapIndexService.VerticalStride + wIdx2D;
                            var col = _waterMap.WaterColumns[wIdx3D];
                            if (col.WaterDepth > 0 && col.Contamination > 0)
                            {
                                waterContamination = col.Contamination;
                                break;
                            }
                        }
                    }
                    catch (System.Exception _ex) { TimberbotLog.Error("write", _ex); }

                    long key = (long)x * 100000 + y;
                    occupants.TryGetValue(key, out var occList);
                    bool isEntrance = entrances.Contains(key);
                    bool isSeedling = seedlings.Contains(key);

                    var tile = new Dictionary<string, object>
                    {
                        ["x"] = x, ["y"] = y,
                        ["terrain"] = terrainHeight,
                        ["water"] = waterHeight
                    };
                    if (waterContamination > 0)
                        tile["badwater"] = Math.Round(waterContamination, 2);
                    if (occList != null)
                    {
                        if (occList.Count == 1)
                            tile["occupant"] = occList[0].name;
                        else
                        {
                            var stacked = new List<object>(occList.Count);
                            foreach (var o in occList)
                                stacked.Add(new Dictionary<string, object> { ["name"] = o.name, ["z"] = o.z });
                            tile["occupants"] = stacked;
                        }
                    }
                    if (isEntrance) tile["entrance"] = true;
                    if (isSeedling) tile["seedling"] = true;
                    if (deadTiles.Contains(key)) tile["dead"] = true;
                    try
                    {
                        if (_soilContaminationService.SoilIsContaminated(new Vector3Int(x, y, terrainHeight)))
                            tile["contaminated"] = true;
                    }
                    catch (System.Exception _ex) { TimberbotLog.Error("write", _ex); }
                    try
                    {
                        if (_soilMoistureService.SoilIsMoist(new Vector3Int(x, y, terrainHeight)))
                            tile["moist"] = true;
                    }
                    catch (System.Exception _ex) { TimberbotLog.Error("write", _ex); }
                    tiles.Add(tile);
                }
            }

            return new
            {
                mapSize = new { x = size.x, y = size.y, z = size.z },
                region = new { x1, y1, x2, y2 },
                tiles
            };
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 1
        // ================================================================

        private static readonly int[] SpeedScale = { 0, 1, 3, 7 };

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
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var pausable = ec.GetComponent<PausableBuilding>();
            if (pausable == null)
                return new { error = "building is not pausable", id = buildingId };

            if (paused)
                pausable.Pause();
            else
                pausable.Resume();
            return new { id = buildingId, name = CleanName(ec.GameObject.name), paused = pausable.Paused };
        }

        // engage/disengage clutch on a building
        public object SetClutch(int buildingId, bool engaged)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var clutch = ec.GetComponent<Clutch>();
            if (clutch == null)
                return new { error = "building has no clutch", id = buildingId };

            clutch.SetMode(engaged ? ClutchMode.Engaged : ClutchMode.Disengaged);
            return new { id = buildingId, name = CleanName(ec.GameObject.name), engaged = clutch.IsEngaged };
        }

        // adjust floodgate water gate height (clamped to max)
        public object SetFloodgateHeight(int buildingId, float height)
        {
            var ec = FindEntity(buildingId);
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
                name = CleanName(ec.GameObject.name),
                height = floodgate.Height,
                maxHeight = floodgate.MaxHeight
            };
        }

        // set construction or workplace priority (VeryLow/Normal/VeryHigh)
        public object SetBuildingPriority(int buildingId, string priorityStr, string type)
        {
            var ec = FindEntity(buildingId);
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
                    return new { id = buildingId, name = CleanName(ec.GameObject.name), constructionPriority = prio.Priority.ToString() };
                }
            }

            if (type == "workplace" || string.IsNullOrEmpty(type))
            {
                var wpPrio = ec.GetComponent<WorkplacePriority>();
                if (wpPrio != null)
                {
                    wpPrio.SetPriority(parsed);
                    return new { id = buildingId, name = CleanName(ec.GameObject.name), workplacePriority = wpPrio.Priority.ToString() };
                }
            }

            return new { error = "building has no priority of that type", id = buildingId, type };
        }

        // haulers deliver goods to this building first
        public object SetHaulPriority(int buildingId, bool prioritized)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var hp = ec.GetComponent<HaulPrioritizable>();
            if (hp == null)
                return new { error = "building has no haul priority", id = buildingId };

            hp.Prioritized = prioritized;
            return new { id = buildingId, name = CleanName(ec.GameObject.name), haulPrioritized = hp.Prioritized };
        }

        // set which recipe a manufactory produces
        public object SetRecipe(int buildingId, string recipeId)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var manufactory = ec.GetComponent<Manufactory>();
            if (manufactory == null)
                return new { error = "building has no manufactory", id = buildingId };

            if (string.IsNullOrEmpty(recipeId) || recipeId == "none")
            {
                manufactory.SetRecipe(null);
                return new { id = buildingId, name = CleanName(ec.GameObject.name), recipe = "none" };
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
            return new { id = buildingId, name = CleanName(ec.GameObject.name), recipe = recipe.Id };
        }

        // prioritize planting vs default (harvest when ready)
        public object SetFarmhouseAction(int buildingId, string action)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var farmhouse = ec.GetComponent<FarmHouse>();
            if (farmhouse == null)
                return new { error = "building is not a farmhouse", id = buildingId };

            if (action == "planting")
            {
                farmhouse.PrioritizePlanting();
                return new { id = buildingId, name = CleanName(ec.GameObject.name), action = "planting" };
            }
            else if (action == "harvesting" || action == "none")
            {
                farmhouse.UnprioritizePlanting();
                return new { id = buildingId, name = CleanName(ec.GameObject.name), action = "default" };
            }

            return new { error = "invalid action, use: planting or harvesting", action };
        }

        // forester/gatherer prioritizes this resource type
        public object SetPlantablePriority(int buildingId, string plantableName)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var prioritizer = ec.GetComponent<PlantablePrioritizer>();
            if (prioritizer == null)
                return new { error = "building has no plantable prioritizer", id = buildingId };

            if (string.IsNullOrEmpty(plantableName) || plantableName == "none")
            {
                prioritizer.PrioritizePlantable(null);
                return new { id = buildingId, name = CleanName(ec.GameObject.name), prioritized = "none" };
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
            return new { id = buildingId, name = CleanName(ec.GameObject.name), prioritized = match.TemplateName };
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 2
        // ================================================================

        // set desired worker count (0 to maxWorkers)
        public object SetWorkers(int buildingId, int count)
        {
            var ec = FindEntity(buildingId);
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
                name = CleanName(ec.GameObject.name),
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
                x1 = minX, y1 = minY, x2 = maxX, y2 = maxY, z,
                marked,
                tiles = coords.Count
            };
        }

        // set max capacity on a stockpile building
        public object SetStockpileCapacity(int buildingId, int capacity)
        {
            var ec = FindEntity(buildingId);
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
                name = CleanName(ec.GameObject.name),
                capacity
            };
        }

        // set which good a single-good stockpile accepts
        public object SetStockpileGood(int buildingId, string goodId)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var sga = ec.GetComponent<SingleGoodAllower>();
            if (sga == null)
                return new { error = "not a single-good stockpile", id = buildingId };

            sga.AllowedGood = goodId;
            return new
            {
                id = buildingId,
                name = CleanName(ec.GameObject.name),
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
                x1 = minX, y1 = minY, x2 = maxX, y2 = maxY, z,
                crop,
                planted, skipped
            };
        }

        // find valid planting spots in an area or within a building's range
        public object FindPlantingSpots(string crop, int buildingId, int x1, int y1, int x2, int y2, int z)
        {
            var spots = new List<object>();

            if (buildingId != 0)
            {
                // building mode: get planting coords from building's InRangePlantingCoordinates
                var ec = FindEntity(buildingId);
                if (ec == null)
                    return new { error = "building not found", id = buildingId };

                var inRange = ec.GetComponent<Timberborn.Planting.InRangePlantingCoordinates>();
                if (inRange == null)
                    return new { error = "building has no planting range", id = buildingId };

                foreach (var c in inRange.GetCoordinates())
                {
                    if (!_plantingAreaValidator.CanPlant(c, crop)) continue;
                    bool moist = _soilMoistureService.SoilIsMoist(c);
                    bool planted = _plantingService.IsResourceAt(c);
                    spots.Add(new { x = c.x, y = c.y, z = c.z, moist, planted });
                }
            }
            else
            {
                // area mode: scan rectangle
                for (int x = Mathf.Min(x1, x2); x <= Mathf.Max(x1, x2); x++)
                {
                    for (int y = Mathf.Min(y1, y2); y <= Mathf.Max(y1, y2); y++)
                    {
                        var c = new Vector3Int(x, y, z);
                        if (!_plantingAreaValidator.CanPlant(c, crop)) continue;
                        bool moist = _soilMoistureService.SoilIsMoist(c);
                        bool planted = _plantingService.IsResourceAt(c);
                        spots.Add(new { x, y, z, moist, planted });
                    }
                }
            }

            return new { crop, spots };
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
                x1 = minX, y1 = minY, x2 = maxX, y2 = maxY, z,
                cleared = true,
                tiles = coords.Count
            };
        }

        // ================================================================
        public object CollectScience()
        {
            var unlockables = new List<object>();
            foreach (var building in _buildingService.Buildings)
            {
                var bs = building.GetSpec<BuildingSpec>();
                if (bs == null || bs.ScienceCost <= 0) continue;
                var templateSpec = building.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                var name = templateSpec?.TemplateName ?? "unknown";
                var unlocked = _buildingUnlockingService.Unlocked(bs);
                unlockables.Add(new { name, cost = bs.ScienceCost, unlocked });
            }

            return new
            {
                points = _scienceService.SciencePoints,
                unlockables
            };
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
                            return new { building = buildingName, unlocked = true,
                                         remaining = _scienceService.SciencePoints,
                                         note = "already unlocked" };
                        var cost = buildingSpec?.ScienceCost ?? 0;
                        if (cost > _scienceService.SciencePoints)
                            return new { error = "not enough science",
                                         building = buildingName,
                                         scienceCost = cost,
                                         currentPoints = _scienceService.SciencePoints };
                        _buildingUnlockingService.Unlock(buildingSpec);
                        _toolUnlockingService.UnlockInternal(blockObjectTool, () => {});
                        return new { building = buildingName, unlocked = true,
                                     remaining = _scienceService.SciencePoints };
                    }
                }

                return new { error = "building not found in toolbar", building = buildingName };
            }
            catch (System.Exception ex)
            {
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

                foreach (var c in _beavers.Read)
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
                var categories = new List<object>();
                foreach (var kvp in groupNeeds)
                {
                    var groupId = kvp.Key;
                    float avgCurrent = beaverCount > 0 ? groupTotals.GetValueOrDefault(groupId) / beaverCount : 0;
                    float avgMax = beaverCount > 0 ? groupMaxTotals.GetValueOrDefault(groupId) / beaverCount : 0;

                    var needs = new List<object>();
                    foreach (var ns in kvp.Value)
                        needs.Add(new { id = ns.Id, favorableWellbeing = ns.FavorableWellbeing, unfavorableWellbeing = ns.UnfavorableWellbeing });

                    categories.Add(new
                    {
                        group = groupId,
                        current = System.Math.Round(avgCurrent, 1),
                        max = System.Math.Round(avgMax, 1),
                        needs
                    });
                }

                return new
                {
                    beavers = beaverCount,
                    categories
                };
            }
            catch (System.Exception ex)
            {
                return new { error = ex.Message };
            }
        }

        public object CollectNotifications()
        {
            var results = new List<object>();
            try
            {
                foreach (var n in _notificationSaver.Notifications)
                {
                    results.Add(new
                    {
                        subject = n.Subject,
                        description = n.Description,
                        cycle = n.Cycle,
                        cycleDay = n.CycleDay
                    });
                }
            }
            catch (System.Exception _ex) { TimberbotLog.Error("write", _ex); }
            return results;
        }

        public object CollectDistribution()
        {
            var results = new List<object>();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var distSetting = dc.GetComponent<Timberborn.DistributionSystem.DistrictDistributionSetting>();
                if (distSetting == null) continue;

                var goods = new List<object>();
                try
                {
                    foreach (var gs in distSetting.GoodDistributionSettings)
                    {
                        goods.Add(new
                        {
                            good = gs.GoodId,
                            importOption = gs.ImportOption.ToString(),
                            exportThreshold = gs.ExportThreshold
                        });
                    }
                }
                catch (System.Exception _ex) { TimberbotLog.Error("write", _ex); }

                results.Add(new
                {
                    district = dc.DistrictName,
                    goods
                });
            }
            return results;
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
                    return new { error = ex.Message, district = districtName, good = goodId };
                }

                return new { district = districtName, good = goodId, importOption, exportThreshold };
            }
            return new { error = "district not found", district = districtName };
        }

        // building work range -- same green circle the player sees
        public object CollectBuildingRange(int buildingId)
        {
            var ec = FindEntity(buildingId);
            if (ec == null)
                return new { error = "building not found", id = buildingId };

            var terrainRange = ec.GetComponent<Timberborn.BuildingsNavigation.BuildingTerrainRange>();
            if (terrainRange == null)
                return new { error = "building has no work range", id = buildingId,
                             name = CleanName(ec.GameObject.name) };

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
                name = CleanName(ec.GameObject.name),
                tiles = range.Count,
                moist = moistCount,
                bounds = range.Count > 0 ? new { x1 = minX, y1 = minY, x2 = maxX, y2 = maxY } : null
            };
        }

        // PLACEMENT VALIDATION
    }
}
