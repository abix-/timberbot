using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace GameStateBridge
{
    /// <summary>
    /// Lightweight HTTP server exposing game state as JSON.
    /// Listener runs on a background thread; game data is collected on the main
    /// thread via a request queue (same pattern as Bevy BRP drain_remote_queues).
    /// </summary>
    class GameStateHttpServer
    {
        private readonly HttpListener _listener;
        private readonly Thread _listenerThread;
        private readonly ConcurrentQueue<PendingRequest> _pending = new ConcurrentQueue<PendingRequest>();
        private volatile bool _running;

        class PendingRequest
        {
            public HttpListenerContext Context;
            public string Route;
        }

        public GameStateHttpServer(int port)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{port}/");

            try
            {
                _listener.Start();
                _running = true;
                _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "GSB-HTTP" };
                _listenerThread.Start();
                Plugin.Log.LogInfo($"HTTP server listening on port {port}");
            }
            catch (HttpListenerException ex)
            {
                // If port binding fails (no admin), fall back to localhost only
                Plugin.Log.LogWarning($"Failed to bind +:{port}, trying localhost: {ex.Message}");
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                _running = true;
                _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "GSB-HTTP" };
                _listenerThread.Start();
                Plugin.Log.LogInfo($"HTTP server listening on localhost:{port}");
            }
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        /// <summary>
        /// Called from Plugin.Update() on the main Unity thread.
        /// Drains pending HTTP requests and responds with game data.
        /// </summary>
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

                // Health check can respond immediately (no game state needed)
                if (path == "/api/ping")
                {
                    Respond(ctx, 200, new { status = "ok", ready = GameState.IsReady });
                    continue;
                }

                // Everything else needs main thread -- queue it
                _pending.Enqueue(new PendingRequest { Context = ctx, Route = path });
            }
        }

        private object RouteRequest(string path)
        {
            switch (path)
            {
                case "/api/summary":
                    return GameState.CollectSummary();
                case "/api/resources":
                    return GameState.CollectResources();
                case "/api/population":
                    return GameState.CollectPopulation();
                case "/api/time":
                    return GameState.CollectTime();
                case "/api/weather":
                    return GameState.CollectWeather();
                case "/api/districts":
                    return GameState.CollectDistricts();
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
                Plugin.Log.LogWarning($"Failed to send response: {ex.Message}");
            }
        }
    }
}
