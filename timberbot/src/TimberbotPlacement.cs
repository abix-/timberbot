// TimberbotPlacement.cs -- Building placement, path routing, terrain queries.
//
// FindPlacement: searches a region for valid building spots using the game's own
// validation (PreviewFactory.Create + BlockObject.IsValid). Checks flooding via
// _waterMap, path connectivity via reflection into NavMesh internals, and power
// adjacency via cached power tile positions. Returns top 10 spots sorted by:
// non-flooded > reachable > pathAccess > nearPower > pathCount.
//
// PlaceBuilding: origin-corrects coordinates (user always specifies bottom-left),
// creates a preview, validates, then calls BlockObjectPlacerService.Place().
//
// RoutePath: places a straight-line path with auto-stairs at z-level changes.
// Handles multi-level jumps by stacking platforms + stairs automatically.
//
// GetTerrainHeight: reads terrain column data for a single tile.

using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.BlockObjectTools;
using Timberborn.Coordinates;
using Timberborn.MapIndexSystem;
using Timberborn.TerrainSystem;
using Timberborn.WaterSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.ScienceSystem;
using Timberborn.BuildingsNavigation;
using UnityEngine;

namespace Timberbot
{
    public class TimberbotPlacement
    {
        private readonly ITerrainService _terrainService;
        private readonly IThreadSafeWaterMap _waterMap;
        private readonly MapIndexService _mapIndexService;
        private readonly IThreadSafeColumnTerrainMap _terrainMap;
        private readonly BuildingService _buildingService;
        private readonly BuildingUnlockingService _buildingUnlockingService;
        private readonly BlockObjectPlacerService _blockObjectPlacerService;
        private readonly EntityService _entityService;
        private readonly Timberborn.Navigation.INavMeshService _navMeshService;
        private readonly DistrictCenterRegistry _districtCenterRegistry;
        private readonly PreviewFactory _previewFactory;
        private readonly ScienceService _scienceService;
        private readonly TimberbotEntityCache _cache;

        public TimberbotPlacement(
            ITerrainService terrainService,
            IThreadSafeWaterMap waterMap,
            MapIndexService mapIndexService,
            IThreadSafeColumnTerrainMap terrainMap,
            BuildingService buildingService,
            BuildingUnlockingService buildingUnlockingService,
            BlockObjectPlacerService blockObjectPlacerService,
            EntityService entityService,
            Timberborn.Navigation.INavMeshService navMeshService,
            DistrictCenterRegistry districtCenterRegistry,
            PreviewFactory previewFactory,
            ScienceService scienceService,
            TimberbotEntityCache cache)
        {
            _terrainService = terrainService;
            _waterMap = waterMap;
            _mapIndexService = mapIndexService;
            _terrainMap = terrainMap;
            _buildingService = buildingService;
            _buildingUnlockingService = buildingUnlockingService;
            _blockObjectPlacerService = blockObjectPlacerService;
            _entityService = entityService;
            _navMeshService = navMeshService;
            _districtCenterRegistry = districtCenterRegistry;
            _previewFactory = previewFactory;
            _scienceService = scienceService;
            _cache = cache;
        }

        private static readonly string[] OrientNames = TimberbotEntityCache.OrientNames;
        private static readonly string[] PriorityNames = TimberbotEntityCache.PriorityNames;
        private static string GetPriorityName(Timberborn.PrioritySystem.Priority p) => TimberbotEntityCache.GetPriorityName(p);

        // Faction suffix detected once at startup from building prefab names.
        // Every prefab has the suffix (e.g. "FarmHouse.IronTeeth", "Stairs.Folktails").
        // Faction never changes during a game session.
        private string _factionSuffix = "";

        // Called once from TimberbotService.Load(). Scans any building template name
        // for a dot-separated suffix to determine the active faction.
        public void DetectFaction()
        {
            foreach (var building in _buildingService.Buildings)
            {
                var name = building.GetSpec<Timberborn.TemplateSystem.TemplateSpec>()?.TemplateName ?? "";
                int dot = name.LastIndexOf('.');
                if (dot > 0)
                {
                    _factionSuffix = name.Substring(dot); // e.g. ".IronTeeth" or ".Folktails"
                    break;
                }
            }
            TimberbotLog.Info($"faction: {_factionSuffix}");
        }

