// TimberbotService.Debug.cs -- Reflection inspector and built-in benchmark.
//
// DebugInspect: walks any game object graph at runtime using reflection.
// Lets you inspect fields, properties, call methods, and chain results with $.
// Gated behind debugEndpointEnabled in settings.json. Not for production use.
//
// RunBenchmark: profiles all read endpoints with Stopwatch timing and GC0 counts.
// Runs both foreach vs for-loop comparisons on collection iteration and full
// endpoint profiling (CollectBuildings, CollectBeavers, etc.) against real game data.
// Returns pass/fail + timing per endpoint. Used to catch performance regressions.

using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockObjectTools;
using UnityEngine;
using CachedBuilding = Timberbot.TimberbotEntityCache.CachedBuilding;
using CachedBeaver = Timberbot.TimberbotEntityCache.CachedBeaver;

namespace Timberbot
{
    public class TimberbotDebug
    {
        private readonly PreviewFactory PreviewFactory;
        private readonly TimberbotJw _jw = new TimberbotJw(512);
        public TimberbotService Service; // set by TimberbotService.Load()

        public TimberbotDebug(PreviewFactory previewFactory)
        {
            PreviewFactory = previewFactory;
        }

        // Profile all read endpoints and collection patterns to catch performance regressions.
        //
        // Methodology:
        //   1. Warmup: 3-10 iterations to JIT-compile methods and fill caches
        //   2. Measure: N iterations with Stopwatch (high-resolution timer, not DateTime)
        //   3. GC tracking: GC.CollectionCount(0) before/after to detect allocations
        //      (any gc0 > 0 means the code allocated enough to trigger garbage collection)
        //   4. Timing: ElapsedTicks / Stopwatch.Frequency gives sub-microsecond precision
        //
        // Returns per-test: totalMs, perCallMs, gc0 count, item count, pass/fail.
        public object RunBenchmark(int iterations)
        {
            var results = new List<object>();
            var buildings = Service.Cache.Buildings.Read;

            // --- Test 1: BreedingPod.Nutrients foreach ---
            // Nutrients is IEnumerable<GoodAmount> (not IList), so foreach is the only
            // option. This test measures the allocation cost of the enumerator.
            var breedingPods = new List<CachedBuilding>();
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i].BreedingPod != null) breedingPods.Add(buildings[i]);

            if (breedingPods.Count > 0)
            {
                // warmup: JIT compile + fill CPU caches
                for (int w = 0; w < 10; w++)
                    for (int pi = 0; pi < breedingPods.Count; pi++)
                        foreach (var ga in breedingPods[pi].BreedingPod.Nutrients) { var _ = ga.Amount; }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long gcBefore = GC.CollectionCount(0);
                for (int iter = 0; iter < iterations; iter++)
                    for (int pi = 0; pi < breedingPods.Count; pi++)
                        foreach (var ga in breedingPods[pi].BreedingPod.Nutrients) { var _ = ga.Amount; }
                sw.Stop();
                long gcForeach = GC.CollectionCount(0) - gcBefore;
                double foreachMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                results.Add(new
                {
                    test = "BreedingPod.Nutrients",
                    count = breedingPods.Count,
                    iterations,
                    totalMs = foreachMs,
                    perCallMs = foreachMs / iterations,
                    gc0 = gcForeach,
                    note = "IEnumerable -- not indexable, foreach is the only option"
                });
            }

            // --- Test 2: Inventories.AllInventories + inv.Stock foreach vs for ---
            var withInv = new List<CachedBuilding>();
            for (int i = 0; i < buildings.Count; i++)
                if (buildings[i].Inventories != null) withInv.Add(buildings[i]);

            if (withInv.Count > 0)
            {
                // Warmup
                for (int w = 0; w < 10; w++)
                    for (int bi = 0; bi < withInv.Count; bi++)
                        foreach (var inv in withInv[bi].Inventories.AllInventories)
                            foreach (var ga in inv.Stock) { var _ = ga.Amount; }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long gcBefore = GC.CollectionCount(0);
                for (int iter = 0; iter < iterations; iter++)
                    for (int bi = 0; bi < withInv.Count; bi++)
                        foreach (var inv in withInv[bi].Inventories.AllInventories)
                            foreach (var ga in inv.Stock) { var _ = ga.Amount; }
                sw.Stop();
                long gcForeach = GC.CollectionCount(0) - gcBefore;
                double foreachMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                // for-loop version
                sw.Restart();
                gcBefore = GC.CollectionCount(0);
                for (int iter = 0; iter < iterations; iter++)
                    for (int bi = 0; bi < withInv.Count; bi++)
                    {
                        var allInv = withInv[bi].Inventories.AllInventories;
                        for (int ii = 0; ii < allInv.Count; ii++)
                        {
                            var stock = allInv[ii].Stock;
                            for (int si = 0; si < stock.Count; si++) { var _ = stock[si].Amount; }
                        }
                    }
                sw.Stop();
                long gcFor = GC.CollectionCount(0) - gcBefore;
                double forMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                results.Add(new
                {
                    test = "Inventories.foreach",
                    count = withInv.Count,
                    iterations,
                    totalMs = foreachMs,
                    perCallMs = foreachMs / iterations,
                    gc0 = gcForeach
                });
                results.Add(new
                {
                    test = "Inventories.forLoop",
                    count = withInv.Count,
                    iterations,
                    totalMs = forMs,
                    perCallMs = forMs / iterations,
                    gc0 = gcFor,
                    speedup = foreachMs > 0 ? foreachMs / forMs : 0
                });

                // breakdown: AllInventories access only (no Stock iteration)
                sw.Restart();
                gcBefore = GC.CollectionCount(0);
                for (int iter = 0; iter < iterations; iter++)
                    for (int bi = 0; bi < withInv.Count; bi++)
                    {
                        var allInv = withInv[bi].Inventories.AllInventories;
                        for (int ii = 0; ii < allInv.Count; ii++) { var _ = allInv[ii].TotalAmountInStock; }
                    }
                sw.Stop();
                results.Add(new
                {
                    test = "Inventories.AllInventories.only",
                    count = withInv.Count,
                    iterations,
                    totalMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                    perCallMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / iterations,
                    gc0 = GC.CollectionCount(0) - gcBefore
                });

                // breakdown: Stock iteration with Dictionary insert (simulates real refresh)
                var testDict = new Dictionary<string, int>();
                sw.Restart();
                gcBefore = GC.CollectionCount(0);
                for (int iter = 0; iter < iterations; iter++)
                    for (int bi = 0; bi < withInv.Count; bi++)
                    {
                        testDict.Clear();
                        var allInv = withInv[bi].Inventories.AllInventories;
                        for (int ii = 0; ii < allInv.Count; ii++)
                        {
                            var stock = allInv[ii].Stock;
                            for (int si = 0; si < stock.Count; si++)
                            {
                                var ga = stock[si];
                                if (ga.Amount > 0)
                                {
                                    if (testDict.ContainsKey(ga.GoodId))
                                        testDict[ga.GoodId] += ga.Amount;
                                    else
                                        testDict[ga.GoodId] = ga.Amount;
                                }
                            }
                        }
                    }
                sw.Stop();
                results.Add(new
                {
                    test = "Inventories.FullRefreshSim",
                    count = withInv.Count,
                    iterations,
                    totalMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                    perCallMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / iterations,
                    gc0 = GC.CollectionCount(0) - gcBefore
                });
            }

