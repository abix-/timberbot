// TimberbotService.cs -- Core service: DI constructor, fields, lifecycle, settings.
//
// This is the main entry point for the Timberbot API mod. Timberborn's Bindito DI
// system injects game services into the constructor. The service runs as a game
// singleton: Load() starts the HTTP server, UpdateSingleton() refreshes cached state
// every N seconds and drains queued POST requests on the Unity main thread.
//
// API logic lives in separate classes, each with their own DI:
//   TimberbotEntityCache   -- Double-buffered entity caching, indexes, RefreshCachedState
//   TimberbotRead          -- All GET read endpoints
//   TimberbotWrite         -- All POST write endpoints
//   TimberbotPlacement     -- Building placement, path routing, terrain
//   TimberbotWebhook       -- Batched push event notifications
//   TimberbotDebug         -- Reflection inspector and benchmark

using Timberborn.SingletonSystem;
using UnityEngine;

namespace Timberbot
{
    // HTTP API service. Injected via Bindito DI, runs as game singleton.
    // Returns plain objects serialized to JSON by TimberbotHttpServer.
    //
    // format param: "toon" (default) = flat for tabular display, "json" = full nested data
    // entity access: no typed queries in Timberborn, so we iterate _entityRegistry.Entities + GetComponent<T>()
    // names: CleanName() strips "(Clone)", ".IronTeeth", ".Folktails" from all output
    // entity lookup: FindEntity() uses per-frame dictionary cache for O(1) writes
    public class TimberbotService : ILoadableSingleton, IUpdatableSingleton, IUnloadableSingleton
    {
        private readonly EventBus _eventBus;
        public readonly TimberbotEntityCache Cache;
        public readonly TimberbotWebhook WebhookMgr;
        public readonly TimberbotRead Read;
        public readonly TimberbotWrite Write;
        public readonly TimberbotPlacement Placement;
        public readonly TimberbotDebug DebugTool;
        private TimberbotHttpServer _server;

        // settings (loaded from settings.json in mod folder)
        private float _refreshInterval = 1.0f;   // seconds between cache refreshes (default: 1s)
        private bool _debugEnabled = false;       // enable /api/debug endpoint (default: off)
        private int _httpPort = 8085;             // HTTP server port
        // webhook settings applied to WebhookMgr in Load()
        private bool _webhooksEnabled = true;
        private float _webhookBatchSeconds = 0.2f;
        private int _webhookCircuitBreaker = 30;

        public TimberbotService(
            EventBus eventBus,
            TimberbotEntityCache cache,
            TimberbotWebhook webhookMgr,
            TimberbotRead read,
            TimberbotWrite write,
            TimberbotPlacement placement,
            TimberbotDebug debug)
        {
            _eventBus = eventBus;
            Cache = cache;
            WebhookMgr = webhookMgr;
            Read = read;
            Write = write;
            Placement = placement;
            DebugTool = debug;
        }

        public void Load()
        {
            LoadSettings();
            var modDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                "Timberborn", "Mods", "Timberbot");
            TimberbotLog.Init(modDir);
            WebhookMgr.Enabled = _webhooksEnabled;
            WebhookMgr.BatchSeconds = _webhookBatchSeconds;
            WebhookMgr.CircuitBreakerThreshold = _webhookCircuitBreaker;
            TimberbotLog.Info($"v0.7.0 port={_httpPort} refresh={_refreshInterval}s debug={_debugEnabled} webhooks={_webhooksEnabled} batchMs={_webhookBatchSeconds * 1000:F0}");
            Cache.WebhookMgr = WebhookMgr;
            DebugTool.Service = this;
            _eventBus.Register(this);
            WebhookMgr.Register();
            Cache.Register();
            Cache.BuildAllIndexes();
            _server = new TimberbotHttpServer(_httpPort, this, _debugEnabled);
            TimberbotLog.Info($"HTTP server started on port {_httpPort}");
        }

        private void LoadSettings()
        {
            try
            {
                var modDir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                    "Timberborn", "Mods", "Timberbot");
                var path = System.IO.Path.Combine(modDir, "settings.json");
                if (System.IO.File.Exists(path))
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path));
                    _refreshInterval = json.Value<float>("refreshIntervalSeconds");
                    if (_refreshInterval <= 0) _refreshInterval = 1.0f;
                    _debugEnabled = json.Value<bool>("debugEndpointEnabled");
                    _httpPort = json.Value<int>("httpPort");
                    if (_httpPort <= 0) _httpPort = 8085;
                    if (json["webhooksEnabled"] != null)
                        _webhooksEnabled = json.Value<bool>("webhooksEnabled");
                    if (json["webhookBatchMs"] != null)
                    {
                        int batchMs = json.Value<int>("webhookBatchMs");
                        _webhookBatchSeconds = batchMs >= 0 ? batchMs / 1000f : 0.2f;
                    }
                    if (json["webhookCircuitBreaker"] != null)
                    {
                        int cb = json.Value<int>("webhookCircuitBreaker");
                        _webhookCircuitBreaker = cb > 0 ? cb : 30;
                    }
                }
            }
            catch (System.Exception ex)
            {
                TimberbotLog.Error("settings.json load failed, using defaults", ex);
            }
        }

        public void Unload()
        {
            Cache.Unregister();
            WebhookMgr.Unregister();
            _eventBus.Unregister(this);
            _server?.Stop();
            _server = null;
            TimberbotLog.Info("HTTP server stopped");
        }

        private float _lastRefreshTime = 0f;

        public void UpdateSingleton()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastRefreshTime >= _refreshInterval)
            {
                _lastRefreshTime = now;
                Cache.RefreshCachedState();
            }
            _server?.DrainRequests();
            WebhookMgr.FlushWebhooks(now);
        }
    }
}
