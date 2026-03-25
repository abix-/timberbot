// TimberbotHttpServer.cs -- HTTP server and request routing.
//
// Runs an HttpListener on a background thread (port 8085 by default).
// Threading model:
//   GET requests  -> served directly on the listener thread (reads cached data)
//   POST requests -> queued to ConcurrentQueue, drained on Unity main thread (max 10/frame)
//
// This split exists because Unity game services are single-threaded. GET endpoints
// only read from the double-buffered cache (thread-safe), while POST endpoints
// call game services that must run on the main thread.
//
// All responses are JSON. The server serializes whatever object TimberbotService
// returns using Newtonsoft.Json. TOON format is handled by TimberbotService returning
// pre-built strings for endpoints that support it.

using System;
using System.Collections.Concurrent;
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
        // volatile: read by main thread, written by Stop(). No lock needed for bool.
        private volatile bool _running;
        private readonly bool _debugEnabled;

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
            public int Limit;                    // max items to return (0 = unlimited, default 100)
            public int Offset;                   // skip first N items
        }

        public TimberbotHttpServer(int port, TimberbotService service, bool debugEnabled = false)
        {
            _service = service;
            _debugEnabled = debugEnabled;
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
            try { _listener?.Stop(); } catch { }
        }

        // Called every frame from UpdateSingleton on the Unity main thread.
        // Drains up to 10 queued POST requests per frame to avoid frame spikes.
        // 10/frame at 60fps = 600 requests/sec throughput, more than enough for any AI.
        public void DrainRequests()
        {
            int processed = 0;
            while (processed < 10 && _pending.TryDequeue(out var req))
            {
                processed++;
                try
                {
                    var data = RouteRequest(req.Route, req.Method, req.Body, req.Format, req.Detail, req.Limit, req.Offset);
                    Respond(req.Context, 200, data);
                }
                catch (Exception ex)
                {
                    TimberbotLog.Error("route.post", ex);
                    Respond(req.Context, 500, new { error = ex.Message });
                }
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    if (!_running) break;
                    continue;
                }

                var path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                var method = ctx.Request.HttpMethod.ToUpperInvariant();

                if (path == "/api/ping")
                {
                    Respond(ctx, 200, new { status = "ok", ready = true });
                    continue;
                }

                // format: "toon" = flat key:value pairs (default, human-readable)
                //         "json" = full nested JSON (machine-parseable)
                // detail: "basic" = compact fields, "full" = all fields including inventory/needs
                var format = ctx.Request.QueryString["format"] ?? "toon";
                var detail = ctx.Request.QueryString["detail"] ?? "basic";
                // pagination: limit=100 default (0=unlimited), offset=0 default
                int.TryParse(ctx.Request.QueryString["limit"], out int limit);
                int.TryParse(ctx.Request.QueryString["offset"], out int offset);
                if (ctx.Request.QueryString["limit"] == null) limit = 100;
                // GET requests: handled RIGHT HERE on the background listener thread.
                // This is the key performance trick -- reads never block the game.
                // All CollectX() methods read from double-buffered cached data, so they're
                // thread-safe without locks. The game keeps running at full speed.
                if (method == "GET")
                {
                    try
                    {
                        var data = RouteRequest(path, method, null, format, detail, limit, offset);
                        Respond(ctx, 200, data);
                    }
                    catch (Exception ex)
                    {
                        TimberbotLog.Error("route.get", ex);
                        Respond(ctx, 500, new { error = ex.Message });
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
                        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                        {
                            var raw = reader.ReadToEnd();
                            body = JObject.Parse(raw);
                        }
                    }
                    catch
                    {
                        Respond(ctx, 400, new { error = "invalid JSON body" });
                        continue;
                    }
                }

                // POST requests can override format/detail/limit/offset in the JSON body too
                // (body takes priority over query string)
                format = body?.Value<string>("format") ?? format;
                detail = body?.Value<string>("detail") ?? detail;
                if (body?["limit"] != null) limit = body.Value<int>("limit");
                if (body?["offset"] != null) offset = body.Value<int>("offset");

                _pending.Enqueue(new PendingRequest
                {
                    Context = ctx,
                    Route = path,
                    Method = method,
                    Body = body,
                    Format = format,
                    Detail = detail,
                    Limit = limit,
                    Offset = offset
                });
            }
        }

        // Central routing table. Maps HTTP method + path to service method calls.
        // GET endpoints return cached data (thread-safe, called from background thread).
        // POST endpoints mutate game state (called from main thread via DrainRequests).
        //
        // Notable exceptions to the GET=read/POST=write convention:
        //   /api/tiles (POST): reads terrain data but needs body params for the region
        //   /api/building/range (POST): reads work radius but needs body param for building ID
        //   /api/placement/find (POST): reads valid spots but needs body params for search area
        // These are logically reads but use POST because GET has no request body.
        private object RouteRequest(string path, string method, JObject body, string format = "toon", string detail = "basic", int limit = 100, int offset = 0)
        {
            // GET endpoints (read from double-buffered cache -- zero contention with game thread)
            if (method == "GET")
            {
                switch (path)
                {
                    case "/api/summary":
                        return _service.Read.CollectSummary(format);
                    case "/api/alerts":
                        return _service.Read.CollectAlerts(limit, offset);
                    case "/api/tree_clusters":
                        return _service.Read.CollectTreeClusters();
                    case "/api/resources":
                        return _service.Read.CollectResources(format);
                    case "/api/population":
                        return _service.Read.CollectPopulation();
                    case "/api/time":
                        return _service.Read.CollectTime();
                    case "/api/weather":
                        return _service.Read.CollectWeather();
                    case "/api/districts":
                        return _service.Read.CollectDistricts(format);
                    case "/api/buildings":
                        return _service.Read.CollectBuildings(format, detail, limit, offset);
                    case "/api/trees":
                        return _service.Read.CollectTrees(limit, offset);
                    case "/api/crops":
                        return _service.Read.CollectCrops(limit, offset);
                    case "/api/gatherables":
                        return _service.Read.CollectGatherables(limit, offset);
                    case "/api/beavers":
                        return _service.Read.CollectBeavers(format, detail, limit, offset);
                    case "/api/distribution":
                        return _service.Read.CollectDistribution();
                    case "/api/science":
                        return _service.Read.CollectScience();
                    case "/api/wellbeing":
                        return _service.Read.CollectWellbeing();
                    case "/api/notifications":
                        return _service.Read.CollectNotifications(limit, offset);
                    case "/api/workhours":
                        return _service.Read.CollectWorkHours();

                    case "/api/power":
                        return _service.Read.CollectPowerNetworks();
                    case "/api/speed":
                        return _service.Read.CollectSpeed();
                    case "/api/prefabs":
                        return _service.Placement.CollectPrefabs();
                    case "/api/webhooks":
                        return _service.WebhookMgr.ListWebhooks();
                }
            }

            // POST endpoints (write -- executed on Unity main thread via queue)
            if (method == "POST")
            {
                switch (path)
                {
                    case "/api/speed":
                        return _service.Write.SetSpeed(body?.Value<int>("speed") ?? 0);
                    case "/api/building/pause":
                        return _service.Write.PauseBuilding(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<bool>("paused") ?? false);
                    case "/api/building/clutch":
                        return _service.Write.SetClutch(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<bool>("engaged") ?? true);
                    case "/api/building/floodgate":
                        return _service.Write.SetFloodgateHeight(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<float>("height") ?? 0f);
                    case "/api/building/priority":
                        return _service.Write.SetBuildingPriority(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("priority") ?? "Normal",
                            body?.Value<string>("type") ?? "");
                    case "/api/building/hauling":
                        return _service.Write.SetHaulPriority(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<bool>("prioritized") ?? true);
                    case "/api/building/recipe":
                        return _service.Write.SetRecipe(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("recipe") ?? "");
                    case "/api/building/farmhouse":
                        return _service.Write.SetFarmhouseAction(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("action") ?? "");
                    case "/api/building/plantable":
                        return _service.Write.SetPlantablePriority(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("plantable") ?? "");
                    case "/api/building/workers":
                        return _service.Write.SetWorkers(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<int>("count") ?? 0);
                    case "/api/planting/mark":
                        return _service.Write.MarkPlanting(
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0,
                            body?.Value<int>("z") ?? 0,
                            body?.Value<string>("crop") ?? "");
                    case "/api/planting/find":
                        return _service.Write.FindPlantingSpots(
                            body?.Value<string>("crop") ?? "",
                            body?.Value<int>("building_id") ?? 0,
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0,
                            body?.Value<int>("z") ?? 0);
                    case "/api/building/range":
                        return _service.Write.CollectBuildingRange(
                            body?.Value<int>("id") ?? 0);
                    case "/api/planting/clear":
                        return _service.Write.UnmarkPlanting(
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0,
                            body?.Value<int>("z") ?? 0);
                    case "/api/cutting/area":
                        return _service.Write.MarkCuttingArea(
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0,
                            body?.Value<int>("z") ?? 0,
                            body?.Value<bool>("marked") ?? true);
                    case "/api/stockpile/capacity":
                        return _service.Write.SetStockpileCapacity(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<int>("capacity") ?? 0);
                    case "/api/stockpile/good":
                        return _service.Write.SetStockpileGood(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("good") ?? "");
                    case "/api/workhours":
                        return _service.Write.SetWorkHours(
                            body?.Value<int>("endHours") ?? 16);
                    case "/api/district/migrate":
                        return _service.Write.MigratePopulation(
                            body?.Value<string>("from") ?? "",
                            body?.Value<string>("to") ?? "",
                            body?.Value<int>("count") ?? 1);
                    case "/api/science/unlock":
                        return _service.Write.UnlockBuilding(
                            body?.Value<string>("building") ?? "");
                    case "/api/distribution":
                        return _service.Write.SetDistribution(
                            body?.Value<string>("district") ?? "",
                            body?.Value<string>("good") ?? "",
                            body?.Value<string>("import") ?? "",
                            body?.Value<int>("exportThreshold") ?? -1);
                    case "/api/tiles":
                        return _service.Read.CollectTiles(
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0);
                    case "/api/building/demolish":
                        return _service.Placement.DemolishBuilding(
                            body?.Value<int>("id") ?? 0);
                    case "/api/webhooks":
                        return _service.WebhookMgr.RegisterWebhook(
                            body?.Value<string>("url") ?? "",
                            body?["events"]?.ToObject<System.Collections.Generic.List<string>>());
                    case "/api/webhooks/delete":
                        return _service.WebhookMgr.UnregisterWebhook(
                            body?.Value<string>("id") ?? "");
                    case "/api/debug":
                        if (!_debugEnabled) return new { error = "debug endpoint disabled in settings.json" };
                        var debugArgs = new System.Collections.Generic.Dictionary<string, string>();
                        if (body != null)
                            foreach (var prop in body.Properties())
                                debugArgs[prop.Name] = prop.Value?.ToString() ?? "";
                        return _service.DebugTool.DebugInspect(
                            body?.Value<string>("target") ?? "help", debugArgs);
                    case "/api/benchmark":
                        if (!_debugEnabled) return new { error = "benchmark endpoint disabled in settings.json" };
                        return _service.DebugTool.RunBenchmark(
                            body?.Value<int>("iterations") ?? 100);
                    case "/api/path/place":
                        return _service.Placement.RoutePath(
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0);
                    case "/api/placement/find":
                        return _service.Placement.FindPlacement(
                            body?.Value<string>("prefab") ?? "",
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0);
                    case "/api/building/place":
                        return _service.Placement.PlaceBuilding(
                            body?.Value<string>("prefab") ?? "",
                            body?.Value<int>("x") ?? 0,
                            body?.Value<int>("y") ?? 0,
                            body?.Value<int>("z") ?? 0,
                            body?.Value<string>("orientation") ?? "south");
                }
            }

            return new
            {
                error = "unknown endpoint",
                endpoints = new[]
                {
                    "GET  /api/ping",
                    "GET  /api/summary",
                    "GET  /api/resources",
                    "GET  /api/population",
                    "GET  /api/time",
                    "GET  /api/weather",
                    "GET  /api/districts",
                    "GET  /api/buildings",
                    "GET  /api/trees",
                    "GET  /api/crops",
                    "GET  /api/speed",
                    "GET  /api/prefabs",
                    "POST /api/speed              {speed: 0-3}",
                    "POST /api/building/pause      {id, paused}",
                    "POST /api/floodgate           {id, height}",
                    "POST /api/priority            {id, priority}",
                    "POST /api/workers             {id, count}",
                    "POST /api/cutting/area        {x1,y1,x2,y2,z,marked}",
                    "POST /api/stockpile/capacity  {id, capacity}",
                    "POST /api/stockpile/good      {id, good, allowed}",
                    "POST /api/building/demolish   {id}",
                    "POST /api/building/place      {prefab, x, y, z, orientation}"
                }
            };
        }

        // Send a JSON response. If data is already a string (from JW serialization),
        // use it directly. Otherwise serialize via Newtonsoft.Json (for anonymous objects).
        // StreamWriter writes directly to the output stream -- no intermediate byte[] allocation.
        private void Respond(HttpListenerContext ctx, int statusCode, object data)
        {
            try
            {
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
            }
            catch (Exception ex)
            {
                TimberbotLog.Error("response", ex);
            }
        }
    }
}