            // --- Test: NeedMgr.GetNeeds() allocation ---
            var beaversWithNeeds = new List<CachedBeaver>();
            var beaverBuf = Service.Cache.Beavers.Read;
            for (int i = 0; i < beaverBuf.Count; i++)
                if (beaverBuf[i].NeedMgr != null) beaversWithNeeds.Add(beaverBuf[i]);

            int n = System.Math.Max(iterations, 10);
            if (beaversWithNeeds.Count > 0)
            {
                for (int w = 0; w < 3; w++)
                    for (int bi = 0; bi < beaversWithNeeds.Count; bi++)
                        foreach (var ns in beaversWithNeeds[bi].NeedMgr.GetNeeds()) { var _ = ns.Id; }

                var nsw = System.Diagnostics.Stopwatch.StartNew();
                long ngc = GC.CollectionCount(0);
                for (int iter = 0; iter < n; iter++)
                    for (int bi = 0; bi < beaversWithNeeds.Count; bi++)
                        foreach (var ns in beaversWithNeeds[bi].NeedMgr.GetNeeds()) { var _ = ns.Id; }
                nsw.Stop();
                long ngc1 = GC.CollectionCount(0) - ngc;
                double nms1 = nsw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                nsw.Restart();
                ngc = GC.CollectionCount(0);
                for (int iter = 0; iter < n; iter++)
                    for (int bi = 0; bi < beaversWithNeeds.Count; bi++)
                    {
                        var mgr = beaversWithNeeds[bi].NeedMgr;
                        foreach (var ns in mgr.GetNeeds())
                        {
                            var need = mgr.GetNeed(ns.Id);
                            var wb = mgr.GetNeedWellbeing(ns.Id);
                            var _ = need.Points;
                        }
                    }
                nsw.Stop();
                long ngc2 = GC.CollectionCount(0) - ngc;
                double nms2 = nsw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                results.Add(new
                {
                    test = "NeedMgr.GetNeeds.foreach",
                    count = beaversWithNeeds.Count,
                    iterations = n,
                    totalMs = nms1,
                    perCallMs = nms1 / n,
                    gc0 = ngc1
                });
                results.Add(new
                {
                    test = "NeedMgr.FullNeedLoop",
                    count = beaversWithNeeds.Count,
                    iterations = n,
                    totalMs = nms2,
                    perCallMs = nms2 / n,
                    gc0 = ngc2
                });
            }

            // --- Test: Math.Round vs manual rounding allocation ---
            // Math.Round(double, int) on Mono may box the return value. Compare GC0
            // between Math.Round and manual integer rounding to see if it matters.
            if (beaversWithNeeds.Count > 0)
            {
                // collect live need points for realistic test data
                var needPoints = new List<float>();
                for (int bi = 0; bi < beaversWithNeeds.Count; bi++)
                    foreach (var ns in beaversWithNeeds[bi].NeedMgr.GetNeeds())
                        needPoints.Add(beaversWithNeeds[bi].NeedMgr.GetNeed(ns.Id).Points);

                if (needPoints.Count > 0)
                {
                    // warmup
                    for (int w = 0; w < 3; w++)
                        for (int pi = 0; pi < needPoints.Count; pi++)
                            { var _ = (float)Math.Round(needPoints[pi], 2); }

                    // Math.Round path
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    long gcBefore = GC.CollectionCount(0);
                    for (int iter = 0; iter < n; iter++)
                        for (int pi = 0; pi < needPoints.Count; pi++)
                            { var _ = (float)Math.Round(needPoints[pi], 2); }
                    sw.Stop();
                    long gcRound = GC.CollectionCount(0) - gcBefore;
                    double roundMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                    // manual rounding: multiply, truncate, divide (same as JW Float approach)
                    sw.Restart();
                    gcBefore = GC.CollectionCount(0);
                    for (int iter = 0; iter < n; iter++)
                        for (int pi = 0; pi < needPoints.Count; pi++)
                            { var _ = (int)(needPoints[pi] * 100 + 0.5f) * 0.01f; }
                    sw.Stop();
                    long gcManual = GC.CollectionCount(0) - gcBefore;
                    double manualMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                    results.Add(new
                    {
                        test = "MathRound.vs.Manual",
                        count = needPoints.Count,
                        iterations = n,
                        roundMs,
                        roundPerCallMs = roundMs / n,
                        roundGc0 = gcRound,
                        manualMs,
                        manualPerCallMs = manualMs / n,
                        manualGc0 = gcManual,
                        speedup = roundMs > 0 ? roundMs / manualMs : 0
                    });
                }
            }

            // --- Endpoint benchmarks: functional + performance ---
            // BenchCall is a generic benchmark wrapper for any endpoint function.
            // It warmups (3 calls), then times N iterations with GC tracking.
            // knownItems overrides auto-detection for endpoints that return strings
            // (JW-serialized JSON) instead of IList objects.
            // pass = false if the endpoint returned an error dictionary.
            object BenchCall(string name, int iters, System.Func<object> fn, int knownItems = -1)
            {
                // warmup: JIT + cache fill
                object result = null;
                for (int w = 0; w < 3; w++) result = fn();

                var bsw = System.Diagnostics.Stopwatch.StartNew();
                long bgc = GC.CollectionCount(0);
                for (int bi = 0; bi < iters; bi++) result = fn();
                bsw.Stop();
                double bms = bsw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                long bgc0 = GC.CollectionCount(0) - bgc;

