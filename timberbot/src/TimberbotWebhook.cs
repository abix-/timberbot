// TimberbotWebhook.cs -- Batched push event notifications to external URLs.
//
// Register a webhook URL via POST /api/webhooks, optionally filtering by event name.
// Events accumulate in _pendingEvents and flush every webhookBatchMs (default 200ms)
// from FlushWebhooks(). Each flush sends ONE batched JSON array POST per webhook.
//
// Circuit breaker: N consecutive failures disables the webhook (visible via GET /api/webhooks).
//
// All [OnEvent] handlers call PushEvent(name, data) which just serializes and appends.
// Actual HTTP delivery happens in FlushWebhooks() on background ThreadPool threads.

using System;
using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.EntitySystem;
using Timberborn.GameCycleSystem;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.WeatherSystem;

namespace Timberbot
{
    public class TimberbotWebhook
    {
        private readonly IDayNightCycle _dayNightCycle;
        private readonly WeatherService _weatherService;
        private readonly GameCycleService _gameCycleService;
        private readonly SpeedManager _speedManager;
        private readonly EventBus _eventBus;

        // config (set by TimberbotService after construction)
        public bool Enabled = true;
        public float BatchSeconds = 0.2f;
        public int CircuitBreakerThreshold = 30;

        private class WebhookRegistration
        {
            public readonly object Sync = new object();
            public readonly List<string> PendingPayloads = new List<string>();
            public string Id;
            public string Url;
            public HashSet<string> Events;
            public int ConsecutiveFailures;
            public bool Disabled;
            public bool InFlight;
        }
        private readonly object _webhooksLock = new object();
        private readonly List<WebhookRegistration> _webhooks = new List<WebhookRegistration>();
        private static readonly System.Net.Http.HttpClient _webhookClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private int _webhookIdCounter = 0;

        private readonly System.Text.StringBuilder _webhookSb = new System.Text.StringBuilder(1024);
        private readonly TimberbotJw _jw = new TimberbotJw(512);
        private float _lastWebhookFlush = 0f;
        private int _activeDeliveries = 0;
        private int _deliveryIdCounter = 0;

        public int Count
        {
            get
            {
                lock (_webhooksLock)
                    return _webhooks.Count;
            }
        }

        public TimberbotWebhook(
            IDayNightCycle dayNightCycle,
            WeatherService weatherService,
            GameCycleService gameCycleService,
            SpeedManager speedManager,
            EventBus eventBus)
        {
            _dayNightCycle = dayNightCycle;
            _weatherService = weatherService;
            _gameCycleService = gameCycleService;
            _speedManager = speedManager;
            _eventBus = eventBus;
        }

        public void Register() => _eventBus.Register(this);
        public void Unregister() => _eventBus.Unregister(this);

        public object RegisterWebhook(string url, List<string> events)
        {
            if (!Enabled) return _jw.Error("disabled: webhooks disabled in settings.json");
            var id = $"wh_{System.Threading.Interlocked.Increment(ref _webhookIdCounter)}";
            var reg = new WebhookRegistration { Id = id, Url = url, Events = events != null && events.Count > 0 ? new HashSet<string>(events) : null };
            lock (_webhooksLock)
                _webhooks.Add(reg);
            return _jw.BeginObj().Prop("id", id).Prop("url", url).Prop("events", reg.Events != null ? (object)events : "all").CloseObj().ToString();
        }

        public object UnregisterWebhook(string id)
        {
            TimberbotLog.Info($"wh.unregister.start id={id} hooks={Count} pendingEvents={PendingEventCount()} activeDeliveries={_activeDeliveries} {ThreadPoolState()}");
            int removed;
            lock (_webhooksLock)
                removed = _webhooks.RemoveAll(w => w.Id == id);
            TimberbotLog.Info($"wh.unregister.done id={id} removed={removed} hooks={Count} pendingEvents={PendingEventCount()} activeDeliveries={_activeDeliveries} {ThreadPoolState()}");
            return _jw.Result(("id", id), ("removed", (removed > 0)));
        }

