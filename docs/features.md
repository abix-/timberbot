# Features

| System | What you can do | Status |
|---|---|---|
| [Beavers](api-reference.md#get-apibeavers) | Every beaver's wellbeing, per-need breakdown (Hunger, Thirst, Campfire, etc.), workplace, age, contamination. Know exactly which needs are unmet | Yes |
| [Wellbeing](api-reference.md#get-apiwellbeing) | Population wellbeing by category (SocialLife, Fun, Nutrition, Aesthetics, Awe) with current vs max. Know what to build next | Yes |
| [Buildings](api-reference.md#get-apibuildings) | Every building with workers, priority, power, reachability, inventory, construction progress, statuses | Yes |
| [Building placement](api-reference.md#post-apibuildingplace) | Place any building with game-native validation. [Find valid spots](api-reference.md#post-apiplacementfind) with reachability, path access, power adjacency | Yes |
| [Building range](api-reference.md#post-apibuildingrange) | Work tiles for farmhouse, lumberjack, forester, gatherer, scavenger, district center. Includes moisture count | Yes |
| [Paths](api-reference.md#post-apipathroute) | Auto-build paths with stairs and platforms across z-levels | Yes |
| [Crops](api-reference.md#post-apiplantingmark) | Plant and clear crops. [Find valid spots](api-reference.md#post-apiplantingfind) in a farmhouse's range with irrigation status | Yes |
| [Trees](api-reference.md#get-apitrees) | All trees with growth and cutting marks. [Find densest clusters](api-reference.md#get-apitree_clusters) for lumberjack placement | Yes |
| [Map](api-reference.md#post-apimap) | Terrain, water, badwater, soil moisture, occupants per tile. [Visual ASCII map](api-reference.md#post-apivisual) with height shading | Yes |
| [Weather](api-reference.md#get-apiweather) | Drought countdown, temperate days, badtide status | Yes |
| [Power](api-reference.md#get-apibuildings) | Per-building power input/output, generator/consumer, powered state | Yes |
| [Science](api-reference.md#get-apiscience) | Points, unlockable buildings with costs. [Unlock](api-reference.md#post-apiscienceunlock) with one call | Yes |
| [Workers](api-reference.md#post-apiworkers) | Worker count, [priority](api-reference.md#post-apipriority), [pause/unpause](api-reference.md#post-apibuildingpause), [haul priority](api-reference.md#post-apihaulpriority) | Yes |
| [Stockpiles](api-reference.md#post-apistockpilecapacity) | Set capacity and allowed goods | Yes |
| [Distribution](api-reference.md#get-apidistribution) | Import/export per good per district | Yes |
| [Districts](api-reference.md#get-apidistricts) | Population, resources per district. [Migrate beavers](api-reference.md#post-apimigrate) between districts | Yes |
| [Production](api-reference.md#post-apirecipe) | Manufactory recipes, farmhouse action, forester tree priority | Yes |
| [Floodgates](api-reference.md#post-apifloodgate) | Set water gate height | Yes |
| [Work schedule](api-reference.md#post-apiworkhours) | Set when beavers stop working | Yes |
| [Game speed](api-reference.md#post-apispeed) | Pause, normal, fast, fastest | Yes |
| [Alerts](api-reference.md#get-apialerts) | Unstaffed, unpowered, unreachable at a glance | Yes |
| [Summary](api-reference.md#get-apisummary) | One call: day, weather, population, resources, food/water projection, housing, employment, wellbeing, alerts | Yes |
| [Debug](api-reference.md#post-apidebug) | Reflect on any game service, entity, or field at runtime | Yes |
| Bot condition/fuel | Bot needs via beavers endpoint | In-Progress |
| Resource projection | Project wood/plank/gear days like foodDays/waterDays | Planned |
| Multi-district management | Better second district support, cross-district resource flow | Planned |

## API features

| Feature | Description |
|---|---|
| Pagination | `limit`/`offset` on buildings, trees, gatherables, beavers |
| TOON format | Compact token-efficient output for AI agents |
| Visual map | ASCII map with terrain height shading, building markers, moisture |

## By design (not gaps)

| System | Why not in Timberbot |
|---|---|
| Automation (levers, adapters, sensors) | Use Timberborn's built-in HTTP API (port 8080) directly |
| Logic gates | In-game only, no external control needed |
