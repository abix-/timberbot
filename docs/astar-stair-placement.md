# A* Path Building with Stair Placement -- Current State

## What we're building

`place_path` routes a path from (x1,y1) to (x2,y2) across a Timberborn map. The path crosses multiple elevation levels. At each z-change, stairs must be placed. The goal is the SHORTEST path with LOWEST cost -- one A* pass, no wasted tiles.

If we want true A* optimality, every traversable edge must have a positive minimum cost that matches the heuristic's lower bound. Existing path reuse therefore needs to cost `1`, not `0`.

## How stairs work in Timberborn

- Stairs are 1x1 buildings placed on the LOWER tile of a z-change edge
- They have an entrance side (lower) and exit side (higher)
- Orientation determines which direction is "uphill": south(0), west(1), north(2), east(3)
- The entrance tile (one tile before the stair, same z as stair) must have an adjacent path
- The exit tile (one tile after the stair, higher z) must have an adjacent path
- Beavers can ONLY enter from the entrance side -- wrong orientation = impassable
- Chaining stairs is fine: exit of one stair can be the entrance of the next

## The diagonal problem

A* on a 4-directional grid produces staircase patterns on diagonal routes (alternating +X and +Y steps). When the path hits a z-change edge, we need to pick a cardinal direction for the stair. The old approach used the single A* step before the z-change -- but on a diagonal staircase, that step is arbitrarily +X or +Y. After several sections, the stair could face sideways, making the entrance inaccessible.

### Failed fix 1: Lookback
Look back 5 waypoints to find "dominant" direction. Problem: the dominant direction didn't match the actual A* path at the z-change point, so the stair entrance was offset from the path, requiring extra detour paths to reach it. Made paths LONGER, not shorter.

### Failed fix 2: Destination-based direction
Orient stairs toward the destination (dominant cardinal axis from z-change to goal). Problem: the stair tile ended up on the wrong terrain. E.g., z-change is between x=168 (z=8) and x=169 (z=9). Destination says go +Y. Stair tile computed at (169,166) which has z=9, but baseZ=8. Terrain conflict.

### Current approach: Pre-computed stair edges in A* graph

Based on standard game dev approach (Unity forums, Red Blob Games): model stairs as directed portal edges IN the A* graph, not computed post-hoc.

**How it works:**
1. `BuildCostGrid` scans every z-change edge in the terrain
2. For each single-level z-change, checks if a stair can physically be placed (lower tile + entrance tile unobstructed, same z)
3. If valid, makes that edge traversable with cost=20 (instead of 255/impassable) and stores the stair info (tile, orientation, entrance, exit) in a lookup dictionary keyed by (fromIdx, toIdx)
4. A* finds a route through these stair edges naturally -- no ambiguity about orientation since it's determined by which edge was crossed
5. Walking the A* result: flat tiles get paths, z-change edges place stairs from the lookup

**What works:**
- 1600-1900 stair edges are found in the grid (validation is working)
- A* finds routes through z-change edges (the cost=20 edges are traversable)
- First stair crossing places correctly (section 1 works)
- Stair orientation is inherently correct (determined by the edge direction)
- The abstraction is correct for 3D Timberborn path building: stairs are directed graph connections, not something to guess after a 2D route is chosen

**What's broken:**
- Existing path cost `0` breaks true A* if the heuristic assumes a positive minimum per-step cost
- `stoppedAt` reports the entrance tile, not the exit tile. The next section starts from the entrance (z=2 side) instead of the exit (z=3 side), so it can't find a route forward
- The heuristic must be updated to match the new minimum edge cost once paths become `1`
- Multi-level stairs (levels > 1 requiring platforms) are marked impassable -- not handled yet
- The `sections` param counts stair crossings and stops, but the stopped position needs to be the EXIT tile of the last stair

## Pathfinding foundation

Pathfinding minimizes total movement cost, not just number of tiles.

- A node = one tile we can stand on
- An edge = one allowed move from one tile to another
- Edge cost = the price of taking that move

The route cost is the sum of all edge costs along the path.

In this system:

- Existing path should cost `1`
- Open ground should cost `2`
- Shallow water should cost `8`
- Deep water should cost `50`
- Single-level stair edge should cost `20`
- Impassable should be `255`

