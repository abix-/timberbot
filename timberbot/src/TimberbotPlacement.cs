// TimberbotPlacement.cs -- Building placement, path routing, terrain queries.
//
// FindPlacement: searches a region for valid building spots using the game's own
// validation (PreviewFactory.Create + BlockObject.IsValid). Checks flooding via
// WaterDepth on ground-required tiles, path connectivity via reflection into NavMesh
// internals, and power adjacency via cached power tile positions. Water buildings
// sort by waterDepth first. Others: non-flooded > reachable > distance (closer) > pathAccess > nearPower.
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
using Timberborn.WaterBuildings;
using Timberborn.WaterSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.ScienceSystem;
using Timberborn.BuildingsNavigation;
using Timberborn.GameFactionSystem;
using UnityEngine;

namespace Timberbot
{
    public struct PlaceBuildingResult
    {
        public int Id;
        public string Name;
        public string Error;
        public string Prefab;
        public int ScienceCost;
        public int CurrentPoints;
        public string Occupant;
        public int X, Y, Z;
        public string Orientation;
        public bool Success => Id != 0;

        public static PlaceBuildingResult Fail(string error, int x = 0, int y = 0, int z = 0)
            => new PlaceBuildingResult { Error = error, X = x, Y = y, Z = z };

        public string ToJson(TimberbotJw jw)
        {
            jw.Reset().OpenObj();
            if (Error != null)
            {
                jw.Prop("error", Error);
                if (X != 0 || Y != 0) jw.Prop("x", X).Prop("y", Y).Prop("z", Z);
                if (Prefab != null) jw.Prop("prefab", Prefab);
                if (ScienceCost > 0) jw.Prop("scienceCost", ScienceCost).Prop("currentPoints", CurrentPoints);
                if (Occupant != null) jw.Prop("occupant", Occupant);
            }
            else
            {
                jw.Prop("id", Id).Prop("name", Name)
                  .Prop("x", X).Prop("y", Y).Prop("z", Z)
                  .Prop("orientation", Orientation);
            }
            return jw.CloseObj().ToString();
        }

        public void WriteErrorJson(TimberbotJw jw)
        {
            jw.OpenObj();
            if (Prefab != null) jw.Prop("prefab", Prefab);
            jw.Prop("error", Error ?? "unknown");
            if (ScienceCost > 0) jw.Prop("scienceCost", ScienceCost).Prop("currentPoints", CurrentPoints);
            jw.CloseObj();
        }
    }

    public class TimberbotPlacement
    {
        public readonly TimberbotJw Jw = new TimberbotJw(1024);
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
        private readonly FactionService _factionService;
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
            FactionService factionService,
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
            _factionService = factionService;
            _cache = cache;
        }

        private static readonly string[] OrientNames = TimberbotEntityCache.OrientNames;
        private static readonly string[] PriorityNames = TimberbotEntityCache.PriorityNames;
        private static string GetPriorityName(Timberborn.PrioritySystem.Priority p) => TimberbotEntityCache.GetPriorityName(p);

        // Faction suffix for prefab names (e.g. ".IronTeeth" or ".Folktails").
        // Detected once at startup via FactionService.Current.Id -- the same API
        // other mods (UnifiedFactions) use. Faction never changes during a game session.
        private string _factionSuffix = "";

