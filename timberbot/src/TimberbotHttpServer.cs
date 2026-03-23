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
    /// <summary>
    /// HTTP server on port 8085. Runs a background listener thread.
    ///
    /// Threading model:
    /// - GET /api/ping and /api/speed: answered on listener thread (safe, simple reads)
    /// - All other requests: queued via ConcurrentQueue, drained on Unity main thread (max 10/frame)
    /// - POST body parsed on listener thread, format param extracted from query string or body
    ///
    /// Format param: ?format=toon (default) returns flat TOON-ready data. ?format=json returns full nested data.
    /// </summary>
    class TimberbotHttpServer
    {
        private readonly TimberbotService _service;
        private readonly HttpListener _listener;
        private readonly Thread _listenerThread;
        private readonly ConcurrentQueue<PendingRequest> _pending = new ConcurrentQueue<PendingRequest>();
        private volatile bool _running;

        class PendingRequest
        {
            public HttpListenerContext Context;
            public string Route;
            public string Method;
            public JObject Body;
            public string Format;
        }

        public TimberbotHttpServer(int port, TimberbotService service)
        {
            _service = service;
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
                    var data = RouteRequest(req.Route, req.Method, req.Body, req.Format);
                    Respond(req.Context, 200, data);
                }
                catch (Exception ex)
                {
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

                // Speed is a simple field read, safe off main thread
                if (path == "/api/speed" && method == "GET")
                {
                    try
                    {
                        Respond(ctx, 200, _service.CollectSpeed());
                    }
                    catch (Exception ex)
                    {
                        Respond(ctx, 500, new { error = ex.Message });
                    }
                    continue;
                }

                JObject body = null;
                if (method == "POST" && ctx.Request.HasEntityBody)
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

                // extract format from query string or body
                var format = ctx.Request.QueryString["format"] ?? body?.Value<string>("format") ?? "toon";

                _pending.Enqueue(new PendingRequest
                {
                    Context = ctx,
                    Route = path,
                    Method = method,
                    Body = body,
                    Format = format
                });
            }
        }

        private object RouteRequest(string path, string method, JObject body, string format = "toon")
        {
            // GET endpoints (read)
            if (method == "GET")
            {
                switch (path)
                {
                    case "/api/summary":
                        return _service.CollectSummary(format);
                    case "/api/alerts":
                        return _service.CollectAlerts();
                    case "/api/tree_clusters":
                        return _service.CollectTreeClusters();
                    case "/api/resources":
                        return _service.CollectResources(format);
                    case "/api/population":
                        return _service.CollectPopulation();
                    case "/api/time":
                        return _service.CollectTime();
                    case "/api/weather":
                        return _service.CollectWeather();
                    case "/api/districts":
                        return _service.CollectDistricts(format);
                    case "/api/buildings":
                        return _service.CollectBuildings(format);
                    case "/api/trees":
                        return _service.CollectTrees();
                    case "/api/gatherables":
                        return _service.CollectGatherables();
                    case "/api/beavers":
                        return _service.CollectBeavers(format);
                    case "/api/distribution":
                        return _service.CollectDistribution();
                    case "/api/science":
                        return _service.CollectScience();
                    case "/api/notifications":
                        return _service.CollectNotifications();
                    case "/api/workhours":
                        return _service.CollectWorkHours();

                    case "/api/map":
                        return _service.CollectMap(0, 0, 0, 0);
                    case "/api/speed":
                        return _service.CollectSpeed();
                    case "/api/prefabs":
                        return _service.CollectPrefabs();
                }
            }

            // POST endpoints (write)
            if (method == "POST")
            {
                switch (path)
                {
                    case "/api/speed":
                        return _service.SetSpeed(body?.Value<int>("speed") ?? 0);
                    case "/api/building/pause":
                        return _service.PauseBuilding(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<bool>("paused") ?? false);
                    case "/api/floodgate":
                        return _service.SetFloodgateHeight(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<float>("height") ?? 0f);
                    case "/api/priority":
                        return _service.SetBuildingPriority(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("priority") ?? "Normal",
                            body?.Value<string>("type") ?? "");
                    case "/api/hauling/priority":
                        return _service.SetHaulPriority(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<bool>("prioritized") ?? true);
                    case "/api/recipe":
                        return _service.SetRecipe(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("recipe") ?? "");
                    case "/api/farmhouse/action":
                        return _service.SetFarmhouseAction(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("action") ?? "");
                    case "/api/plantable/priority":
                        return _service.SetPlantablePriority(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("plantable") ?? "");
                    case "/api/workers":
                        return _service.SetWorkers(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<int>("count") ?? 0);
                    case "/api/planting/mark":
                        return _service.MarkPlanting(
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0,
                            body?.Value<int>("z") ?? 0,
                            body?.Value<string>("crop") ?? "");
                    case "/api/planting/clear":
                        return _service.UnmarkPlanting(
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0,
                            body?.Value<int>("z") ?? 0);
                    case "/api/cutting/area":
                        return _service.MarkCuttingArea(
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0,
                            body?.Value<int>("z") ?? 0,
                            body?.Value<bool>("marked") ?? true);
                    case "/api/stockpile/capacity":
                        return _service.SetStockpileCapacity(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<int>("capacity") ?? 0);
                    case "/api/stockpile/good":
                        return _service.SetStockpileGood(
                            body?.Value<int>("id") ?? 0,
                            body?.Value<string>("good") ?? "");
                    case "/api/workhours":
                        return _service.SetWorkHours(
                            body?.Value<int>("endHours") ?? 16);
                    case "/api/district/migrate":
                        return _service.MigratePopulation(
                            body?.Value<string>("from") ?? "",
                            body?.Value<string>("to") ?? "",
                            body?.Value<int>("count") ?? 1);
                    case "/api/science/unlock":
                        return _service.UnlockBuilding(
                            body?.Value<string>("building") ?? "");
                    case "/api/distribution":
                        return _service.SetDistribution(
                            body?.Value<string>("district") ?? "",
                            body?.Value<string>("good") ?? "",
                            body?.Value<string>("import") ?? "",
                            body?.Value<int>("exportThreshold") ?? -1);
                    case "/api/map":
                        return _service.CollectMap(
                            body?.Value<int>("x1") ?? 0,
                            body?.Value<int>("y1") ?? 0,
                            body?.Value<int>("x2") ?? 0,
                            body?.Value<int>("y2") ?? 0);
                    case "/api/scan":
                        return _service.CollectScan(
                            body?.Value<int>("x") ?? 128,
                            body?.Value<int>("y") ?? 128,
                            body?.Value<int>("radius") ?? 10);
                    case "/api/building/demolish":
                        return _service.DemolishBuilding(
                            body?.Value<int>("id") ?? 0);
                    case "/api/building/place":
                        return _service.PlaceBuilding(
                            body?.Value<string>("prefab") ?? "",
                            body?.Value<int>("x") ?? 0,
                            body?.Value<int>("y") ?? 0,
                            body?.Value<int>("z") ?? 0,
                            body?.Value<int>("orientation") ?? 0);
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
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = statusCode;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Timberbot] response failed: {ex.Message}");
            }
        }
    }
}