That preserves the gameplay preference:

- prefer reusing existing paths
- still allow building fresh path
- strongly discourage stairs and water unless they are worth it

## Dijkstra vs real A*

### Dijkstra

Dijkstra uses only the real cost already spent from the start.

- Priority = `g`
- `g` = cheapest known cost from start to this node

Dijkstra is always correct for non-negative edge costs, including `0`-cost roads. It is usually slower because it does not try to aim at the goal; it expands the cheapest frontier in every direction.

### Real A*

A* uses:

- `g` = real cost spent so far
- `h` = estimated remaining cost to the goal
- `f = g + h`

Real A* is guaranteed optimal only when `h` never overestimates the true cheapest remaining cost.

That means:

- `h(node) <= actual cheapest remaining cost from node to goal`

### Why `path = 0` breaks A*

The current search uses Manhattan distance as a heuristic. A Manhattan heuristic only works as a true lower bound if each remaining step costs at least some positive minimum amount.

If existing paths cost `0`, that condition fails.

Example:

- Goal is 5 tiles away
- Those 5 tiles are already existing path
- True remaining cost can be `0`
- Any positive Manhattan-based heuristic says `> 0`

That is an overestimate. Once `h` overestimates, the search is no longer guaranteed to return the cheapest path. At that point it is not "real A*" in the optimality sense.

### Why `path = 1` fixes it

If the minimum traversable edge cost is `1`, then plain Manhattan distance is a valid lower bound:

- every remaining move to the goal costs at least `1`
- therefore `h = abs(gx - x) + abs(gy - y)` can never overestimate

That restores true A* optimality while still making existing paths cheaper than fresh ground.

### Design decision

We want real A*, not Dijkstra.

Therefore:

- existing path reuse should cost `1`, not `0`
- the heuristic should be plain Manhattan distance: `h = abs(gx - x) + abs(gy - y)`
- the score should be exactly `f = g + h`
- style bias must NOT be added into `f`
- if we keep any path-shaping preference, it may only be used as a secondary ordering key when two nodes have equal `f`

With that change, the stair-edge graph design remains sound.

### Implementation details for real safe A*

In `AStarPath()` the safe implementation should look like this conceptually:

- start heuristic: `h0 = abs(gx - sx) + abs(gy - sy)`
- per-neighbor heuristic: `h = abs(gx - nx) + abs(gy - ny)`
- final score: `nf = tentG + h`

That means:

- remove the current `* 2` heuristic scaling
- do not replace it with `* 1`; just use plain Manhattan
- remove `bias` from the numeric `f` score
- keep `edgeCost >= 255` as the impassable sentinel check

Safe pseudocode:

```csharp
int h0 = Math.Abs(gx - sx) + Math.Abs(gy - sy);
fScore[startIdx] = h0;
open.Add((h0, startIdx));
...
int h = Math.Abs(gx - nx) + Math.Abs(gy - ny);
int nf = tentG + h;
```

If path shape still matters (`direct` vs `straight`), do not encode that preference by changing `nf`. Instead, keep the optimality-preserving score untouched and use shape preference only as a secondary tie-break among equal-`f` candidates.

## Key files

- `timberbot/src/TimberbotPlacement.cs`
  - `RoutePath()` -- main entry point, walks A* result and places paths/stairs
  - `BuildCostGrid()` -- builds edge-based cost grid with pre-computed stair edges
  - `AStarPath()` -- 4-directional A* with edge-based costs
  - `StairEdge` struct -- pre-computed stair info (tile, z, orientation, entrance, exit)
- `timberbot/src/TimberbotHttpServer.cs` -- routes POST /api/path/place to RoutePath
- `timberbot/script/timberbot.py`
  - `place_path()` -- Python client for the API
  - `map()` -- ASCII map renderer (fixed: now shows topmost occupant by z)
  - `_launch()` -- game launcher (fixed: now kills existing Timberborn before launching)

## Edge-based cost grid layout

`ushort[w * h * 4]` -- 4 entry costs per tile.

Direction indices: 0=from west(+X), 1=from east(-X), 2=from south(+Y), 3=from north(-Y).

