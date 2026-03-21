using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

namespace GameStateBridge
{
    class GameStateHttpServer
    {
        private readonly GameStateBridgeService _service;
        private readonly HttpListener _listener;
        private readonly Thread _listenerThread;
        private readonly ConcurrentQueue<PendingRequest> _pending = new ConcurrentQueue<PendingRequest>();
        private volatile bool _running;

        class PendingRequest
        {
            public HttpListenerContext Context;
            public string Route;
        }

        public GameStateHttpServer(int port, GameStateBridgeService service)
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
                Debug.Log($"[GameStateBridge] port +:{port} failed, falling back to localhost");
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
            }

            _running = true;
            _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "GSB-HTTP" };
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
                    var data = RouteRequest(req.Route);
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

                if (path == "/api/ping")
                {
                    Respond(ctx, 200, new { status = "ok", ready = true });
                    continue;
                }

                _pending.Enqueue(new PendingRequest { Context = ctx, Route = path });
            }
        }

        private object RouteRequest(string path)
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
                default:
                    return new
                    {
                        error = "unknown endpoint",
                        endpoints = new[]
                        {
                            "/api/ping",
                            "/api/summary",
                            "/api/resources",
                            "/api/population",
                            "/api/time",
                            "/api/weather",
                            "/api/districts"
                        }
                    };
            }
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
                Debug.LogWarning($"[GameStateBridge] response failed: {ex.Message}");
            }
        }
    }
}
