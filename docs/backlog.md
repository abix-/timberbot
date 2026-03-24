# Backlog

Prioritized technical debt and improvements. Ordered by criticality.

## Now (before next release)

All resolved.

## Soon (next release cycle)

| # | Issue | Effort | Details |
|---|---|---|---|
| ~~1~~ | ~~Dead StringContent alloc~~ | -- | **FIXED** -- deleted dead line |
| ~~2~~ | ~~Data payload alloc before count check~~ | -- | **FIXED** -- guarded with `if (_webhooks.Count > 0)` |
| ~~3~~ | ~~`_webhooks.ToArray()` per event~~ | -- | **FIXED** -- replaced with index loop |
| ~~4~~ | ~~Silent catches in Cache + Webhooks~~ | -- | **FIXED** -- `LogOnce(site, ex)` logs first occurrence per catch site |
| 4b | Silent catches in Write, Placement, Debug (13 sites) | 30 min | Lower priority -- per-request paths, not per-frame. Same LogOnce pattern |
| 5 | No webhook error logging | 15 min | Can't troubleshoot "my webhook doesn't work." Log first failure per URL to Unity console |
| 6 | CachedBuilding 48-field struct copy | 1 hr | 24K field copies per refresh at 1500 buildings. Convert to class (ref copy instead of value copy) |

## Later (quality of life)

| # | Issue | Effort | Details |
|---|---|---|---|
| 7 | Webhook rate limiting | 2 hr | ThreadPool exhaustion if user subscribes to all events. Batch per 200ms or per-type throttle |
| 8 | Webhook circuit breaker | 30 min | Dead URL burns 5s ThreadPool thread per event. After 5 failures, disable webhook + log |
| 9 | Gatherables still uses Dictionary | 15 min | 6.7ms vs ~2ms with StringBuilder. Inconsistent with trees/buildings/beavers |
| 10 | TimberbotService.cs 3500 lines | 3 hr | God object. Extract WebhookService, CacheService, DebugService |