        // Called once from TimberbotService.Load(), before BuildAllIndexes.
        // Sets both the local suffix (for RoutePath prefabs) and the static suffix
        // on TimberbotEntityCache (for CleanName to strip faction from entity names).
        public void DetectFaction()
        {
            _factionSuffix = "." + _factionService.Current.Id;
            TimberbotEntityCache.FactionSuffix = _factionSuffix;
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

        // Check if a tile has an overhang (multiple terrain columns).
        // Multiple columns = cave/overhang = unsafe for path/stair placement.
        private bool HasOverhang(int x, int y)
        {
            var size = _terrainService.Size;
            if (x < 0 || x >= size.x || y < 0 || y >= size.y) return false;
            var index2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
            return _terrainMap.ColumnCounts[index2D] > 1;
        }

        // Read water depth at a tile from the water column data.
        // Iterates columns top-down, returns first with WaterDepth > 0.
        private float GetWaterDepth(int x, int y)
        {
            try
            {
                var idx2D = _mapIndexService.CellToIndex(new Vector2Int(x, y));
                int colCount = _waterMap.ColumnCount(idx2D);
                var stride = _mapIndexService.VerticalStride;
                for (int ci = colCount - 1; ci >= 0; ci--)
                {
                    int idx3D = ci * stride + idx2D;
                    var col = _waterMap.WaterColumns[idx3D];
                    if (col.WaterDepth > 0) return col.WaterDepth;
                }
            }
            catch (System.Exception _ex) { TimberbotLog.Error("water", _ex); }
            return 0f;
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
            var jw = _cache.Jw.BeginArr();
            foreach (var building in _buildingService.Buildings)
            {
                var templateSpec = building.GetSpec<Timberborn.TemplateSystem.TemplateSpec>();
                var blockSpec = building.GetSpec<BlockObjectSpec>();
                jw.OpenObj().Prop("name", templateSpec?.TemplateName ?? "unknown");
                if (blockSpec != null)
                {
                    var size = blockSpec.Size;
                    jw.Prop("sizeX", size.x).Prop("sizeY", size.y).Prop("sizeZ", size.z);
                }
                var bs = building.GetSpec<BuildingSpec>();
                if (bs != null)
                {
                    if (bs.ScienceCost > 0)
                        jw.Prop("scienceCost", bs.ScienceCost).Prop("unlocked", _buildingUnlockingService.Unlocked(bs));
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
                            jw.Arr("cost");
                            for (int ci = 0; ci < costs.Count; ci++)
                                jw.OpenObj().Prop("good", costs[ci].good).Prop("amount", costs[ci].amount).CloseObj();
                            jw.CloseArr();
                        }
                    }
                    catch (System.Exception _ex) { TimberbotLog.Error("prefabs.cost", _ex); }
                }
                jw.CloseObj();
            }
            return jw.End();
        }

        // remove a building from the world
        public object DemolishBuilding(int buildingId)
        {
            var ec = _cache.FindEntity(buildingId);
            if (ec == null)
                return Jw.Error("not_found", ("id", buildingId));

            var name = TimberbotEntityCache.CleanName(ec.GameObject.name);
            _entityService.Delete(ec);
            return Jw.Result(("id", buildingId), ("name", name), ("demolished", true));
        }

        // Route a path from (x1,y1) to (x2,y2) using A* to avoid obstacles.
        // Falls back to straight-line when A* finds no route (fully blocked).
        // Three-pass: 1) A* finds 2D waypoints, 2) plan elevation (stairs/platforms), 3) place.
        public object RoutePath(int x1, int y1, int x2, int y2, string style = "direct", int sections = 0)
        {
            string stairsPrefab = "Stairs" + _factionSuffix;
            string platformPrefab = "Platform" + _factionSuffix;
            var stairsSpec = _buildingService.GetBuildingTemplate(stairsPrefab);
            var platformSpec = _buildingService.GetBuildingTemplate(platformPrefab);
            var stairsBs = stairsSpec?.GetSpec<BuildingSpec>();
            var platformBs = platformSpec?.GetSpec<BuildingSpec>();
            bool stairsUnlocked = stairsBs == null || stairsBs.ScienceCost <= 0 || _buildingUnlockingService.Unlocked(stairsBs);
            bool platformUnlocked = platformBs == null || platformBs.ScienceCost <= 0 || _buildingUnlockingService.Unlocked(platformBs);

            // --- SETUP ---
            const int PADDING = 10;
            int minX = System.Math.Min(x1, x2) - PADDING;
            int minY = System.Math.Min(y1, y2) - PADDING;
            int maxX = System.Math.Max(x1, x2) + PADDING;
            int maxY = System.Math.Max(y1, y2) + PADDING;
            var mapSize = _terrainService.Size;
            if (minX < 0) minX = 0;
            if (minY < 0) minY = 0;
            if (maxX >= mapSize.x) maxX = mapSize.x - 1;
            if (maxY >= mapSize.y) maxY = mapSize.y - 1;
            int gw = maxX - minX + 1;
            int gh = maxY - minY + 1;

            var costGrid = BuildCostGrid(minX, minY, gw, gh);
            if (style != "direct" && style != "straight") style = "direct";

            var existingPathTiles = new HashSet<long>();
            foreach (var cb in _cache.Buildings.Read)
            {
                if (cb.OccupiedTiles == null) continue;
                if (!cb.Name.Contains("Path") && !cb.Name.Contains("Stairs") && !cb.Name.Contains("Platform")) continue;
                foreach (var t in cb.OccupiedTiles)
                    existingPathTiles.Add((long)t.x * 100000 + t.y);
            }

            System.Func<int, int, bool> isBlocked = (tx, ty) =>
            {
                int lx = tx - minX, ly = ty - minY;
                if (lx < 0 || lx >= gw || ly < 0 || ly >= gh) return true;
                int bi = (ly * gw + lx) * 4;
                return costGrid[bi] >= 255 && costGrid[bi + 1] >= 255
                    && costGrid[bi + 2] >= 255 && costGrid[bi + 3] >= 255;
            };

            // mark a tile as impassable in the edge-based cost grid
            System.Action<int, int> markImpassable = (tx, ty) =>
            {
                int lx = tx - minX, ly = ty - minY;
                if (lx >= 0 && lx < gw && ly >= 0 && ly < gh)
                {
                    int si = (ly * gw + lx) * 4;
                    costGrid[si] = costGrid[si + 1] = costGrid[si + 2] = costGrid[si + 3] = 255;
                }
            };

            var placedTiles = new HashSet<long>(); // all tiles we've placed on (stairs + paths)
            var errors = new List<string>();
            int pathCount = 0, stairCount = 0, platformCount = 0, skipped = 0;
            var failedResults = new List<PlaceBuildingResult>();
            var sectionLog = new List<(string type, int fromX, int fromY, int toX, int toY, int paths, int stairs)>();
            int maxSections = 50; // safety limit

            // --- SECTIONAL LOOP ---
            int curX = x1, curY = y1;

            for (int section = 0; section < maxSections; section++)
            {
                // A* from current position to destination
                var waypoints = AStarPath(costGrid, gw, gh,
                    curX - minX, curY - minY, x2 - minX, y2 - minY, gw * gh, style);

                if (waypoints == null)
                {
                    errors.Add($"section {section}: A* found no route from ({curX},{curY}) to ({x2},{y2})");
                    break;
                }

                // walk waypoints looking for z-change
                int prevZ = GetTerrainHeight(curX, curY);
                int zChangeWi = -1;
                for (int wi = 1; wi < waypoints.Count; wi++)
                {
                    int wx = waypoints[wi].Item1 + minX;
                    int wy = waypoints[wi].Item2 + minY;
                    int wz = GetTerrainHeight(wx, wy);
                    if (wz <= 0) continue;
                    if (wz != prevZ) { zChangeWi = wi; break; }
                    prevZ = wz;
                }

                if (zChangeWi < 0)
                {
                    // no z-change -- place paths along entire route to destination
                    int secPaths = 0;
                    foreach (var (lwx, lwy) in waypoints)
                    {
                        int px = lwx + minX, py = lwy + minY;
                        int pz = GetTerrainHeight(px, py);
                        if (pz <= 0) continue;
                        long key = (long)px * 100000 + py;
                        if (placedTiles.Contains(key) || existingPathTiles.Contains(key)) continue;
                        var r = PlaceBuilding("Path", px, py, pz, "south");
                        if (r.Success) { pathCount++; secPaths++; placedTiles.Add(key); markImpassable(px, py); }
                        else { skipped++; failedResults.Add(r); }
                    }
                    sectionLog.Add(("flat", curX, curY, x2, y2, secPaths, 0));
                    break; // done
                }

                // z-change found -- compute stair
                int cx = waypoints[zChangeWi].Item1 + minX;
                int cy = waypoints[zChangeWi].Item2 + minY;
                int tz = GetTerrainHeight(cx, cy);
                int pzBefore = GetTerrainHeight(waypoints[zChangeWi - 1].Item1 + minX, waypoints[zChangeWi - 1].Item2 + minY);
                int zDiff = tz - pzBefore;
                int levels = System.Math.Abs(zDiff);
                bool goingUp = zDiff > 0;
                int baseZ = System.Math.Min(pzBefore, tz);

                if (!stairsUnlocked)
                {
                    errors.Add($"section {section}: stairs not unlocked at ({cx},{cy})");
                    break;
                }
                if (levels > 1 && !platformUnlocked)
                {
                    errors.Add($"section {section}: platforms not unlocked for {levels}-level at ({cx},{cy})");
                    break;
                }

                // travel direction: dominant cardinal axis from z-change toward destination
                // ensures entrance faces back toward curPos, exit faces toward destination
                int dirX = x2 - (waypoints[zChangeWi].Item1 + minX);
                int dirY = y2 - (waypoints[zChangeWi].Item2 + minY);
                int tdx, tdy;
                if (System.Math.Abs(dirX) >= System.Math.Abs(dirY))
                    { tdx = dirX > 0 ? 1 : -1; tdy = 0; }
                else
                    { tdx = 0; tdy = dirY > 0 ? 1 : -1; }
                // stair orientation: faces uphill
                int updx = goingUp ? tdx : -tdx;
                int updy = goingUp ? tdy : -tdy;
                int orientIdx = updx > 0 ? 3 : updx < 0 ? 1 : updy > 0 ? 2 : 0;

                // compute ramp tiles
                var rampTiles = new List<(int x, int y, int bz, int plats)>();
                int entX, entY, extX, extY;

                if (levels == 1)
                {
                    int stairX = goingUp ? cx - tdx : cx;
                    int stairY = goingUp ? cy - tdy : cy;
                    entX = stairX - tdx;
                    entY = stairY - tdy;
                    extX = stairX + tdx;
                    extY = stairY + tdy;
                    rampTiles.Add((stairX, stairY, baseZ, 0));
                }
                else
                {
                    int firstX = cx - tdx * levels;
                    int firstY = cy - tdy * levels;
                    for (int step = 0; step < levels; step++)
                        rampTiles.Add((firstX + tdx * step, firstY + tdy * step, baseZ, step));
                    entX = firstX - tdx;
                    entY = firstY - tdy;
                    int lastX = firstX + tdx * (levels - 1);
                    int lastY = firstY + tdy * (levels - 1);
                    extX = lastX + tdx;
                    extY = lastY + tdy;
                }

                // check obstructions
                bool blocked = isBlocked(entX, entY) || isBlocked(extX, extY);
                foreach (var rt in rampTiles)
                    if (!blocked && isBlocked(rt.x, rt.y)) blocked = true;
                if (blocked)
                {
                    errors.Add($"section {section}: stair obstructed at ({cx},{cy})");
                    break;
                }

                // place stair
                int secStairs = 0;
                foreach (var (rtx, rty, rtBaseZ, platCount) in rampTiles)
                {
                    long key = (long)rtx * 100000 + rty;
                    if (existingPathTiles.Contains(key)) continue;
                    for (int p = 0; p < platCount; p++)
                    {
                        var r = PlaceBuilding(platformPrefab, rtx, rty, rtBaseZ + p, "south");
                        if (r.Success) platformCount++; else { skipped++; failedResults.Add(r); }
                    }
                    var rs = PlaceBuilding(stairsPrefab, rtx, rty, rtBaseZ + platCount, OrientNames[orientIdx]);
                    if (rs.Success) { stairCount++; secStairs++; } else { skipped++; failedResults.Add(rs); }
                    placedTiles.Add(key);
                    markImpassable(rtx, rty);
                }

                // place entrance/exit paths
                foreach (var (bx, by) in new[] { (entX, entY), (extX, extY) })
                {
                    long bkey = (long)bx * 100000 + by;
                    if (placedTiles.Contains(bkey) || existingPathTiles.Contains(bkey)) continue;
                    int bz = GetTerrainHeight(bx, by);
                    if (bz <= 0) continue;
                    var br = PlaceBuilding("Path", bx, by, bz, "south");
                    if (br.Success) { pathCount++; placedTiles.Add(bkey); existingPathTiles.Add(bkey); }
                    else { skipped++; failedResults.Add(br); }
                }

                // A* from curX,curY to stair entrance, place paths
                int secPaths2 = 0;
                if (curX != entX || curY != entY)
                {
                    var entWaypoints = AStarPath(costGrid, gw, gh,
                        curX - minX, curY - minY, entX - minX, entY - minY, gw * gh, style);
                    if (entWaypoints != null)
                    {
                        foreach (var (lwx, lwy) in entWaypoints)
                        {
                            int px = lwx + minX, py = lwy + minY;
                            int pz = GetTerrainHeight(px, py);
                            if (pz <= 0) continue;
                            long key = (long)px * 100000 + py;
                            if (placedTiles.Contains(key) || existingPathTiles.Contains(key)) continue;
                            var r = PlaceBuilding("Path", px, py, pz, "south");
                            if (r.Success) { pathCount++; secPaths2++; placedTiles.Add(key); markImpassable(px, py); }
                            else { skipped++; failedResults.Add(r); }
                        }
                    }
                    else
                    {
                        errors.Add($"section {section}: no route from ({curX},{curY}) to stair entrance ({entX},{entY})");
                    }
                }

                sectionLog.Add(("stair", curX, curY, extX, extY, secPaths2, secStairs));

                // sections limit: stop after N sections
                if (sections > 0 && section + 1 >= sections) { curX = extX; curY = extY; break; }

                // advance to stair exit
                curX = extX;
                curY = extY;
            }

            // serialize
            var jw = _cache.Jw.BeginObj();
            if (sections > 0) jw.Prop("sections", sections);
            jw.Obj("placed").Prop("paths", pathCount);
            if (stairCount > 0) jw.Prop("stairs", stairCount);
            if (platformCount > 0) jw.Prop("platforms", platformCount);
            jw.CloseObj().Prop("skipped", skipped);

            // debug: sections
            jw.Arr("sections");
            foreach (var s in sectionLog)
                jw.OpenObj().Prop("type", s.type).Prop("from", $"{s.fromX},{s.fromY}")
                    .Prop("to", $"{s.toX},{s.toY}").Prop("paths", s.paths).Prop("stairs", s.stairs).CloseObj();
            jw.CloseArr();

            if (curX != x2 || curY != y2)
                jw.Prop("stoppedAt", $"{curX},{curY}");

            if (failedResults.Count > 0 || errors.Count > 0)
            {
                jw.Arr("errors");
                foreach (var r in failedResults)
                    r.WriteErrorJson(jw);
                foreach (var e in errors)
                    jw.OpenObj().Prop("error", e).CloseObj();
                jw.CloseArr();
            }
            jw.CloseObj();
            return jw.ToString();
        }

        // Build an edge-based cost grid for A* over the region (minX,minY) with dimensions (w,h).
        // Layout: ushort[w * h * 4] -- 4 directional entry costs per tile.
        // Direction indices match ddx/ddy: 0=from west(+X), 1=from east(-X), 2=from south(+Y), 3=from north(-Y).
        // Base costs: 0=existing path, 2=open ground, 8=shallow water, 50=deep water, 255=impassable.
        // Z-change penalty: +30 per z-level difference on that edge. Caps at 254.
        private ushort[] BuildCostGrid(int minX, int minY, int w, int h)
        {
            // first pass: compute base tile costs and terrain heights
            var baseCost = new ushort[w * h];
            var heights = new int[w * h];

            for (int ly = 0; ly < h; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    int idx = ly * w + lx;
                    int tz = GetTerrainHeight(minX + lx, minY + ly);
                    heights[idx] = tz;
                    baseCost[idx] = tz > 0 ? (ushort)2 : (ushort)255;
                }
            }

            // water
            for (int ly = 0; ly < h; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    int idx = ly * w + lx;
                    if (baseCost[idx] >= 255) continue;
                    float depth = GetWaterDepth(minX + lx, minY + ly);
                    if (depth > 0.5f) baseCost[idx] = 50;
                    else if (depth > 0f) baseCost[idx] = 8;
                }
            }

            // overhangs
            for (int ly = 0; ly < h; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    int idx = ly * w + lx;
                    if (baseCost[idx] >= 255) continue;
                    if (HasOverhang(minX + lx, minY + ly))
                        baseCost[idx] = 255;
                }
            }

            // buildings
            foreach (var cb in _cache.Buildings.Read)
            {
                if (cb.OccupiedTiles == null) continue;
                bool isPath = cb.Name.Contains("Path") || cb.Name.Contains("Stairs") || cb.Name.Contains("Platform");
                foreach (var t in cb.OccupiedTiles)
                {
                    int lx = t.x - minX, ly = t.y - minY;
                    if (lx < 0 || lx >= w || ly < 0 || ly >= h) continue;
                    baseCost[ly * w + lx] = isPath ? (ushort)0 : (ushort)255;
                }
            }

            // natural resources
            foreach (var nr in _cache.NaturalResources.Read)
            {
                int lx = nr.X - minX, ly = nr.Y - minY;
                if (lx < 0 || lx >= w || ly < 0 || ly >= h) continue;
                baseCost[ly * w + lx] = 255;
            }

            // second pass: build edge-based grid with z-change penalties
            // directions: 0=from west(+X), 1=from east(-X), 2=from south(+Y), 3=from north(-Y)
            int[] ndx = { -1, 1, 0, 0 };  // neighbor offset for each entry direction
            int[] ndy = { 0, 0, -1, 1 };
            var grid = new ushort[w * h * 4];

            for (int ly = 0; ly < h; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    int idx = ly * w + lx;
                    int idx4 = idx * 4;
                    ushort bc = baseCost[idx];

                    if (bc >= 255)
                    {
                        grid[idx4] = grid[idx4 + 1] = grid[idx4 + 2] = grid[idx4 + 3] = 255;
                        continue;
                    }

                    for (int d = 0; d < 4; d++)
                    {
                        int nlx = lx + ndx[d], nly = ly + ndy[d];
                        if (nlx < 0 || nlx >= w || nly < 0 || nly >= h)
                        {
                            grid[idx4 + d] = bc;
                            continue;
                        }
                        int nIdx = nly * w + nlx;
                        if (heights[idx] != heights[nIdx])
                            grid[idx4 + d] = 255; // z-change = impassable, handled by stair logic
                        else
                            grid[idx4 + d] = bc;
                    }
                }
            }

            return grid;
        }

        // A* pathfinding on a flat cost grid. 4-directional, Manhattan heuristic.
        // Every tile has a cost (1=open, 255=obstacle). No impassable -- cost alone steers routing.
        // style: "direct" = staircase (alternate axes), "straight" = minimize turns (long runs).
        // Returns list of (localX, localY) from start to goal, or null if maxNodes exceeded.
        private static List<(int, int)> AStarPath(ushort[] grid, int w, int h, int sx, int sy, int gx, int gy, int maxNodes, string style)
        {
            if (sx < 0 || sx >= w || sy < 0 || sy >= h) return null;
            if (gx < 0 || gx >= w || gy < 0 || gy >= h) return null;

            bool straight = style == "straight";

            // Scale fScore by 4 to leave room for tie-breaking bias (0-3).
            // This preserves A* optimality -- ties between equal-cost paths are broken by style.
            var gScore = new Dictionary<int, int>();
            var fScore = new Dictionary<int, int>();
            var cameFrom = new Dictionary<int, int>();
            var open = new SortedSet<(int f, int idx)>();

            int startIdx = sy * w + sx;
            int goalIdx = gy * w + gx;
            gScore[startIdx] = 0;
            int h0 = (System.Math.Abs(gx - sx) + System.Math.Abs(gy - sy)) * 4;
            fScore[startIdx] = h0;
            open.Add((h0, startIdx));

            int[] ddx = { 1, -1, 0, 0 };
            int[] ddy = { 0, 0, 1, -1 };
            int[] opposite = { 1, 0, 3, 2 }; // entry direction when moving in direction d
            int nodesExpanded = 0;

            while (open.Count > 0)
            {
                var (cf, cidx) = open.Min;
                open.Remove(open.Min);

                if (cidx == goalIdx)
                {
                    var path = new List<(int, int)>();
                    int cur = goalIdx;
                    while (cur != startIdx)
                    {
                        path.Add((cur % w, cur / w));
                        cur = cameFrom[cur];
                    }
                    path.Add((sx, sy));
                    path.Reverse();
                    return path;
                }

                if (++nodesExpanded > maxNodes) return null;

                int cx2 = cidx % w;
                int cy2 = cidx / w;
                int cg = gScore.ContainsKey(cidx) ? gScore[cidx] : int.MaxValue;

                // direction we arrived from (for "straight" style turn penalty)
                int prevDx = 0, prevDy = 0;
                if (straight && cameFrom.ContainsKey(cidx))
                {
                    int prev = cameFrom[cidx];
                    prevDx = cx2 - (prev % w);
                    prevDy = cy2 - (prev / w);
                }

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx2 + ddx[d];
                    int ny = cy2 + ddy[d];
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int nidx = ny * w + nx;
                    int edgeCost = grid[nidx * 4 + opposite[d]];
                    if (edgeCost >= 255) continue; // impassable wall
                    int tentG = cg + edgeCost;
                    int prevG = gScore.ContainsKey(nidx) ? gScore[nidx] : int.MaxValue;
                    if (tentG < prevG)
                    {
                        cameFrom[nidx] = cidx;
                        gScore[nidx] = tentG;
                        int baseH = System.Math.Abs(gx - nx) + System.Math.Abs(gy - ny);
                        // tie-breaking bias (0-3): doesn't affect optimality, just path shape
                        int bias;
                        if (straight)
                        {
                            // penalize direction changes: 0 = same dir, 2 = turn
                            bool samedir = (ddx[d] == prevDx && ddy[d] == prevDy) || (prevDx == 0 && prevDy == 0);
                            bias = samedir ? 0 : 2;
                        }
                        else
                        {
                            // "direct": prefer reducing the larger remaining axis first
                            // bias=0 when stepping toward the axis with more distance, bias=2 otherwise
                            int remX = System.Math.Abs(gx - nx);
                            int remY = System.Math.Abs(gy - ny);
                            int remXbefore = System.Math.Abs(gx - cx2);
                            int remYbefore = System.Math.Abs(gy - cy2);
                            bool reducedX = remX < remXbefore;
                            bool reducedY = remY < remYbefore;
                            if (reducedX && remXbefore >= remYbefore) bias = 0;      // reduced the larger axis
                            else if (reducedY && remYbefore >= remXbefore) bias = 0;  // reduced the larger axis
                            else if (reducedX || reducedY) bias = 1;                  // reduced an axis, not the larger
                            else bias = 3;                                            // moved away from goal
                        }
                        int nf = baseH * 4 + bias;
                        if (fScore.ContainsKey(nidx))
                            open.Remove((fScore[nidx], nidx));
                        fScore[nidx] = nf;
                        open.Add((nf, nidx));
                    }
                }
            }

            return null; // no path
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
        // 4. FLOOD CHECK: checks WaterDepth > 0 on ground-required tiles only
        //    (MatterBelow.GroundOrStackable). Water intake tiles are expected wet.
        //
        // 5. PLACEMENT VALIDATION: uses the game's own PreviewFactory to create a
        //    preview entity, Reposition it, and check IsValid(). This runs the same
        //    9 validators the player UI uses (terrain, occupancy, water buildings, etc).
        //
        // Results sorted by: non-flooded > reachable > distance (closer) > pathAccess > nearPower.
        // Returns top 10 candidates.
        public object FindPlacement(string prefabName, int x1, int y1, int x2, int y2)
        {
            BuildingSpec buildingSpec;
            try { buildingSpec = _buildingService.GetBuildingTemplate(prefabName); }
            catch { return Jw.Error("not_found", ("prefab", prefabName)); }
            if (buildingSpec == null)
                return Jw.Error("not_found", ("prefab", prefabName));
            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            if (blockObjectSpec == null)
                return Jw.Error("invalid_type: no block object spec", ("prefab", prefabName));

            var size = blockObjectSpec.Size;
            var waterInputSpec = buildingSpec.GetSpec<WaterInputSpec>();
            Vector3Int? waterInputLocal = waterInputSpec != null
                ? (Vector3Int?)waterInputSpec.WaterInputCoordinates : null;

            // STEP 1: REACHABILITY
            // Use reflection to access the game's NavMesh internals. These APIs are
            // private because they're not meant for mods, but we need them to determine
            // if a building site is connected to the district center via paths.
            var reachableRoadCoords = new Dictionary<Vector3Int, float>();
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
                        var wcType = wc.GetType();
                        var coordsProp = wcType.GetProperty("Coordinates");
                        var distProp = wcType.GetProperty("Distance");
                        if (coordsProp != null)
                        {
                            var coords = (Vector3Int)coordsProp.GetValue(wc);
                            float dist = distProp != null ? (float)distProp.GetValue(wc) : -1f;
                            reachableRoadCoords[coords] = dist;
                        }
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
            var results = new List<(int x, int y, int z, int orient, bool pathAccess, bool reachable, float distance, bool nearPower, bool flooded, float waterDepth, int entranceX, int entranceY)>();

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
                        bool bestHasPath = false;

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

                            // check if doorstep tile (in front of entrance) has a path
                            bool hasPath = false;
                            if (cachedPreview.BlockObject.HasEntrance)
                            {
                                var ds = cachedPreview.BlockObject.PositionedEntrance.Coordinates;
                                hasPath = pathTiles.Contains((long)ds.x * 1000000 + (long)ds.y * 1000 + ds.z);
                            }

                            // prefer orientation with path access, then first valid
                            if (hasPath && !bestHasPath || (!bestHasPath && bestOrient < 0))
                            {
                                bestHasPath = hasPath;
                                bestOrient = orient;
                            }
                        }

                        if (bestOrient >= 0)
                        {
                            // reposition preview to best orientation to read game state
                            int brx2 = size.x, bry2 = size.y;
                            if (bestOrient == 1 || bestOrient == 3) { brx2 = size.y; bry2 = size.x; }
                            int bgx = tx, bgy = ty;
                            switch (bestOrient)
                            {
                                case 1: bgy = ty + bry2 - 1; break;
                                case 2: bgx = tx + brx2 - 1; bgy = ty + bry2 - 1; break;
                                case 3: bgx = tx + brx2 - 1; break;
                            }
                            cachedPreview.Reposition(new Placement(new Vector3Int(bgx, bgy, tz),
                                (Timberborn.Coordinates.Orientation)bestOrient, FlipMode.Unflipped));

                            // read doorstep coords
                            int entranceX = tx, entranceY = ty;
                            if (cachedPreview.BlockObject.HasEntrance)
                            {
                                var ds = cachedPreview.BlockObject.PositionedEntrance.Coordinates;
                                entranceX = ds.x;
                                entranceY = ds.y;
                            }

                            // check reachability: doorstep tile in reachable road network
                            bool reachable = false;
                            float distance = -1f;
                            if (bestHasPath && cachedPreview.BlockObject.HasEntrance)
                            {
                                var ds = cachedPreview.BlockObject.PositionedEntrance.Coordinates;
                                if (reachableRoadCoords.TryGetValue(ds, out float dist))
                                {
                                    reachable = true;
                                    distance = dist;
                                }
                            }

                            // check power adjacency on all 4 sides of footprint
                            bool nearPower = false;
                            for (int px = tx - 1; px <= tx + brx2 && !nearPower; px++)
                                for (int py = ty - 1; py <= ty + bry2 && !nearPower; py++)
                                {
                                    if (px >= tx && px < tx + brx2 && py >= ty && py < ty + bry2) continue;
                                    if (powerTiles.Contains((long)px * 1000000 + (long)py * 1000 + tz))
                                        nearPower = true;
                                }

                            // use the game's positioned blocks (already rotated) for flooding + water depth
                            bool flooded = false;
                            float waterDepth = 0f;
                            foreach (var block in cachedPreview.BlockObject.PositionedBlocks.GetAllBlocks())
                            {
                                var c = block.Coordinates;
                                if (c.z != tz) continue;
                                float depth = GetWaterDepth(c.x, c.y);
                                if (block.MatterBelow == MatterBelow.GroundOrStackable)
                                {
                                    if (depth > 0) flooded = true;
                                }
                                else if (block.MatterBelow != MatterBelow.Air && waterInputLocal.HasValue)
                                {
                                    if (depth > waterDepth) waterDepth = depth;
                                }
                            }

                            results.Add((tx, ty, tz, bestOrient, bestHasPath, reachable, distance, nearPower, flooded, waterDepth, entranceX, entranceY));
                        }
                    }
                }

                // sort: water buildings prioritize waterDepth first, others prioritize non-flooded
                results.Sort((a, b) =>
                {
                    if (waterInputLocal.HasValue)
                    {
                        if (a.waterDepth != b.waterDepth) return b.waterDepth.CompareTo(a.waterDepth);
                    }
                    if (a.flooded != b.flooded) return a.flooded ? 1 : -1;
                    if (a.reachable != b.reachable) return b.reachable ? 1 : -1;
                    if (a.distance != b.distance)
                    {
                        // both unreachable (-1) are equal; otherwise closer to DC wins
                        if (a.distance < 0) return 1;
                        if (b.distance < 0) return -1;
                        return a.distance.CompareTo(b.distance);
                    }
                    if (a.pathAccess != b.pathAccess) return b.pathAccess ? 1 : -1;
                    if (a.nearPower != b.nearPower) return b.nearPower ? 1 : -1;
                    return 0;
                });

            } // end try
            finally
            {
                if (cachedPreview != null)
                    UnityEngine.Object.Destroy(cachedPreview.GameObject);
            }

            int count = results.Count > 10 ? 10 : results.Count;

            var jw = _cache.Jw.BeginObj()
                .Prop("prefab", prefabName)
                .Prop("sizeX", size.x).Prop("sizeY", size.y)
                .Arr("placements");
            for (int i = 0; i < count; i++)
            {
                var r = results[i];
                jw.OpenObj()
                    .Prop("x", r.x).Prop("y", r.y).Prop("z", r.z)
                    .Prop("orientation", orientNames[r.orient])
                    .Prop("entranceX", r.entranceX).Prop("entranceY", r.entranceY)
                    .Prop("pathAccess", r.pathAccess ? 1 : 0)
                    .Prop("reachable", r.reachable ? 1 : 0)
                    .Prop("distance", r.distance, "F1")
                    .Prop("nearPower", r.nearPower ? 1 : 0)
                    .Prop("flooded", r.flooded ? 1 : 0);
                if (waterInputLocal.HasValue)
                    jw.Prop("waterDepth", r.waterDepth, "F2");
                jw.CloseObj();
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

        public PlaceBuildingResult PlaceBuilding(string prefabName, int x, int y, int z, string orientationStr)
        {
            int orientation = ParseOrientation(orientationStr);
            if (orientation < 0)
                return PlaceBuildingResult.Fail("invalid_param: invalid orientation, use south, west, north, east");

            BuildingSpec buildingSpec;
            try { buildingSpec = _buildingService.GetBuildingTemplate(prefabName); }
            catch { return new PlaceBuildingResult { Error = "not_found", X = x, Y = y, Z = z, Prefab = prefabName }; }
            if (buildingSpec == null)
                return new PlaceBuildingResult { Error = "not_found", X = x, Y = y, Z = z, Prefab = prefabName };

            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            if (blockObjectSpec == null)
                return PlaceBuildingResult.Fail("invalid_type: no block object spec", x, y, z);

            // check building is unlocked
            var bs = buildingSpec.GetSpec<BuildingSpec>();
            if (bs != null && bs.ScienceCost > 0 && !_buildingUnlockingService.Unlocked(bs))
                return new PlaceBuildingResult { Error = "not_unlocked", X = x, Y = y, Z = z, Prefab = prefabName, ScienceCost = bs.ScienceCost, CurrentPoints = _scienceService.SciencePoints };

            // Origin correction: user always specifies bottom-left corner (smallest x,y).
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

            var validationReason = ValidatePlacement(buildingSpec, blockObjectSpec, x, y, z, orientation);
            if (validationReason != null)
                return new PlaceBuildingResult { Error = validationReason, X = x, Y = y, Z = z, Prefab = prefabName };

            var orient = (Timberborn.Coordinates.Orientation)orientation;
            var placement = new Placement(new Vector3Int(gx, gy, z), orient, FlipMode.Unflipped);

            var placer = _blockObjectPlacerService.GetMatchingPlacer(blockObjectSpec);
            int placedId = 0;
            string placedName = "";
            placer.Place(blockObjectSpec, placement, (entity) =>
            {
                placedId = entity.GameObject.GetInstanceID();
                placedName = TimberbotEntityCache.CleanName(entity.GameObject.name);
            });

            if (placedId == 0)
                return new PlaceBuildingResult { Error = "operation_failed", X = x, Y = y, Z = z, Prefab = prefabName };

            return new PlaceBuildingResult { Id = placedId, Name = placedName, X = x, Y = y, Z = z, Orientation = OrientNames[orientation] };
        }

        // Returns null if valid, or a reason string if invalid.
        // Iterates game's 9 validators individually to get the specific failure reason.
        private string ValidatePlacement(BuildingSpec buildingSpec, BlockObjectSpec blockObjectSpec, int x, int y, int z, int orientation)
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
            if (placeableSpec == null) return "no placeable spec";
            Preview preview = null;
            try
            {
                var placement = new Placement(new Vector3Int(gx, gy, z),
                    (Timberborn.Coordinates.Orientation)orientation, FlipMode.Unflipped);
                preview = _previewFactory.Create(placeableSpec);
                preview.Reposition(placement);
                if (preview.BlockObject.IsValid()) return null; // valid

                // invalid -- check block-level conflicts first (occupancy, terrain)
                var bv = preview.BlockObject._blockValidator;
                if (bv != null)
                {
                    foreach (var block in preview.BlockObject.PositionedBlocks.GetAllBlocks())
                    {
                        if (bv.BlockConflictsWithExistingObject(block))
                        {
                            var bc = block.Coordinates;
                            string blocker = null;
                            foreach (var cb in _cache.Buildings.Read)
                            {
                                if (cb.OccupiedTiles == null) continue;
                                foreach (var t in cb.OccupiedTiles)
                                    if (t.x == bc.x && t.y == bc.y) { blocker = cb.Name; break; }
                                if (blocker != null) break;
                            }
                            if (blocker == null)
                                foreach (var nr in _cache.NaturalResources.Read)
                                    if (nr.X == bc.x && nr.Y == bc.y) { blocker = nr.Name; break; }
                            return $"occupied by {blocker ?? "unknown"} at ({bc.x},{bc.y},{bc.z})";
                        }
                        if (bv.BlockConflictsWithTerrain(block))
                            return $"terrain conflict at ({block.Coordinates.x},{block.Coordinates.y},{block.Coordinates.z})";
                        if (bv.BlockConflictsWithBlocksBelow(block))
                            return $"blocked below at ({block.Coordinates.x},{block.Coordinates.y},{block.Coordinates.z})";
                        if (bv.BlockConflictsWithBlockAbove(block))
                            return $"blocked above at ({block.Coordinates.x},{block.Coordinates.y},{block.Coordinates.z})";
                    }
                }

                // check service-level validators (district, water buildings, etc)
                var validationSvc = preview.BlockObject._blockObjectValidationService;
                if (validationSvc != null)
                {
                    var validators = validationSvc._blockObjectValidators;
                    for (int i = 0; i < validators.Length; i++)
                    {
                        string reason = null;
                        if (!validators[i].IsValid(preview.BlockObject, out reason))
                            return reason ?? validators[i].GetType().Name;
                    }
                }
                return "placement invalid";
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error($"ValidatePlacement at ({x},{y},{z})", ex);
                return ex.Message;
            }
            finally
            {
                if (preview != null)
                    UnityEngine.Object.Destroy(preview.GameObject);
            }
        }
    }
}
