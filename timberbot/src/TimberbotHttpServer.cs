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
using UnityEngine;

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
        private readonly ConcurrentQueue<PendingRequest> _pending = new ConcurrentQueue<PendingRequest>();
        private volatile bool _running;
        private readonly bool _debugEnabled;

        class PendingRequest
        {
            public HttpListenerContext Context;
            public string Route;
            public string Method;
            public JObject Body;
            public string Format;
            public string Detail;
        }

        public TimberbotHttpServer(int port, TimberbotService service, bool debugEnabled = false)
        {
            _service = service;
            _debugEnabled = debugEnabled;
            _listener = new HttpListener();

            try
            {
                _listener.Prefixes.Add($"http://+:{port}/");
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                Debug.Log($"[Timberbot] port +:{port} failed, falling back to localhost");
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

        public void DrainRequests()
        {
            int processed = 0;
            while (processed < 10 && _pending.TryDequeue(out var req))
            {
                processed++;
                try
                {
                    var data = RouteRequest(req.Route, req.Method, req.Body, req.Format, req.Detail);
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

                // extract query params
                var format = ctx.Request.QueryString["format"] ?? "toon";
                var detail = ctx.Request.QueryString["detail"] ?? "basic";
                var serial = ctx.Request.QueryString["serial"] ?? "dict";

                // GET requests: handled RIGHT HERE on the background listener thread.
                // This is the key performance trick -- reads never block the game.
                // All CollectX() methods read from double-buffered cached data, so they're
                // thread-safe without locks. The game keeps running at full speed.
                if (method == "GET")
                {
                    try
                    {
                        var data = RouteRequest(path, method, null, format, detail, serial);
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

                format = body?.Value<string>("format") ?? format;
                detail = body?.Value<string>("detail") ?? detail;

                _pending.Enqueue(new PendingRequest
                {
                    Context = ctx,
                    Route = path,
                    Method = method,
                    Body = body,
                    Format = format,
                    Detail = detail
                });
            }
        }

        private object RouteRequest(string path, string method, JObject body, string format = "toon", string detail = "basic", string serial = "dict")
        {
            // GET endpoints (read)
            if (method == "GET")
            {
                switch (path)
                {
                    case "/api/summary":
                        return _service.Read.CollectSummary(format);
                    case "/api/alerts":
                        return _service.Read.CollectAlerts();
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
                        return _service.Read.CollectBuildings(format, detail);
                    case "/api/trees":
                        return _service.Read.CollectTrees();
                    case "/api/crops":
                        return _service.Read.CollectCrops();
                    case "/api/gatherables":
                        return _service.Read.CollectGatherables();
                    case "/api/beavers":
                        return _service.Read.CollectBeavers(format, detail);
                    case "/api/distribution":
                        return _service.Write.CollectDistribution();
                    case "/api/science":
                        return _service.Write.CollectScience();
                    case "/api/wellbeing":
                        return _service.Write.CollectWellbeing();
                    case "/api/notifications":
                        return _service.Write.CollectNotifications();
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

            // POST endpoints (write)
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
                        return _service.Read.SetWorkHours(
                            body?.Value<int>("endHours") ?? 16);
                    case "/api/district/migrate":
                        return _service.Read.MigratePopulation(
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
                        return _service.Write.CollectTiles(
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

        private void Respond(HttpListenerContext ctx, int statusCode, object data)
        {
            try
            {
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
