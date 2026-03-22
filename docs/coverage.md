# API Coverage

What Timberbot can and can't do, audited against every Timberborn 1.0 game system.

## Full coverage

| System | Read | Write | How |
|---|---|---|---|
| Weather (drought/badtide) | YES | Game controlled | `summary`, `weather` |
| Water levels | YES | NO | `map` water height per tile |
| Floodgates | YES | YES | `set_floodgate` |
| Water pumps | YES | YES | `buildings` + `set_workers` |
| Beaver population | YES | NO | `summary`, `population` |
| Beaver wellbeing + needs | YES | NO | `beavers` (active needs only) |
| Beaver contamination | YES | NO | `beavers` (contaminated field) |
| Beaver workplace | YES | NO | `beavers` (workplace field) |
| Beaver age | YES | NO | `beavers` (lifeProgress) |
| Bot distinction | YES | NO | `beavers` (isBot field) |
| Place/demolish buildings | YES | YES | `place_building`, `demolish_building` |
| Construction progress | YES | NO | `buildProgress`, `materialProgress`, `hasMaterials` |
| Building inventory | YES | NO | `inventory` field |
| Building reachability | YES | NO | `reachable` field |
| Building statuses/alerts | YES | NO | `statuses` field, `alerts()` helper |
| Pause/unpause | YES | YES | `pause_building`, `unpause_building` |
| Workers | YES | YES | `set_workers` |
| Priority | YES | YES | `set_priority` |
| Paths/stairs/platforms | YES | YES | `place_building` |
| Ziplines/tubeways | YES | NO | Visible as buildings |
| Power network | YES | NO | `powered`, `isGenerator`, `isConsumer`, `powerDemand`, `powerSupply` |
| Science points | YES | NO | `science` |
| Unlock buildings | YES | YES | `unlock_building` |
| Crops | YES | YES | `plant_crop`, `clear_planting` |
| Trees | YES | YES | `trees`, `mark_trees`, `clear_trees`, `tree_clusters` |
| Stockpiles | YES | YES | `set_capacity`, `set_good` |
| Districts | YES | NO | `districts` |
| Distribution | YES | YES | `distribution`, `set_distribution` |
| Work schedule | YES | YES | `workhours`, `set_workhours` |
| Wonders | YES | NO | `isWonder`, `wonderActive` |
| Notifications | YES | NO | `notifications` |
| Landscaping (levees/dams) | YES | YES | `place_building` with Levee/Dam/TerrainBlock |
| Decorations | YES | YES | `place_building` |
| Map/terrain | YES | NO | `map`, `scan`, `visual` |
| Game speed | YES | YES | `speed`, `set_speed` (0-3) |
| Automation levers | NO | NO | Use Timberborn's built-in HTTP API (port 8080) directly |
| Automation adapters | NO | NO | Use Timberborn's built-in HTTP API (port 8080) directly |
| Pagination | YES | N/A | `limit`/`offset` on buildings, trees, gatherables, beavers |

## Known gaps

| Gap | Severity | Notes |
|---|---|---|
| Bot condition/fuel/energy | Medium | Bots have Condition instead of wellbeing. Can't see fuel/charge status |
| District migration | Medium | Can't move beavers between districts via API |
| Forestry tree planting | Untested | `plant_crop crop:Pine` should work but not confirmed |
| Badwater contamination per tile | Minor | Map shows water height but not contamination level |
| Per-building power input/output | Minor | Only graph-level demand/supply, not per-building |
| Clutch status | Closed | `isClutch`, `clutchEngaged` on buildings (read only, toggle via lever) |
| Dwelling occupant count | Minor | `hasHome` on beavers but no occupant count on buildings |
| Wellbeing tiers | Minor | No tier labels (just numeric score) |

## By design (not gaps)

| System | Why not in Timberbot |
|---|---|
| Automation (levers, adapters, sensors) | Use Timberborn's built-in HTTP API (port 8080) directly |
| Logic gates | In-game only, no external control needed |
| Reproduction | Happens naturally when needs are met |
| Water physics/flow | Game engine handles this, not controllable |
