# Features

What Timberbot can and can't do, audited against every Timberborn 1.0 game system.

## Supported systems

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
| Paths/stairs/platforms | YES | YES | `place_building`, `place_path` (auto-stairs + platforms for multi-level z-changes) |
| Placement validation | YES | YES | `find_placement` (reachability, path access, power adjacency) |
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
| Soil contamination | YES | NO | `contaminated` field on map tiles |
| District migration | YES | YES | `migrate from_district:X to_district:Y count:N` |
| Clutch status | YES | NO | `isClutch`, `clutchEngaged` on buildings |
| Per-building power | YES | NO | `nominalPowerInput`, `nominalPowerOutput` on buildings |
| Automation levers | NO | NO | Use Timberborn's built-in HTTP API (port 8080) directly |
| Automation adapters | NO | NO | Use Timberborn's built-in HTTP API (port 8080) directly |
| Hauler priority | YES | YES | `set_haul_priority` on any building that receives goods |
| Manufactory recipes | YES | YES | `set_recipe` on lumber mills, gear workshops, bot factories |
| Farmhouse action | YES | YES | `set_farmhouse_action` planting vs default |
| Plantable priority | YES | YES | `set_plantable_priority` on foresters (tree type) |
| Water contamination | YES | NO | `badwater` field on water tiles (0-1) |
| Pagination | YES | N/A | `limit`/`offset` on buildings, trees, gatherables, beavers |

## Known gaps

| Gap | Severity | Notes |
|---|---|---|
| Bot condition/fuel | In-Progress | Bots use same NeedManager, needs should show via `beavers` endpoint. Needs verification with bots |

## By design (not gaps)

| System | Why not in Timberbot |
|---|---|
| Automation (levers, adapters, sensors) | Use Timberborn's built-in HTTP API (port 8080) directly |
| Logic gates | In-game only, no external control needed |
