// TimberbotService.Webhooks.cs -- Push event notifications to external URLs.
//
// Register a webhook URL via POST /api/webhooks, optionally filtering by event name.
// When a game event fires (drought, beaver death, building placed, etc.), PushEvent()
// sends a JSON POST to all matching subscribers on a background thread.
//
// This is NOT the same as Timberborn's vanilla HTTP Adapter system (port 8080), which
// sends binary on/off signals from in-game sensor buildings. Our webhooks push 68
// rich game events with data payloads, no in-game buildings required.
//
// All [OnEvent] handlers are one-liners that call PushEvent(name, data).
// Fire-and-forget: no retries, no persistence, 5s timeout. Subscribers filter
// by event name (null = all events).

using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.BuilderPrioritySystem;
using Timberborn.Buildings;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockObjectTools;
using Timberborn.Coordinates;
using Timberborn.Cutting;
using Timberborn.TemplateInstantiation;
using Timberborn.MapIndexSystem;
using Timberborn.TerrainSystem;
using Timberborn.WaterSystem;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.Planting;
using Timberborn.Gathering;
using Timberborn.GameCycleSystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.PrioritySystem;
using Timberborn.ResourceCountingSystem;
using Timberborn.SingletonSystem;
using Timberborn.Stockpiles;
using Timberborn.TimeSystem;
using Timberborn.WaterBuildings;
using Timberborn.WeatherSystem;
using Timberborn.WorkSystem;
using Timberborn.NeedSystem;
using Timberborn.LifeSystem;
using Timberborn.Wellbeing;
using Timberborn.BuildingsReachability;
using Timberborn.ConstructionSites;
using Timberborn.MechanicalSystem;
using Timberborn.ScienceSystem;
using Timberborn.BeaverContaminationSystem;
using Timberborn.Bots;
using Timberborn.Carrying;
using Timberborn.DeteriorationSystem;
using Timberborn.Wonders;
using Timberborn.NotificationSystem;
using Timberborn.StatusSystem;
using Timberborn.DwellingSystem;
using Timberborn.PowerManagement;
using Timberborn.SoilContaminationSystem;
using Timberborn.Hauling;
using Timberborn.Workshops;
using Timberborn.Reproduction;
using Timberborn.Fields;
using Timberborn.GameDistrictsMigration;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;
using Timberborn.PlantingUI;
using Timberborn.BuildingsNavigation;
using Timberborn.SoilMoistureSystem;
using Timberborn.NeedSpecs;
using Timberborn.GameFactionSystem;
using Timberborn.RangedEffectSystem;
using UnityEngine;

namespace Timberbot
{
    public partial class TimberbotService
    {
        // webhooks: fire-and-forget push to registered URLs
        private class WebhookRegistration { public string Id; public string Url; public System.Collections.Generic.HashSet<string> Events; }
        private readonly List<WebhookRegistration> _webhooks = new List<WebhookRegistration>();
        private static readonly System.Net.Http.HttpClient _webhookClient = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(5) };
        private int _webhookIdCounter = 0;

        public object RegisterWebhook(string url, List<string> events)
        {
            if (!_webhooksEnabled) return new { error = "webhooks disabled in settings.json" };
            var id = $"wh_{System.Threading.Interlocked.Increment(ref _webhookIdCounter)}";
            var reg = new WebhookRegistration { Id = id, Url = url, Events = events != null && events.Count > 0 ? new System.Collections.Generic.HashSet<string>(events) : null };
            _webhooks.Add(reg);
            return new { id, url, events = reg.Events != null ? (object)events : "all" };
        }

        public object UnregisterWebhook(string id)
        {
            int removed = _webhooks.RemoveAll(w => w.Id == id);
            return new { id, removed = removed > 0 };
        }

        public object ListWebhooks()
        {
            var result = new List<object>();
            foreach (var w in _webhooks)
                result.Add(new { w.Id, w.Url, events = w.Events != null ? (object)new List<string>(w.Events) : "all" });
            return result;
        }

