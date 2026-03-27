using System;
using System.Collections.Generic;
using System.Threading;
using Timberborn.BlockSystem;
using Timberborn.BuilderPrioritySystem;
using Timberborn.Buildings;
using Timberborn.BuildingsReachability;
using Timberborn.ConstructionSites;
using Timberborn.DwellingSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.InventorySystem;
using Timberborn.MechanicalSystem;
using Timberborn.PowerManagement;
using Timberborn.PrioritySystem;
using Timberborn.RangedEffectSystem;
using Timberborn.Reproduction;
using Timberborn.SingletonSystem;
using Timberborn.StatusSystem;
using Timberborn.WaterBuildings;
using Timberborn.Wonders;
using Timberborn.WorkSystem;
using Timberborn.Workshops;

namespace Timberbot
{
    // Fresh-on-request building snapshots. Main thread owns live component refs and publishes
    // DTO-only snapshots. Listener-thread requests wait for the next publish, then serialize off-thread.
    public class TimberbotBuildingsV2
    {
        private readonly EntityRegistry _entityRegistry;
        private readonly EventBus _eventBus;
        private readonly object _refreshLock = new object();
        private readonly List<TrackedBuildingRef> _tracked = new List<TrackedBuildingRef>();
        private readonly Dictionary<int, TrackedBuildingRef> _trackedById = new Dictionary<int, TrackedBuildingRef>();
        private readonly TimberbotJw _jw = new TimberbotJw(300000);

        private bool _refreshRequested;
        private bool _refreshFullRequested;
        private readonly List<RefreshWaiter> _waiters = new List<RefreshWaiter>();
        private PublishedBuildingsSnapshot _published = PublishedBuildingsSnapshot.Empty;
        private int _publishSequence;

        public TimberbotBuildingsV2(EntityRegistry entityRegistry, EventBus eventBus)
        {
            _entityRegistry = entityRegistry;
            _eventBus = eventBus;
        }

        public int PublishSequence => _publishSequence;
        public int LastPublishedCount => _published.Definitions.Length;
        public float LastPublishedAt => _published.PublishedAt;

        public void Register() => _eventBus.Register(this);
        public void Unregister() => _eventBus.Unregister(this);

        public void BuildAll()
        {
            _tracked.Clear();
            _trackedById.Clear();
            foreach (var ec in _entityRegistry.Entities)
                TryAddTracked(ec);
            Publish(false, 0f);
        }

        public void ProcessPendingRefresh(float now)
        {
            List<RefreshWaiter> toWake = null;
            bool fullDetail = false;
            lock (_refreshLock)
            {
                if (!_refreshRequested) return;
                _refreshRequested = false;
                fullDetail = _refreshFullRequested;
                _refreshFullRequested = false;
                if (_waiters.Count > 0)
                {
                    toWake = new List<RefreshWaiter>(_waiters);
                    _waiters.Clear();
                }
            }

            try
            {
                Publish(fullDetail, now);
            }
            finally
            {
                if (toWake != null)
                    foreach (var waiter in toWake)
                        waiter.Signal.Set();
            }
        }