        public object ListWebhooks()
        {
            var result = new List<object>();
            var webhooks = SnapshotWebhooks();
            for (int i = 0; i < webhooks.Length; i++)
            {
                var w = webhooks[i];
                bool disabled;
                int failures;
                lock (w.Sync)
                {
                    disabled = w.Disabled;
                    failures = w.ConsecutiveFailures;
                }
                result.Add(new { w.Id, w.Url, events = w.Events != null ? (object)new List<string>(w.Events) : "all", disabled, failures });
            }
            return result;
        }

        // accumulate event into pending batch (called from main thread EventBus handlers)
        // no data -- most events
        public void PushEvent(string eventName)
        {
            if (Count == 0) return;
            var payload = _jw.BeginObj()
                .Prop("event", eventName)
                .Prop("day", _dayNightCycle.DayNumber)
                .Prop("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                .Key("data").Null()
                .CloseObj().ToString();
            QueueEventForMatchingWebhooks(eventName, payload);
        }

        // with pre-built data JSON string
        public void PushEvent(string eventName, string dataJson)
        {
            if (Count == 0) return;
            var payload = _jw.BeginObj()
                .Prop("event", eventName)
                .Prop("day", _dayNightCycle.DayNumber)
                .Prop("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                .RawProp("data", dataJson)
                .CloseObj().ToString();
            QueueEventForMatchingWebhooks(eventName, payload);
        }

        // Flush all pending events to registered webhooks.
        // Called every frame from UpdateSingleton, but only actually sends if enough
        // time has passed (BatchSeconds, default 200ms). This batching reduces HTTP
        // overhead: instead of 50 individual POSTs for 50 events, send 1 POST with
        // a JSON array of 50 events.
        //
        // For each webhook:
        // 1. Build a JSON array of matching events (filter by event name if configured)
        // 2. Send via ThreadPool (non-blocking, doesn't stall the game)
        // 3. Circuit breaker: N consecutive failures disables the webhook to prevent
        //    the game from accumulating thousands of failed HTTP requests
        public void FlushWebhooks(float now)
        {
            if (BatchSeconds > 0 && now - _lastWebhookFlush < BatchSeconds) return;
            var webhooks = SnapshotWebhooks();
            if (webhooks.Length == 0) return;
            int pendingEvents = PendingEventCount(webhooks);
            if (pendingEvents == 0) return;
            _lastWebhookFlush = now;
            TimberbotLog.Info($"wh.flush pendingEvents={pendingEvents} hooks={webhooks.Length} activeDeliveries={_activeDeliveries} {ThreadPoolState()}");

            for (int i = 0; i < webhooks.Length; i++)
            {
                var wh = webhooks[i];
                string batchPayload = null;
                int count = 0;
                lock (wh.Sync)
                {
                    if (wh.Disabled || wh.InFlight || wh.PendingPayloads.Count == 0) continue;
                    _webhookSb.Clear();
                    var sb = _webhookSb;
                    sb.Append('[');
                    count = wh.PendingPayloads.Count;
                    for (int j = 0; j < count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append(wh.PendingPayloads[j]);
                    }
                    sb.Append(']');
                    batchPayload = sb.ToString();
                    wh.PendingPayloads.Clear();
                    wh.InFlight = true;
                }
                if (count == 0) continue;

                // send on ThreadPool thread -- never block the game's main thread
                var url = wh.Url;
                var whRef = wh;
                var threshold = CircuitBreakerThreshold;
                var deliveryId = System.Threading.Interlocked.Increment(ref _deliveryIdCounter);
                TimberbotLog.Info($"wh.dispatch delivery={deliveryId} webhook={wh.Id} events={count} activeDeliveries={_activeDeliveries} {ThreadPoolState()}");
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    int activeNow = System.Threading.Interlocked.Increment(ref _activeDeliveries);
                    try
                    {
                        TimberbotLog.Info($"wh.delivery.start delivery={deliveryId} webhook={whRef.Id} events={count} activeDeliveries={activeNow} {ThreadPoolState()}");
                        using var response = _webhookClient.PostAsync(url, new System.Net.Http.StringContent(batchPayload, System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                        if (response.IsSuccessStatusCode)
                        {
                            lock (whRef.Sync)
                            {
                                whRef.ConsecutiveFailures = 0;
                                whRef.InFlight = false;
                            }
                            TimberbotLog.Info($"wh.delivery.done delivery={deliveryId} webhook={whRef.Id} status={(int)response.StatusCode} activeDeliveries={activeNow} {ThreadPoolState()}");
                            return;
                        }

                        int failures;
                        bool disabled;
                        lock (whRef.Sync)
                        {
                            whRef.ConsecutiveFailures++;
                            whRef.InFlight = false;
                            whRef.Disabled = whRef.ConsecutiveFailures >= threshold;
                            failures = whRef.ConsecutiveFailures;
                            disabled = whRef.Disabled;
                        }
                        TimberbotLog.Info($"wh.delivery.fail delivery={deliveryId} webhook={whRef.Id} failures={failures} activeDeliveries={activeNow} status={(int)response.StatusCode} reason={response.ReasonPhrase} {ThreadPoolState()}");
                        if (disabled)
                            TimberbotLog.Info($"webhook {whRef.Id} disabled after {threshold} failures: {url} status={(int)response.StatusCode}");
                        else
                            TimberbotLog.Info($"webhook.post status={(int)response.StatusCode} webhook={whRef.Id} url={url}");
                    }
                    catch (Exception _ex)
                    {
                        int failures;
                        bool disabled;
                        lock (whRef.Sync)
                        {
                            whRef.ConsecutiveFailures++;
                            whRef.InFlight = false;
                            whRef.Disabled = whRef.ConsecutiveFailures >= threshold;
                            failures = whRef.ConsecutiveFailures;
                            disabled = whRef.Disabled;
                        }
                        TimberbotLog.Info($"wh.delivery.fail delivery={deliveryId} webhook={whRef.Id} failures={failures} activeDeliveries={activeNow} ex={_ex.GetType().Name}:{_ex.Message} {ThreadPoolState()}");
                        if (disabled)
                            TimberbotLog.Error($"webhook {whRef.Id} disabled after {threshold} failures: {url}", _ex);
                        else
                            TimberbotLog.Error("webhook.post", _ex);
                    }
                    finally
                    {
                        lock (whRef.Sync)
                            whRef.InFlight = false;
                        int activeAfter = System.Threading.Interlocked.Decrement(ref _activeDeliveries);
                        TimberbotLog.Info($"wh.delivery.end delivery={deliveryId} webhook={whRef.Id} activeDeliveries={activeAfter} {ThreadPoolState()}");
                    }
                });
            }
        }

        private WebhookRegistration[] SnapshotWebhooks()
        {
            lock (_webhooksLock)
                return _webhooks.ToArray();
        }

        private void QueueEventForMatchingWebhooks(string eventName, string payload)
        {
            var webhooks = SnapshotWebhooks();
            for (int i = 0; i < webhooks.Length; i++)
            {
                var wh = webhooks[i];
                lock (wh.Sync)
                {
                    if (wh.Disabled) continue;
                    if (wh.Events != null && !wh.Events.Contains(eventName)) continue;
                    wh.PendingPayloads.Add(payload);
                }
            }
        }

        private int PendingEventCount() => PendingEventCount(SnapshotWebhooks());

        private static int PendingEventCount(WebhookRegistration[] webhooks)
        {
            int pending = 0;
            for (int i = 0; i < webhooks.Length; i++)
            {
                lock (webhooks[i].Sync)
                    pending += webhooks[i].PendingPayloads.Count;
            }
            return pending;
        }

        private static string ThreadPoolState()
        {
            System.Threading.ThreadPool.GetAvailableThreads(out int workerAvail, out int ioAvail);
            System.Threading.ThreadPool.GetMaxThreads(out int workerMax, out int ioMax);
            return $"tp={workerAvail}/{workerMax} io={ioAvail}/{ioMax}";
        }

        // helpers for building data JSON without anonymous objects
        private static string CanonicalName(string name) => TimberbotEntityRegistry.CanonicalName(name);

        public string DataInt(string key, int val) =>
            _jw.BeginObj().Key(key).Int(val).CloseObj().ToString();

        public string DataEntity(int id, string name) =>
            _jw.BeginObj().Prop("id", id).Prop("name", name).CloseObj().ToString();

        public string DataEntityBot(int id, string name, bool isBot) =>
            _jw.BeginObj().Prop("id", id).Prop("name", name).Prop("isBot", isBot).CloseObj().ToString();

        // ================================================================
        // WEBHOOK EVENT HANDLERS
        // ================================================================

        // weather
        [OnEvent] public void OnDroughtStart(Timberborn.HazardousWeatherSystem.HazardousWeatherStartedEvent e) { if (_webhooks.Count > 0) PushEvent("drought.start", DataInt("duration", _weatherService.HazardousWeatherDuration)); }
        [OnEvent] public void OnDroughtEnd(Timberborn.HazardousWeatherSystem.HazardousWeatherEndedEvent e) => PushEvent("drought.end");
        [OnEvent] public void OnDroughtApproaching(Timberborn.HazardousWeatherSystemUI.HazardousWeatherApproachingEvent e) => PushEvent("drought.approaching");
        [OnEvent] public void OnCycleStart(Timberborn.GameCycleSystem.CycleStartedEvent e) { if (_webhooks.Count > 0) PushEvent("cycle.start", DataInt("cycle", _gameCycleService.Cycle)); }
        [OnEvent] public void OnCycleEnd(Timberborn.GameCycleSystem.CycleEndedEvent e) { if (_webhooks.Count > 0) PushEvent("cycle.end", DataInt("cycle", _gameCycleService.Cycle)); }
        [OnEvent] public void OnCycleDay(Timberborn.GameCycleSystem.CycleDayStartedEvent e) { if (_webhooks.Count > 0) PushEvent("cycle.day", _jw.BeginObj().Prop("cycle", _gameCycleService.Cycle).Prop("cycleDay", _gameCycleService.CycleDay).CloseObj().ToString()); }

        // time
        [OnEvent] public void OnDayStart(Timberborn.TimeSystem.DaytimeStartEvent e) { if (_webhooks.Count > 0) PushEvent("day.start", DataInt("day", _dayNightCycle.DayNumber)); }
        [OnEvent] public void OnNightStart(Timberborn.TimeSystem.NighttimeStartEvent e) { if (_webhooks.Count > 0) PushEvent("night.start", DataInt("day", _dayNightCycle.DayNumber)); }

        // buildings
        [OnEvent] public void OnBuildingFinished(EnteredFinishedStateEvent e) { try { var go = e.BlockObject?.GetComponent<EntityComponent>()?.GameObject; PushEvent("building.finished", DataEntity(go?.GetInstanceID() ?? 0, go != null ? CanonicalName(go.name) : "")); } catch (Exception _ex) { TimberbotLog.Error("webhook.building_finished", _ex); } }
        [OnEvent] public void OnDistrictChanged(Timberborn.GameDistricts.DistrictCenterRegistryChangedEvent e) => PushEvent("district.changed");

        // population
        [OnEvent] public void OnPopulationChanged(Timberborn.Population.PopulationChangedEvent e) => PushEvent("population.changed");
        [OnEvent] public void OnCharacterCreated(Timberborn.Characters.CharacterCreatedEvent e) => PushEvent("character.created");
        [OnEvent] public void OnCharacterKilled(Timberborn.Characters.CharacterKilledEvent e) => PushEvent("character.killed");
        [OnEvent] public void OnBeaverBornEvt(Timberborn.Beavers.BeaverBornEvent e) => PushEvent("beaver.born.event");
        [OnEvent] public void OnBotManufactured(Timberborn.BotUpkeep.BotManufacturedEvent e) => PushEvent("bot.manufactured");
        [OnEvent] public void OnMigration(Timberborn.GameDistricts.MigrationEvent e) => PushEvent("migration");

        // needs/wellbeing
        [OnEvent] public void OnContaminationChanged(Timberborn.BeaverContaminationSystem.ContaminableContaminationChangedEvent e) => PushEvent("contamination.changed");
        [OnEvent] public void OnTeethChipped(Timberborn.Healthcare.TeethChippedEvent e) => PushEvent("teeth.chipped");
        [OnEvent] public void OnWellbeingHighscore(Timberborn.Wellbeing.NewWellbeingHighscoreEvent e) => PushEvent("wellbeing.highscore");
        [OnEvent] public void OnStatusAlert(Timberborn.StatusSystem.StatusAlertAddedEvent e) => PushEvent("status.alert");

        // trees/crops
        [OnEvent] public void OnTreeCut(Timberborn.Forestry.TreeCutEvent e) => PushEvent("tree.cut");
        [OnEvent] public void OnCuttableHarvested(Timberborn.Cutting.CuttableCutEvent e) => PushEvent("cuttable.cut", null);
        [OnEvent] public void OnTreeCuttingAreaChanged(Timberborn.Forestry.TreeCuttingAreaChangedEvent e) => PushEvent("cutting.area.changed", null);
        [OnEvent] public void OnTreeAddedToCuttingArea(Timberborn.Forestry.TreeAddedToCuttingAreaEvent e) => PushEvent("tree.marked", null);
        [OnEvent] public void OnCropPlanted(Timberborn.NaturalResources.NaturalResourcePlantedEvent e) => PushEvent("crop.planted", null);
        [OnEvent] public void OnPlantingMarked(Timberborn.Planting.PlantingAreaMarkedEvent e) => PushEvent("planting.marked", null);

        // wonders
        [OnEvent] public void OnWonderActivated(Timberborn.Wonders.WonderActivatedEvent e) => PushEvent("wonder.activated", null);
        [OnEvent] public void OnWonderCompleted(Timberborn.GameWonderCompletion.WonderCompletedEvent e) => PushEvent("wonder.completed", null);
        [OnEvent] public void OnWonderCountdown(Timberborn.GameWonderCompletion.WonderCompletionCountdownStartedEvent e) => PushEvent("wonder.countdown", null);

        // power
        [OnEvent] public void OnPowerNetworkCreated(Timberborn.MechanicalSystem.MechanicalGraphCreatedEvent e) => PushEvent("power.network.created", null);
        [OnEvent] public void OnPowerNetworkRemoved(Timberborn.MechanicalSystem.MechanicalGraphRemovedEvent e) => PushEvent("power.network.removed", null);

        // buildings (continued)
        [OnEvent] public void OnBuildingUnlocked(Timberborn.ScienceSystem.BuildingUnlockedEvent e) => PushEvent("building.unlocked", null);
        [OnEvent] public void OnBuildingDeconstructed(Timberborn.DeconstructionSystem.BuildingDeconstructedEvent e) => PushEvent("building.deconstructed", null);
        [OnEvent] public void OnDemolishableMarked(Timberborn.Demolishing.DemolishableMarkedEvent e) => PushEvent("demolish.marked", null);
        [OnEvent] public void OnDemolishableUnmarked(Timberborn.Demolishing.DemolishableUnmarkedEvent e) => PushEvent("demolish.unmarked", null);

        // game state
        [OnEvent] public void OnGameOver(Timberborn.GameOver.GameOverEvent e) => PushEvent("game.over", null);
        [OnEvent] public void OnSpeedChanged(CurrentSpeedChangedEvent e) { if (_webhooks.Count > 0) PushEvent("speed.changed", DataInt("speed", (int)_speedManager.CurrentSpeed)); }
        [OnEvent] public void OnWorkHoursChanged(Timberborn.WorkSystem.WorkingHoursChangedEvent e) => PushEvent("workhours.changed", null);
        [OnEvent] public void OnWorkHoursTransitioned(Timberborn.WorkSystem.WorkingHoursTransitionedEvent e) => PushEvent("workhours.transitioned", null);
        [OnEvent] public void OnAutosave(Timberborn.Autosaving.AutosaveEvent e) => PushEvent("autosave", null);

        // explosions
        [OnEvent] public void OnDynamiteDetonated(Timberborn.Explosions.DynamiteDetonatedEvent e) => PushEvent("explosion", null);
        [OnEvent] public void OnExplosionKill(Timberborn.Explosions.MortalDiedFromExplosionEvent e) => PushEvent("explosion.kill", null);

        // terrain
        [OnEvent] public void OnTerrainDestroyed(Timberborn.TerrainPhysics.TerrainDestroyedEvent e) => PushEvent("terrain.destroyed", null);
        [OnEvent] public void OnWindChanged(Timberborn.WindSystem.WindChangedEvent e) => PushEvent("wind.changed", null);

        // zipline
        [OnEvent] public void OnZiplineActivated(Timberborn.ZiplineSystem.ZiplineConnectionActivatedEvent e) => PushEvent("zipline.activated", null);

        // blocks
        [OnEvent] public void OnBlockSet(BlockObjectSetEvent e) => PushEvent("block.set", null);
        [OnEvent] public void OnBlockUnset(BlockObjectUnsetEvent e) => PushEvent("block.unset", null);
        [OnEvent] public void OnConstructionStarted(EnteredUnfinishedStateEvent e) => PushEvent("construction.started", null);
        [OnEvent] public void OnBuildingUnfinished(ExitedFinishedStateEvent e) => PushEvent("building.unfinished", null);

        // entities
        [OnEvent] public void OnEntityCreated(EntityCreatedEvent e) => PushEvent("entity.created", null);

        // factions
        [OnEvent] public void OnFactionUnlocked(Timberborn.FactionSystem.FactionUnlockedEvent e) => PushEvent("faction.unlocked", null);

        // districts
        [OnEvent] public void OnDistrictConnectionsChanged(Timberborn.GameDistricts.DistrictConnectionsChangedEvent e) => PushEvent("district.connections.changed", null);
        [OnEvent] public void OnMigrationDistrictChanged(Timberborn.GameDistrictsMigration.MigrationDistrictChangedEvent e) => PushEvent("migration.district.changed", null);

        // weather (selection)
        [OnEvent] public void OnWeatherSelected(Timberborn.HazardousWeatherSystem.HazardousWeatherSelectedEvent e) => PushEvent("weather.selected", null);

        // power (detailed)
        [OnEvent] public void OnPowerGeneratorAdded(Timberborn.MechanicalSystem.MechanicalGraphGeneratorAddedEvent e) => PushEvent("power.generator.added", null);
        [OnEvent] public void OnPowerGeneratorUpdated(Timberborn.MechanicalSystem.MechanicalGraphGeneratorUpdatedEvent e) => PushEvent("power.generator.updated", null);

        // planting
        [OnEvent] public void OnPlantingCoordsSet(Timberborn.Planting.PlantingCoordinatesSetEvent e) => PushEvent("planting.coords.set", null);
        [OnEvent] public void OnPlantingCoordsUnset(Timberborn.Planting.PlantingCoordinatesUnsetEvent e) => PushEvent("planting.coords.unset", null);

        // game startup
        [OnEvent] public void OnNewGame(Timberborn.Common.NewGameInitializedEvent e) => PushEvent("game.new", null);
        [OnEvent] public void OnStartingBuilding(Timberborn.GameStartup.StartingBuildingPlacedEvent e) => PushEvent("game.starting.building", null);
        [OnEvent] public void OnSpeedLockChanged(SpeedLockChangedEvent e) => PushEvent("speed.lock.changed", null);

        // naming + alerts
        [OnEvent] public void OnEntityRenamed(Timberborn.EntityNaming.EntityNameChangedEvent e) => PushEvent("entity.renamed", null);
        [OnEvent] public void OnDynamicAlert(Timberborn.StatusSystem.DynamicStatusAlertAddedEvent e) => PushEvent("status.dynamic.alert", null);

        // construction mode
        [OnEvent] public void OnConstructionMode(Timberborn.ConstructionMode.ConstructionModeChangedEvent e) => PushEvent("construction.mode.changed", null);
    }
}