        private void PushEvent(string eventName, object data)
        {
            if (_webhooks.Count == 0) return;
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                @event = eventName,
                day = _dayNightCycle.DayNumber,
                timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                data
            });
            for (int i = 0; i < _webhooks.Count; i++)
            {
                var wh = _webhooks[i];
                if (wh.Events != null && !wh.Events.Contains(eventName)) continue;
                var url = wh.Url;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _webhookClient.PostAsync(url, new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json")).Wait(); }
                    catch (System.Exception _ex) { LogOnce(1001, _ex); }
                });
            }
        }

        // ================================================================
        // WEBHOOK EVENT HANDLERS -- one line per event. Add new events here.
        // All use PushEvent(name, data) which is fire-and-forget on background thread.
        // ================================================================

        // weather
        [OnEvent] public void OnDroughtStart(Timberborn.HazardousWeatherSystem.HazardousWeatherStartedEvent e) { if (_webhooks.Count > 0) PushEvent("drought.start", new { duration = _weatherService.HazardousWeatherDuration }); }
        [OnEvent] public void OnDroughtEnd(Timberborn.HazardousWeatherSystem.HazardousWeatherEndedEvent e) => PushEvent("drought.end", null);
        [OnEvent] public void OnDroughtApproaching(Timberborn.HazardousWeatherSystemUI.HazardousWeatherApproachingEvent e) => PushEvent("drought.approaching", null);
        [OnEvent] public void OnCycleStart(Timberborn.GameCycleSystem.CycleStartedEvent e) { if (_webhooks.Count > 0) PushEvent("cycle.start", new { cycle = _gameCycleService.Cycle }); }
        [OnEvent] public void OnCycleEnd(Timberborn.GameCycleSystem.CycleEndedEvent e) { if (_webhooks.Count > 0) PushEvent("cycle.end", new { cycle = _gameCycleService.Cycle }); }
        [OnEvent] public void OnCycleDay(Timberborn.GameCycleSystem.CycleDayStartedEvent e) { if (_webhooks.Count > 0) PushEvent("cycle.day", new { cycle = _gameCycleService.Cycle, cycleDay = _gameCycleService.CycleDay }); }

        // time
        [OnEvent] public void OnDayStart(Timberborn.TimeSystem.DaytimeStartEvent e) { if (_webhooks.Count > 0) PushEvent("day.start", new { day = _dayNightCycle.DayNumber }); }
        [OnEvent] public void OnNightStart(Timberborn.TimeSystem.NighttimeStartEvent e) { if (_webhooks.Count > 0) PushEvent("night.start", new { day = _dayNightCycle.DayNumber }); }

        // buildings (continued)
        [OnEvent] public void OnBuildingFinished(Timberborn.BlockSystem.EnteredFinishedStateEvent e) { try { var go = e.BlockObject?.GetComponent<EntityComponent>()?.GameObject; PushEvent("building.finished", new { id = go?.GetInstanceID() ?? 0, name = go != null ? CleanName(go.name) : "" }); } catch (System.Exception _ex) { LogOnce(1002, _ex); } }
        [OnEvent] public void OnDistrictChanged(Timberborn.GameDistricts.DistrictCenterRegistryChangedEvent e) => PushEvent("district.changed", null);

        // population
        [OnEvent] public void OnPopulationChanged(Timberborn.Population.PopulationChangedEvent e) => PushEvent("population.changed", null);
        [OnEvent] public void OnCharacterCreated(Timberborn.Characters.CharacterCreatedEvent e) => PushEvent("character.created", null);
        [OnEvent] public void OnCharacterKilled(Timberborn.Characters.CharacterKilledEvent e) => PushEvent("character.killed", null);
        [OnEvent] public void OnBeaverBornEvt(Timberborn.Beavers.BeaverBornEvent e) => PushEvent("beaver.born.event", null);
        [OnEvent] public void OnBotManufactured(Timberborn.BotUpkeep.BotManufacturedEvent e) => PushEvent("bot.manufactured", null);
        [OnEvent] public void OnMigration(Timberborn.GameDistricts.MigrationEvent e) => PushEvent("migration", null);

        // needs/wellbeing
        [OnEvent] public void OnContaminationChanged(Timberborn.BeaverContaminationSystem.ContaminableContaminationChangedEvent e) => PushEvent("contamination.changed", null);
        [OnEvent] public void OnTeethChipped(Timberborn.Healthcare.TeethChippedEvent e) => PushEvent("teeth.chipped", null);
        [OnEvent] public void OnWellbeingHighscore(Timberborn.Wellbeing.NewWellbeingHighscoreEvent e) => PushEvent("wellbeing.highscore", null);
        [OnEvent] public void OnStatusAlert(Timberborn.StatusSystem.StatusAlertAddedEvent e) => PushEvent("status.alert", null);

        // trees/crops
        [OnEvent] public void OnTreeCut(Timberborn.Forestry.TreeCutEvent e) => PushEvent("tree.cut", null);
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

        // buildings
        [OnEvent] public void OnBuildingUnlocked(Timberborn.ScienceSystem.BuildingUnlockedEvent e) => PushEvent("building.unlocked", null);
        [OnEvent] public void OnBuildingDeconstructed(Timberborn.DeconstructionSystem.BuildingDeconstructedEvent e) => PushEvent("building.deconstructed", null);
        [OnEvent] public void OnDemolishableMarked(Timberborn.Demolishing.DemolishableMarkedEvent e) => PushEvent("demolish.marked", null);
        [OnEvent] public void OnDemolishableUnmarked(Timberborn.Demolishing.DemolishableUnmarkedEvent e) => PushEvent("demolish.unmarked", null);

        // game state
        [OnEvent] public void OnGameOver(Timberborn.GameOver.GameOverEvent e) => PushEvent("game.over", null);
        [OnEvent] public void OnSpeedChanged(Timberborn.TimeSystem.CurrentSpeedChangedEvent e) { if (_webhooks.Count > 0) PushEvent("speed.changed", new { speed = _speedManager.CurrentSpeed }); }
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

        // blocks (lower-level than building -- paths, levees, platforms, everything)
        [OnEvent] public void OnBlockSet(Timberborn.BlockSystem.BlockObjectSetEvent e) => PushEvent("block.set", null);
        [OnEvent] public void OnBlockUnset(Timberborn.BlockSystem.BlockObjectUnsetEvent e) => PushEvent("block.unset", null);
        [OnEvent] public void OnConstructionStarted(Timberborn.BlockSystem.EnteredUnfinishedStateEvent e) => PushEvent("construction.started", null);
        [OnEvent] public void OnBuildingUnfinished(Timberborn.BlockSystem.ExitedFinishedStateEvent e) => PushEvent("building.unfinished", null);

        // entities (lower-level)
        [OnEvent] public void OnEntityCreated(Timberborn.EntitySystem.EntityCreatedEvent e) => PushEvent("entity.created", null);

        // factions
        [OnEvent] public void OnFactionUnlocked(Timberborn.FactionSystem.FactionUnlockedEvent e) => PushEvent("faction.unlocked", null);

        // districts (connections)
        [OnEvent] public void OnDistrictConnectionsChanged(Timberborn.GameDistricts.DistrictConnectionsChangedEvent e) => PushEvent("district.connections.changed", null);
        [OnEvent] public void OnMigrationDistrictChanged(Timberborn.GameDistrictsMigration.MigrationDistrictChangedEvent e) => PushEvent("migration.district.changed", null);

        // weather (selection)
        [OnEvent] public void OnWeatherSelected(Timberborn.HazardousWeatherSystem.HazardousWeatherSelectedEvent e) => PushEvent("weather.selected", null);

        // power (detailed)
        [OnEvent] public void OnPowerGeneratorAdded(Timberborn.MechanicalSystem.MechanicalGraphGeneratorAddedEvent e) => PushEvent("power.generator.added", null);
        [OnEvent] public void OnPowerGeneratorUpdated(Timberborn.MechanicalSystem.MechanicalGraphGeneratorUpdatedEvent e) => PushEvent("power.generator.updated", null);

        // planting (coordinates)
        [OnEvent] public void OnPlantingCoordsSet(Timberborn.Planting.PlantingCoordinatesSetEvent e) => PushEvent("planting.coords.set", null);
        [OnEvent] public void OnPlantingCoordsUnset(Timberborn.Planting.PlantingCoordinatesUnsetEvent e) => PushEvent("planting.coords.unset", null);

        // game startup
        [OnEvent] public void OnNewGame(Timberborn.Common.NewGameInitializedEvent e) => PushEvent("game.new", null);
        [OnEvent] public void OnStartingBuilding(Timberborn.GameStartup.StartingBuildingPlacedEvent e) => PushEvent("game.starting.building", null);
        [OnEvent] public void OnSpeedLockChanged(Timberborn.TimeSystem.SpeedLockChangedEvent e) => PushEvent("speed.lock.changed", null);

        // naming + alerts
        [OnEvent] public void OnEntityRenamed(Timberborn.EntityNaming.EntityNameChangedEvent e) => PushEvent("entity.renamed", null);
        [OnEvent] public void OnDynamicAlert(Timberborn.StatusSystem.DynamicStatusAlertAddedEvent e) => PushEvent("status.dynamic.alert", null);

        // construction mode
        [OnEvent] public void OnConstructionMode(Timberborn.ConstructionMode.ConstructionModeChangedEvent e) => PushEvent("construction.mode.changed", null);
    }
}
