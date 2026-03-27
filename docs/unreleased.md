# Unreleased (v0.7.0)

## Breaking changes

- map: x/y/radius -> x1/y1/x2/y2
- booleans: true/false -> 0/1 everywhere
- uniform schema: all list endpoints always emit all fields (enables toon CSV)
- tiles occupants: z-range format (DistrictCenter:z2-6), moved to last column

## Features

- A* pathfinding for `place_path`: edge-based cost grid, pre-computed stair edges in graph, auto-stairs across z-levels, obstacle avoidance, water avoidance, existing path reuse (cost=1), style param (direct/straight), sections param for incremental routing
- A* stair orientation: orient stairs toward destination, not by arbitrary step direction
- A* overhang avoidance: skip tiles with multiple terrain columns
- map: show topmost occupant (highest z) for correct top-down view
- auto-load: `timberbot.py launch settlement:<name>` auto-loads save via `autoload.json` + `steam://` protocol
- auto-load: skip mod manager screen with `-skipModManager`
- debug endpoint: generic reflection inspector (`get`, `fields`, `call` with $ chaining, `validate`, `validate_all`)
- debug endpoint: assertion targets (`eq`, `ne`, `null`, `not_null`, `contains`, `gt`, `gte`, `lt`, `lte`, `assert`, `compare`, `describe`, `roots`)
- benchmark endpoint: `/api/benchmark` with GC0 tracking, micro-benchmarks, endpoint profiling, toon variants
- benchmark: MathRound.vs.Manual, StringInterpolation, StringConcat.Needs, GetBeaverNeeds tests
- brain: live summary + persistent goal/tasks/maps, per-settlement memory folders
- summary: all brain fields server-side -- settlement, faction, DC per district, building role counts, treeClusters, foodClusters, per-district housing/employment/wellbeing, tree/crop species breakdowns, wellbeing categories, speed field
- food_clusters endpoint: grid-clustered gatherable food near DC
- settlement endpoint: lightweight save name for per-settlement memory
- clear_brain: wipe settlement memory and start fresh
- map name param: saves ANSI map to memory and indexes in brain
- map delta ANSI: 35KB -> 6KB output
- find_placement distance: path cost from DC via flow field
- --host= and --port= CLI flags for remote connections, httpHost in settings.json
- science/distribution endpoints: pre-built on main thread, background thread reads cached JSON

## Performance

- thread-unsafe reads fixed: CollectTreeClusters/CollectFoodClusters now use cached primitives instead of live Unity component reads
- CollectSummary json: eliminated Newtonsoft DeserializeObject, uses WriteClustersFiltered inline
- CollectSummary: ~20 temp collections hoisted to field-level, cleared per call. Static roleMap/cropNames
- CollectBuildings full toon: reuses field-level _invSb/_recSb instead of new StringBuilder per building
- CollectTreeClusters/FoodClusters: reuses field-level _clusterCells/_clusterSpecies/_clusterSorted
- CollectTiles: reuses field-level _tileOccupants/_tileEntrances/_tileSeedlings/_tileDeadTiles/_tileSb
- CollectScience/CollectDistribution: moved to main-thread cache (RefreshMainThreadData), eliminates GetSpec/GetComponent on background thread
- district refresh: reuses existing CachedDistrict objects, updates in place, zero alloc steady state
- RefreshCachedState: confirmed 0 GC0 across all hot paths (10K iteration benchmarks)
- Math.Round boxing claim disproved: 0 GC0 across 11.4M calls on this Mono version
- debug endpoint enabled by default in settings.json

## Fixes

- localhost DNS -> 127.0.0.1 (2300ms -> 310ms latency)
- session reuse: 200x brain speedup
- toon summary aggregates population/resources across districts
- A* path cost=0 for existing paths broke admissibility; changed to cost=1

## Tests

- 3 new A* path tests: diagonal, obstacle, no-route
- 63 total tests (up from 51)

## Docs

- architecture.md: thread model table, reusable collections, main-thread cached endpoints, webhook internals reference
- performance.md: full audit (3 high, 8 medium, 11 low), all high+medium fixed, restructured into authoritative sections
- developing.md: owns file structure, testing, build instructions
- webhooks.md: authoritative for events/setup, references architecture.md for internals
- thread-safe-surfaces.md: new doc -- Timberborn thread-safety guidance for off-thread reads
- astar-stair-placement.md: A* design doc with cost model, connector rules, implementation status
- docs split: each doc authoritative for its domain, no cross-doc duplication
- unreleased.md: renamed from release-notes.md

## Internal

- uniform schema + 0/1 booleans documented in architecture.md
- TimberbotAutoLoad.cs + TimberbotAutoLoadConfigurator.cs: new files for auto-load
- TimberbotDebug.cs: expanded from simple benchmark to full reflection inspector + validation + assertions
