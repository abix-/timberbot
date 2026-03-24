# Features

| System | What you get | Status |
|---|---|---|
| [Beavers](api-reference.md#get-apibeavers) | Per-beaver wellbeing, every unmet need by name, workplace, age, contamination | Yes |
| [Wellbeing](api-reference.md#get-apiwellbeing) | Population wellbeing by category (Social, Fun, Nutrition, Aesthetics, Awe) with current/max | Yes |
| [Buildings](api-reference.md#get-apibuildings) | Workers, priority, power, reachability, inventory, construction progress | Yes |
| [Placement](api-reference.md#post-apibuildingplace) | Place any building with game-native validation. Find spots with reachability and power | Yes |
| [Building range](api-reference.md#post-apibuildingrange) | Work radius for farmhouse, lumberjack, forester, gatherer, scavenger, district center | Yes |
| [Paths](api-reference.md#post-apipathroute) | Auto-stairs and platforms across z-levels | Yes |
| [Crops](api-reference.md#post-apiplantingmark) | Plant, clear, find valid irrigated spots within farmhouse range | Yes |
| [Trees](api-reference.md#get-apitrees) | Growth, cutting marks, densest clusters | Yes |
| [Map](api-reference.md#post-apimap) | Terrain, water, badwater, moisture, occupants. [ASCII visual](api-reference.md#visual) with height shading | Yes |
| [Weather](api-reference.md#get-apiweather) | Drought countdown, badtide status | Yes |
| [Power](api-reference.md#get-apibuildings) | Per-building input/output, generator/consumer, powered state | Yes |
| [Science](api-reference.md#get-apiscience) | Points, unlock costs, unlock with one call | Yes |
| [Workers](api-reference.md#post-apiworkers) | Count, priority, pause, haul priority | Yes |
| [Distribution](api-reference.md#get-apidistribution) | Import/export per good, beaver migration between districts | Yes |
| [Production](api-reference.md#post-apirecipe) | Recipes, farmhouse action, forester priority | Yes |
| [Stockpiles](api-reference.md#post-apistockpilecapacity) | Capacity and allowed goods per stockpile | Yes |
| [Floodgates](api-reference.md#post-apifloodgate) | Water gate height | Yes |
| [Work schedule](api-reference.md#post-apiworkhours) | Work end hour | Yes |
| [Game speed](api-reference.md#post-apispeed) | Pause, normal, fast, fastest | Yes |
| [Alerts](api-reference.md#get-apialerts) | Unstaffed, unpowered, unreachable buildings | Yes |
| [Scan](api-reference.md#post-apiscan) | Compact area snapshot with occupants | Yes |
| [Visual](api-reference.md#visual) | ASCII map with terrain height shading and building markers | Yes |
| [Summary](api-reference.md#get-apisummary) | Entire colony snapshot in one call | Yes |
| [Debug](api-reference.md#post-apidebug) | Reflection-based inspection of any game object at runtime | Yes |
| Bot condition/fuel | Bot needs via beavers endpoint | In-Progress |
| Resource projection | Projected days of wood, planks, gears | Planned |

## By design

| System | Why not in Timberbot |
|---|---|
| Automation | Use Timberborn's built-in HTTP API (port 8080) |
| Logic gates | In-game only |
