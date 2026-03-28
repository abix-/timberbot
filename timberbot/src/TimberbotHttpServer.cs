// TimberbotHttpServer.cs -- HTTP server and request routing.
//
// Runs an HttpListener on a background thread (port 8085 by default).
// Threading model:
//   GET requests  -> served directly on the listener thread from ReadV2 snapshots
//   POST requests -> queued to ConcurrentQueue, drained on Unity main thread (max 10/frame)
//
// This split exists because Unity game services are single-threaded. GET endpoints
// read only published snapshots or explicit thread-safe game services, while POST
// endpoints call live game services on the main thread.
//
// All responses are JSON. The server serializes whatever object TimberbotService
// returns using Newtonsoft.Json. TOON format is handled by TimberbotService returning
// pre-built strings for endpoints that support it.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Timberbot
{
    // HTTP server on port 8085. Background listener thread queues requests,
    // Unity main thread drains them (max 10/frame). ping + speed answered on listener thread.
    // format param: ?format=toon (default) = flat, ?format=json = full nested
    class TimberbotHttpServer
    {
        private readonly TimberbotService _service;
        private readonly HttpListener _listener;
        private readonly Thread _listenerThread;
        // thread-safe queue: background listener thread enqueues, Unity main thread dequeues
        private readonly ConcurrentQueue<PendingRequest> _pending = new ConcurrentQueue<PendingRequest>();
        private readonly Queue<PendingRequest> _writeQueue = new Queue<PendingRequest>();
        private PendingRequest _activeWriteRequest;
        private ITimberbotWriteJob _activeWriteJob;
        private readonly Dictionary<string, PostRouteDescriptor> _postRoutes;
        private long _nextRequestId;
        // volatile: read by main thread, written by Stop(). No lock needed for bool.
        private volatile bool _running;
        private readonly bool _debugEnabled;
        // separate JW for HTTP-layer errors (thread-safe: not shared with read/write paths)
        private readonly TimberbotJw _jw = new TimberbotJw(512);

        class PostRouteDescriptor
        {
            public string Path;
            public bool Queued;
            public Func<PendingRequest, ITimberbotWriteJob> JobFactory;
        }

        // Captures everything needed to process a POST request on the main thread.
        // The JSON body is parsed on the listener thread (cheap) so the main thread
        // only does game state mutations (expensive, must be single-threaded).
        class PendingRequest
        {
            public HttpListenerContext Context;  // HTTP response handle
            public string Route;                 // e.g. "/api/building/pause"
            public string Method;                // "POST" (GET never queued)
            public JObject Body;                 // parsed JSON body (null if no body)
            public string Format;                // "toon" or "json" (response format)
            public string Detail;                // "basic" or "full" (response detail level)
            public int Id;                       // single-entity targeting for collection reads / entity POSTs
            public int Limit;                    // max items to return (0 = unlimited, default 100)
            public int Offset;                   // skip first N items
            public string FilterName;            // name substring filter (case-insensitive)
            public int FilterX, FilterY, FilterRadius; // proximity filter (Manhattan distance)
            public long QueuedAtTicks;           // listener-thread enqueue timestamp
            public int QueuedAtFrame;            // main-thread queue admission frame
            public long RequestId;               // correlated request lifecycle logging
            public PostRouteDescriptor PostRoute;
        }

        public TimberbotHttpServer(int port, TimberbotService service, bool debugEnabled = false)
        {
            _service = service;
            _debugEnabled = debugEnabled;
            _postRoutes = BuildPostRoutes();
            _listener = new HttpListener();

            // Try wildcard binding first (http://+:port/) which accepts connections
            // from any interface (LAN, WSL, etc). Requires admin/URL reservation on Windows.
            // Falls back to localhost-only if that fails (no admin needed).
            try
            {
                _listener.Prefixes.Add($"http://+:{port}/");
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                TimberbotLog.Info($"port +:{port} failed, falling back to localhost");
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
            }

            _running = true;
            _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "Timberbot-HTTP" };
            _listenerThread.Start();
        }

        public void Stop()
        {
            _running = false;
            FailOutstanding("operation_failed: server_stopped");
            try { _listener?.Stop(); } catch { }
        }

        // Called every frame from UpdateSingleton on the Unity main thread.
        // Admits up to 10 queued POST requests per frame.
        public void DrainRequests()
        {
            int processed = 0;
            while (processed < 10 && _pending.TryDequeue(out var req))
            {
                processed++;
                try
                {
                    LogRequest("req.drain", req, $"pending={_pending.Count} writeQueue={_writeQueue.Count} active={(_activeWriteJob != null ? _activeWriteJob.Name : "none")}");
                    if (req.PostRoute == null)
                    {
                        TimberbotLog.Info($"req.unknown id={req.RequestId} route={req.Route}");
                        RespondAsync(req.Context, 200, UnknownEndpoint(), req.RequestId, req.Route);
                    }
                    else
                    {
                        req.QueuedAtFrame = UnityEngine.Time.frameCount;
                        _writeQueue.Enqueue(req);
                        LogRequest("req.queue", req, $"writeQueue={_writeQueue.Count} pending={_pending.Count}");
                    }
                }
                catch (Exception ex)
                {
                    TimberbotLog.Error("route.post", ex);
                    RespondAsync(req.Context, 500, _jw.Error("internal_error: " + ex.Message.Replace("\"", "'").Replace("\r", "").Replace("\n", " | ")), req.RequestId, req.Route);
                }
            }
        }

        // Called every frame. Steps the active write job forward until it completes
        // or the per-frame budget (default 1ms) runs out. When the job completes,
        // sends the HTTP response back to the waiting client.
        public void ProcessWriteJobs(float now, double budgetMs)
        {
            if (!_running) return;

            if (_activeWriteJob == null && _writeQueue.Count > 0)
            {
                _activeWriteRequest = _writeQueue.Dequeue();
                try
                {
                    _activeWriteJob = _activeWriteRequest.PostRoute?.JobFactory?.Invoke(_activeWriteRequest);
                    if (_activeWriteJob == null)
                    {
                        TimberbotLog.Info($"req.start.null id={_activeWriteRequest.RequestId} route={_activeWriteRequest.Route}");
                        RespondAsync(_activeWriteRequest.Context, 200, UnknownEndpoint(), _activeWriteRequest.RequestId, _activeWriteRequest.Route);
                        _activeWriteRequest = null;
                    }
                    else
                    {
                        LogRequest("req.start", _activeWriteRequest, $"job={_activeWriteJob.Name} writeQueue={_writeQueue.Count}");
                    }
                }
                catch (Exception ex)
                {
                    TimberbotLog.Error("write.admit", ex);
                    TimberbotLog.Info($"req.start.fail id={_activeWriteRequest.RequestId} route={_activeWriteRequest.Route} ex={ex.GetType().Name}:{ex.Message}");
                    RespondAsync(_activeWriteRequest.Context, 500, _jw.Error("internal_error: " + ex.Message.Replace("\"", "'").Replace("\r", "").Replace("\n", " | ")), _activeWriteRequest.RequestId, _activeWriteRequest.Route);
                    _activeWriteRequest = null;
                    _activeWriteJob = null;
                }
            }

            if (_activeWriteJob == null) return;

            try
            {
                _activeWriteJob.Step(now, budgetMs);
                if (_activeWriteJob.IsCompleted)
                {
                    LogRequest("req.done", _activeWriteRequest, $"job={_activeWriteJob.Name} status={_activeWriteJob.StatusCode}");
                    RespondAsync(_activeWriteRequest.Context, _activeWriteJob.StatusCode, _activeWriteJob.Result, _activeWriteRequest.RequestId, _activeWriteRequest.Route);
                    _activeWriteRequest = null;
                    _activeWriteJob = null;
                }
            }
            catch (Exception ex)
            {
                TimberbotLog.Error("write.step", ex);
                TimberbotLog.Info($"req.step.fail id={_activeWriteRequest.RequestId} route={_activeWriteRequest.Route} job={_activeWriteJob?.Name ?? "null"} ex={ex.GetType().Name}:{ex.Message}");
                RespondAsync(_activeWriteRequest.Context, 500, _jw.Error("internal_error: " + ex.Message.Replace("\"", "'").Replace("\r", "").Replace("\n", " | ")), _activeWriteRequest.RequestId, _activeWriteRequest.Route);
                _activeWriteRequest = null;
                _activeWriteJob = null;
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    if (_debugEnabled) TimberbotLog.Info($"listen.waiting {ThreadPoolState()}");
                    ctx = _listener.GetContext();
                }
                catch
                {
                    if (!_running) break;
                    continue;
                }

                var path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                var method = ctx.Request.HttpMethod.ToUpperInvariant();
                if (_debugEnabled) TimberbotLog.Info($"listen.accepted path={path} method={method} {ThreadPoolState()}");

                if (path == "/api/ping")
                {
                    Respond(ctx, 200, "{\"status\":\"ok\",\"ready\":true}");
                    continue;
                }
                if (path == "/api/settlement")
                {
                    Respond(ctx, 200, "{\"name\":\"" + _service.ReadV2.GetSettlementName().Replace("\"", "\\\"") + "\"}");
                    continue;
                }

                // format: "toon" = flat key:value pairs (default, human-readable)
                //         "json" = full nested JSON (machine-parseable)
                // detail: "basic" = compact fields, "full" = all fields including inventory/needs
                var format = ctx.Request.QueryString["format"] ?? "toon";
                var detail = ctx.Request.QueryString["detail"] ?? "basic";
                int.TryParse(ctx.Request.QueryString["id"], out int id);
                // pagination: limit=100 default (0=unlimited), offset=0 default
                int.TryParse(ctx.Request.QueryString["limit"], out int limit);
                int.TryParse(ctx.Request.QueryString["offset"], out int offset);
                if (ctx.Request.QueryString["limit"] == null) limit = 100;
                // server-side filtering: name (substring), x/y/radius (proximity)
                var filterName = ctx.Request.QueryString["name"];
                int.TryParse(ctx.Request.QueryString["x"], out int filterX);
                int.TryParse(ctx.Request.QueryString["y"], out int filterY);
                int.TryParse(ctx.Request.QueryString["radius"], out int filterRadius);
                int.TryParse(ctx.Request.QueryString["x1"], out int tileX1);
                int.TryParse(ctx.Request.QueryString["y1"], out int tileY1);
                int.TryParse(ctx.Request.QueryString["x2"], out int tileX2);
                int.TryParse(ctx.Request.QueryString["y2"], out int tileY2);
                // GET requests: handled on the background listener thread and served from
                // ReadV2's published snapshots or explicit thread-safe game services.
                if (method == "GET")
                {
                    try
                    {
                        var data = path == "/api/tiles"
                            ? _service.ReadV2.CollectTiles(format, tileX1, tileY1, tileX2, tileY2)
                            : RouteReadRequest(path, format, detail, id, limit, offset, filterName, filterX, filterY, filterRadius);
                        Respond(ctx, 200, data);
                    }
                    catch (Exception ex)
                    {
                        TimberbotLog.Error("route.get", ex);
                        Respond(ctx, 500, _jw.Error("internal_error: " + ex.Message.Replace("\"", "'").Replace("\r", "").Replace("\n", " | ")));
                    }
                    continue;
                }

                // POST requests: can't execute on this thread because Unity game services
                // are single-threaded. Instead, we parse the JSON body here, then queue the
                // request to a ConcurrentQueue. The main thread drains up to 10 queued requests
                // per frame in DrainRequests() (called from UpdateSingleton).
                JObject body = null;
                if (ctx.Request.HasEntityBody)
                {
                    try
                    {
                        if (_debugEnabled) TimberbotLog.Info($"listen.body.read path={path}");
                        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                        {
                            var raw = reader.ReadToEnd();
                            body = JObject.Parse(raw);
                        }
                        if (_debugEnabled) TimberbotLog.Info($"listen.body.done path={path}");
                    }
                    catch
                    {
                        Respond(ctx, 400, _jw.Error("invalid_body"));
                        continue;
                    }
                }

                // POST requests can override format/detail/id/limit/offset in the JSON body too
                // (body takes priority over query string)
                format = body?.Value<string>("format") ?? format;
                detail = body?.Value<string>("detail") ?? detail;
                if (TryReadBodyInt(body, "id", out int bodyId)) id = bodyId;
                if (TryReadBodyInt(body, "limit", out int bodyLimit)) limit = bodyLimit;
                if (TryReadBodyInt(body, "offset", out int bodyOffset)) offset = bodyOffset;
                if (body?["name"] != null) filterName = body.Value<string>("name");
                if (TryReadBodyInt(body, "x", out int bodyX)) filterX = bodyX;
                if (TryReadBodyInt(body, "y", out int bodyY)) filterY = bodyY;
                if (TryReadBodyInt(body, "radius", out int bodyRadius)) filterRadius = bodyRadius;

                var req = new PendingRequest
                {
                    Context = ctx,
                    Route = path,
                    Method = method,
                    Body = body,
                    Format = format,
                    Detail = detail,
                    Id = id,
                    Limit = limit,
                    Offset = offset,
                    FilterName = filterName,
                    FilterX = filterX,
                    FilterY = filterY,
                    FilterRadius = filterRadius,
                    QueuedAtTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
                    RequestId = System.Threading.Interlocked.Increment(ref _nextRequestId),
                    PostRoute = ResolvePostRoute(path)
                };
                LogRequest("req.admit", req, $"pending={_pending.Count} writeQueue={_writeQueue.Count}");
                _pending.Enqueue(req);
            }
        }

        // GET routing table. All POST routing is descriptor-driven via BuildPostRoutes().
        private object RouteReadRequest(string path, string format = "toon", string detail = "basic", int id = 0, int limit = 100, int offset = 0, string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
        {
            switch (path)
            {
                case "/api/summary":
                    return _service.ReadV2.CollectSummary(format);
                case "/api/alerts":
                    return _service.ReadV2.CollectAlerts(format, limit, offset);
                case "/api/tree_clusters":
                    return _service.ReadV2.CollectTreeClusters(format);
                case "/api/food_clusters":
                    return _service.ReadV2.CollectFoodClusters(format);
                case "/api/resources":
                    return _service.ReadV2.CollectResources(format);
                case "/api/population":
                    return _service.ReadV2.CollectPopulation();
                case "/api/time":
                    return _service.ReadV2.CollectTime();
                case "/api/weather":
                    return _service.ReadV2.CollectWeather();
                case "/api/districts":
                    return _service.ReadV2.CollectDistricts(format);
                case "/api/buildings":
                    return _service.ReadV2.CollectBuildings(format, detail, id, limit, offset, filterName, filterX, filterY, filterRadius);
                case "/api/trees":
                    return _service.ReadV2.CollectTrees(format, limit, offset, filterName, filterX, filterY, filterRadius);
                case "/api/crops":
                    return _service.ReadV2.CollectCrops(format, limit, offset, filterName, filterX, filterY, filterRadius);
                case "/api/gatherables":
                    return _service.ReadV2.CollectGatherables(format, limit, offset, filterName, filterX, filterY, filterRadius);
                case "/api/beavers":
                    return _service.ReadV2.CollectBeavers(format, detail, id, limit, offset, filterName, filterX, filterY, filterRadius);
                case "/api/distribution":
                    return _service.ReadV2.CollectDistribution(format);
                case "/api/science":
                    return _service.ReadV2.CollectScience(format);
                case "/api/wellbeing":
                    return _service.ReadV2.CollectWellbeing(format);
                case "/api/notifications":
                    return _service.ReadV2.CollectNotifications(format, limit, offset);
                case "/api/workhours":
                    return _service.ReadV2.CollectWorkHours();
                case "/api/power":
                    return _service.ReadV2.CollectPowerNetworks(format);
                case "/api/speed":
                    return _service.ReadV2.CollectSpeed();
                case "/api/prefabs":
                    return _service.Placement.CollectPrefabs();
                case "/api/webhooks":
                    return _service.WebhookMgr.ListWebhooks();
                case "/api/agent/status":
                    return _service.Agent.Status();
            }

            return UnknownEndpoint();
        }

        private PostRouteDescriptor ResolvePostRoute(string path)
        {
            _postRoutes.TryGetValue(path, out var route);
            return route;
        }

        private object UnknownEndpoint()
        {
            return _jw.Error("unknown_endpoint", ("endpoints", new[] {
                "GET /api/ping", "GET /api/summary",
                "GET /api/buildings", "GET /api/trees",
                "GET /api/beavers", "GET /api/resources", "GET /api/districts", "GET /api/weather",
                "GET /api/time", "GET /api/speed", "GET /api/prefabs", "GET /api/power", "GET /api/tiles",
                "POST /api/speed", "POST /api/building/place", "POST /api/building/demolish"
            }));
        }

        private Dictionary<string, PostRouteDescriptor> BuildPostRoutes()
        {
            PostRouteDescriptor Queued(string path, Func<PendingRequest, ITimberbotWriteJob> jobFactory)
                => new PostRouteDescriptor { Path = path, Queued = true, JobFactory = jobFactory };

            var routes = new[]
            {
                Queued("/api/speed", req => new LambdaWriteJob(req.Route, () => _service.Write.SetSpeed(req.Body?.Value<int>("speed") ?? 0))),
                Queued("/api/building/pause", req => new LambdaWriteJob(req.Route, () => _service.Write.PauseBuilding(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<bool>("paused") ?? false))),
                Queued("/api/building/clutch", req => new LambdaWriteJob(req.Route, () => _service.Write.SetClutch(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<bool>("engaged") ?? true))),
                Queued("/api/building/floodgate", req => new LambdaWriteJob(req.Route, () => _service.Write.SetFloodgateHeight(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<float>("height") ?? 0f))),
                Queued("/api/building/priority", req => new LambdaWriteJob(req.Route, () => _service.Write.SetBuildingPriority(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<string>("priority") ?? "Normal", req.Body?.Value<string>("type") ?? ""))),
                Queued("/api/building/hauling", req => new LambdaWriteJob(req.Route, () => _service.Write.SetHaulPriority(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<bool>("prioritized") ?? true))),
                Queued("/api/building/recipe", req => new LambdaWriteJob(req.Route, () => _service.Write.SetRecipe(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<string>("recipe") ?? ""))),
                Queued("/api/building/farmhouse", req => new LambdaWriteJob(req.Route, () => _service.Write.SetFarmhouseAction(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<string>("action") ?? ""))),
                Queued("/api/building/plantable", req => new LambdaWriteJob(req.Route, () => _service.Write.SetPlantablePriority(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<string>("plantable") ?? ""))),
                Queued("/api/building/workers", req => new LambdaWriteJob(req.Route, () => _service.Write.SetWorkers(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<int>("count") ?? 0))),
                Queued("/api/planting/mark", req => new LambdaWriteJob(req.Route, () => _service.Write.MarkPlanting(req.Body?.Value<int>("x1") ?? 0, req.Body?.Value<int>("y1") ?? 0, req.Body?.Value<int>("x2") ?? 0, req.Body?.Value<int>("y2") ?? 0, req.Body?.Value<int>("z") ?? 0, req.Body?.Value<string>("crop") ?? ""))),
                Queued("/api/planting/find", req => _service.Write.CreateFindPlantingSpotsJob(req.Body?.Value<string>("crop") ?? "", req.Body?.Value<int?>("id") ?? req.Body?.Value<int>("building_id") ?? 0, req.Body?.Value<int>("x1") ?? 0, req.Body?.Value<int>("y1") ?? 0, req.Body?.Value<int>("x2") ?? 0, req.Body?.Value<int>("y2") ?? 0, req.Body?.Value<int>("z") ?? 0)),
                Queued("/api/building/range", req => _service.Write.CreateCollectBuildingRangeJob(req.Body?.Value<int>("id") ?? 0)),
                Queued("/api/planting/clear", req => new LambdaWriteJob(req.Route, () => _service.Write.UnmarkPlanting(req.Body?.Value<int>("x1") ?? 0, req.Body?.Value<int>("y1") ?? 0, req.Body?.Value<int>("x2") ?? 0, req.Body?.Value<int>("y2") ?? 0, req.Body?.Value<int>("z") ?? 0))),
                Queued("/api/cutting/area", req => new LambdaWriteJob(req.Route, () => _service.Write.MarkCuttingArea(req.Body?.Value<int>("x1") ?? 0, req.Body?.Value<int>("y1") ?? 0, req.Body?.Value<int>("x2") ?? 0, req.Body?.Value<int>("y2") ?? 0, req.Body?.Value<int>("z") ?? 0, req.Body?.Value<bool>("marked") ?? true))),
                Queued("/api/stockpile/capacity", req => new LambdaWriteJob(req.Route, () => _service.Write.SetStockpileCapacity(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<int>("capacity") ?? 0))),
                Queued("/api/stockpile/good", req => new LambdaWriteJob(req.Route, () => _service.Write.SetStockpileGood(req.Body?.Value<int>("id") ?? 0, req.Body?.Value<string>("good") ?? ""))),
                Queued("/api/workhours", req => new LambdaWriteJob(req.Route, () => _service.Write.SetWorkHours(req.Body?.Value<int>("endHours") ?? 16))),
                Queued("/api/district/migrate", req => new LambdaWriteJob(req.Route, () => _service.Write.MigratePopulation(req.Body?.Value<string>("from") ?? "", req.Body?.Value<string>("to") ?? "", req.Body?.Value<int>("count") ?? 1))),
                Queued("/api/science/unlock", req => new LambdaWriteJob(req.Route, () => _service.Write.UnlockBuilding(req.Body?.Value<string>("building") ?? ""))),
                Queued("/api/distribution", req => new LambdaWriteJob(req.Route, () => _service.Write.SetDistribution(req.Body?.Value<string>("district") ?? "", req.Body?.Value<string>("good") ?? "", req.Body?.Value<string>("import") ?? "", req.Body?.Value<int>("exportThreshold") ?? -1))),
                Queued("/api/building/demolish", req => new LambdaWriteJob(req.Route, () => _service.Placement.DemolishBuilding(req.Body?.Value<int>("id") ?? 0))),
                Queued("/api/crop/demolish", req => new LambdaWriteJob(req.Route, () => _service.Placement.DemolishCrop(req.Body?.Value<int>("id") ?? 0))),
                Queued("/api/webhooks", req => new LambdaWriteJob(req.Route, () => _service.WebhookMgr.RegisterWebhook(req.Body?.Value<string>("url") ?? "", req.Body?["events"]?.ToObject<List<string>>()))),
                Queued("/api/webhooks/delete", req => new LambdaWriteJob(req.Route, () => _service.WebhookMgr.UnregisterWebhook(req.Body?.Value<string>("id") ?? ""))),
                Queued("/api/debug", req => new LambdaWriteJob(req.Route, () =>
                {
                    if (!_debugEnabled) return _jw.Error("disabled: debug endpoint");
                    var debugArgs = new Dictionary<string, string>();
                    if (req.Body != null)
                        foreach (var prop in req.Body.Properties())
                            debugArgs[prop.Name] = prop.Value?.ToString() ?? "";
                    return _service.DebugTool.DebugInspect(req.Body?.Value<string>("target") ?? "help", debugArgs);
                }, 0)),
                Queued("/api/benchmark", req =>
                {
                    if (!_debugEnabled) return new LambdaWriteJob(req.Route, () => _jw.Error("disabled: benchmark endpoint"), 0);
                    return _service.DebugTool.CreateBenchmarkJob(req.Body?.Value<int>("iterations") ?? 100);
                }),
                Queued("/api/path/place", req => _service.Placement.CreateRoutePathJob(req.Body?.Value<int>("x1") ?? 0, req.Body?.Value<int>("y1") ?? 0, req.Body?.Value<int>("x2") ?? 0, req.Body?.Value<int>("y2") ?? 0, req.Body?.Value<string>("style") ?? "direct", req.Body?.Value<int>("sections") ?? 0, req.Body?.Value<bool?>("timings") ?? false, req.QueuedAtTicks, req.QueuedAtFrame)),
                Queued("/api/placement/find", req => _service.Placement.CreateFindPlacementJob(req.Body?.Value<string>("prefab") ?? "", req.Body?.Value<int>("x1") ?? 0, req.Body?.Value<int>("y1") ?? 0, req.Body?.Value<int>("x2") ?? 0, req.Body?.Value<int>("y2") ?? 0)),
                Queued("/api/building/place", req => new LambdaWriteJob(req.Route, () => _service.Placement.PlaceBuilding(req.Body?.Value<string>("prefab") ?? "", req.Body?.Value<int>("x") ?? 0, req.Body?.Value<int>("y") ?? 0, req.Body?.Value<int>("z") ?? 0, req.Body?.Value<string>("orientation") ?? "south").ToJson(_service.Placement.Jw))),
                Queued("/api/agent/start", req => new LambdaWriteJob(req.Route, () => _service.Agent.Start(req.Body?.Value<string>("binary") ?? "claude", req.Body?.Value<int>("turns") ?? 1, req.Body?.Value<string>("model"), req.Body?.Value<int>("interval") ?? 10, req.Body?.Value<string>("prompt"), req.Body?.Value<int>("timeout") ?? 120))),
                Queued("/api/agent/stop", req => new LambdaWriteJob(req.Route, () => _service.Agent.Stop())),
            };

            var result = new Dictionary<string, PostRouteDescriptor>(System.StringComparer.Ordinal);
            for (int i = 0; i < routes.Length; i++)
                result[routes[i].Path] = routes[i];
            return result;
        }

        private void FailOutstanding(string error)
        {
            var payload = _jw.Error(error.Replace("\"", "'"));
            while (_pending.TryDequeue(out var req))
                RespondAsync(req.Context, 500, payload, req.RequestId, req.Route);
            while (_writeQueue.Count > 0)
            {
                var req = _writeQueue.Dequeue();
                RespondAsync(req.Context, 500, payload, req.RequestId, req.Route);
            }
            if (_activeWriteJob != null && _activeWriteRequest != null)
            {
                _activeWriteJob.Cancel(error);
                RespondAsync(_activeWriteRequest.Context, _activeWriteJob.StatusCode, _activeWriteJob.Result, _activeWriteRequest.RequestId, _activeWriteRequest.Route);
                _activeWriteJob = null;
                _activeWriteRequest = null;
            }
        }

        private void RespondAsync(HttpListenerContext ctx, int statusCode, object data, long requestId = 0, string route = null)
        {
            TimberbotLog.Info($"resp.queue id={requestId} route={route ?? ""} status={statusCode} {ThreadPoolState()}");
            ThreadPool.QueueUserWorkItem(_ => Respond(ctx, statusCode, data, requestId, route));
        }

        // Send a JSON response. If data is already a string (from JW serialization),
        // use it directly. Otherwise serialize via Newtonsoft.Json (for anonymous objects).
        // StreamWriter writes directly to the output stream -- no intermediate byte[] allocation.
        private void Respond(HttpListenerContext ctx, int statusCode, object data, long requestId = 0, string route = null)
        {
            try
            {
                TimberbotLog.Info($"resp.start id={requestId} route={route ?? ""} status={statusCode} {ThreadPoolState()}");
                // JW endpoints return pre-serialized strings; anonymous objects need serialization
                var json = data is string s ? s : JsonConvert.SerializeObject(data);
                ctx.Response.StatusCode = statusCode;
                ctx.Response.ContentType = "application/json";
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                // write directly to output stream -- avoids intermediate byte[] allocation
                // UTF8Encoding(false) = no BOM prefix (JSON parsers reject BOM)
                using (var sw = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)))
                    sw.Write(json);
                ctx.Response.OutputStream.Close();
                TimberbotLog.Info($"resp.done id={requestId} route={route ?? ""} status={statusCode} bytes={json.Length} {ThreadPoolState()}");
            }
            catch (Exception ex)
            {
                TimberbotLog.Info($"resp.fail id={requestId} route={route ?? ""} status={statusCode} ex={ex.GetType().Name}:{ex.Message} {ThreadPoolState()}");
                TimberbotLog.Error("response", ex);
            }
        }

        private static string ThreadPoolState()
        {
            ThreadPool.GetAvailableThreads(out int workerAvail, out int ioAvail);
            ThreadPool.GetMaxThreads(out int workerMax, out int ioMax);
            return $"tp={workerAvail}/{workerMax} io={ioAvail}/{ioMax}";
        }

        private static void LogRequest(string phase, PendingRequest req, string extra = null)
        {
            double ageMs = req.QueuedAtTicks > 0
                ? (System.Diagnostics.Stopwatch.GetTimestamp() - req.QueuedAtTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
                : 0;
            TimberbotLog.Info($"{phase} id={req.RequestId} route={req.Route} frame={UnityEngine.Time.frameCount} ageMs={ageMs:F1} {ThreadPoolState()}" + (string.IsNullOrEmpty(extra) ? "" : $" {extra}"));
        }

        private static bool TryReadBodyInt(JObject body, string key, out int value)
        {
            value = 0;
            var token = body?[key];
            if (token == null) return false;
            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }
            if (token.Type == JTokenType.String)
                return int.TryParse(token.Value<string>(), out value);
            return false;
        }
    }
}
