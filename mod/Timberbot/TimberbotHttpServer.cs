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
                    var data = RouteRequest(req.Route, req.Method, req.Body);
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

                _pending.Enqueue(new PendingRequest
                {
                    Context = ctx,
                    Route = path,
                    Method = method,
                    Body = body
                });
            }
        }

        private object RouteRequest(string path, string method, JObject body)
        {
            // GET endpoints (read)
            if (method == "GET")
            {
                switch (path)
                {
                    case "/api/summary":
                        return _service.CollectSummary();
                    case "/api/resources":
                        return _service.CollectResources();
                    case "/api/population":
                        return _service.CollectPopulation();
                    case "/api/time":
                        return _service.CollectTime();
                    case "/api/weather":
                        return _service.CollectWeather();
                    case "/api/districts":
                        return _service.CollectDistricts();
                    case "/api/buildings":
                        return _service.CollectBuildings();
                    case "/api/speed":
                        return _service.CollectSpeed();
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
                            body?.Value<string>("priority") ?? "Normal");
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
                    "GET  /api/speed",
                    "POST /api/speed         {speed: 0-3}",
                    "POST /api/building/pause {id, paused}",
                    "POST /api/floodgate      {id, height}",
                    "POST /api/priority       {id, priority}"
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