        public object CollectBuildings(string format = "toon", string detail = "basic", int limit = 100, int offset = 0,
            string filterName = null, int filterX = 0, int filterY = 0, int filterRadius = 0)
        {
            int? singleId = null;
            if (detail != null && detail.StartsWith("id:"))
            {
                if (int.TryParse(detail.Substring(3), out int parsed))
                    singleId = parsed;
            }
            bool fullDetail = detail == "full" || singleId.HasValue;

            PublishedBuildingsSnapshot snapshot;
            try
            {
                snapshot = RequestFreshSnapshot(fullDetail, 2000);
            }
            catch (TimeoutException)
            {
                return _jw.Error("refresh_timeout");
            }

            bool hasFilter = filterName != null || filterRadius > 0;
            bool paginated = limit > 0 && !singleId.HasValue;
            int total = snapshot.Definitions.Length;
            if (paginated && hasFilter)
            {
                total = 0;
                for (int i = 0; i < snapshot.Definitions.Length; i++)
                {
                    var d = snapshot.Definitions[i];
                    if (PassesFilter(d.Name, d.X, d.Y, filterName, filterX, filterY, filterRadius)) total++;
                }
            }

            int skipped = 0, emitted = 0;
            var jw = _jw.Reset();
            if (paginated) jw.OpenObj().Prop("total", total).Prop("offset", offset).Prop("limit", limit).Key("items");
            jw.OpenArr();

            for (int i = 0; i < snapshot.Definitions.Length; i++)
            {
                var d = snapshot.Definitions[i];
                if (singleId.HasValue && d.Id != singleId.Value) continue;
                if (hasFilter && !PassesFilter(d.Name, d.X, d.Y, filterName, filterX, filterY, filterRadius)) continue;
                if (offset > 0 && skipped < offset) { skipped++; continue; }
                if (paginated && emitted >= limit) break;
                emitted++;

                var s = snapshot.States[i];
                BuildingDetailState detailState = null;
                if (fullDetail && snapshot.Details != null && i < snapshot.Details.Length)
                    detailState = snapshot.Details[i];

                jw.OpenObj()
                    .Prop("id", d.Id)
                    .Prop("name", d.Name)
                    .Prop("x", d.X).Prop("y", d.Y).Prop("z", d.Z)
                    .Prop("orientation", d.Orientation ?? "")
                    .Prop("finished", s.Finished)
                    .Prop("paused", s.Paused);

                if (!fullDetail)
                {
                    jw.Prop("priority", s.ConstructionPriority ?? "")
                        .Prop("workers", s.MaxWorkers > 0 ? $"{s.AssignedWorkers}/{s.DesiredWorkers}" : "")
                        .CloseObj();
                    continue;
                }

                jw.Prop("constructionPriority", s.ConstructionPriority ?? "")
                    .Prop("workplacePriority", s.WorkplacePriorityStr ?? "")
                    .Prop("maxWorkers", s.MaxWorkers)
                    .Prop("desiredWorkers", s.DesiredWorkers)
                    .Prop("assignedWorkers", s.AssignedWorkers)
                    .Prop("reachable", s.Reachable)
                    .Prop("powered", s.Powered)
                    .Prop("isGenerator", d.IsGenerator)
                    .Prop("isConsumer", d.IsConsumer)
                    .Prop("nominalPowerInput", d.NominalPowerInput)
                    .Prop("nominalPowerOutput", d.NominalPowerOutput)
                    .Prop("powerDemand", s.PowerDemand)
                    .Prop("powerSupply", s.PowerSupply)
                    .Prop("buildProgress", s.BuildProgress)
                    .Prop("materialProgress", s.MaterialProgress)
                    .Prop("hasMaterials", s.HasMaterials)
                    .Prop("stock", s.Stock)
                    .Prop("capacity", s.Capacity)
                    .Prop("dwellers", s.Dwellers)
                    .Prop("maxDwellers", s.MaxDwellers)
                    .Prop("floodgate", d.HasFloodgate)
                    .Prop("height", d.HasFloodgate != 0 ? s.FloodgateHeight : 0f, "F1")
                    .Prop("maxHeight", d.HasFloodgate != 0 ? d.FloodgateMaxHeight : 0f, "F1")
                    .Prop("isClutch", d.HasClutch)
                    .Prop("clutchEngaged", s.ClutchEngaged)
                    .Prop("currentRecipe", s.CurrentRecipe ?? "")
                    .Prop("productionProgress", s.ProductionProgress)
                    .Prop("readyToProduce", s.ReadyToProduce)
                    .Prop("effectRadius", d.EffectRadius)
                    .Prop("isWonder", d.HasWonder)
                    .Prop("wonderActive", s.WonderActive);

                if (format == "toon")
                {
                    jw.Prop("inventory", detailState?.InventoryToon ?? "")
                        .Prop("recipes", detailState?.RecipesToon ?? "");
                }
                else
                {
                    jw.Obj("inventory");
                    if (detailState?.Inventory != null)
                        foreach (var kvp in detailState.Inventory)
                            jw.Key(kvp.Key).Int(kvp.Value);
                    jw.CloseObj();
                    jw.Arr("recipes");
                    if (detailState?.Recipes != null)
                        for (int ri = 0; ri < detailState.Recipes.Length; ri++)
                            jw.Str(detailState.Recipes[ri]);
                    jw.CloseArr();
                }
                jw.CloseObj();
            }

            jw.CloseArr();
            if (paginated) jw.CloseObj();
            return jw.ToString();
        }

        private PublishedBuildingsSnapshot RequestFreshSnapshot(bool fullDetail, int timeoutMs)
        {
            var waiter = new RefreshWaiter();
            lock (_refreshLock)
            {
                _refreshRequested = true;
                if (fullDetail) _refreshFullRequested = true;
                _waiters.Add(waiter);
            }

            if (!waiter.Signal.Wait(timeoutMs))
            {
                lock (_refreshLock)
                    _waiters.Remove(waiter);
                throw new TimeoutException();
            }
            return _published;
        }

