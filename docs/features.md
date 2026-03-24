# Features

What Timberbot can and can't do, audited against every Timberborn 1.0 game system.

## Supported systems

| System | How |
|---|---|
| Weather (drought/badtide) | [summary](api-reference.md#get-apisummary), [weather](api-reference.md#get-apiweather) |
| Water levels | [map](api-reference.md#post-apimap) water height per tile |
| Floodgates | [buildings](api-reference.md#get-apibuildings), [set_floodgate](api-reference.md#post-apifloodgate) |
| Water pumps | [buildings](api-reference.md#get-apibuildings), [set_workers](api-reference.md#post-apiworkers) |
| Beaver population | [summary](api-reference.md#get-apisummary), [population](api-reference.md#get-apipopulation) |
| Beaver wellbeing + needs | [beavers](api-reference.md#get-apibeavers), [wellbeing](api-reference.md#get-apiwellbeing) (per-category breakdown) |
| Beaver contamination | [beavers](api-reference.md#get-apibeavers) (contaminated field) |
| Beaver workplace | [beavers](api-reference.md#get-apibeavers) (workplace field) |
| Beaver age | [beavers](api-reference.md#get-apibeavers) (lifeProgress) |
| Bot distinction | [beavers](api-reference.md#get-apibeavers) (isBot field) |
| Place/demolish buildings | [place_building](api-reference.md#post-apibuildingplace), [demolish_building](api-reference.md#post-apibuildingdemolish), [find_placement](api-reference.md#post-apiplacementfind). Game-native `PreviewFactory` + `IsValid()` validation |
| Construction progress | [buildings](api-reference.md#get-apibuildings) (buildProgress, materialProgress, hasMaterials) |
| Building inventory | [buildings](api-reference.md#get-apibuildings) (inventory field) |
| Building reachability | [buildings](api-reference.md#get-apibuildings) (reachable field) |
| Building statuses/alerts | [buildings](api-reference.md#get-apibuildings) (statuses field), [alerts](api-reference.md#get-apialerts) |
| Pause/unpause | [pause_building](api-reference.md#post-apibuildingpause) |
| Workers | [set_workers](api-reference.md#post-apiworkers) |
| Priority | [set_priority](api-reference.md#post-apipriority) (construction + workplace) |
| Paths/stairs/platforms | [place_building](api-reference.md#post-apibuildingplace), [place_path](api-reference.md#post-apipathroute) (auto-stairs + platforms) |
| Ziplines/tubeways | [buildings](api-reference.md#get-apibuildings) (visible as buildings) |
| Power network | [buildings](api-reference.md#get-apibuildings) (powered, isGenerator, isConsumer, powerDemand, powerSupply) |
| Science points | [science](api-reference.md#get-apiscience) |
| Unlock buildings | [unlock_building](api-reference.md#post-apiscienceunlock) |
| Crops | [plant_crop](api-reference.md#post-apiplantingmark), [clear_planting](api-reference.md#post-apiplantingclear). Game-native `PlantingAreaValidator.CanPlant()` validation |
| Trees | [trees](api-reference.md#get-apitrees), [mark_trees](api-reference.md#post-apicuttingarea), [tree_clusters](api-reference.md#get-apitree_clusters) |
| Stockpiles | [set_capacity](api-reference.md#post-apistockpilecapacity), [set_good](api-reference.md#post-apistockpilegood) |
| Districts | [districts](api-reference.md#get-apidistricts) |
| Distribution | [distribution](api-reference.md#get-apidistribution), [set_distribution](api-reference.md#post-apidistribution) |
| Work schedule | [workhours](api-reference.md#get-apiworkhours), [set_workhours](api-reference.md#post-apiworkhours) |
| Wonders | [buildings](api-reference.md#get-apibuildings) (isWonder, wonderActive) |
| Notifications | [notifications](api-reference.md#get-apinotifications) |
| Landscaping (levees/dams) | [place_building](api-reference.md#post-apibuildingplace) with Levee/Dam/TerrainBlock |
| Decorations | [place_building](api-reference.md#post-apibuildingplace) |
| Map/terrain | [map](api-reference.md#post-apimap), [scan](api-reference.md#post-apiscan), [visual](api-reference.md#post-apivisual) |
| Game speed | [speed](api-reference.md#get-apispeed), [set_speed](api-reference.md#post-apispeed) |
| Soil contamination | [map](api-reference.md#post-apimap) (contaminated field) |
| Soil moisture | [map](api-reference.md#post-apimap) (moist field) |
| Water contamination | [map](api-reference.md#post-apimap) (badwater field) |
| District migration | [migrate](api-reference.md#post-apimigrate) |
| Clutch status | [buildings](api-reference.md#get-apibuildings) (isClutch, clutchEngaged) |
| Per-building power | [buildings](api-reference.md#get-apibuildings) (nominalPowerInput, nominalPowerOutput) |
| Hauler priority | [set_haul_priority](api-reference.md#post-apihaulpriority) |
| Manufactory recipes | [set_recipe](api-reference.md#post-apirecipe) |
| Farmhouse action | [set_farmhouse_action](api-reference.md#post-apifarmhouseaction) |
| Plantable priority | [set_plantable_priority](api-reference.md#post-apiplantablepriority) |

## API features

| Feature | Description |
|---|---|
| Pagination | `limit`/`offset` on buildings, trees, gatherables, beavers |
| Debug inspection | [debug](api-reference.md#post-apidebug) -- reflection-based inspection of any game service, entity component, or field |
| TOON format | Compact token-efficient output for AI agents |
| Visual map | ASCII map with terrain height shading, building markers, moisture |

## Known gaps

| Gap | Severity | Notes |
|---|---|---|
| Bot condition/fuel | In-Progress | Bots use same NeedManager, needs should show via `beavers` endpoint. Needs verification with bots |

## Roadmap

| Feature | Value | Notes |
|---|---|---|
| Per-beaver wellbeing needs | High | Which specific needs are unmet per beaver, not just overall score |
| Find placement for crops | High | "Where can I plant Kohlrabi on irrigated soil near this farmhouse?" |
| Building work range | High | Farmhouse, lumberjack, forester work radii for smarter placement |
| Resource projection | Medium | Project wood/plank/gear days like foodDays/waterDays |
| Building power connections | Medium | Which buildings are in the same power chain |
| Automation bridge | Medium | Expose port 8080 levers/adapters through Timberbot API |
| Multi-district management | Medium | Better support for second district, cross-district resource flow |

## By design (not gaps)

| System | Why not in Timberbot |
|---|---|
| Automation (levers, adapters, sensors) | Use Timberborn's built-in HTTP API (port 8080) directly |
| Logic gates | In-game only, no external control needed |