        // ================================================================

        // Read the terrain height at a single tile.
        // Timberborn stores terrain as columns: each (x,y) cell has N stacked terrain
        // segments (for caves, overhangs, etc). We want the ceiling of the topmost
        // segment, which is the surface height where buildings can sit.
        // ColumnCounts[index2D] = how many segments at this cell.
        // topIndex = base + (count-1) * stride = the last (topmost) segment.
        private int GetTerrainHeight(int x, int y)
        {
            var size = _terrainService.Size;
            if (x < 0 || x >= size.x || y < 0 || y >= size.y) return 0;
            var index2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
            var stride = _mapIndexService.VerticalStride;
            var columnCount = _terrainMap.ColumnCounts[index2D];
            if (columnCount <= 0) return 0;
            var topIndex = index2D + (columnCount - 1) * stride;
            return _terrainMap.GetColumnCeiling(topIndex);
        }

        // ================================================================
        // WRITE ENDPOINTS -- Tier 3
        // ================================================================

        // List all building templates (prefabs) the faction can build.
        // BuildingService.Buildings iterates the registered template definitions, not
        // placed buildings. Each template has a TemplateSpec (name), BlockObjectSpec
        // (footprint size), and BuildingSpec (science cost, material cost).
        //
        // BuildingCost uses reflection because the property names changed between
        // Timberborn versions (GoodId vs Id). Collect-then-emit pattern on costs
        // prevents partial JSON if reflection throws mid-iteration.
        public object CollectPrefabs()
        {
            var jw = _cache.Jw.Reset().OpenArr();
            foreach (var building in _buildingService.Buildings)
            {
                var templateSpec = building.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                var blockSpec = building.GetSpec<BlockObjectSpec>();
                jw.OpenObj().Key("name").Str(templateSpec?.TemplateName ?? "unknown");
                if (blockSpec != null)
                {
                    var size = blockSpec.Size;
                    jw.Key("sizeX").Int(size.x).Key("sizeY").Int(size.y).Key("sizeZ").Int(size.z);
                }
                var bs = building.GetSpec<BuildingSpec>();
                if (bs != null)
                {
                    if (bs.ScienceCost > 0)
                        jw.Key("scienceCost").Int(bs.ScienceCost).Key("unlocked").Bool(_buildingUnlockingService.Unlocked(bs));
                    // collect costs into a temp list, then write to JSON.
                    // if reflection fails mid-iteration, the JW state is still clean
                    try
                    {
                        var costs = new List<(string good, int amount)>();
                        foreach (var ga in bs.BuildingCost)
                        {
                            // reflection: try GoodId first (newer API), fall back to Id (older)
                            var goodProp = ga.GetType().GetProperty("GoodId") ?? ga.GetType().GetProperty("Id");
                            var amtProp = ga.GetType().GetProperty("Amount");
                            if (goodProp != null && amtProp != null)
                                costs.Add((goodProp.GetValue(ga)?.ToString(), (int)amtProp.GetValue(ga)));
                        }
                        if (costs.Count > 0)
                        {
                            jw.Key("cost").OpenArr();
                            for (int ci = 0; ci < costs.Count; ci++)
                                jw.OpenObj().Key("good").Str(costs[ci].good).Key("amount").Int(costs[ci].amount).CloseObj();
                            jw.CloseArr();
                        }
                    }
                    catch (System.Exception _ex) { TimberbotLog.Error("prefabs.cost", _ex); }
                }
                jw.CloseObj();
            }
            jw.CloseArr();
            return jw.ToString();
        }

        // remove a building from the world
        public object DemolishBuilding(int buildingId)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return new { error = "entity not found", id = buildingId };

            var name = TimberbotEntityCache.CleanName(ec.GameObject.name);
            _entityService.Delete(ec);
            return new { id = buildingId, name, demolished = true };
        }