        private void Publish(bool includeDetail, float now)
        {
            var definitions = new BuildingDefinition[_tracked.Count];
            var states = new BuildingState[_tracked.Count];
            var details = includeDetail ? new BuildingDetailState[_tracked.Count] : null;

            for (int i = 0; i < _tracked.Count; i++)
            {
                var t = _tracked[i];
                definitions[i] = t.Definition;
                states[i] = BuildState(t);
                if (includeDetail) details[i] = BuildDetail(t);
            }

            _published = new PublishedBuildingsSnapshot
            {
                Definitions = definitions,
                States = states,
                Details = details,
                PublishedAt = now
            };
            _publishSequence++;
        }

        private static BuildingState BuildState(TrackedBuildingRef t)
        {
            var s = new BuildingState();
            var bo = t.BlockObject;
            if (bo != null) s.Finished = bo.IsFinished ? 1 : 0;
            s.Paused = t.Pausable != null && t.Pausable.Paused ? 1 : 0;
            s.Unreachable = t.Reachability != null && t.Reachability.IsAnyUnreachable() ? 1 : 0;
            s.Reachable = t.Reachability != null ? (s.Unreachable == 0 ? 1 : 0) : 0;
            s.Powered = t.Mechanical != null && t.Mechanical.ActiveAndPowered ? 1 : 0;
            if (t.DistrictBuilding != null)
                s.District = t.DistrictBuilding.District?.DistrictName;
            if (t.Workplace != null)
            {
                s.AssignedWorkers = t.Workplace.NumberOfAssignedWorkers;
                s.DesiredWorkers = t.Workplace.DesiredWorkers;
                s.MaxWorkers = t.Workplace.MaxWorkers;
            }
            if (t.Dwelling != null)
            {
                s.Dwellers = t.Dwelling.NumberOfDwellers;
                s.MaxDwellers = t.Dwelling.MaxBeavers;
            }
            if (t.Floodgate != null)
                s.FloodgateHeight = t.Floodgate.Height;
            s.ConstructionPriority = t.BuilderPrio != null ? TimberbotEntityCache.GetPriorityName(t.BuilderPrio.Priority) : null;
            s.WorkplacePriorityStr = t.WorkplacePrio != null ? TimberbotEntityCache.GetPriorityName(t.WorkplacePrio.Priority) : null;
            if (t.Site != null)
            {
                s.BuildProgress = t.Site.BuildTimeProgress;
                s.MaterialProgress = t.Site.MaterialProgress;
                s.HasMaterials = t.Site.HasMaterialsToResumeBuilding ? 1 : 0;
            }
            if (t.Clutch != null) s.ClutchEngaged = t.Clutch.IsEngaged ? 1 : 0;
            if (t.Wonder != null) s.WonderActive = t.Wonder.IsActive ? 1 : 0;
            if (t.PowerNode != null)
            {
                try
                {
                    var g = t.PowerNode.Graph;
                    if (g != null)
                    {
                        s.PowerDemand = (int)g.PowerDemand;
                        s.PowerSupply = (int)g.PowerSupply;
                        s.PowerNetworkId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g);
                    }
                }
                catch (Exception ex) { TimberbotLog.Error("buildings_v2.power", ex); }
            }
            if (t.Manufactory != null)
            {
                s.CurrentRecipe = t.Manufactory.HasCurrentRecipe ? t.Manufactory.CurrentRecipe.Id : "";
                s.ProductionProgress = t.Manufactory.ProductionProgress;
                s.ReadyToProduce = t.Manufactory.IsReadyToProduce ? 1 : 0;
            }
            if (t.BreedingPod != null)
                s.NeedsNutrients = t.BreedingPod.NeedsNutrients ? 1 : 0;
            if (t.Inventories != null)
            {
                int totalStock = 0, totalCapacity = 0;
                try
                {
                    var allInv = t.Inventories.AllInventories;
                    for (int ii = 0; ii < allInv.Count; ii++)
                    {
                        var inv = allInv[ii];
                        if (inv.ComponentName == ConstructionSiteInventoryInitializer.InventoryComponentName) continue;
                        totalStock += inv.TotalAmountInStock;
                        totalCapacity += inv.Capacity;
                    }
                }
                catch (Exception ex) { TimberbotLog.Error("buildings_v2.stock", ex); }
                s.Stock = totalStock;
                s.Capacity = totalCapacity;
            }
            return s;
        }

