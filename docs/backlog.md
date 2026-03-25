# Backlog

Prioritized technical debt and improvements. Ordered by criticality.

## Now (before next release)

All resolved.

## Resolved

| # | Issue | Fix |
|---|---|---|
| ~~1~~ | Dead StringContent alloc | deleted dead line |
| ~~2~~ | Data payload alloc before count check | guarded with `if (_webhooks.Count > 0)` |
| ~~3~~ | `_webhooks.ToArray()` per event | replaced with index loop |
| ~~4~~ | Silent catches in Cache + Webhooks | `TimberbotLog.Error(context, ex)` -- file + console logging |
| ~~4b~~ | Silent catches in Write, Placement, Debug | same `TimberbotLog.Error` pattern, all 22 catch sites covered |
| ~~5~~ | No error logging | `TimberbotLog` class -- file-based, timestamped, fresh per session |
| ~~6~~ | CachedBuilding 48-field struct copy | converted to class + `Clone()` via MemberwiseClone. Zero value copies |
| ~~7~~ | Gatherables still uses Dictionary | converted to StringBuilder + Jw like trees/buildings |
| ~~8~~ | All remaining Dictionary/List endpoints | converted all 14 endpoints to JwWriter. Zero Dictionary/Newtonsoft allocs |

## Soon (next release cycle)

| # | Issue | Effort | Details |
|---|---|---|---|
| 9 | Migrate high-volume endpoints to JwWriter | 2 hr | buildings, trees, crops, gatherables, beavers still use old `Jw` static helper + manual separator tracking. Convert to `JwWriter` then delete `Jw` class |

## Later (quality of life)

| # | Issue | Effort | Details |
|---|---|---|---|
| 10 | Webhook rate limiting | 2 hr | ThreadPool exhaustion if user subscribes to all events. Batch per 200ms or per-type throttle |
| 11 | Webhook circuit breaker | 30 min | Dead URL burns 5s ThreadPool thread per event. After 5 failures, disable webhook + log |
| 12 | TimberbotService.cs 3500 lines | 3 hr | God object. Extract WebhookService, CacheService, DebugService |