        // Route a straight-line path from (x1,y1) to (x2,y2), auto-placing stairs at z-level changes.
        // Only axis-aligned lines (x1==x2 or y1==y2). This replaces dozens of individual
        // PlaceBuilding calls with a single intelligent route that handles:
        //   - flat path tiles on level ground
        //   - stairs at single z-level changes
        //   - stacked platforms + stairs for multi-level jumps (e.g. z=2 to z=5 = 3 platforms + stairs)
        //   - demolishing existing paths that overlap with ramp positions
        //   - skipping occupied tiles (existing paths, buildings)
        public object RoutePath(int x1, int y1, int x2, int y2)
        {
            if (x1 != x2 && y1 != y2)
                return new { error = "path must be a straight line (x1==x2 or y1==y2)" };

            // step direction: +1, -1, or 0 for each axis
            int dx = x2 > x1 ? 1 : x2 < x1 ? -1 : 0;
            int dy = y2 > y1 ? 1 : y2 < y1 ? -1 : 0;
            // stairs face the direction of uphill travel
            // enum: south=0, west=1, north=2, east=3
            int stairsOrient = dx > 0 ? 3 : dx < 0 ? 1 : dy > 0 ? 2 : 0;

            int placed = 0, skipped = 0, stairs = 0;
            var errors = new List<string>();
            int cx = x1, cy = y1;
            int prevZ = GetTerrainHeight(cx, cy);

            while (true)
            {
                int tz = GetTerrainHeight(cx, cy);
                if (tz <= 0)
                {
                    errors.Add($"no terrain at ({cx},{cy})");
                    if (cx == x2 && cy == y2) break;
                    cx += dx; cy += dy;
                    continue;
                }

                int zDiff = tz - prevZ;

                if (zDiff != 0)
                {
                    // Multi-level ramp building:
                    // levels = how many z-levels to climb/descend
                    // Each ramp tile gets (step) platforms stacked underneath + 1 stair on top.
                    // Example: 3-level climb needs 3 tiles, each progressively taller:
                    //   tile 0: 0 platforms + stair (ground level)
                    //   tile 1: 1 platform + stair (z+1)
                    //   tile 2: 2 platforms + stair (z+2)
                    int levels = System.Math.Abs(zDiff);
                    int baseZ = System.Math.Min(prevZ, tz);
                    bool goingUp = zDiff > 0;
                    // going down = reverse the stair orientation (rotate 180 degrees)
                    int rampOrient = goingUp ? stairsOrient : (stairsOrient + 2) % 4;

                    // helper: demolish any path at a tile position
                    // O(n) scan but only called once per z-level change (max ~6 times per route)
                    void DemolishPathAt(int px, int py, int pz)
                    {
                        foreach (var cb in _cache.Buildings.Read)
                        {
                            if (cb.BlockObject == null) continue;
                            var c = cb.BlockObject.Coordinates;
                            if (c.x == px && c.y == py && c.z == pz && cb.Name.Contains("Path"))
                            {
                                DemolishBuilding(cb.Id);
                                placed--;
                                break;
                            }
                        }
                    }

                    // build ramp: N tiles, each with (tileIndex) platforms + 1 stair on top
                    // going up: ramp starts at previous tile, extends backward
                    // going down: ramp starts at current tile, extends forward
                    for (int step = 0; step < levels; step++)
                    {
                        int rampTileX, rampTileY;
                        if (goingUp)
                        {
                            // going up: first ramp tile is the previous tile, then go backward
                            rampTileX = cx - dx * (levels - step);
                            rampTileY = cy - dy * (levels - step);
                        }
                        else
                        {
                            // going down: ramp tiles go forward from current position
                            rampTileX = cx + dx * step;
                            rampTileY = cy + dy * step;
                        }

                        // demolish any path we placed on this ramp tile
                        DemolishPathAt(rampTileX, rampTileY, GetTerrainHeight(rampTileX, rampTileY));

                        // stack platforms: step count of them
                        for (int p = 0; p < step; p++)
                        {
                            var platResult = PlaceBuilding("Platform" + _factionSuffix, rampTileX, rampTileY, baseZ + p, "south");
                            if (platResult.GetType().GetProperty("id") == null)
                            {
                                var err = platResult.GetType().GetProperty("error")?.GetValue(platResult);
                                if (err != null && !err.ToString().Contains("occupied"))
                                    errors.Add($"platform at ({rampTileX},{rampTileY},z={baseZ + p}): {err}");
                            }
                        }

                        // place stair on top
                        int stairZ = baseZ + step;
                        var stairResult = PlaceBuilding("Stairs" + _factionSuffix, rampTileX, rampTileY, stairZ, OrientNames[rampOrient]);
                        if (stairResult.GetType().GetProperty("id") != null)
                            stairs++;
                        else
                        {
                            var err = stairResult.GetType().GetProperty("error")?.GetValue(stairResult);
                            if (err != null && !err.ToString().Contains("occupied"))
                                errors.Add($"stairs at ({rampTileX},{rampTileY},z={stairZ}): {err}");
                        }
                    }

                    if (!goingUp)
                    {
                        // skip past the ramp tiles we just built
                        for (int skip = 0; skip < levels - 1; skip++)
                        {
                            cx += dx; cy += dy;
                        }
                    }

                    prevZ = tz;
                    // fall through to place path at current tile (first tile at new z-level)
                }

                // place path at current tile
                var result = PlaceBuilding("Path", cx, cy, tz, "south");
                if (result.GetType().GetProperty("id") != null)
                    placed++;
                else
                {
                    var err = result.GetType().GetProperty("error")?.GetValue(result);
                    if (err != null && !err.ToString().Contains("occupied"))
                        errors.Add($"path at ({cx},{cy}): {err}");
                    else
                        skipped++;
                }

                prevZ = tz;
                if (cx == x2 && cy == y2) break;
                cx += dx; cy += dy;
            }