        private static BuildingDetailState BuildDetail(TrackedBuildingRef t)
        {
            var d = new BuildingDetailState();
            if (t.Inventories != null)
            {
                var inv = new Dictionary<string, int>();
                try
                {
                    var allInv = t.Inventories.AllInventories;
                    for (int ii = 0; ii < allInv.Count; ii++)
                    {
                        var item = allInv[ii];
                        if (item.ComponentName == ConstructionSiteInventoryInitializer.InventoryComponentName) continue;
                        var stock = item.Stock;
                        for (int si = 0; si < stock.Count; si++)
                        {
                            var ga = stock[si];
                            if (ga.Amount <= 0) continue;
                            if (inv.ContainsKey(ga.GoodId)) inv[ga.GoodId] += ga.Amount;
                            else inv[ga.GoodId] = ga.Amount;
                        }
                    }
                }
                catch (Exception ex) { TimberbotLog.Error("buildings_v2.inventory", ex); }
                d.Inventory = inv;
                d.InventoryToon = ToToonDict(inv);
            }
            if (t.Manufactory != null)
            {
                var recipes = new List<string>();
                foreach (var r in t.Manufactory.ProductionRecipes)
                    recipes.Add(r.Id);
                d.Recipes = recipes.ToArray();
                d.RecipesToon = string.Join("/", d.Recipes);
            }
            if (t.BreedingPod != null)
            {
                var nutrients = new Dictionary<string, int>();
                try
                {
                    foreach (var ga in t.BreedingPod.Nutrients)
                        if (ga.Amount > 0) nutrients[ga.GoodId] = ga.Amount;
                }
                catch (Exception ex) { TimberbotLog.Error("buildings_v2.nutrients", ex); }
                d.NutrientStock = nutrients;
            }
            return d;
        }

