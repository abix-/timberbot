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
            results.Add(BenchCall("CollectTiles.20x20", nHeavy, () => Service.Read.CollectTiles(120, 130, 140, 150), 400));
            results.Add(BenchCall("FindPlacement", nHeavy, () => Service.Placement.FindPlacement("Path", 120, 135, 130, 145)));

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
        // DEBUG -- reflection-based game state inspector
        // ================================================================
        // Walk any game object graph at runtime using .NET reflection.
        // This is the "god mode" inspector -- it can read/write any field, call any
        // method, and chain results using $ (last result). Gated behind settings.json
        // because it can crash the game if you call the wrong method.
        //
        // Targets:
        //   help    -- list available root objects and examples
        //   get     -- resolve a dot-path and return the value (e.g. "_scienceService.SciencePoints")
        //   fields  -- list all fields/properties/methods on an object (with optional filter)
        //   call    -- call a method with typed arguments (auto-parses int, float, Vector3Int, etc.)
        //   validate     -- compare cached vs live state for one entity
        //   validate_all -- compare cached vs live for ALL entities
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

            // Parse a string argument into a typed .NET object.
            // Supports primitives (int, float, bool, string) and Unity types (Vector3Int, Vector3, Vector2Int).
            // $ = reference to _debugLastResult for chaining calls.
            object ParseArg(string argStr, System.Type pType)
            {
                if (argStr == "$") return _debugLastResult;
                if (pType == typeof(string)) return argStr;
                if (pType == typeof(int)) return int.Parse(argStr);
                if (pType == typeof(float)) return float.Parse(argStr);
                if (pType == typeof(double)) return double.Parse(argStr);
                if (pType == typeof(bool)) return bool.Parse(argStr);
                if (pType == typeof(long)) return long.Parse(argStr);
                if (pType == typeof(Vector3Int))
                {
                    var c = argStr.Split(',');
                    return new Vector3Int(int.Parse(c[0]), int.Parse(c[1]), int.Parse(c[2]));
                }
                if (pType == typeof(Vector3))
                {
                    var c = argStr.Split(',');
                    return new Vector3(float.Parse(c[0]), float.Parse(c[1]), float.Parse(c[2]));
                }
                if (pType == typeof(Vector2Int))
                {
                    var c = argStr.Split(',');
                    return new Vector2Int(int.Parse(c[0]), int.Parse(c[1]));
                }
                // try Convert.ChangeType as fallback
                try { return System.Convert.ChangeType(argStr, pType); } catch (System.Exception _ex) { TimberbotLog.Error("debug", _ex); }
                return null;
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

            // dump an object's fields and properties
            void DumpObject(object obj, Dictionary<string, object> into, int maxItems = 5)
            {
                if (obj == null) { into["value"] = "null"; return; }
                into["type"] = obj.GetType().FullName;
                if (obj is string s) { into["value"] = s; return; }
                if (obj is System.Collections.IEnumerable enumerable)
                {
                    int count = 0;
                    var samples = new List<string>();
                    foreach (var item in enumerable)
                    {
                        count++;
                        if (samples.Count < maxItems) samples.Add(item?.ToString() ?? "null");
                    }
                    into["count"] = count;
                    into["samples"] = samples;
                    return;
                }
                into["value"] = obj.ToString();
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
                            "call -- call method. args: path (to object), method, arg0..argN (string args, Vector3Int as x,y,z)",
                        };
                        info["roots"] = new[]
                        {
                            "_buildingService", "_entityRegistry", "_districtCenterRegistry",
                            "_navMeshService", "_soilMoistureService", "_toolButtonService",
                            "_blockObjectPlacerService", "_scienceService", "_buildingUnlockingService",
                            "_districtPathNavRegistrar", "_toolUnlockingService"
                        };
                        info["examples"] = new[]
                        {
                            "debug target:fields path:_navMeshService filter:Road",
                            "debug target:get path:_scienceService.SciencePoints",
                            "debug target:call path:_navMeshService method:AreConnectedRoadInstant arg0:120,142,2 arg1:130,142,2",
                        };
                        break;

                    case "get":
                        {
                            var path = Arg("path", "");
                            if (string.IsNullOrEmpty(path)) return _jw.Error("invalid_param: pass path:_fieldName.nested.field");
                            var obj = Resolve(path);
                            _debugLastResult = obj;
                            info["path"] = path;
                            DumpObject(obj, info);
                            break;
                        }

                    case "fields":
                        {
                            var path = Arg("path", "");
                            object obj = string.IsNullOrEmpty(path) ? (object)Service : Resolve(path);
                            if (obj == null) return _jw.Error($"not_found: could not resolve '{path}'");
                            info["type"] = obj.GetType().FullName;
                            var filter = Arg("filter", "");
                            var members = new List<string>();
                            foreach (var f in obj.GetType().GetFields(flags))
                                if (string.IsNullOrEmpty(filter) || f.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                                    members.Add($"F {f.Name}:{f.FieldType.Name}");
                            foreach (var p in obj.GetType().GetProperties(flags))
                                if (string.IsNullOrEmpty(filter) || p.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                                    members.Add($"P {p.Name}:{p.PropertyType.Name}");
                            foreach (var m in obj.GetType().GetMethods(flags))
                            {
                                if (m.DeclaringType == typeof(object) || m.IsSpecialName) continue;
                                if (!string.IsNullOrEmpty(filter) && m.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                                var parms = m.GetParameters();
                                members.Add($"M {m.Name}({string.Join(",", System.Linq.Enumerable.Select(parms, p => p.ParameterType.Name))})->{m.ReturnType.Name}");
                            }
                            info["members"] = members;
                            break;
                        }

                    case "call":
                        {
                            var path = Arg("path", "");
                            var methodName = Arg("method", "");
                            if (string.IsNullOrEmpty(methodName)) return _jw.Error("invalid_param: pass method:MethodName");
                            object obj = string.IsNullOrEmpty(path) ? (object)Service : Resolve(path);
                            if (obj == null) return _jw.Error($"not_found: could not resolve '{path}'");
                            // find all overloads
                            var methods = obj.GetType().GetMethods(flags);
                            System.Reflection.MethodInfo bestMethod = null;
                            foreach (var m in methods)
                                if (m.Name == methodName) { bestMethod = m; break; }
                            if (bestMethod == null) return _jw.Error($"not_found: method {methodName} not found on {obj.GetType().Name}");
                            // build args from arg0, arg1, etc
                            var methodParams = bestMethod.GetParameters();
                            var callArgs = new object[methodParams.Length];
                            for (int i = 0; i < methodParams.Length; i++)
                            {
                                var argStr = Arg($"arg{i}", "");
                                callArgs[i] = ParseArg(argStr, methodParams[i].ParameterType);
                            }
                            var result = bestMethod.Invoke(obj, callArgs);
                            _debugLastResult = result;
                            DumpObject(result, info);
                            info["stored"] = "result stored in $ for chaining";
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
                        return _jw.Error($"invalid_param: unknown target '{target}'. use: help, get, fields, call, validate, validate_all");
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