            var ret = new
            {
                placed,
                stairs,
                skipped,
                errors = errors.Count > 0 ? errors.ToArray() : null
            };
            return ret;
        }

        // general purpose debug endpoint -- navigate, inspect, and call methods on any game object
        // chain through objects with dot-separated paths: "type._field1._field2.MethodName"
        // ================================================================
        // BENCHMARK -- compare foreach vs for-loop on game collections
        // ================================================================


        // Find valid building placement spots in a rectangular search area.
        // This is the most complex method in the mod. It does 5 things:
        //
        // 1. REACHABILITY: uses reflection to access the game's internal NavMesh
        //    and determine which tiles are connected to the district center via paths.
        //    This is the same data the game uses to draw green/red path indicators.
        //
        // 2. PATH SCORING: counts how many path tiles are adjacent to each candidate's
        //    entrance side. More paths = better connectivity.
        //
        // 3. POWER ADJACENCY: checks if any power-conducting building is adjacent to
        //    the placement footprint. Buildings need to be adjacent to conduct power.
        //
        // 4. FLOOD CHECK: reads water height from IThreadSafeWaterMap for every tile
        //    in the building footprint. Any water = flooded = non-functional.
        //
        // 5. PLACEMENT VALIDATION: uses the game's own PreviewFactory to create a
        //    preview entity, Reposition it, and check IsValid(). This runs the same
        //    9 validators the player UI uses (terrain, occupancy, water buildings, etc).
        //
        // Results sorted by: non-flooded > reachable > pathAccess > nearPower > pathCount.
        // Returns top 10 candidates.
        public object FindPlacement(string prefabName, int x1, int y1, int x2, int y2)
        {
            var buildingSpec = _buildingService.GetBuildingTemplate(prefabName);
            if (buildingSpec == null)
                return new { error = "unknown prefab", prefab = prefabName };
            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            if (blockObjectSpec == null)
                return new { error = "no block object spec", prefab = prefabName };

            var size = blockObjectSpec.Size;

