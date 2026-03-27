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

        // Route a path from (x1,y1) to (x2,y2) using safe A* over a coherent 3D surface graph.
        // Nodes represent walkable surfaces at (x,y,z). Edges represent either flat movement or
        // directed vertical connectors. Existing stairs/platforms are reusable graph edges/surfaces,
        // and generated connectors use the same edge abstraction with RequiresPlacement=true.
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

            if (style != "direct" && style != "straight") style = "direct";

            List<SurfaceNode> nodes;
            List<GraphEdge>[] adj;
            Dictionary<long, List<int>> nodesByTile;
            int connectorCount;
            BuildSurfaceGraph(minX, minY, gw, gh, out nodes, out adj, out nodesByTile, out connectorCount);

            string PathKey(int x, int y, int z) => $"P|{x}|{y}|{z}";
            string PlatformKey(int x, int y, int z) => $"F|{x}|{y}|{z}";
            string StairKey(int x, int y, int z, int orient) => $"S|{x}|{y}|{z}|{orient}";
            bool IsBenignOccupied(PlaceBuildingResult r) => r.Error != null && r.Error.StartsWith("occupied by");

            var existingPathKeys = new HashSet<string>();
            var existingPlatformKeys = new HashSet<string>();
            var existingStairKeys = new HashSet<string>();
            foreach (var cb in _cache.Buildings.Read)
            {
                if (cb.Name == null || cb.OccupiedTiles == null) continue;
                bool isPath = cb.Name.Contains("Path");
                bool isStairs = cb.Name.Contains("Stairs");
                bool isPlatform = cb.Name.Contains("Platform");
                if (!isPath && !isStairs && !isPlatform) continue;
                foreach (var t in cb.OccupiedTiles)
                {
                    if (isPath) existingPathKeys.Add(PathKey(t.x, t.y, t.z));
                    if (isPlatform) existingPlatformKeys.Add(PlatformKey(t.x, t.y, t.z));
                }
                if (isStairs)
                    existingStairKeys.Add(StairKey(cb.X, cb.Y, cb.Z, ParseOrientation(cb.Orientation ?? "south")));
            }

            var placedPathKeys = new HashSet<string>();
            var placedPlatformKeys = new HashSet<string>();
            var placedStairKeys = new HashSet<string>();
            var errors = new List<string>();
            var failedResults = new List<PlaceBuildingResult>();
            int pathCount = 0, stairCount = 0, platformCount = 0, skipped = 0;
            int connectorCrossings = 0;
            int stoppedX = x2, stoppedY = y2;
            bool stopped = false;

            void TryPlacePathNode(SurfaceNode node)
            {
                if (!node.PlacePath || node.Z <= 0) return;
                string key = PathKey(node.X, node.Y, node.Z);
                if (existingPathKeys.Contains(key) || placedPathKeys.Contains(key)) return;
                var r = PlaceBuilding("Path", node.X, node.Y, node.Z, "south");
                if (r.Success)
                {
                    pathCount++;
                    placedPathKeys.Add(key);
                }
                else if (IsBenignOccupied(r))
                {
                    placedPathKeys.Add(key);
                }
                else
                {
                    skipped++;
                    failedResults.Add(r);
                }
            }

            List<int> startIds;
            List<int> goalIds;
            if (!nodesByTile.TryGetValue(TileKey(x1, y1), out startIds) || startIds.Count == 0)
                startIds = null;
            if (!nodesByTile.TryGetValue(TileKey(x2, y2), out goalIds) || goalIds.Count == 0)
                goalIds = null;

            if (startIds == null || goalIds == null)
            {
                if (startIds == null) errors.Add($"no walkable surface at start ({x1},{y1})");
                if (goalIds == null) errors.Add($"no walkable surface at goal ({x2},{y2})");
            }
            else
            {
                var path = AStarPath(nodes, adj, startIds, goalIds, nodes.Count * 8, style);
                if (path == null)
                {
                    errors.Add($"A* found no route from ({x1},{y1}) to ({x2},{y2}) -- {connectorCount} connectors in graph");
                }
                else
                {
                    if (path.Nodes.Count > 0)
                        TryPlacePathNode(nodes[path.Nodes[0]]);

                    for (int i = 0; i < path.Edges.Count; i++)
                    {
                        var edge = path.Edges[i];
                        var src = nodes[path.Nodes[i]];
                        var dst = nodes[path.Nodes[i + 1]];

                        if (edge.IsConnector)
                        {
                            if (edge.RequiresPlacement)
                            {
                                if (!stairsUnlocked)
                                {
                                    errors.Add($"stairs not unlocked at ({src.X},{src.Y})");
                                    stoppedX = src.X; stoppedY = src.Y; stopped = true;
                                    break;
                                }
                                if (edge.Levels > 1 && !platformUnlocked)
                                {
                                    errors.Add($"platforms not unlocked for {edge.Levels}-level connector at ({src.X},{src.Y})");
                                    stoppedX = src.X; stoppedY = src.Y; stopped = true;
                                    break;
                                }

                                bool connectorFailed = false;
                                foreach (var rt in edge.RampTiles)
                                {
                                    for (int p = 0; p < rt.platCount; p++)
                                    {
                                        int platformZ = rt.baseZ + p;
                                        string pk = PlatformKey(rt.x, rt.y, platformZ);
                                        if (existingPlatformKeys.Contains(pk) || placedPlatformKeys.Contains(pk)) continue;
                                        var pr = PlaceBuilding(platformPrefab, rt.x, rt.y, platformZ, "south");
                                        if (pr.Success)
                                        {
                                            platformCount++;
                                            placedPlatformKeys.Add(pk);
                                        }
                                        else if (IsBenignOccupied(pr))
                                        {
                                            placedPlatformKeys.Add(pk);
                                        }
                                        else
                                        {
                                            skipped++;
                                            failedResults.Add(pr);
                                            errors.Add($"platform failed at ({rt.x},{rt.y},{platformZ})");
                                            stoppedX = src.X; stoppedY = src.Y; stopped = true;
                                            connectorFailed = true;
                                            break;
                                        }
                                    }
                                    if (connectorFailed) break;

                                    int stairZ = rt.baseZ + rt.platCount;
                                    string sk = StairKey(rt.x, rt.y, stairZ, edge.OrientIdx);
                                    if (existingStairKeys.Contains(sk) || placedStairKeys.Contains(sk)) continue;
                                    var sr = PlaceBuilding(stairsPrefab, rt.x, rt.y, stairZ, OrientNames[edge.OrientIdx]);
                                    if (sr.Success)
                                    {
                                        stairCount++;
                                        placedStairKeys.Add(sk);
                                    }
                                    else if (IsBenignOccupied(sr))
                                    {
                                        placedStairKeys.Add(sk);
                                    }
                                    else
                                    {
                                        skipped++;
                                        failedResults.Add(sr);
                                        errors.Add($"stair failed at ({rt.x},{rt.y},{stairZ})");
                                        stoppedX = src.X; stoppedY = src.Y; stopped = true;
                                        connectorFailed = true;
                                        break;
                                    }
                                }
                                if (connectorFailed) break;
                            }

                            TryPlacePathNode(dst);
                            connectorCrossings++;
                            if (sections > 0 && connectorCrossings >= sections)
                            {
                                stoppedX = dst.X; stoppedY = dst.Y; stopped = true;
                                break;
                            }
                            continue;
                        }

                        TryPlacePathNode(dst);
                    }
                }
            }

            var jw = _cache.Jw.BeginObj();
            jw.Obj("placed").Prop("paths", pathCount);
            if (stairCount > 0) jw.Prop("stairs", stairCount);
            if (platformCount > 0) jw.Prop("platforms", platformCount);
            jw.CloseObj().Prop("skipped", skipped);
            jw.Prop("stairEdgesInGrid", connectorCount);
            jw.Prop("connectorEdgesInGrid", connectorCount);
            if (stopped) jw.Prop("stoppedAt", $"{stoppedX},{stoppedY}");

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

        private enum SurfaceKind : byte
        {
            Terrain,
            ExistingPath,
            ExistingPlatformTop
        }

        private struct SurfaceNode
        {
            public int X, Y, Z;
            public ushort EntryCost;
            public SurfaceKind Kind;
            public bool Existing;
            public bool PlacePath;
        }

        private struct GraphEdge
        {
            public int ToId;
            public ushort Cost;
            public bool IsConnector;
            public bool RequiresPlacement;
            public int BaseZ, Levels;
            public int EntX, EntY, EntZ, ExtX, ExtY, ExtZ;
            public int OrientIdx;
            public List<(int x, int y, int baseZ, int platCount)> RampTiles;
        }

        private sealed class SurfacePath
        {
            public List<int> Nodes;
            public List<GraphEdge> Edges;
        }

        private static long TileKey(int x, int y)
        {
            return ((long)x << 20) ^ (uint)y;
        }

        private static long SurfaceKey(int x, int y, int z)
        {
            return ((long)x << 40) ^ ((long)y << 20) ^ (uint)z;
        }

        private static bool PreferEdge(GraphEdge next, GraphEdge prev)
        {
            if (next.Cost != prev.Cost) return next.Cost < prev.Cost;
            if (next.RequiresPlacement != prev.RequiresPlacement) return !next.RequiresPlacement;
            if (next.IsConnector != prev.IsConnector) return !next.IsConnector;
            return false;
        }
        private bool TryBuildGeneratedConnector(int minX, int minY, int w, int h, ushort[] baseCost, int[] terrainHeights,
            int lowLx, int lowLy, int highLx, int highLy, out GraphEdge upEdge, out GraphEdge downEdge)
        {
            upEdge = default(GraphEdge);
            downEdge = default(GraphEdge);

            bool InBounds(int x, int y) => x >= 0 && x < w && y >= 0 && y < h;
            int LocalIdx(int x, int y) => y * w + x;
            bool ValidTile(int x, int y, int expectedZ)
            {
                if (!InBounds(x, y)) return false;
                int li = LocalIdx(x, y);
                return baseCost[li] < 255 && terrainHeights[li] == expectedZ;
            }

            int lowIdx = LocalIdx(lowLx, lowLy);
            int highIdx = LocalIdx(highLx, highLy);
            int lowZ = terrainHeights[lowIdx];
            int highZ = terrainHeights[highIdx];
            int levels = highZ - lowZ;
            if (levels <= 0) return false;

            int dx = highLx - lowLx;
            int dy = highLy - lowLy;
            int orientIdx = dx > 0 ? 3 : dx < 0 ? 1 : dy > 0 ? 2 : 0;
            int baseZ = lowZ;
            var rampTiles = new List<(int x, int y, int baseZ, int platCount)>();
            int entLx, entLy, extLx, extLy;

            if (levels == 1)
            {
                int stairLx = lowLx;
                int stairLy = lowLy;
                entLx = stairLx - dx;
                entLy = stairLy - dy;
                extLx = stairLx + dx;
                extLy = stairLy + dy;
                if (!ValidTile(stairLx, stairLy, baseZ) || !ValidTile(entLx, entLy, lowZ) || !ValidTile(extLx, extLy, highZ))
                    return false;
                rampTiles.Add((stairLx + minX, stairLy + minY, baseZ, 0));
            }
            else
            {
                int firstLx = highLx - dx * levels;
                int firstLy = highLy - dy * levels;
                entLx = firstLx - dx;
                entLy = firstLy - dy;
                int lastLx = firstLx + dx * (levels - 1);
                int lastLy = firstLy + dy * (levels - 1);
                extLx = lastLx + dx;
                extLy = lastLy + dy;

                if (!ValidTile(entLx, entLy, lowZ) || !ValidTile(extLx, extLy, highZ))
                    return false;

                for (int step = 0; step < levels; step++)
                {
                    int rampLx = firstLx + dx * step;
                    int rampLy = firstLy + dy * step;
                    if (!ValidTile(rampLx, rampLy, baseZ))
                        return false;
                    rampTiles.Add((rampLx + minX, rampLy + minY, baseZ, step));
                }
            }

            upEdge = new GraphEdge
            {
                Cost = (ushort)(20 * levels),
                IsConnector = true,
                RequiresPlacement = true,
                BaseZ = baseZ,
                Levels = levels,
                EntX = entLx + minX,
                EntY = entLy + minY,
                EntZ = lowZ,
                ExtX = extLx + minX,
                ExtY = extLy + minY,
                ExtZ = highZ,
                OrientIdx = orientIdx,
                RampTiles = rampTiles
            };
            downEdge = new GraphEdge
            {
                Cost = (ushort)(20 * levels),
                IsConnector = true,
                RequiresPlacement = true,
                BaseZ = baseZ,
                Levels = levels,
                EntX = extLx + minX,
                EntY = extLy + minY,
                EntZ = highZ,
                ExtX = entLx + minX,
                ExtY = entLy + minY,
                ExtZ = lowZ,
                OrientIdx = orientIdx,
                RampTiles = rampTiles
            };
            return true;
        }

        // Build a coherent 3D surface graph over the region.
        // Terrain/path/platform surfaces become nodes at (x,y,z).
        // Existing stairs and generated ramps become directed connector edges.
        private void BuildSurfaceGraph(int minX, int minY, int w, int h,
            out List<SurfaceNode> nodes, out List<GraphEdge>[] adj,
            out Dictionary<long, List<int>> nodesByTile, out int connectorCount)
        {
            var baseCost = new ushort[w * h];
            var terrainHeights = new int[w * h];

            for (int ly = 0; ly < h; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    int idx = ly * w + lx;
                    int tz = GetTerrainHeight(minX + lx, minY + ly);
                    terrainHeights[idx] = tz;
                    baseCost[idx] = tz > 0 ? (ushort)2 : (ushort)255;
                }
            }

            for (int ly = 0; ly < h; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    int idx = ly * w + lx;
                    if (baseCost[idx] >= 255) continue;
                    float depth = GetWaterDepth(minX + lx, minY + ly);
                    if (depth > 0.5f) baseCost[idx] = 50;
                    else if (depth > 0f) baseCost[idx] = 8;
                    if (HasOverhang(minX + lx, minY + ly))
                        baseCost[idx] = 255;
                }
            }

            var existingPathSurfaces = new HashSet<long>();
            var existingPlatformSurfaces = new HashSet<long>();
            var existingStairs = new List<(int sx, int sy, int sz, int dir)>();

            foreach (var cb in _cache.Buildings.Read)
            {
                if (cb.Name == null || cb.OccupiedTiles == null) continue;
                bool isPath = cb.Name.Contains("Path");
                bool isStairs = cb.Name.Contains("Stairs");
                bool isPlatform = cb.Name.Contains("Platform");
                bool isReusable = isPath || isStairs || isPlatform;

                if (isStairs)
                    existingStairs.Add((cb.X, cb.Y, cb.Z, ParseOrientation(cb.Orientation ?? "south")));

                foreach (var t in cb.OccupiedTiles)
                {
                    int lx = t.x - minX;
                    int ly = t.y - minY;
                    if (lx < 0 || lx >= w || ly < 0 || ly >= h) continue;
                    int idx = ly * w + lx;

                    if (isPath)
                    {
                        existingPathSurfaces.Add(SurfaceKey(t.x, t.y, t.z));
                        if (baseCost[idx] < 255) baseCost[idx] = 1;
                    }
                    else if (isPlatform)
                    {
                        existingPlatformSurfaces.Add(SurfaceKey(t.x, t.y, t.z + 1));
                    }
                    else if (isStairs)
                    {
                        baseCost[idx] = 255;
                    }
                    else if (!isReusable)
                    {
                        baseCost[idx] = 255;
                    }
                }
            }

            foreach (var nr in _cache.NaturalResources.Read)
            {
                int lx = nr.X - minX;
                int ly = nr.Y - minY;
                if (lx < 0 || lx >= w || ly < 0 || ly >= h) continue;
                baseCost[ly * w + lx] = 255;
            }

            var nodeList = new List<SurfaceNode>();
            var nodeIdBySurface = new Dictionary<long, int>();

            void UpsertNode(int x, int y, int z, ushort entryCost, SurfaceKind kind, bool existing, bool placePath)
            {
                long key = SurfaceKey(x, y, z);
                int id;
                if (nodeIdBySurface.TryGetValue(key, out id))
                {
                    var n = nodeList[id];
                    if (entryCost < n.EntryCost) n.EntryCost = entryCost;
                    if ((byte)kind > (byte)n.Kind) n.Kind = kind;
                    n.Existing = n.Existing || existing;
                    n.PlacePath = n.PlacePath && placePath;
                    nodeList[id] = n;
                    return;
                }

                nodeIdBySurface[key] = nodeList.Count;
                nodeList.Add(new SurfaceNode
                {
                    X = x,
                    Y = y,
                    Z = z,
                    EntryCost = entryCost,
                    Kind = kind,
                    Existing = existing,
                    PlacePath = placePath
                });
            }

            for (int ly = 0; ly < h; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    int idx = ly * w + lx;
                    if (baseCost[idx] >= 255) continue;
                    int wx = minX + lx;
                    int wy = minY + ly;
                    int wz = terrainHeights[idx];
                    bool hasExistingPath = existingPathSurfaces.Contains(SurfaceKey(wx, wy, wz));
                    UpsertNode(wx, wy, wz, hasExistingPath ? (ushort)1 : baseCost[idx], hasExistingPath ? SurfaceKind.ExistingPath : SurfaceKind.Terrain, hasExistingPath, !hasExistingPath);
                }
            }

            foreach (var s in existingPathSurfaces)
            {
                int x = (int)(s >> 40);
                int y = (int)((s >> 20) & 0xFFFFF);
                int z = (int)(s & 0xFFFFF);
                UpsertNode(x, y, z, 1, SurfaceKind.ExistingPath, true, false);
            }
            foreach (var s in existingPlatformSurfaces)
            {
                int x = (int)(s >> 40);
                int y = (int)((s >> 20) & 0xFFFFF);
                int z = (int)(s & 0xFFFFF);
                UpsertNode(x, y, z, 1, SurfaceKind.ExistingPlatformTop, true, false);
            }

            nodesByTile = new Dictionary<long, List<int>>();
            for (int i = 0; i < nodeList.Count; i++)
            {
                var n = nodeList[i];
                long tileKey = TileKey(n.X, n.Y);
                List<int> list;
                if (!nodesByTile.TryGetValue(tileKey, out list))
                {
                    list = new List<int>();
                    nodesByTile[tileKey] = list;
                }
                list.Add(i);
            }
            var edgeMap = new Dictionary<(int from, int to), GraphEdge>();

            void AddEdge(int fromId, GraphEdge edge)
            {
                var key = (fromId, edge.ToId);
                GraphEdge prev;
                if (!edgeMap.TryGetValue(key, out prev) || PreferEdge(edge, prev))
                    edgeMap[key] = edge;
            }

            int[] ndx = { 1, -1, 0, 0 };
            int[] ndy = { 0, 0, 1, -1 };
            for (int i = 0; i < nodeList.Count; i++)
            {
                var n = nodeList[i];
                for (int d = 0; d < 4; d++)
                {
                    long nk = SurfaceKey(n.X + ndx[d], n.Y + ndy[d], n.Z);
                    int toId;
                    if (!nodeIdBySurface.TryGetValue(nk, out toId)) continue;
                    AddEdge(i, new GraphEdge
                    {
                        ToId = toId,
                        Cost = nodeList[toId].EntryCost,
                        IsConnector = false,
                        RequiresPlacement = false
                    });
                }
            }

            foreach (var s in existingStairs)
            {
                int dx = s.dir == 3 ? 1 : s.dir == 1 ? -1 : 0;
                int dy = s.dir == 2 ? 1 : s.dir == 0 ? -1 : 0;
                int entX = s.sx - dx;
                int entY = s.sy - dy;
                int entZ = s.sz;
                int extX = s.sx + dx;
                int extY = s.sy + dy;
                int extZ = s.sz + 1;

                int fromId, toId;
                if (nodeIdBySurface.TryGetValue(SurfaceKey(entX, entY, entZ), out fromId) && nodeIdBySurface.TryGetValue(SurfaceKey(extX, extY, extZ), out toId))
                {
                    AddEdge(fromId, new GraphEdge
                    {
                        ToId = toId,
                        Cost = 20,
                        IsConnector = true,
                        RequiresPlacement = false,
                        BaseZ = s.sz,
                        Levels = 1,
                        EntX = entX,
                        EntY = entY,
                        EntZ = entZ,
                        ExtX = extX,
                        ExtY = extY,
                        ExtZ = extZ,
                        OrientIdx = s.dir,
                        RampTiles = null
                    });
                }
                if (nodeIdBySurface.TryGetValue(SurfaceKey(extX, extY, extZ), out fromId) && nodeIdBySurface.TryGetValue(SurfaceKey(entX, entY, entZ), out toId))
                {
                    AddEdge(fromId, new GraphEdge
                    {
                        ToId = toId,
                        Cost = 20,
                        IsConnector = true,
                        RequiresPlacement = false,
                        BaseZ = s.sz,
                        Levels = 1,
                        EntX = extX,
                        EntY = extY,
                        EntZ = extZ,
                        ExtX = entX,
                        ExtY = entY,
                        ExtZ = entZ,
                        OrientIdx = s.dir,
                        RampTiles = null
                    });
                }
            }

            for (int ly = 0; ly < h; ly++)
            {
                for (int lx = 0; lx < w; lx++)
                {
                    int idx = ly * w + lx;
                    if (baseCost[idx] >= 255) continue;

                    if (lx + 1 < w && baseCost[idx + 1] < 255)
                    {
                        int z0 = terrainHeights[idx];
                        int z1 = terrainHeights[idx + 1];
                        if (z0 != z1)
                        {
                            int lowLx = z0 < z1 ? lx : lx + 1;
                            int lowLy = ly;
                            int highLx = z0 < z1 ? lx + 1 : lx;
                            int highLy = ly;
                            GraphEdge upEdge, downEdge;
                            if (TryBuildGeneratedConnector(minX, minY, w, h, baseCost, terrainHeights, lowLx, lowLy, highLx, highLy, out upEdge, out downEdge))
                            {
                                int fromId, toId;
                                if (nodeIdBySurface.TryGetValue(SurfaceKey(upEdge.EntX, upEdge.EntY, upEdge.EntZ), out fromId) && nodeIdBySurface.TryGetValue(SurfaceKey(upEdge.ExtX, upEdge.ExtY, upEdge.ExtZ), out toId))
                                {
                                    upEdge.ToId = toId;
                                    AddEdge(fromId, upEdge);
                                }
                                if (nodeIdBySurface.TryGetValue(SurfaceKey(downEdge.EntX, downEdge.EntY, downEdge.EntZ), out fromId) && nodeIdBySurface.TryGetValue(SurfaceKey(downEdge.ExtX, downEdge.ExtY, downEdge.ExtZ), out toId))
                                {
                                    downEdge.ToId = toId;
                                    AddEdge(fromId, downEdge);
                                }
                            }
                        }
                    }

                    if (ly + 1 < h && baseCost[idx + w] < 255)
                    {
                        int z0 = terrainHeights[idx];
                        int z1 = terrainHeights[idx + w];
                        if (z0 != z1)
                        {
                            int lowLx = lx;
                            int lowLy = z0 < z1 ? ly : ly + 1;
                            int highLx = lx;
                            int highLy = z0 < z1 ? ly + 1 : ly;
                            GraphEdge upEdge, downEdge;
                            if (TryBuildGeneratedConnector(minX, minY, w, h, baseCost, terrainHeights, lowLx, lowLy, highLx, highLy, out upEdge, out downEdge))
                            {
                                int fromId, toId;
                                if (nodeIdBySurface.TryGetValue(SurfaceKey(upEdge.EntX, upEdge.EntY, upEdge.EntZ), out fromId) && nodeIdBySurface.TryGetValue(SurfaceKey(upEdge.ExtX, upEdge.ExtY, upEdge.ExtZ), out toId))
                                {
                                    upEdge.ToId = toId;
                                    AddEdge(fromId, upEdge);
                                }
                                if (nodeIdBySurface.TryGetValue(SurfaceKey(downEdge.EntX, downEdge.EntY, downEdge.EntZ), out fromId) && nodeIdBySurface.TryGetValue(SurfaceKey(downEdge.ExtX, downEdge.ExtY, downEdge.ExtZ), out toId))
                                {
                                    downEdge.ToId = toId;
                                    AddEdge(fromId, downEdge);
                                }
                            }
                        }
                    }
                }
            }

            nodes = nodeList;
            adj = new List<GraphEdge>[nodeList.Count];
            for (int i = 0; i < adj.Length; i++) adj[i] = new List<GraphEdge>();
            connectorCount = 0;
            foreach (var kv in edgeMap)
            {
                adj[kv.Key.from].Add(kv.Value);
                if (kv.Value.IsConnector) connectorCount++;
            }
        }

        // Safe A* over the coherent surface graph. Plain Manhattan on x/y remains admissible because
        // every traversable edge has minimum cost >= 1. style only acts as a secondary tie-break.
        private static SurfacePath AStarPath(List<SurfaceNode> nodes, List<GraphEdge>[] adj, List<int> startIds, List<int> goalIds, int maxNodes, string style)
        {
            if (startIds == null || startIds.Count == 0 || goalIds == null || goalIds.Count == 0) return null;

            bool straight = style == "straight";
            var goalSet = new HashSet<int>(goalIds);
            int Heuristic(int nodeId)
            {
                var n = nodes[nodeId];
                int best = int.MaxValue;
                for (int i = 0; i < goalIds.Count; i++)
                {
                    var g = nodes[goalIds[i]];
                    int h = System.Math.Abs(g.X - n.X) + System.Math.Abs(g.Y - n.Y);
                    if (h < best) best = h;
                }
                return best;
            }

            var gScore = new Dictionary<int, int>();
            var openScore = new Dictionary<int, (int f, int bias)>();
            var cameFrom = new Dictionary<int, int>();
            var cameEdge = new Dictionary<int, GraphEdge>();
            var open = new SortedSet<(int f, int bias, int idx)>();

            for (int i = 0; i < startIds.Count; i++)
            {
                int sid = startIds[i];
                gScore[sid] = 0;
                int h0 = Heuristic(sid);
                openScore[sid] = (h0, 0);
                open.Add((h0, 0, sid));
            }

            int nodesExpanded = 0;
            while (open.Count > 0)
            {
                var current = open.Min;
                open.Remove(current);
                int cidx = current.idx;

                if (goalSet.Contains(cidx))
                {
                    var nodePath = new List<int>();
                    var edgePath = new List<GraphEdge>();
                    int cur = cidx;
                    nodePath.Add(cur);
                    while (cameFrom.ContainsKey(cur))
                    {
                        edgePath.Add(cameEdge[cur]);
                        cur = cameFrom[cur];
                        nodePath.Add(cur);
                    }
                    nodePath.Reverse();
                    edgePath.Reverse();
                    return new SurfacePath { Nodes = nodePath, Edges = edgePath };
                }

                if (++nodesExpanded > maxNodes) return null;
                int cg = gScore[cidx];

                int prevDx = 0, prevDy = 0;
                if (straight && cameFrom.ContainsKey(cidx))
                {
                    var prevNode = nodes[cameFrom[cidx]];
                    var curNode = nodes[cidx];
                    prevDx = System.Math.Sign(curNode.X - prevNode.X);
                    prevDy = System.Math.Sign(curNode.Y - prevNode.Y);
                }

                foreach (var edge in adj[cidx])
                {
                    int nidx = edge.ToId;
                    int tentG = cg + edge.Cost;
                    int prevG;
                    if (gScore.TryGetValue(nidx, out prevG) && tentG >= prevG) continue;

                    cameFrom[nidx] = cidx;
                    cameEdge[nidx] = edge;
                    gScore[nidx] = tentG;

                    var curNode = nodes[cidx];
                    var nextNode = nodes[nidx];
                    int moveDx = System.Math.Sign(nextNode.X - curNode.X);
                    int moveDy = System.Math.Sign(nextNode.Y - curNode.Y);
                    int bias;
                    if (straight)
                    {
                        bool sameDir = (moveDx == prevDx && moveDy == prevDy) || (prevDx == 0 && prevDy == 0);
                        bias = sameDir ? 0 : 2;
                    }
                    else
                    {
                        var target = nodes[goalIds[0]];
                        int remX = System.Math.Abs(target.X - nextNode.X);
                        int remY = System.Math.Abs(target.Y - nextNode.Y);
                        int remXbefore = System.Math.Abs(target.X - curNode.X);
                        int remYbefore = System.Math.Abs(target.Y - curNode.Y);
                        bool reducedX = remX < remXbefore;
                        bool reducedY = remY < remYbefore;
                        if (reducedX && remXbefore >= remYbefore) bias = 0;
                        else if (reducedY && remYbefore >= remXbefore) bias = 0;
                        else if (reducedX || reducedY) bias = 1;
                        else bias = 3;
                    }

                    int nf = tentG + Heuristic(nidx);
                    (int f, int bias) prevOpen;
                    if (openScore.TryGetValue(nidx, out prevOpen))
                        open.Remove((prevOpen.f, prevOpen.bias, nidx));
                    openScore[nidx] = (nf, bias);
                    open.Add((nf, bias, nidx));
                }
            }

            return null;
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