The neighbor offset arrays `ndx/ndy` point to where the neighbor IS (not the travel direction):
- d=0: ndx=-1, ndy=0 (neighbor to the west)
- d=1: ndx=+1, ndy=0 (neighbor to the east)
- d=2: ndx=0, ndy=-1 (neighbor to the south)
- d=3: ndx=0, ndy=+1 (neighbor to the north)

`grid[idx*4 + d]` = cost of entering tile idx from neighbor at ndx[d],ndy[d].

A* reads `grid[nidx * 4 + opposite[d]]` when stepping in direction d to tile nidx. `opposite = {1,0,3,2}`.

Recommended entry costs:

- existing path/platform/stairs entry: `1`
- open ground entry: `2`
- shallow water entry: `8`
- deep water entry: `50`
- valid single-level stair edge: `20`
- blocked edge: `255`

## Stair edge computation

For each tile (lx,ly) and each neighbor direction d:
- If `heights[idx] != heights[nIdx]` (z-change):
  - Travel direction: from neighbor to this tile = `(-ndx[d], -ndy[d])`
  - goingUp: `heights[idx] > heights[nIdx]` (this tile higher than neighbor)
  - Stair tile = lower of the two tiles
  - Entrance = one tile back from stair in travel direction (must be same z as stair, unobstructed)
  - Exit = the higher tile
  - Orientation: uphill direction mapped to orient index
  - Edge key: `(nIdx, idx)` -- A* steps FROM neighbor TO this tile

## What needs fixing

1. **Change existing path cost from `0` to `1`**: true A* needs a positive minimum edge cost so Manhattan can be an admissible heuristic.

2. **Use plain Manhattan for the heuristic**: once existing path cost is `1`, the minimum traversable edge cost is `1`, so `h = abs(gx - x) + abs(gy - y)` is admissible.

3. **Set the score to exactly `f = g + h`**: remove style bias from the numeric `f` score so the algorithm remains mathematically safe A*.

4. **If needed, keep style only as a secondary tie-breaker**: `direct` vs `straight` should affect ordering only when two nodes have equal `f`, not by changing the score itself.

5. **stoppedAt position**: When `sections > 0` and we stop after N stair crossings, `stoppedAt` must report the stair EXIT tile (higher z), not the entrance. The next invocation starts from stoppedAt.

6. **Multi-level stairs**: Currently marked impassable. Need to check if platforms are unlocked, then model multi-level ramps as a sequence of stair+platform placements with higher cost.

7. **Edge direction consistency**: The ndx/ndy (neighbor offset) vs ddx/ddy (travel direction) confusion caused multiple bugs. The grid uses ndx/ndy convention. The A* uses ddx/ddy. The `opposite` array bridges them. This mapping is fragile and needs careful documentation or unification.

## Implementation checklist

- In `AStarPath()`, change the start heuristic from `Manhattan * 2` to plain Manhattan
- In `AStarPath()`, change neighbor scoring from `tentG + baseH * 2 + bias` to `tentG + baseH`
- Remove `bias` from the numeric `f` score
- If needed, keep style preference only as a secondary ordering key among equal-`f` nodes
- Keep `if (edgeCost >= 255) continue;` exactly as the impassable-edge check
- Update comments to say the algorithm is only true A* when `f = g + h` and `h` is admissible

## Commits so far

- `92ee47b` a* path: orient stairs toward destination (reverted -- caused wrong z)
- `49afb70` map: show topmost occupant (highest z) for correct top-down view
- Uncommitted: pre-computed stair edges in A* graph, stair failure bailout, launch kills existing game, sections support

## Research sources

- [Unity multi-level pathfinding](https://forum.unity.com/threads/multi-level-height-pathfinding.373728/) -- model level connections as portal edges
- [Red Blob Games grid algorithms](https://www.redblobgames.com/pathfinding/grids/algorithms.html) -- edge-based costs, heuristic admissibility
- [Red Blob Games A* introduction](https://www.redblobgames.com/pathfinding/a-star/introduction.html) -- `g`, `h`, `f`, and lower-bound heuristics
- [Staircase mod](https://timberborn.thunderstore.io/package/KnatteAnka_And_Tobbert/Staircase/) -- alternative stair orientations
- [A* 3D grid pathfinding](https://answers.unity.com/questions/1096235/using-stairs-in-3d-grid-based-pathfinding-a.html) -- stairs as graph edges
