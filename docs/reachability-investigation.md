# Reachability Investigation

## Goal
Check if a building placement is reachable from the district center BEFORE placing, using the same method the game uses to draw the green-to-red path line.

## What the player sees
When you pick up a building and hover it over the map, a green-to-red gradient line shows the path distance from the district center. Green = close, red = far, no line = unreachable.

## Game flow (from DLL analysis + runtime testing)

### Components involved
- `DistrictPathNavRangeDrawer` -- component on the DC entity, draws the green line
- `NavigationRangeService` -- wraps road/terrain/spill range services
- `RoadNavigationRangeService` -- has `GetNodesInRange(Vector3)` which returns all reachable road nodes from a world position
- `NodeIdService` -- converts between grid `Vector3Int` and nav mesh `Int32` node IDs
- `InstantRoadNavMeshGraph` -- the live road nav mesh graph, has `IsOnNavMesh(nodeId)`, `AreConnected(nodeId, nodeId)` (adjacency only), `GetNeighbors(nodeId)`
- `AccessFlowField` -- flow field with `HasNode(nodeId)` for reachability checks
- `RoadFlowFieldCache` -- caches flow fields per node, but entries are empty at rest (filled on-demand)

### Path from DC entity to the range service
```
DC entity
  -> GetComponent<DistrictPathNavRangeDrawer>()
    -> _navigationRangeService (NavigationRangeService)
      -> _roadNavigationRangeService (RoadNavigationRangeService)
        -> GetNodesInRange(Vector3 worldPos) -> IEnumerable<WeightedCoordinates>
```

### Coordinate systems
- Grid coords: `Vector3Int(x, y, z)` -- what our API uses (e.g. 120, 142, 2)
- Nav mesh node IDs: `Int32` -- internal, converted via `NodeIdService.GridToId(Vector3Int)`
- World coords: `Vector3(x, y, z)` -- Unity world space floats, converted via `NodeIdService.IdToWorld(nodeId)` or grid-to-world

### What works
- `NodeIdService.GridToId(Vector3Int)` -- converts grid to node ID correctly
- `InstantRoadNavMeshGraph.IsOnNavMesh(nodeId)` -- returns true for path tiles
- `InstantRoadNavMeshGraph.AreConnected(nodeA, nodeB)` -- works for ADJACENT nodes only (single-hop)
- `InstantRoadNavMeshGraph.GetNeighbors(nodeId)` -- returns neighbor list for a node

### What doesn't work
- `NavMeshService.AreConnectedRoadInstant(Vector3Int, Vector3Int)` -- returns false for all pairs, even adjacent paths. Reason unknown. May need world coords internally.
- `DistrictPathNavRangeDrawer._roadNodes` -- empty HashSet at runtime (only filled during active rendering)
- `RoadFlowFieldCache.GetFlowFieldAtNode(nodeId)` -- returns AccessFlowField with 0 nodes (cache entries exist but flow fields are empty at rest)
- `AccessFlowField.HasNode(nodeId)` -- always false because flow fields are empty

### What hasn't been tried
- **`GetNodesInRange(Vector3)`** -- the actual method the green line uses. Returns all reachable road nodes from a world position. NOT TESTED because debug `call` handler only supports `Vector3Int`, not `Vector3` (float). Need to add Vector3 support to debug endpoint.
- **`FilledReusableFlowField(graph, districtMap, Vector3)`** -- explicitly fills a flow field from a world position. Same Vector3 issue.

## Next step
Add `Vector3` (float) support to the debug endpoint's `call` handler, then test:
```
1. Convert DC grid coords to world: NodeIdService.GridToId(125,143,2) -> nodeId -> IdToWorld(nodeId) -> Vector3
2. Call GetNodesInRange(worldVector3) on _roadNavigationRangeService
3. Check if the result contains path tiles adjacent to proposed building
```

If `GetNodesInRange` works, integrate it into `FindPlacement` -- call it once with the DC's world position, collect all reachable node IDs into a HashSet, then check each candidate's adjacent path tile against that set.

## Debug endpoint
Generic debug endpoint at `POST /api/debug` with targets:
- `help` -- list targets and examples
- `get` -- navigate object chains with dot paths, `[N]` for list indexing, `~TypeName` for GetComponent, `$` for last result
- `fields` -- list fields/properties/methods on any object
- `call` -- invoke methods with typed args (int, float, bool, string, Vector3Int). Results stored in `$` for chaining.

Needs: `Vector3` arg support (float x,y,z) to test GetNodesInRange.
