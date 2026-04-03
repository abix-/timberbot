using System;
using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.Bots;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.Goods;
using Timberborn.PrioritySystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace Timberbot
{
    // TimberbotEntityRegistry.cs -- Entity lookup and ID translation.
    //
    // WHY TWO ID SYSTEMS
    // -------------------
    // Timberborn internally identifies entities by GUID (EntityComponent.EntityId).
    // But GUIDs are terrible for humans typing API calls ("set_workers id:abc123-...").
    // So the public API uses short numeric IDs (Unity's GameObject.GetInstanceID()),
    // and this class translates between them.
    //
    // Also holds shared constants (faction suffix, species lists, priority names)
    // and the EventBus lifecycle hooks that keep ReadV2's tracked refs in sync.
    public class TimberbotEntityRegistry
    {
        private readonly EntityRegistry _entityRegistry;
        private readonly TreeCuttingArea _treeCuttingArea;
        private readonly EventBus _eventBus;
        private readonly IGoodService _goodService;
        private readonly Dictionary<int, Guid> _legacyToEntityId = new Dictionary<int, Guid>();
        private readonly Dictionary<Guid, int> _entityIdToLegacy = new Dictionary<Guid, int>();

        public TimberbotWebhook WebhookMgr;

        public static readonly HashSet<string> TreeSpecies = new HashSet<string>
            { "Pine", "Birch", "Oak", "Maple", "Chestnut", "Mangrove" };
        public static readonly HashSet<string> CropSpecies = new HashSet<string>
            { "Kohlrabi", "Soybean", "Corn", "Sunflower", "Eggplant", "Algae", "Cassava", "Mushroom", "Potato", "Wheat", "Carrot" };
        public static string FactionSuffix = "";
        public static readonly string[] OrientNames = { "south", "west", "north", "east" };
        public static readonly string[] PriorityNames = { "VeryLow", "Low", "Normal", "High", "VeryHigh" };

        public TimberbotEntityRegistry(
            EntityRegistry entityRegistry,
            TreeCuttingArea treeCuttingArea,
            EventBus eventBus,
            IGoodService goodService)
        {
            _entityRegistry = entityRegistry;
            _treeCuttingArea = treeCuttingArea;
            _eventBus = eventBus;
            _goodService = goodService;
        }

        public void Register() => _eventBus.Register(this);
        public void Unregister() => _eventBus.Unregister(this);

        public static string GetPriorityName(Priority p)
        {
            int i = (int)p;
            return (i >= 0 && i < PriorityNames.Length) ? PriorityNames[i] : "Normal";
        }

        public static string CanonicalName(string name) => TimberbotPure.CanonicalName(name);

        public static string CleanName(string name) => TimberbotPure.CleanName(name, FactionSuffix);

        public IReadOnlyList<string> AllGoodIds => _goodService.Goods;

        public bool TreeInCuttingArea(Vector3Int coords) => _treeCuttingArea.IsInCuttingArea(coords);

        public EntityComponent FindEntity(int id)
        {
            if (!TryGetEntityId(id, out var entityId))
                return null;

            var ec = FindEntity(entityId);
            if (ec != null)
                return ec;

            _legacyToEntityId.Remove(id);
            if (entityId != Guid.Empty)
                _entityIdToLegacy.Remove(entityId);
            return null;
        }

        public EntityComponent FindEntity(Guid entityId)
            => entityId == Guid.Empty ? null : _entityRegistry.GetEntity(entityId);

        public bool TryGetEntityId(int legacyId, out Guid entityId)
            => _legacyToEntityId.TryGetValue(legacyId, out entityId);

        public bool TryGetLegacyId(Guid entityId, out int legacyId)
        {
            if (_entityIdToLegacy.TryGetValue(entityId, out legacyId))
                return true;

            var ec = FindEntity(entityId);
            if (ec == null)
            {
                legacyId = 0;
                return false;
            }

            legacyId = GetLegacyId(ec);
            return legacyId != 0;
        }

        public int GetLegacyId(EntityComponent ec)
        {
            if (ec == null || ec.GameObject == null)
                return 0;

            int legacyId = ec.GameObject.GetInstanceID();
            var entityId = ec.EntityId;
            if (legacyId != 0 && entityId != Guid.Empty)
            {
                _legacyToEntityId[legacyId] = entityId;
                _entityIdToLegacy[entityId] = legacyId;
            }
            return legacyId;
        }

        public void BuildAllIndexes()
        {
            _legacyToEntityId.Clear();
            _entityIdToLegacy.Clear();
            foreach (var ec in _entityRegistry.Entities)
                IndexEntity(ec);
        }

        private void IndexEntity(EntityComponent ec)
        {
            if (ec == null || ec.GameObject == null)
                return;

            var entityId = ec.EntityId;
            if (entityId == Guid.Empty)
                return;

            int legacyId = ec.GameObject.GetInstanceID();
            if (legacyId == 0)
                return;

            _legacyToEntityId[legacyId] = entityId;
            _entityIdToLegacy[entityId] = legacyId;
        }

        private void RemoveEntity(EntityComponent ec)
        {
            if (ec == null)
                return;

            var entityId = ec.EntityId;
            if (entityId != Guid.Empty && _entityIdToLegacy.TryGetValue(entityId, out var legacyId))
            {
                _entityIdToLegacy.Remove(entityId);
                _legacyToEntityId.Remove(legacyId);
                return;
            }

            if (ec.GameObject != null)
                _legacyToEntityId.Remove(ec.GameObject.GetInstanceID());
        }

        [OnEvent]
        public void OnEntityInitialized(EntityInitializedEvent e)
        {
            IndexEntity(e.Entity);

            if (WebhookMgr == null || WebhookMgr.Count <= 0)
                return;

            var ec = e.Entity;
            if (ec.GetComponent<Timberborn.Buildings.Building>() != null)
                WebhookMgr.PushEvent("building.placed", WebhookMgr.DataEntity(GetLegacyId(ec), CanonicalName(ec.GameObject.name)));
            else if (ec.GetComponent<Timberborn.NeedSystem.NeedManager>() != null)
                WebhookMgr.PushEvent("beaver.born", WebhookMgr.DataEntityBot(GetLegacyId(ec), CanonicalName(ec.GameObject.name), ec.GetComponent<Bot>() != null));
        }

        [OnEvent]
        public void OnEntityDeleted(EntityDeletedEvent e)
        {
            if (WebhookMgr != null && WebhookMgr.Count > 0)
            {
                var ec = e.Entity;
                if (ec.GetComponent<Timberborn.Buildings.Building>() != null)
                    WebhookMgr.PushEvent("building.demolished", WebhookMgr.DataEntity(GetLegacyId(ec), CanonicalName(ec.GameObject.name)));
                else if (ec.GetComponent<Timberborn.NeedSystem.NeedManager>() != null)
                    WebhookMgr.PushEvent("beaver.died", WebhookMgr.DataEntity(GetLegacyId(ec), CanonicalName(ec.GameObject.name)));
            }

            RemoveEntity(e.Entity);
        }
    }
}

