# Features

| System | What you can do |
|---|---|
| [Beavers](api-reference.md#get-apibeavers) | See every beaver's wellbeing, per-need breakdown (Hunger, Thirst, Campfire, etc.), workplace, age, contamination. Know exactly which needs are unmet and critical |
| [Wellbeing](api-reference.md#get-apiwellbeing) | Population-wide wellbeing breakdown by category (SocialLife, Fun, Nutrition, Aesthetics, Awe) with current vs max scores. Know exactly what to build next |
| [Buildings](api-reference.md#get-apibuildings) | Every building with workers, priority, power, reachability, inventory, construction progress, statuses. Full colony infrastructure at a glance |
| [Building placement](api-reference.md#post-apibuildingplace) | Place any building with game-native validation. [Find valid spots](api-reference.md#post-apiplacementfind) with reachability, path access, power adjacency. Auto-build [paths with stairs](api-reference.md#post-apipathroute) across z-levels |
| [Building range](api-reference.md#post-apibuildingrange) | See exactly which tiles a farmhouse, lumberjack, forester, gatherer, scavenger, or district center covers. Includes moisture count for farming |
| [Crops](api-reference.md#post-apiplantingmark) | Plant and clear crops. [Find valid planting spots](api-reference.md#post-apiplantingfind) within a farmhouse's range or any area, with irrigation status |
| [Trees](api-reference.md#get-apitrees) | All trees with growth, cutting marks. [Find densest clusters](api-reference.md#get-apitree_clusters) for lumberjack placement. Mark/clear cutting areas |
| [Map](api-reference.md#post-apimap) | Terrain height, water levels, badwater contamination, soil moisture, building occupants per tile. [Visual ASCII map](api-reference.md#post-apivisual) with height shading |
| [Weather](api-reference.md#get-apiweather) | Drought countdown, temperate days remaining, badtide status. Plan ahead |
| [Power](api-reference.md#get-apibuildings) | Per-building power input/output, generator/consumer status, powered state. Plan power chains |
| [Science](api-reference.md#get-apiscience) | Points, all unlockable buildings with costs. [Unlock buildings](api-reference.md#post-apiscienceunlock) with one call |
| [Workers](api-reference.md#post-apiworkers) | Set worker count, [priority](api-reference.md#post-apipriority) (construction + workplace), [pause/unpause](api-reference.md#post-apibuildingpause), [haul priority](api-reference.md#post-apihaulpriority) |
| [Stockpiles](api-reference.md#post-apistockpilecapacity) | Set capacity and allowed goods per stockpile |
| [Distribution](api-reference.md#get-apidistribution) | Import/export settings per good per district. Control resource flow |
| [Districts](api-reference.md#get-apidistricts) | Population, resources per district. [Migrate beavers](api-reference.md#post-apimigrate) between districts |
| [Production](api-reference.md#post-apirecipe) | Set [manufactory recipes](api-reference.md#post-apirecipe), [farmhouse action](api-reference.md#post-apifarmhouseaction), [forester tree priority](api-reference.md#post-apiplantablepriority) |
| [Floodgates](api-reference.md#post-apifloodgate) | Set water gate height |
| [Work schedule](api-reference.md#post-apiworkhours) | Set when beavers stop working |
| [Game speed](api-reference.md#post-apispeed) | Pause, normal, fast, fastest |
| [Alerts](api-reference.md#get-apialerts) | Unstaffed, unpowered, unreachable buildings at a glance |
| [Summary](api-reference.md#get-apisummary) | One call: day, weather, population, resources, food/water days projection, housing, employment, wellbeing, alerts |

## API features

| Feature | Description |
|---|---|
| Pagination | `limit`/`offset` on buildings, trees, gatherables, beavers |
| [Debug inspection](api-reference.md#post-apidebug) | Reflect on any game service, entity component, or field at runtime |
| TOON format | Compact token-efficient output for AI agents |
| Visual map | ASCII map with terrain height shading, building markers, moisture |

## Known gaps

| Gap | Severity | Notes |
|---|---|---|
| Bot condition/fuel | In-Progress | Bots use same NeedManager, needs should show via `beavers` endpoint. Needs verification with bots |

## Roadmap

| Feature | Value | Notes |
|---|---|---|
| Resource projection | Medium | Project wood/plank/gear days like foodDays/waterDays |
| Multi-district management | Medium | Better support for second district, cross-district resource flow |

## By design (not gaps)

| System | Why not in Timberbot |
|---|---|
| Automation (levers, adapters, sensors) | Use Timberborn's built-in HTTP API (port 8080) directly |
| Logic gates | In-game only, no external control needed |