            // STEP 1: REACHABILITY
            // Use reflection to access the game's NavMesh internals. These APIs are
            // private because they're not meant for mods, but we need them to determine
            // if a building site is connected to the district center via paths.
            var reachableRoadCoords = new HashSet<Vector3Int>();
            try
            {
                var reflFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var nodeIdSvc = _navMeshService.GetType().GetField("_nodeIdService", reflFlags)
                    ?.GetValue(_navMeshService) as Timberborn.Navigation.NodeIdService;

                foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    var cachingFF = dc.GetComponent<BuildingCachingFlowField>();
                    if (cachingFF == null || nodeIdSvc == null) continue;
                    var accessCoords = (Vector3Int)cachingFF.GetType().GetField("_accessCoordinates", reflFlags).GetValue(cachingFF);
                    int dcNodeId = nodeIdSvc.GridToId(accessCoords);
                    Vector3 dcWorldPos = nodeIdSvc.IdToWorld(dcNodeId);

                    var drawer = dc.GetComponent<DistrictPathNavRangeDrawer>();
                    if (drawer == null) continue;
                    var navRangeSvc = drawer.GetType().GetField("_navigationRangeService", reflFlags)?.GetValue(drawer);
                    if (navRangeSvc == null) continue;

                    var nodesInRange = navRangeSvc.GetType().GetMethod("GetRoadNodesInRange")
                        ?.Invoke(navRangeSvc, new object[] { dcWorldPos }) as System.Collections.IEnumerable;
                    if (nodesInRange == null) continue;

                    foreach (var wc in nodesInRange)
                    {
                        var coordsProp = wc.GetType().GetProperty("Coordinates");
                        if (coordsProp != null)
                            reachableRoadCoords.Add((Vector3Int)coordsProp.GetValue(wc));
                    }
                    break;
                }
            }
            catch (System.Exception _ex) { TimberbotLog.Error("placement", _ex); }

            // Build HashSets of path and power tile positions for O(1) adjacency checks.
            // Tiles are encoded as a single long: x*1000000 + y*1000 + z
            // This avoids allocating Vector3Int keys and works for maps up to 999x999x999.
            var pathTiles = new HashSet<long>();
            var powerTiles = new HashSet<long>();
            foreach (var cb in _cache.Buildings.Read)
            {
                if (cb.BlockObject == null) continue;
                // paths and stairs provide connectivity for reachability scoring
                if (cb.Name.Contains("Path") || cb.Name.Contains("Stairs"))
                {
                    foreach (var block in cb.BlockObject.PositionedBlocks.GetAllBlocks())
                    {
                        var c = block.Coordinates;
                        pathTiles.Add((long)c.x * 1000000 + (long)c.y * 1000 + c.z);
                    }
                }
                // power-conducting buildings (anything with a PowerNode component)
                if (cb.PowerNode != null)
                {
                    foreach (var block in cb.BlockObject.PositionedBlocks.GetAllBlocks())
                    {
                        var c = block.Coordinates;
                        powerTiles.Add((long)c.x * 1000000 + (long)c.y * 1000 + c.z);
                    }
                }
            }

            var orientNames = new[] { "south", "west", "north", "east" };
            var results = new List<(int x, int y, int z, int orient, bool pathAccess, int pathCount, bool reachable, bool nearPower, bool flooded)>();