                // item count: use knownItems if provided, otherwise try IList.Count
                int items = knownItems >= 0 ? knownItems
                    : result is System.Collections.IList blist ? blist.Count
                    : result != null ? 1 : 0;

                // functional check: did the endpoint return an error?
                bool pass = !(result is Dictionary<string, object> bd && bd.ContainsKey("error"));

                return Service.Cache.Jw.Result(("test", name), ("iterations", iters),
                    ("totalMs", bms), ("perCallMs", bms / iters), ("gc0", bgc0),
                    ("items", items), ("pass", pass));
            }

            // Heavy endpoints (CollectBuildings.full, CollectTiles, FindPlacement) run 10x
            // fewer iterations because they're O(n*fields) and would take too long at full count
            int nHeavy = System.Math.Max(n / 10, 1);
            int nb = Service.Cache.Buildings.Read.Count;
            int nv = Service.Cache.Beavers.Read.Count;
            int nr = Service.Cache.NaturalResources.Read.Count;

            results.Add(BenchCall("CollectSummary", n, () => Service.Read.CollectSummary("json")));
            results.Add(BenchCall("CollectBuildings", n, () => Service.Read.CollectBuildings("json", "basic"), nb));
            results.Add(BenchCall("CollectBuildings.full", nHeavy, () => Service.Read.CollectBuildings("json", "full"), nb));
            results.Add(BenchCall("CollectBeavers", n, () => Service.Read.CollectBeavers("json", "basic"), nv));
            results.Add(BenchCall("CollectBeavers.full", nHeavy, () => Service.Read.CollectBeavers("json", "full"), nv));
            results.Add(BenchCall("CollectTrees", n, () => Service.Read.CollectTrees(), nr));
            results.Add(BenchCall("CollectCrops", n, () => Service.Read.CollectCrops(), nr));
            results.Add(BenchCall("CollectGatherables", n, () => Service.Read.CollectGatherables()));
            results.Add(BenchCall("CollectPowerNetworks", n, () => Service.Read.CollectPowerNetworks()));
            results.Add(BenchCall("CollectAlerts", n, () => Service.Read.CollectAlerts()));
            results.Add(BenchCall("CollectWellbeing", n, () => Service.Read.CollectWellbeing(), nv));
            results.Add(BenchCall("CollectScience", n, () => Service.Read.CollectScience()));
            results.Add(BenchCall("CollectResources", n, () => Service.Read.CollectResources("json")));
            results.Add(BenchCall("CollectPopulation", n, () => Service.Read.CollectPopulation(), nv));
            results.Add(BenchCall("CollectDistricts", n, () => Service.Read.CollectDistricts("json")));
            results.Add(BenchCall("CollectDistribution", n, () => Service.Read.CollectDistribution()));
            results.Add(BenchCall("CollectTime", n, () => Service.Read.CollectTime()));
            results.Add(BenchCall("CollectWeather", n, () => Service.Read.CollectWeather()));
            results.Add(BenchCall("CollectSpeed", n, () => Service.Read.CollectSpeed()));
            results.Add(BenchCall("CollectWorkHours", n, () => Service.Read.CollectWorkHours()));
            results.Add(BenchCall("CollectNotifications", n, () => Service.Read.CollectNotifications()));
            results.Add(BenchCall("CollectTreeClusters", nHeavy, () => Service.Read.CollectTreeClusters()));
            results.Add(BenchCall("CollectPrefabs", nHeavy, () => Service.Placement.CollectPrefabs()));
            results.Add(BenchCall("CollectTiles.20x20", nHeavy, () => Service.Read.CollectTiles("toon", 120, 130, 140, 150), 400));
            results.Add(BenchCall("FindPlacement", nHeavy, () => Service.Placement.FindPlacement("Path", 120, 135, 130, 145)));

            // --- Low issue micro-benchmarks (#12-20) ---
            // #12-14: string interpolation alloc ($"{a}/{b}" per building)
            {
                int a = 3, b = 5;
                for (int w = 0; w < 10; w++) { var _ = $"{a}/{b}"; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long gcBefore = GC.CollectionCount(0);
                for (int iter = 0; iter < n; iter++)
                    for (int bi = 0; bi < nb; bi++) { var _ = $"{a}/{b}"; }
                sw.Stop();
                long gcInterp = GC.CollectionCount(0) - gcBefore;
                double interpMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                results.Add(new { test = "StringInterpolation", count = nb, iterations = n,
                    totalMs = interpMs, perCallMs = interpMs / n, gc0 = gcInterp,
                    note = "#12-14: string interp per building" });
            }

            // #15: string concat (critical + "+" + n.Id per beaver)
            if (beaversWithNeeds.Count > 0)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long gcBefore = GC.CollectionCount(0);
                for (int iter = 0; iter < n; iter++)
                    for (int bi = 0; bi < beaversWithNeeds.Count; bi++)
                    {
                        string crit = "";
                        foreach (var ns in beaversWithNeeds[bi].NeedMgr.GetNeeds())
                            crit = crit.Length > 0 ? crit + "+" + ns.Id : ns.Id;
                    }
                sw.Stop();
                long gcConcat = GC.CollectionCount(0) - gcBefore;
                double concatMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                results.Add(new { test = "StringConcat.Needs", count = beaversWithNeeds.Count, iterations = n,
                    totalMs = concatMs, perCallMs = concatMs / n, gc0 = gcConcat,
                    note = "#15: concat per beaver per need" });
            }

            // #20: GetBeaverNeeds alloc check
            {
                var fns = Service.Read._factionNeedService;
                if (fns != null)
                {
                    for (int w = 0; w < 3; w++) { var _ = fns.GetBeaverNeeds(); }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    long gcBefore = GC.CollectionCount(0);
                    for (int iter = 0; iter < n; iter++) { var _ = fns.GetBeaverNeeds(); }
                    sw.Stop();
                    long gcNeeds = GC.CollectionCount(0) - gcBefore;
                    double needsMs = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    results.Add(new { test = "GetBeaverNeeds", count = 1, iterations = n,
                        totalMs = needsMs, perCallMs = needsMs / n, gc0 = gcNeeds,
                        note = "#20: thread-safety + alloc check" });
                }
            }