        private static string ToToonDict(Dictionary<string, int> dict)
        {
            if (dict == null || dict.Count == 0) return "";
            var sb = new System.Text.StringBuilder(128);
            foreach (var kvp in dict)
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(kvp.Key).Append(':').Append(kvp.Value);
            }
            return sb.ToString();
        }

        private void TryAddTracked(EntityComponent ec)
        {
            if (ec.GetComponent<Building>() == null) return;
            int id = ec.GameObject.GetInstanceID();
            if (_trackedById.ContainsKey(id)) return;

            var t = new TrackedBuildingRef
            {
                Id = id,
                Name = TimberbotEntityCache.CleanName(ec.GameObject.name),
                BlockObject = ec.GetComponent<BlockObject>(),
                Pausable = ec.GetComponent<PausableBuilding>(),
                Floodgate = ec.GetComponent<Floodgate>(),
                BuilderPrio = ec.GetComponent<BuilderPrioritizable>(),
                Workplace = ec.GetComponent<Workplace>(),
                WorkplacePrio = ec.GetComponent<WorkplacePriority>(),
                Reachability = ec.GetComponent<EntityReachabilityStatus>(),
                Mechanical = ec.GetComponent<MechanicalBuilding>(),
                Status = ec.GetComponent<StatusSubject>(),
                PowerNode = ec.GetComponent<MechanicalNode>(),
                Site = ec.GetComponent<ConstructionSite>(),
                Inventories = ec.GetComponent<Inventories>(),
                Wonder = ec.GetComponent<Wonder>(),
                Dwelling = ec.GetComponent<Dwelling>(),
                Clutch = ec.GetComponent<Clutch>(),
                Manufactory = ec.GetComponent<Manufactory>(),
                BreedingPod = ec.GetComponent<BreedingPod>(),
                RangedEffect = ec.GetComponent<RangedEffectBuildingSpec>(),
                DistrictBuilding = ec.GetComponent<DistrictBuilding>(),
            };

            var def = new BuildingDefinition
            {
                Id = id,
                Name = t.Name,
                HasFloodgate = t.Floodgate != null ? 1 : 0,
                FloodgateMaxHeight = t.Floodgate?.MaxHeight ?? 0f,
                HasClutch = t.Clutch != null ? 1 : 0,
                HasWonder = t.Wonder != null ? 1 : 0,
                IsGenerator = t.PowerNode?.IsGenerator == true ? 1 : 0,
                IsConsumer = t.PowerNode?.IsConsumer == true ? 1 : 0,
                NominalPowerInput = t.PowerNode?._nominalPowerInput ?? 0,
                NominalPowerOutput = t.PowerNode?._nominalPowerOutput ?? 0,
                EffectRadius = t.RangedEffect?.EffectRadius ?? 0
            };

            var bo = t.BlockObject;
            if (bo != null)
            {
                var coords = bo.Coordinates;
                def.X = coords.x;
                def.Y = coords.y;
                def.Z = coords.z;
                def.Orientation = TimberbotEntityCache.OrientNames[(int)bo.Orientation];
                var occupied = new List<(int, int, int)>();
                try
                {
                    foreach (var block in bo.PositionedBlocks.GetAllBlocks())
                    {
                        var tc = block.Coordinates;
                        occupied.Add((tc.x, tc.y, tc.z));
                    }
                }
                catch { occupied.Add((def.X, def.Y, def.Z)); }
                def.OccupiedTiles = occupied.ToArray();
                if (bo.HasEntrance)
                {
                    try
                    {
                        var ent = bo.PositionedEntrance.DoorstepCoordinates;
                        def.HasEntrance = 1;
                        def.EntranceX = ent.x;
                        def.EntranceY = ent.y;
                    }
                    catch (Exception ex) { TimberbotLog.Error("buildings_v2.entrance", ex); }
                }
            }

            t.Definition = def;
            _tracked.Add(t);
            _trackedById[id] = t;
        }

        private void RemoveTracked(EntityComponent ec)
        {
            int id = ec.GameObject.GetInstanceID();
            if (!_trackedById.TryGetValue(id, out var tracked)) return;
            _trackedById.Remove(id);
            _tracked.Remove(tracked);
        }

        [OnEvent]
        public void OnEntityInitialized(EntityInitializedEvent e)
        {
            TryAddTracked(e.Entity);
        }

        [OnEvent]
        public void OnEntityDeleted(EntityDeletedEvent e)
        {
            RemoveTracked(e.Entity);
        }

        private static bool PassesFilter(string entityName, int entityX, int entityY,
            string filterName, int filterX, int filterY, int filterRadius)
        {
            if (filterName != null && entityName.IndexOf(filterName, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (filterRadius > 0 && (Math.Abs(entityX - filterX) + Math.Abs(entityY - filterY)) > filterRadius)
                return false;
            return true;
        }

        private sealed class RefreshWaiter
        {
            public readonly ManualResetEventSlim Signal = new ManualResetEventSlim(false);
        }

        private sealed class TrackedBuildingRef
        {
            public int Id;
            public string Name;
            public BlockObject BlockObject;
            public PausableBuilding Pausable;
            public Floodgate Floodgate;
            public BuilderPrioritizable BuilderPrio;
            public Workplace Workplace;
            public WorkplacePriority WorkplacePrio;
            public EntityReachabilityStatus Reachability;
            public MechanicalBuilding Mechanical;
            public StatusSubject Status;
            public MechanicalNode PowerNode;
            public ConstructionSite Site;
            public Inventories Inventories;
            public Wonder Wonder;
            public Dwelling Dwelling;
            public Clutch Clutch;
            public Manufactory Manufactory;
            public BreedingPod BreedingPod;
            public RangedEffectBuildingSpec RangedEffect;
            public DistrictBuilding DistrictBuilding;
            public BuildingDefinition Definition;
        }

        private sealed class PublishedBuildingsSnapshot
        {
            public static readonly PublishedBuildingsSnapshot Empty = new PublishedBuildingsSnapshot
            {
                Definitions = Array.Empty<BuildingDefinition>(),
                States = Array.Empty<BuildingState>(),
                Details = null,
                PublishedAt = 0f
            };

            public BuildingDefinition[] Definitions;
            public BuildingState[] States;
            public BuildingDetailState[] Details;
            public float PublishedAt;
        }

        private sealed class BuildingDefinition
        {
            public int Id;
            public string Name;
            public int X, Y, Z;
            public string Orientation;
            public int HasFloodgate;
            public float FloodgateMaxHeight;
            public int HasClutch;
            public int HasWonder;
            public int IsGenerator, IsConsumer;
            public int NominalPowerInput, NominalPowerOutput;
            public int EffectRadius;
            public (int x, int y, int z)[] OccupiedTiles;
            public int HasEntrance;
            public int EntranceX, EntranceY;
        }

        private sealed class BuildingState
        {
            public int Finished, Paused, Unreachable, Reachable, Powered;
            public string District;
            public int AssignedWorkers, DesiredWorkers, MaxWorkers;
            public int Dwellers, MaxDwellers;
            public float FloodgateHeight;
            public string ConstructionPriority, WorkplacePriorityStr;
            public float BuildProgress, MaterialProgress;
            public int HasMaterials;
            public int ClutchEngaged, WonderActive;
            public int PowerDemand, PowerSupply, PowerNetworkId;
            public string CurrentRecipe;
            public float ProductionProgress;
            public int ReadyToProduce;
            public int NeedsNutrients;
            public int Stock, Capacity;
        }

        private sealed class BuildingDetailState
        {
            public Dictionary<string, int> Inventory;
            public string InventoryToon;
            public string[] Recipes;
            public string RecipesToon;
            public Dictionary<string, int> NutrientStock;
        }
    }
}