            // PERF: create ONE preview entity, reuse it for every candidate position.
            // Preview is a Unity GameObject with validation components attached.
            // Creating one per candidate would be ~1000x slower (Instantiate + Destroy).
            // Reposition() moves the same preview to each new position for validation.
            var placeableSpec = buildingSpec.GetSpec<PlaceableBlockObjectSpec>();
            Preview cachedPreview = null;
            try { if (placeableSpec != null) cachedPreview = _previewFactory.Create(placeableSpec); } catch (System.Exception _ex) { TimberbotLog.Error("placement", _ex); }
            try
            {

                for (int ty = y1; ty <= y2; ty++)
                {
                    for (int tx = x1; tx <= x2; tx++)
                    {
                        int tz = GetTerrainHeight(tx, ty);
                        if (tz <= 0) continue;

                        // Try all 4 orientations and pick the one with the most adjacent
                        // path tiles on its entrance side. This maximizes connectivity.
                        int bestOrient = -1;
                        int bestPathCount = -1;

                        for (int orient = 0; orient < 4; orient++)
                        {
                            // validate using the cached preview (game's own placement rules)
                            if (cachedPreview == null) continue;
                            // orient 1,3 (west/east) swap x and y dimensions of the footprint
                            int vrx = size.x, vry = size.y;
                            if (orient == 1 || orient == 3) { vrx = size.y; vry = size.x; }
                            // origin correction: the game uses top-right corner as origin for some
                            // orientations. We always think in bottom-left, so translate.
                            int vgx = tx, vgy = ty;
                            switch (orient)
                            {
                                case 1: vgy = ty + vry - 1; break;
                                case 2: vgx = tx + vrx - 1; vgy = ty + vry - 1; break;
                                case 3: vgx = tx + vrx - 1; break;
                            }
                            var placement = new Placement(new Vector3Int(vgx, vgy, tz),
                                (Timberborn.Coordinates.Orientation)orient, FlipMode.Unflipped);
                            cachedPreview.Reposition(placement);
                            if (!cachedPreview.BlockObject.IsValid()) continue;

                            // count path tiles on entrance side
                            int rx = size.x, ry = size.y;
                            if (orient == 1 || orient == 3) { rx = size.y; ry = size.x; }

                            int pathCount = 0;
                            switch (orient)
                            {
                                case 0: // south: check y-1 row
                                    for (int px = tx; px < tx + rx; px++)
                                        if (pathTiles.Contains((long)px * 1000000 + (long)(ty - 1) * 1000 + tz)) pathCount++;
                                    break;
                                case 1: // west: check x-1 column
                                    for (int py = ty; py < ty + ry; py++)
                                        if (pathTiles.Contains((long)(tx - 1) * 1000000 + (long)py * 1000 + tz)) pathCount++;
                                    break;
                                case 2: // north: check y+ry row
                                    for (int px = tx; px < tx + rx; px++)
                                        if (pathTiles.Contains((long)px * 1000000 + (long)(ty + ry) * 1000 + tz)) pathCount++;
                                    break;
                                case 3: // east: check x+rx column
                                    for (int py = ty; py < ty + ry; py++)
                                        if (pathTiles.Contains((long)(tx + rx) * 1000000 + (long)py * 1000 + tz)) pathCount++;
                                    break;
                            }

                            if (pathCount > bestPathCount)
                            {
                                bestPathCount = pathCount;
                                bestOrient = orient;
                            }
                        }

                        if (bestOrient >= 0)
                        {
                            // check district road reachability on entrance-side path tiles
                            bool reachable = false;
                            if (bestPathCount > 0)
                            {
                                int erx = size.x, ery = size.y;
                                if (bestOrient == 1 || bestOrient == 3) { erx = size.y; ery = size.x; }
                                var checkCoords = new List<Vector3Int>();
                                switch (bestOrient)
                                {
                                    case 0:
                                        for (int px = tx; px < tx + erx; px++)
                                            checkCoords.Add(new Vector3Int(px, ty - 1, tz));
                                        break;
                                    case 1:
                                        for (int py = ty; py < ty + ery; py++)
                                            checkCoords.Add(new Vector3Int(tx - 1, py, tz));
                                        break;
                                    case 2:
                                        for (int px = tx; px < tx + erx; px++)
                                            checkCoords.Add(new Vector3Int(px, ty + ery, tz));
                                        break;
                                    case 3:
                                        for (int py = ty; py < ty + ery; py++)
                                            checkCoords.Add(new Vector3Int(tx + erx, py, tz));
                                        break;
                                }
                                foreach (var coord in checkCoords)
                                {
                                    if (reachableRoadCoords.Contains(coord))
                                    { reachable = true; break; }
                                }
                            }

                            // check power adjacency on all 4 sides of footprint
                            int brx = size.x, bry = size.y;
                            if (bestOrient == 1 || bestOrient == 3) { brx = size.y; bry = size.x; }
                            bool nearPower = false;
                            for (int px = tx - 1; px <= tx + brx && !nearPower; px++)
                                for (int py = ty - 1; py <= ty + bry && !nearPower; py++)
                                {
                                    if (px >= tx && px < tx + brx && py >= ty && py < ty + bry) continue;
                                    if (powerTiles.Contains((long)px * 1000000 + (long)py * 1000 + tz))
                                        nearPower = true;
                                }

                            // check flooding: any water on footprint tiles means building will flood
                            int frx = size.x, fry = size.y;
                            if (bestOrient == 1 || bestOrient == 3) { frx = size.y; fry = size.x; }
                            bool flooded = false;
                            for (int fx = tx; fx < tx + frx && !flooded; fx++)
                                for (int fy = ty; fy < ty + fry && !flooded; fy++)
                                {
                                    try
                                    {
                                        float wh = _waterMap.CeiledWaterHeight(new Vector3Int(fx, fy, tz));
                                        if (wh > 0) flooded = true;
                                    }
                                    catch (System.Exception _ex) { TimberbotLog.Error("placement", _ex); }
                                }

                            results.Add((tx, ty, tz, bestOrient, bestPathCount > 0, bestPathCount, reachable, nearPower, flooded));
                        }
                    }
                }

                // sort: non-flooded > reachable > path access > power > path count
                results.Sort((a, b) =>
                {
                    if (a.flooded != b.flooded) return a.flooded ? 1 : -1;
                    if (a.reachable != b.reachable) return b.reachable ? 1 : -1;
                    if (a.pathAccess != b.pathAccess) return b.pathAccess ? 1 : -1;
                    if (a.nearPower != b.nearPower) return b.nearPower ? 1 : -1;
                    return b.pathCount - a.pathCount;
                });

            } // end try
            finally
            {
                if (cachedPreview != null)
                    UnityEngine.Object.Destroy(cachedPreview.GameObject);
            }