            // toon variants: catch toon-specific allocs (interpolation, List<string>, string.Join)
            results.Add(BenchCall("CollectSummary.toon", n, () => Service.Read.CollectSummary("toon")));
            results.Add(BenchCall("CollectBuildings.toon", n, () => Service.Read.CollectBuildings("toon", "basic"), nb));
            results.Add(BenchCall("CollectBuildings.full.toon", nHeavy, () => Service.Read.CollectBuildings("toon", "full"), nb));
            results.Add(BenchCall("CollectBeavers.toon", n, () => Service.Read.CollectBeavers("toon", "basic"), nv));
            results.Add(BenchCall("CollectAlerts.toon", n, () => Service.Read.CollectAlerts("toon")));
            results.Add(BenchCall("CollectTiles.toon.20x20", nHeavy, () => Service.Read.CollectTiles("toon", 120, 130, 140, 150), 400));

            // metadata
            results.Insert(0, new
            {
                test = "_meta",
                buildings = Service.Cache.Buildings.Read.Count,
                beavers = Service.Cache.Beavers.Read.Count,
                trees = Service.Cache.NaturalResources.Read.Count
            });

            return Service.Cache.Jw.Result(("benchmarks", results));
        }

        // ================================================================
        // DEBUG -- reflection-based game state inspector and validator
        // ================================================================
        // Walk any game object graph at runtime using .NET reflection.
        // This is the main development probe surface for Timberbot: inspect values,
        // list members, call methods, compare live values, and assert assumptions.
        //
        // $ chaining: the result of any get/call is stored in _debugLastResult.
        // Next call can use "$.PropertyName" to continue from where the last call left off.
        private static object _debugLastResult;

        public object DebugInspect(string target, Dictionary<string, string> args = null)
        {
            var info = new Dictionary<string, object>();
            // include both public and private members -- we need access to game internals
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            args = args ?? new Dictionary<string, string>();

            string Arg(string key, string def = "") => args.ContainsKey(key) ? args[key] : def;

            object ParseLooseValue(string raw)
            {
                if (raw == "$") return _debugLastResult;
                if (string.Equals(raw, "null", System.StringComparison.OrdinalIgnoreCase)) return null;
                if (bool.TryParse(raw, out var boolVal)) return boolVal;
                if (int.TryParse(raw, out var intVal)) return intVal;
                if (long.TryParse(raw, out var longVal)) return longVal;
                if (double.TryParse(raw, out var dblVal)) return dblVal;
                return raw;
            }

            // Parse a string argument into a typed .NET object.
            // Supports primitives, enums, nullable types, object, and common Unity vectors.
            bool TryParseArg(string argStr, System.Type pType, out object value)
            {
                value = null;
                if (argStr == "$")
                {
                    value = _debugLastResult;
                    return true;
                }
                if (pType == typeof(string))
                {
                    value = argStr;
                    return true;
                }
                if (pType == typeof(object))
                {
                    value = ParseLooseValue(argStr);
                    return true;
                }
                var nullable = System.Nullable.GetUnderlyingType(pType);
                if (nullable != null)
                {
                    if (string.Equals(argStr, "null", System.StringComparison.OrdinalIgnoreCase))
                    {
                        value = null;
                        return true;
                    }
                    pType = nullable;
                }
                if (pType.IsEnum)
                {
                    try
                    {
                        value = System.Enum.Parse(pType, argStr, true);
                        return true;
                    }
                    catch { return false; }
                }
                try
                {
                    if (pType == typeof(int)) value = int.Parse(argStr);
                    else if (pType == typeof(float)) value = float.Parse(argStr);
                    else if (pType == typeof(double)) value = double.Parse(argStr);
                    else if (pType == typeof(bool)) value = bool.Parse(argStr);
                    else if (pType == typeof(long)) value = long.Parse(argStr);
                    else if (pType == typeof(Vector3Int))
                    {
                        var c = argStr.Split(',');
                        value = new Vector3Int(int.Parse(c[0]), int.Parse(c[1]), int.Parse(c[2]));
                    }
                    else if (pType == typeof(Vector3))
                    {
                        var c = argStr.Split(',');
                        value = new Vector3(float.Parse(c[0]), float.Parse(c[1]), float.Parse(c[2]));
                    }
                    else if (pType == typeof(Vector2Int))
                    {
                        var c = argStr.Split(',');
                        value = new Vector2Int(int.Parse(c[0]), int.Parse(c[1]));
                    }
                    else
                        value = System.Convert.ChangeType(argStr, pType);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            // Resolve a dot-separated path from TimberbotService to any nested object.
            // Each segment can be:
            //   fieldName     -- reads a field (public or private via reflection)
            //   PropertyName  -- reads a property
            //   MethodName    -- calls a parameterless method
            //   [N]           -- indexes into IList or IEnumerable
            //   ~TypeName     -- finds a component by type name (like GetComponent<T>)
            //   $             -- starts from _debugLastResult instead of Service
            //
            // Examples:
            //   "_districtCenterRegistry.FinishedDistrictCenters.[0].AllComponents"
            //   "$.HasNode" (call method on last result)
            //   "_navMeshService.~NodeIdService" (find component by type)
            object Resolve(string path)
            {
                var parts = path.Split('.');
                object current = parts[0] == "$" ? _debugLastResult : (object)Service;
                if (parts[0] == "$") parts = parts.Skip(1).ToArray();
                foreach (var part in parts)
                {
                    if (current == null) return null;

                    // list/array indexing: [N]
                    if (part.StartsWith("[") && part.EndsWith("]"))
                    {
                        int idx = int.Parse(part.Substring(1, part.Length - 2));
                        if (current is System.Collections.IList list)
                        {
                            current = idx < list.Count ? list[idx] : null;
                        }
                        else if (current is System.Collections.IEnumerable enumerable)
                        {
                            int i = 0;
                            current = null;
                            foreach (var item in enumerable)
                            {
                                if (i == idx) { current = item; break; }
                                i++;
                            }
                        }
                        else return null;
                        continue;
                    }

                    // GetComponent<TypeName> syntax: ~TypeName
                    if (part.StartsWith("~"))
                    {
                        var typeName = part.Substring(1);
                        var getCompMethod = current.GetType().GetMethod("GetComponent",
                            System.Type.EmptyTypes);
                        // try finding the right generic overload by iterating AllComponents
                        var allCompsProp = current.GetType().GetProperty("AllComponents", flags);
                        if (allCompsProp != null)
                        {
                            var allComps = allCompsProp.GetValue(current) as System.Collections.IEnumerable;
                            if (allComps != null)
                            {
                                current = null;
                                foreach (var comp in allComps)
                                {
                                    if (comp.GetType().Name == typeName || comp.GetType().FullName.Contains(typeName))
                                    { current = comp; break; }
                                }
                            }
                        }
                        continue;
                    }

                    var t = current.GetType();
                    var field = t.GetField(part, flags);
                    if (field != null) { current = field.GetValue(current); continue; }
                    var prop = t.GetProperty(part, flags);
                    if (prop != null) { current = prop.GetValue(current); continue; }
                    // try as parameterless method
                    var method = t.GetMethod(part, flags, null, System.Type.EmptyTypes, null);
                    if (method != null) { current = method.Invoke(current, null); continue; }
                    return null;
                }
                return current;
            }

            bool TryGetNumeric(object value, out double numeric)
            {
                numeric = 0;
                if (value == null) return false;
                try
                {
                    if (value is bool b)
                    {
                        numeric = b ? 1 : 0;
                        return true;
                    }
                    if (value is System.IConvertible)
                    {
                        numeric = System.Convert.ToDouble(value);
                        return true;
                    }
                }
                catch { }
                return false;
            }

            bool ValuesEqual(object left, object right)
            {
                if (left == null || right == null) return left == right;
                if (TryGetNumeric(left, out var leftNum) && TryGetNumeric(right, out var rightNum))
                    return System.Math.Abs(leftNum - rightNum) < 0.0001;
                return Equals(left, right);
            }

            int CompareValues(object left, object right, out bool comparable)
            {
                comparable = false;
                if (TryGetNumeric(left, out var leftNum) && TryGetNumeric(right, out var rightNum))
                {
                    comparable = true;
                    return leftNum.CompareTo(rightNum);
                }
                if (left is string leftStr && right is string rightStr)
                {
                    comparable = true;
                    return string.Compare(leftStr, rightStr, System.StringComparison.Ordinal);
                }
                return 0;
            }

            string DescribeKind(object obj)
            {
                if (obj == null) return "null";
                var type = obj.GetType();
                if (obj is string) return "string";
                if (type.IsPrimitive || obj is decimal || type.IsEnum) return "scalar";
                if (obj is System.Collections.IDictionary) return "dictionary";
                if (obj is System.Collections.IEnumerable) return "enumerable";
                return "object";
            }

            Dictionary<string, object> DescribeValue(object obj, int depth, int maxDepth, int maxItems)
            {
                var result = new Dictionary<string, object>
                {
                    ["kind"] = DescribeKind(obj)
                };
                if (obj == null)
                {
                    result["type"] = "null";
                    result["value"] = null;
                    return result;
                }

                var type = obj.GetType();
                result["type"] = type.FullName;

                if (obj is string || type.IsPrimitive || obj is decimal || type.IsEnum)
                {
                    result["value"] = obj;
                    return result;
                }

                if (obj is System.Collections.IDictionary dict)
                {
                    var sample = new List<object>();
                    var count = 0;
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        count++;
                        if (sample.Count < maxItems)
                        {
                            sample.Add(new Dictionary<string, object>
                            {
                                ["key"] = entry.Key?.ToString(),
                                ["value"] = depth < maxDepth ? DescribeValue(entry.Value, depth + 1, maxDepth, maxItems) : (object)(entry.Value?.ToString())
                            });
                        }
                    }
                    result["count"] = count;
                    result["sample"] = sample;
                    return result;
                }

                if (obj is System.Collections.IEnumerable enumerable)
                {
                    var sample = new List<object>();
                    var count = 0;
                    foreach (var item in enumerable)
                    {
                        count++;
                        if (sample.Count < maxItems)
                            sample.Add(depth < maxDepth ? (object)DescribeValue(item, depth + 1, maxDepth, maxItems) : item?.ToString());
                    }
                    result["count"] = count;
                    result["sample"] = sample;
                    return result;
                }

                result["value"] = obj.ToString();
                return result;
            }

            Dictionary<string, object> DescribeMembers(object obj, string filter)
            {
                var members = new Dictionary<string, object>
                {
                    ["fields"] = new List<string>(),
                    ["properties"] = new List<string>(),
                    ["methods"] = new List<string>()
                };
                if (obj == null) return members;

                var fieldList = (List<string>)members["fields"];
                foreach (var f in obj.GetType().GetFields(flags))
                    if (string.IsNullOrEmpty(filter) || f.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        fieldList.Add($"{f.Name}:{f.FieldType.Name}");

                var propList = (List<string>)members["properties"];
                foreach (var p in obj.GetType().GetProperties(flags))
                    if (string.IsNullOrEmpty(filter) || p.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        propList.Add($"{p.Name}:{p.PropertyType.Name}");

                var methodList = (List<string>)members["methods"];
                foreach (var m in obj.GetType().GetMethods(flags))
                {
                    if (m.DeclaringType == typeof(object) || m.IsSpecialName) continue;
                    if (!string.IsNullOrEmpty(filter) && m.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var parms = m.GetParameters();
                    methodList.Add($"{m.Name}({string.Join(",", System.Linq.Enumerable.Select(parms, p => p.ParameterType.Name))})->{m.ReturnType.Name}");
                }
                return members;
            }

            void StoreAndDescribe(object obj, string path = null, int maxDepth = 1, int maxItems = 5)
            {
                _debugLastResult = obj;
                if (!string.IsNullOrEmpty(path)) info["path"] = path;
                info["result"] = DescribeValue(obj, 0, maxDepth, maxItems);
                info["stored"] = "result stored in $ for chaining";
            }

            bool EvaluateAssertion(object left, string op, object right, out string detail)
            {
                detail = "";
                op = (op ?? "eq").ToLowerInvariant();
                switch (op)
                {
                    case "eq":
                        return ValuesEqual(left, right);
                    case "ne":
                        return !ValuesEqual(left, right);
                    case "null":
                        return left == null;
                    case "not_null":
                        return left != null;
                    case "true":
                        return left is bool leftBool && leftBool;
                    case "false":
                        return left is bool leftBool2 && !leftBool2;
                    case "contains":
                        if (left is string leftStr && right != null) return leftStr.IndexOf(right.ToString(), System.StringComparison.OrdinalIgnoreCase) >= 0;
                        if (left is System.Collections.IEnumerable seq)
                        {
                            foreach (var item in seq)
                                if (ValuesEqual(item, right)) return true;
                            return false;
                        }
                        detail = "left is not searchable";
                        return false;
                    case "not_contains":
                        if (left is string leftStr2 && right != null) return leftStr2.IndexOf(right.ToString(), System.StringComparison.OrdinalIgnoreCase) < 0;
                        if (left is System.Collections.IEnumerable seq2)
                        {
                            foreach (var item in seq2)
                                if (ValuesEqual(item, right)) return false;
                            return true;
                        }
                        detail = "left is not searchable";
                        return false;
                    case "gt":
                    case "gte":
                    case "lt":
                    case "lte":
                        {
                            var cmp = CompareValues(left, right, out var comparable);
                            if (!comparable)
                            {
                                detail = "values are not comparable";
                                return false;
                            }
                            if (op == "gt") return cmp > 0;
                            if (op == "gte") return cmp >= 0;
                            if (op == "lt") return cmp < 0;
                            return cmp <= 0;
                        }
                    default:
                        detail = $"unknown op '{op}'";
                        return false;
                }
            }

            (System.Reflection.MethodInfo method, object[] callArgs, string error) ResolveMethodCall(object obj, string methodName)
            {
                var candidates = obj.GetType().GetMethods(flags)
                    .Where(m => m.Name == methodName && !m.IsSpecialName)
                    .OrderBy(m => m.GetParameters().Length)
                    .ToList();
                if (candidates.Count == 0)
                    return (null, null, $"not_found: method {methodName} not found on {obj.GetType().Name}");

                foreach (var method in candidates)
                {
                    var methodParams = method.GetParameters();
                    var callArgs = new object[methodParams.Length];
                    var ok = true;
                    for (int i = 0; i < methodParams.Length; i++)
                    {
                        if (!args.ContainsKey($"arg{i}"))
                        {
                            if (methodParams[i].HasDefaultValue)
                                callArgs[i] = methodParams[i].DefaultValue;
                            else
                            {
                                ok = false;
                                break;
                            }
                        }
                        else if (!TryParseArg(Arg($"arg{i}", ""), methodParams[i].ParameterType, out callArgs[i]))
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (!ok) continue;

                    var extraArg = false;
                    for (int i = methodParams.Length; args.ContainsKey($"arg{i}"); i++)
                        extraArg = true;
                    if (extraArg) continue;

                    return (method, callArgs, null);
                }

                return (null, null, $"invalid_param: no overload of {methodName} matched supplied args");
            }

            try
            {
                switch (target)
                {
                    case "help":
                        info["targets"] = new[]
                        {
                            "help -- this message",
                            "get -- navigate object chain. args: path (dot-separated from TimberbotService)",
                            "fields -- list members. args: path, filter",
                            "describe -- structured type + value + members. args: path, filter, depth, sample",
                            "call -- call a method. args: path (to object), method, arg0..argN (typed args)",
                            "compare -- compare left/right paths or left path vs value. args: left, right|value",
                            "assert -- assert a condition on a path. args: path, op, right|value",
                            "roots -- list top-level TimberbotService fields",
                        };
                        info["roots"] = Service.GetType().GetFields(flags)
                            .Where(f => !f.IsStatic)
                            .Select(f => $"{f.Name}:{f.FieldType.Name}")
                            .OrderBy(n => n)
                            .ToArray();
                        info["examples"] = new[]
                        {
                            "debug target:fields path:Read filter:Collect",
                            "debug target:describe path:Cache.Buildings depth:2 sample:3",
                            "debug target:get path:_scienceService.SciencePoints",
                            "debug target:call path:_navMeshService method:AreConnectedRoadInstant arg0:120,142,2 arg1:130,142,2",
                            "debug target:compare left:Cache.Buildings.Read.[0].Id right:Cache.Buildings.Write.[0].Id",
                            "debug target:assert path:_debugEnabled op:false",
                        };
                        break;

                    case "roots":
                        info["roots"] = Service.GetType().GetFields(flags)
                            .Where(f => !f.IsStatic)
                            .Select(f => new Dictionary<string, object>
                            {
                                ["name"] = f.Name,
                                ["type"] = f.FieldType.FullName
                            })
                            .OrderBy(f => (string)f["name"])
                            .ToList();
                        break;

                    case "get":
                        {
                            var path = Arg("path", "");
                            if (string.IsNullOrEmpty(path)) return _jw.Error("invalid_param: pass path:_fieldName.nested.field");
                            var obj = Resolve(path);
                            StoreAndDescribe(obj, path,
                                int.TryParse(Arg("depth", "1"), out var getDepth) ? getDepth : 1,
                                int.TryParse(Arg("sample", "5"), out var getSample) ? getSample : 5);
                            break;
                        }

                    case "fields":
                        {
                            var path = Arg("path", "");
                            object obj = string.IsNullOrEmpty(path) ? (object)Service : Resolve(path);
                            if (obj == null) return _jw.Error($"not_found: could not resolve '{path}'");
                            info["path"] = path;
                            info["type"] = obj.GetType().FullName;
                            var filter = Arg("filter", "");
                            info["members"] = DescribeMembers(obj, filter);
                            break;
                        }

                    case "describe":
                        {
                            var path = Arg("path", "");
                            object obj = string.IsNullOrEmpty(path) ? (object)Service : Resolve(path);
                            if (obj == null) return _jw.Error($"not_found: could not resolve '{path}'");
                            var filter = Arg("filter", "");
                            var depth = int.TryParse(Arg("depth", "1"), out var describeDepth) ? describeDepth : 1;
                            var sample = int.TryParse(Arg("sample", "5"), out var describeSample) ? describeSample : 5;
                            info["path"] = path;
                            info["result"] = DescribeValue(obj, 0, depth, sample);
                            info["members"] = DescribeMembers(obj, filter);
                            break;
                        }

                    case "call":
                        {
                            var path = Arg("path", "");
                            var methodName = Arg("method", "");
                            if (string.IsNullOrEmpty(methodName)) return _jw.Error("invalid_param: pass method:MethodName");
                            object obj = string.IsNullOrEmpty(path) ? (object)Service : Resolve(path);
                            if (obj == null) return _jw.Error($"not_found: could not resolve '{path}'");
                            var resolved = ResolveMethodCall(obj, methodName);
                            if (resolved.error != null) return _jw.Error(resolved.error);
                            var result = resolved.method.Invoke(obj, resolved.callArgs);
                            info["path"] = path;
                            info["method"] = $"{resolved.method.Name}({string.Join(",", System.Linq.Enumerable.Select(resolved.method.GetParameters(), p => p.ParameterType.Name))})";
                            StoreAndDescribe(result, null,
                                int.TryParse(Arg("depth", "1"), out var callDepth) ? callDepth : 1,
                                int.TryParse(Arg("sample", "5"), out var callSample) ? callSample : 5);
                            break;
                        }

                    case "compare":
                        {
                            var leftPath = Arg("left", Arg("path", ""));
                            if (string.IsNullOrEmpty(leftPath)) return _jw.Error("invalid_param: pass left:path.to.value");
                            var rightPath = Arg("right", "");
                            var hasValue = args.ContainsKey("value");
                            if (string.IsNullOrEmpty(rightPath) && !hasValue) return _jw.Error("invalid_param: pass right:path.to.value or value:literal");
                            var left = Resolve(leftPath);
                            object right = hasValue ? ParseLooseValue(Arg("value", "")) : Resolve(rightPath);
                            info["leftPath"] = leftPath;
                            if (!string.IsNullOrEmpty(rightPath)) info["rightPath"] = rightPath;
                            info["equal"] = ValuesEqual(left, right);
                            info["left"] = DescribeValue(left, 0, 1, 5);
                            info["right"] = DescribeValue(right, 0, 1, 5);
                            break;
                        }

                    case "assert":
                        {
                            var path = Arg("path", "");
                            if (string.IsNullOrEmpty(path)) return _jw.Error("invalid_param: pass path:path.to.value");
                            var op = Arg("op", "eq");
                            var left = Resolve(path);
                            object right = null;
                            if (args.ContainsKey("right"))
                                right = Resolve(Arg("right", ""));
                            else if (args.ContainsKey("value"))
                                right = ParseLooseValue(Arg("value", ""));
                            var passed = EvaluateAssertion(left, op, right, out var detail);
                            info["path"] = path;
                            info["op"] = op;
                            info["ok"] = passed;
                            info["left"] = DescribeValue(left, 0, 1, 5);
                            info["right"] = DescribeValue(right, 0, 1, 5);
                            if (!string.IsNullOrEmpty(detail)) info["detail"] = detail;
                            break;
                        }

                    case "validate":
                        {
                            int valId = int.Parse(Arg("id", "0"));
                            return ValidateEntity(valId);
                        }

                    case "validate_all":
                        {
                            return ValidateAll();
                        }

                    default:
                        return _jw.Error($"invalid_param: unknown target '{target}'. use: help, roots, get, fields, describe, call, compare, assert, validate, validate_all");
                }
            }
            catch (System.Exception ex)
            {
                return _jw.Error("internal_error: " + ex.Message.Replace("\"", "'").Replace("\r", "").Replace("\n", " | "));
            }
            // serialize the info dict via JW (Prop(string, object) handles complex values)
            var jw = Service.Cache.Jw.BeginObj();
            foreach (var kvp in info)
                jw.Prop(kvp.Key, kvp.Value);
            return jw.End();
        }

        // ValidatePlacement lives in TimberbotService.Placement.cs (used by PlaceBuilding)

        // ================================================================
        // VALIDATE -- data accuracy verification.
        //
        // The double-buffer cache (TimberbotEntityCache) is refreshed every ~1 second.
        // This validator reads the CACHED value from the Read buffer AND the LIVE value
        // directly from the Unity component, then compares them field by field.
        //
        // Why this matters: if the cache is wrong, the AI makes decisions based on stale
        // or incorrect data. The validator catches these bugs before production.
        //
        // Numeric comparison uses Convert.ToDouble on both sides because the cache may
        // store an int (e.g. wellbeing=19) while the live component returns float (19.0f).
        // A tolerance of 0.5 handles rounding from Math.Round in the cache.
        // ================================================================

        private object ValidateEntity(int id)
        {
            var fields = new Dictionary<string, object>();
            int mismatches = 0, total = 0;

            void Add(string name, object cached, object live)
            {
                bool match = Equals(cached, live);
                // numeric comparison: convert both to double for cross-type matching (int vs float, etc)
                if (!match && cached is System.IConvertible && live is System.IConvertible)
                {
                    try
                    {
                        double dc = System.Convert.ToDouble(cached);
                        double dl = System.Convert.ToDouble(live);
                        match = System.Math.Abs(dc - dl) < 0.5;
                    }
                    catch { }
                }
                fields[name] = new { cached, live, match };
                total++;
                if (!match) mismatches++;
            }

            // check buildings
            var buildings = Service.Cache.Buildings.Read;
            for (int i = 0; i < buildings.Count; i++)
            {
                var c = buildings[i];
                if (c.Id != id) continue;

                // found cached building -- now read live state from components
                var bo = c.BlockObject;
                if (bo != null)
                {
                    var coords = bo.Coordinates;
                    Add("x", c.X, coords.x);
                    Add("y", c.Y, coords.y);
                    Add("z", c.Z, coords.z);
                    Add("finished", c.Finished, bo.IsFinished);
                }
                if (c.Pausable != null)
                    Add("paused", c.Paused, c.Pausable.Paused);
                if (c.Workplace != null)
                {
                    Add("assignedWorkers", c.AssignedWorkers, c.Workplace.NumberOfAssignedWorkers);
                    Add("desiredWorkers", c.DesiredWorkers, c.Workplace.DesiredWorkers);
                    Add("maxWorkers", c.MaxWorkers, c.Workplace.MaxWorkers);
                }
                if (c.Dwelling != null)
                {
                    Add("dwellers", c.Dwellers, c.Dwelling.NumberOfDwellers);
                    Add("maxDwellers", c.MaxDwellers, c.Dwelling.MaxBeavers);
                }
                if (c.Mechanical != null)
                    Add("powered", c.Powered, c.Mechanical.ActiveAndPowered);
                if (c.Floodgate != null)
                    Add("floodgateHeight", c.FloodgateHeight, c.Floodgate.Height);
                if (c.Clutch != null)
                    Add("clutchEngaged", c.ClutchEngaged, c.Clutch.IsEngaged);
                if (c.Wonder != null)
                    Add("wonderActive", c.WonderActive, c.Wonder.IsActive);
                Add("name", c.Name, c.Entity != null ? TimberbotEntityCache.CleanName(c.Entity.GameObject.name) : "?");

                return Service.Cache.Jw.Result(("id", id), ("type", "building"), ("name", (c.Name)), ("fields", fields), ("mismatches", mismatches), ("total", total));
            }

            // check beavers
            var beavers = Service.Cache.Beavers.Read;
            for (int i = 0; i < beavers.Count; i++)
            {
                var c = beavers[i];
                if (c.Id != id) continue;

                if (c.WbTracker != null)
                    Add("wellbeing", c.Wellbeing, c.WbTracker.Wellbeing);
                if (c.Citizen != null && c.Citizen.AssignedDistrict != null)
                    Add("district", c.District, c.Citizen.AssignedDistrict.DistrictName);
                if (c.Go != null)
                {
                    var pos = c.Go.transform.position;
                    Add("x", c.X, Mathf.FloorToInt(pos.x));
                    Add("y", c.Y, Mathf.FloorToInt(pos.z));
                    Add("z", c.Z, Mathf.FloorToInt(pos.y));
                }
                var wp = c.Worker?.Workplace;
                Add("workplace", c.Workplace ?? "", wp != null ? TimberbotEntityCache.CleanName(wp.GameObject.name) : "");

                return Service.Cache.Jw.Result(("id", id), ("type", "beaver"), ("name", (c.Name)), ("fields", fields), ("mismatches", mismatches), ("total", total));
            }

            // check natural resources
            var natRes = Service.Cache.NaturalResources.Read;
            for (int i = 0; i < natRes.Count; i++)
            {
                var c = natRes[i];
                if (c.Id != id) continue;

                if (c.BlockObject != null)
                {
                    var coords = c.BlockObject.Coordinates;
                    Add("x", c.X, coords.x);
                    Add("y", c.Y, coords.y);
                    Add("z", c.Z, coords.z);
                }
                if (c.Living != null)
                    Add("alive", c.Alive, !c.Living.IsDead);
                if (c.Growable != null)
                {
                    Add("grown", c.Grown, c.Growable.IsGrown);
                    Add("growth", c.Growth, c.Growable.GrowthProgress);
                }
                Add("name", c.Name, c.Name);

                return Service.Cache.Jw.Result(("id", id), ("type", "naturalResource"), ("name", (c.Name)), ("fields", fields), ("mismatches", mismatches), ("total", total));
            }

            return Service.Cache.Jw.Error("not_found", ("id", id));
        }

        // Validate every entity in the cache against live game state.
        // Returns aggregate stats (entities, fields, mismatches) and only includes
        // entities with mismatches in the failures list (to keep response small).
        // Used by test_validation.py to verify data accuracy across the entire colony.
        private object ValidateAll()
        {
            var results = new List<object>();
            int totalEntities = 0, totalFields = 0, totalMismatches = 0;

            // validate all buildings
            var buildings = Service.Cache.Buildings.Read;
            for (int i = 0; i < buildings.Count; i++)
            {
                var result = ValidateEntity(buildings[i].Id);
                totalEntities++;
                if (result is Dictionary<string, object>) continue; // error
                var rt = result.GetType();
                var mm = (int)rt.GetProperty("mismatches").GetValue(result);
                var tf = (int)rt.GetProperty("total").GetValue(result);
                totalFields += tf;
                totalMismatches += mm;
                if (mm > 0) results.Add(result);
            }

            // validate all beavers
            var beavers = Service.Cache.Beavers.Read;
            for (int i = 0; i < beavers.Count; i++)
            {
                var result = ValidateEntity(beavers[i].Id);
                totalEntities++;
                var rt = result.GetType();
                var mmProp = rt.GetProperty("mismatches");
                if (mmProp == null) continue;
                var mm = (int)mmProp.GetValue(result);
                var tf = (int)rt.GetProperty("total").GetValue(result);
                totalFields += tf;
                totalMismatches += mm;
                if (mm > 0) results.Add(result);
            }

            // validate all natural resources
            var natRes = Service.Cache.NaturalResources.Read;
            for (int i = 0; i < natRes.Count; i++)
            {
                var result = ValidateEntity(natRes[i].Id);
                totalEntities++;
                var rt = result.GetType();
                var mmProp = rt.GetProperty("mismatches");
                if (mmProp == null) continue;
                var mm = (int)mmProp.GetValue(result);
                var tf = (int)rt.GetProperty("total").GetValue(result);
                totalFields += tf;
                totalMismatches += mm;
                if (mm > 0) results.Add(result);
            }

            // validate districts (cached vs live)
            try
            {
                var cachedDistricts = Service.Cache.Districts;
                var liveDistricts = new Dictionary<string, (int adults, int children, int bots)>();
                foreach (var dc in Service.Cache.DistrictRegistry.FinishedDistrictCenters)
                {
                    var pop = dc.DistrictPopulation;
                    liveDistricts[dc.DistrictName] = (
                        pop != null ? pop.NumberOfAdults : 0,
                        pop != null ? pop.NumberOfChildren : 0,
                        pop != null ? pop.NumberOfBots : 0);
                }
                foreach (var cd in cachedDistricts)
                {
                    totalEntities++;
                    var fields = new Dictionary<string, object>();
                    int mm = 0, tf = 0;
                    void AddD(string name, object cached, object live)
                    {
                        bool match = Equals(cached, live);
                        if (!match && cached is int ci && live is int li) match = ci == li;
                        fields[name] = new { cached, live, match };
                        tf++;
                        if (!match) mm++;
                    }
                    if (liveDistricts.TryGetValue(cd.Name, out var live))
                    {
                        AddD("adults", cd.Adults, live.adults);
                        AddD("children", cd.Children, live.children);
                        AddD("bots", cd.Bots, live.bots);
                    }
                    else
                    {
                        AddD("exists", false, true);
                    }
                    totalFields += tf;
                    totalMismatches += mm;
                    if (mm > 0) results.Add(Service.Cache.Jw.Result(("type", "district"), ("name", cd.Name), ("fields", fields), ("mismatches", mm), ("total", tf)));
                }
            }
            catch (System.Exception _ex) { TimberbotLog.Error("validate.districts", _ex); }

            return Service.Cache.Jw.Result(("entities", totalEntities), ("fields", totalFields),
                ("mismatches", totalMismatches), ("failures", results));
        }
    }
}
