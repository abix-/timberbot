# Webhooks

Push notifications for game events. Instead of polling, the mod sends HTTP POST requests to your registered URLs when events happen in-game.

## Setup

1. Configure in `settings.json` (enabled by default):
```json
{
  "webhooksEnabled": true,
  "webhookBatchMs": 200
}
```

`webhookBatchMs` controls the batching window in milliseconds. Events accumulate and flush in a single POST per webhook. Default 200ms. Set to 0 for immediate dispatch (no batching).

2. Register a webhook:
```bash
timberbot.py register_webhook url:http://localhost:9000/events events:drought.start,drought.end,beaver.died
```

Or via API:
```
POST /api/webhooks
{ "url": "http://localhost:9000/events", "events": ["drought.start", "drought.end"] }
```

Omit `events` to receive all events.

3. Your server receives batched POST requests (JSON array):
```json
[
  {"event": "drought.start", "day": 45, "timestamp": 1711300000, "data": {"duration": 8}},
  {"event": "beaver.died", "day": 45, "timestamp": 1711300000, "data": null}
]
```

Each POST contains an array of events that accumulated during the batch window. Single events arrive as a 1-element array.

## Management

```bash
timberbot.py list_webhooks                         # GET /api/webhooks
timberbot.py unregister_webhook webhook_id:wh_1    # POST /api/webhooks/delete
```

Webhooks are stored in memory -- they reset on game restart. Re-register on startup.

## Events (68 total)

### Weather (6)

| Event | Fires when |
|---|---|
| `drought.start` | drought/badtide begins |
| `drought.end` | drought/badtide ends |
| `drought.approaching` | drought warning (UI notification) |
| `weather.selected` | next weather type chosen for cycle |
| `cycle.start` | new weather cycle begins |
| `cycle.end` | weather cycle ends |
| `cycle.day` | new day within weather cycle |

### Time (2)

| Event | Fires when |
|---|---|
| `day.start` | dawn |
| `night.start` | dusk |

### Buildings (9)

| Event | Fires when |
|---|---|
| `building.placed` | building placed on map |
| `building.demolished` | building demolished |
| `building.finished` | construction complete |
| `building.unfinished` | building reverted to unfinished |
| `building.unlocked` | science unlock |
| `building.deconstructed` | building deconstructed |
| `construction.started` | construction begins |
| `demolish.marked` | marked for demolition |
| `demolish.unmarked` | demolition mark removed |

### Blocks (2)

| Event | Fires when |
|---|---|
| `block.set` | any block placed (paths, levees, platforms) |
| `block.unset` | any block removed |

### Population (8)

| Event | Fires when |
|---|---|
| `beaver.born` | beaver/bot created (from entity system) |
| `beaver.born.event` | beaver born (from beaver system) |
| `beaver.died` | beaver/bot died (from entity system) |
| `character.created` | character created |
| `character.killed` | character killed |
| `bot.manufactured` | bot assembled |
| `population.changed` | population count changed |
| `migration` | beaver migrated between districts |

### Districts (3)

| Event | Fires when |
|---|---|
| `district.changed` | district added/removed |
| `district.connections.changed` | path connections between districts changed |
| `migration.district.changed` | migration district selection changed |

### Needs/Wellbeing (4)

| Event | Fires when |
|---|---|
| `contamination.changed` | beaver contamination status changed |
| `teeth.chipped` | beaver teeth chipped (injury) |
| `wellbeing.highscore` | new wellbeing highscore |
| `status.alert` | status alert added |
| `status.dynamic.alert` | dynamic status alert added |

### Trees/Crops (6)

| Event | Fires when |
|---|---|
| `tree.cut` | tree cut down |
| `tree.marked` | tree added to cutting area |
| `cuttable.cut` | cuttable resource harvested |
| `cutting.area.changed` | cutting area modified |
| `crop.planted` | natural resource planted |
| `planting.marked` | planting area marked |
| `planting.coords.set` | specific planting tile set |
| `planting.coords.unset` | planting tile cleared |

### Wonders (3)

| Event | Fires when |
|---|---|
| `wonder.activated` | wonder activated |
| `wonder.completed` | wonder completed |
| `wonder.countdown` | wonder completion countdown started |

### Power (4)

| Event | Fires when |
|---|---|
| `power.network.created` | power network created |
| `power.network.removed` | power network destroyed |
| `power.generator.added` | generator added to network |
| `power.generator.updated` | generator output changed |

### Game State (7)

| Event | Fires when |
|---|---|
| `game.over` | all beavers dead |
| `game.new` | new game started |
| `game.starting.building` | first building placed in new game |
| `speed.changed` | game speed changed |
| `speed.lock.changed` | speed lock toggled |
| `workhours.changed` | work hours changed |
| `workhours.transitioned` | work hours transitioned |
| `autosave` | autosave triggered |

### Explosions (2)

| Event | Fires when |
|---|---|
| `explosion` | dynamite detonated |
| `explosion.kill` | beaver killed by explosion |

### Terrain/Wind (2)

| Event | Fires when |
|---|---|
| `terrain.destroyed` | terrain destroyed |
| `wind.changed` | wind direction/speed changed |

### Misc (3)

| Event | Fires when |
|---|---|
| `zipline.activated` | zipline connection activated |
| `entity.created` | any entity created (low-level) |
| `entity.renamed` | entity renamed |
| `construction.mode.changed` | entered/exited construction mode |
| `faction.unlocked` | faction unlocked |

## Not included (UI/visual only)

44 game events are excluded because they're pure UI, visual, or editor events with no gameplay value:

- Panel show/hide, selection, batch control (12)
- Tool enter/exit, tool groups (8)
- Camera level, water opacity, decals (5)
- Input, keybinds, keyword matching (3)
- Debug/dev mode toggles (2)
- Main menu, settlement relocation (2)
- Benchmark, Steam Workshop, tutorials, undo, preview (5)
- Automation building UI pins (3)
- Map editor events (4)

## Circuit breaker

After 5 consecutive delivery failures, a webhook is automatically disabled. Check status via `GET /api/webhooks` -- disabled webhooks show `"disabled": true` and `"failures": 5`. Re-register to reset.

## Architecture

- Events fire on the Unity main thread via Timberborn's `EventBus`
- `PushEvent()` serializes and appends to a pending list (no ThreadPool dispatch)
- `FlushWebhooks()` runs every `webhookBatchMs` from `UpdateSingleton` on main thread
- Each flush sends ONE batched POST per webhook on background `ThreadPool`
- Static `HttpClient` with 5s timeout
- Circuit breaker: 5 consecutive failures disables the webhook
- Subscribers filter by event name (null = all events)