            int count = results.Count > 10 ? 10 : results.Count;

            var jw = _cache.Jw.Reset().OpenObj()
                .Key("prefab").Str(prefabName)
                .Key("sizeX").Int(size.x).Key("sizeY").Int(size.y)
                .Key("placements").OpenArr();
            for (int i = 0; i < count; i++)
            {
                var r = results[i];
                jw.OpenObj()
                    .Key("x").Int(r.x).Key("y").Int(r.y).Key("z").Int(r.z)
                    .Key("orientation").Str(orientNames[r.orient])
                    .Key("pathAccess").Bool(r.pathAccess)
                    .Key("pathCount").Int(r.pathCount)
                    .Key("reachable").Bool(r.reachable)
                    .Key("nearPower").Bool(r.nearPower)
                    .Key("flooded").Bool(r.flooded)
                    .CloseObj();
            }
            jw.CloseArr().CloseObj();
            return jw.ToString();
        }

        // Place a building at exact coordinates with full validation:
        // 1. Prefab must exist in BuildingService and be unlocked (if it costs science)
        // 2. Origin correction: the user specifies bottom-left corner, but the game's
        //    internal coordinate system uses a different origin per orientation.
        //    We translate so the caller never has to think about rotation math.
        // 3. PreviewFactory validation: creates a temporary preview entity and checks
        //    IsValid() -- this runs the game's own 9 validators (terrain, occupancy,
        //    water buildings, district boundaries, etc.)
        // 4. Only after all checks pass: BlockObjectPlacerService.Place() creates the
        //    real building entity in the game world.

        private static int ParseOrientation(string orient)
        {
            if (string.IsNullOrEmpty(orient)) return 0;
            var lower = orient.Trim().ToLowerInvariant();
            switch (lower)
            {
                case "south": return 0;
                case "west": return 1;
                case "north": return 2;
                case "east": return 3;
                default: return -1;
            }
        }

        public object PlaceBuilding(string prefabName, int x, int y, int z, string orientationStr)
        {
            int orientation = ParseOrientation(orientationStr);
            if (orientation < 0)
                return new
                {
                    error = $"invalid orientation '{orientationStr}', use: south, west, north, east",
                    prefab = prefabName
                };

            var buildingSpec = _buildingService.GetBuildingTemplate(prefabName);
            if (buildingSpec == null)
                return new { error = "unknown prefab", prefab = prefabName };

            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            if (blockObjectSpec == null)
                return new { error = "no block object spec", prefab = prefabName };

            // check building is unlocked
            var bs = buildingSpec.GetSpec<BuildingSpec>();
            if (bs != null && bs.ScienceCost > 0 && !_buildingUnlockingService.Unlocked(bs))
                return new
                {
                    error = "building not unlocked",
                    prefab = prefabName,
                    scienceCost = bs.ScienceCost,
                    currentPoints = _scienceService.SciencePoints
                };

            // Origin correction: user always specifies bottom-left corner (smallest x,y).
            // The game's Placement struct expects a different origin depending on orientation:
            //   south (0): bottom-left = same as user    -> gx=x, gy=y
            //   west  (1): bottom-left is at top-left    -> shift gy up by height-1
            //   north (2): bottom-left is at top-right   -> shift both gx right, gy up
            //   east  (3): bottom-left is at bottom-right -> shift gx right by width-1
            // For orient 1,3: footprint dimensions swap (a 2x4 building becomes 4x2)
            var size = blockObjectSpec.Size;
            int rx = size.x, ry = size.y;
            if (orientation == 1 || orientation == 3) { rx = size.y; ry = size.x; }
            int gx = x, gy = y;
            switch (orientation)
            {
                case 1: gy = y + ry - 1; break;
                case 2: gx = x + rx - 1; gy = y + ry - 1; break;
                case 3: gx = x + rx - 1; break;
            }

            // validate using the game's own preview system -- identical to what the player
            // sees when placing a building (green = valid, red = invalid)
            if (!ValidatePlacement(buildingSpec, blockObjectSpec, x, y, z, orientation))
                return new
                {
                    error = $"Cannot place BlockObject {prefabName} at ({gx}, {gy}, {z}).",
                    prefab = prefabName,
                    x,
                    y,
                    z,
                    orientation = OrientNames[orientation]
                };

            // Validation passed -- create the real building.
            // GetMatchingPlacer returns the right placer for the block type (regular,
            // stackable, etc). The callback fires synchronously with the new entity.
            // We capture its InstanceID (same ID used everywhere in the API) and
            // clean name (strips "(Clone)" suffix Unity adds).
            var orient = (Timberborn.Coordinates.Orientation)orientation;
            var placement = new Placement(new Vector3Int(gx, gy, z), orient,
                FlipMode.Unflipped);

            var placer = _blockObjectPlacerService.GetMatchingPlacer(blockObjectSpec);
            int placedId = 0;
            string placedName = "";
            placer.Place(blockObjectSpec, placement, (entity) =>
            {
                placedId = entity.GameObject.GetInstanceID();
                placedName = TimberbotEntityCache.CleanName(entity.GameObject.name);
            });

            if (placedId == 0)
            {
                return new
                {
                    error = "placement rejected by game engine",
                    prefab = prefabName,
                    x,
                    y,
                    z,
                    orientation,
                    sizeX = size.x,
                    sizeY = size.y,
                    sizeZ = size.z,
                    hint = "passed pre-validation but game rejected it"
                };
            }

            return new { id = placedId, name = placedName, x, y, z, orientation = OrientNames[orientation] };
        }

        private bool ValidatePlacement(BuildingSpec buildingSpec, BlockObjectSpec blockObjectSpec, int x, int y, int z, int orientation)
        {
            var size = blockObjectSpec.Size;
            int rx = size.x, ry = size.y;
            if (orientation == 1 || orientation == 3) { rx = size.y; ry = size.x; }
            int gx = x, gy = y;
            switch (orientation)
            {
                case 1: gy = y + ry - 1; break;
                case 2: gx = x + rx - 1; gy = y + ry - 1; break;
                case 3: gx = x + rx - 1; break;
            }
            var placeableSpec = buildingSpec.GetSpec<PlaceableBlockObjectSpec>();
            if (placeableSpec == null) return false;
            Preview preview = null;
            try
            {
                var placement = new Placement(new Vector3Int(gx, gy, z),
                    (Timberborn.Coordinates.Orientation)orientation, FlipMode.Unflipped);
                preview = _previewFactory.Create(placeableSpec);
                preview.Reposition(placement);
                return preview.BlockObject.IsValid();
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error($"ValidatePlacement at ({x},{y},{z})", ex);
                return false;
            }
            finally
            {
                if (preview != null)
                    UnityEngine.Object.Destroy(preview.GameObject);
            }
        }
    }
}
