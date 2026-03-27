# Buildings V2 Performance Results

Date: 2026-03-27

Test command:

```powershell
python timberbot/script/test_validation.py buildings_v2_performance -n 50
```

Result file:

- [20260327-094047-buildings_v2_performance.txt](/C:/code/timberborn/timberbot/test-results/20260327-094047-buildings_v2_performance.txt)

## Summary

`buildings_v2 full` performed slightly better than legacy `buildings full`.

`buildings_v2` basic performed much worse than legacy `buildings`, and it also had failures during the 50-iteration run.

## Raw Comparison

| Endpoint | Avg | Min | Max | Success |
|---|---:|---:|---:|---:|
| `buildings` | 257 ms | 230 ms | 413 ms | 50 / 50 |
| `buildings_v2` | 635 ms | 232 ms | 3771 ms | 42 / 50 |
| `buildings full` | 275 ms | 251 ms | 333 ms | 50 / 50 |
| `buildings_v2 full` | 252 ms | 227 ms | 319 ms | 50 / 50 |

## Legacy vs V2

| Scenario | Legacy | V2 | Delta | Ratio |
|---|---:|---:|---:|---:|
| basic | 257 ms | 635 ms | +378 ms | 2.47x |
| full | 275 ms | 252 ms | -23 ms | 0.92x |

## Interpretation

- `buildings_v2 full` looks viable from a read-latency standpoint.
- `buildings_v2` basic is currently not viable.
- The main issue is not just slower average latency. The worst-case latency spiked to `3771 ms`, and `8` of `50` requests failed.
- That strongly suggests a refresh coordination problem in the basic path rather than a general serialization cost problem, because the full path stayed stable.

## Next Investigation

Focus on `buildings_v2` basic specifically:

1. Verify whether the failures are `refresh_timeout` responses.
2. Check whether repeated basic requests are queueing behind the fresh-read waiter path.
3. Compare the basic and full code paths to see why full is stable while basic spikes.
4. Add lightweight timing/debug counters around refresh request, publish, and waiter release.
