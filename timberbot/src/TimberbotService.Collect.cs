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
        // ================================================================
        // READ ENDPOINTS
        // Each returns an object serialized to JSON. The "format" param controls shape:
        //   toon: flat dicts/lists for tabular TOON display (default for CLI)
        //   json: full nested data for programmatic access (--json flag)
        // ================================================================

        // PERF: uses typed indexes instead of scanning all entities.
        // Three passes over subsets (buildings, natural resources, beavers) instead of one pass over everything.
        public object CollectSummary(string format = "toon")
        {
            int treeMarkedGrown = 0, treeMarkedSeedling = 0, treeUnmarkedGrown = 0;
            int cropReady = 0, cropGrowing = 0;
            int occupiedBeds = 0, totalBeds = 0;
            int totalVacancies = 0, assignedWorkers = 0;
            float totalWellbeing = 0f;
            int beaverCount = 0;
            int alertUnstaffed = 0, alertUnpowered = 0, alertUnreachable = 0;
            int miserable = 0, critical = 0;

            // natural resources: split into trees vs crops
            var _cropNames = new System.Collections.Generic.HashSet<string>
                { "Kohlrabi", "Soybean", "Corn", "Sunflower", "Eggplant", "Algae", "Cassava", "Mushroom", "Potato", "Wheat", "Carrot" };
            foreach (var c in _naturalResources.Read)
            {
                if (c.Cuttable == null) continue;
                if (!c.Alive) continue;
                if (_cropNames.Contains(c.Name))
                {
                    if (c.Grown) cropReady++;
                    else cropGrowing++;
                }
                else
                {
                    if (c.Marked && c.Grown) treeMarkedGrown++;
                    else if (c.Marked && !c.Grown) treeMarkedSeedling++;
                    else if (!c.Marked && c.Grown) treeUnmarkedGrown++;
                }
            }

            // buildings (read cached primitives only -- zero Unity calls)
            foreach (var c in _buildings.Read)
            {
                if (c.Dwelling != null)
                {
                    occupiedBeds += c.Dwellers;
                    totalBeds += c.MaxDwellers;
                }
                if (c.Workplace != null)
                {
                    assignedWorkers += c.AssignedWorkers;
                    totalVacancies += c.DesiredWorkers;
                    if (c.DesiredWorkers > 0 && c.AssignedWorkers < c.DesiredWorkers)
                        alertUnstaffed++;
                }
                if (c.IsConsumer && !c.Powered)
                    alertUnpowered++;
                if (c.Unreachable)
                    alertUnreachable++;
            }

            // beavers: cached wellbeing + critical needs
            foreach (var c in _beavers.Read)
            {
                totalWellbeing += c.Wellbeing;
                beaverCount++;
                if (c.Wellbeing < 4) miserable++;
                if (c.AnyCritical) critical++;
            }
            // count adults only (children can't work, shouldn't count as idle haulers)
            int totalAdults = 0;
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var pop = dc.GetComponent<DistrictPopulation>();
                if (pop != null) totalAdults += pop.NumberOfAdults;
            }
            int homeless = System.Math.Max(0, beaverCount - occupiedBeds);
            int unemployed = System.Math.Max(0, totalAdults - assignedWorkers);
            float avgWellbeing = beaverCount > 0 ? totalWellbeing / beaverCount : 0;

            if (format == "json")
            {
                return new
                {
                    time = CollectTime(),
                    weather = CollectWeather(),
                    districts = CollectDistricts("json"),
                    trees = new { markedGrown = treeMarkedGrown, markedSeedling = treeMarkedSeedling, unmarkedGrown = treeUnmarkedGrown },
                    crops = new { ready = cropReady, growing = cropGrowing },
                    housing = new { occupiedBeds, totalBeds, homeless },
                    employment = new { assigned = assignedWorkers, vacancies = totalVacancies, unemployed },
                    wellbeing = new { average = System.Math.Round(avgWellbeing, 1), miserable, critical },
                    science = _scienceService.SciencePoints,
                    alerts = new { unstaffed = alertUnstaffed, unpowered = alertUnpowered, unreachable = alertUnreachable }
                };
            }

            // build flat summary matching TOON output format
            var flat = new Dictionary<string, object>();

            // time
            flat["day"] = _dayNightCycle.DayNumber;
            flat["dayProgress"] = System.Math.Round(_dayNightCycle.DayProgress, 2);

            // weather
            flat["cycle"] = _gameCycleService.Cycle;
            flat["cycleDay"] = _gameCycleService.CycleDay;
            flat["isHazardous"] = _weatherService.IsHazardousWeather;
            flat["tempDays"] = _weatherService.TemperateWeatherDuration;
            flat["hazardDays"] = _weatherService.HazardousWeatherDuration;

            // trees (actual trees only, not crops)
            flat["markedGrown"] = treeMarkedGrown;
            flat["markedSeedling"] = treeMarkedSeedling;
            flat["unmarkedGrown"] = treeUnmarkedGrown;
            // crops
            flat["cropReady"] = cropReady;
            flat["cropGrowing"] = cropGrowing;

            // population + resources (first district)
            var goods = _goodService.Goods;
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var pop = dc.DistrictPopulation;
                flat["adults"] = pop.NumberOfAdults;
                flat["children"] = pop.NumberOfChildren;
                flat["bots"] = pop.NumberOfBots;
                var counter = dc.GetComponent<DistrictResourceCounter>();
                if (counter != null)
                {
                    foreach (var goodId in goods)
                    {
                        var rc = counter.GetResourceCount(goodId);
                        if (rc.AllStock > 0)
                            flat[goodId] = rc.AvailableStock;
                    }
                }
            }

            // resource projection
            int totalPop = beaverCount;
            if (totalPop > 0)
            {
                int totalFood = 0;
                int totalWater = 0;
                foreach (var kv in flat)
                {
                    if (kv.Value is int stock && stock > 0)
                    {
                        var g = kv.Key;
                        if (g == "Water") totalWater += stock;
                        else if (g == "Berries" || g == "Kohlrabi" || g == "Carrot" || g == "Potato"
                              || g == "Wheat" || g == "Bread" || g == "Cassava" || g == "Corn"
                              || g == "Eggplant" || g == "Soybean" || g == "MapleSyrup")
                            totalFood += stock;
                    }
                }
                flat["foodDays"] = System.Math.Round((double)totalFood / totalPop, 1);
                flat["waterDays"] = System.Math.Round((double)totalWater / (totalPop * 2.0), 1);

                // material projection -- stock / pop, same rough estimate as food/water
                int logs = flat.ContainsKey("Log") && flat["Log"] is int ls ? ls : 0;
                int planks = flat.ContainsKey("Plank") && flat["Plank"] is int ps ? ps : 0;
                int gears = flat.ContainsKey("Gear") && flat["Gear"] is int gs ? gs : 0;
                flat["logDays"] = System.Math.Round((double)logs / totalPop, 1);
                flat["plankDays"] = System.Math.Round((double)planks / totalPop, 1);
                flat["gearDays"] = System.Math.Round((double)gears / totalPop, 1);
            }

            // housing
            flat["beds"] = $"{occupiedBeds}/{totalBeds}";
            flat["homeless"] = homeless;

            // employment
            flat["workers"] = $"{assignedWorkers}/{totalVacancies}";
            flat["unemployed"] = unemployed;

            // wellbeing
            flat["wellbeing"] = System.Math.Round(avgWellbeing, 1);
            flat["miserable"] = miserable;
            flat["critical"] = critical;

            // science
            flat["science"] = _scienceService.SciencePoints;

            // alerts
            var alertParts = new List<string>();
            if (alertUnstaffed > 0) alertParts.Add($"{alertUnstaffed} unstaffed");
            if (alertUnpowered > 0) alertParts.Add($"{alertUnpowered} unpowered");
            if (alertUnreachable > 0) alertParts.Add($"{alertUnreachable} unreachable");
            flat["alerts"] = alertParts.Count > 0 ? string.Join(", ", alertParts) : "none";

            return flat;
        }

        // PERF: iterates _buildings.Read instead of all entities.
        public object CollectAlerts()
        {
            var alerts = new List<object>();
            foreach (var c in _buildings.Read)
            {
                if (c.Workplace != null && c.DesiredWorkers > 0 && c.AssignedWorkers < c.DesiredWorkers)
                    alerts.Add(new { type = "unstaffed", id = c.Id, name = c.Name, workers = $"{c.AssignedWorkers}/{c.DesiredWorkers}" });

                if (c.IsConsumer && !c.Powered)
                    alerts.Add(new { type = "unpowered", id = c.Id, name = c.Name });

                if (c.Unreachable)
                    alerts.Add(new { type = "unreachable", id = c.Id, name = c.Name });
            }
            return alerts;
        }

        // PERF: O(n) entity scan + grid bucketing. Called occasionally for tree management.
        public object CollectTreeClusters(int cellSize = 10, int top = 5)
        {
            var cells = new Dictionary<long, int[]>(); // key -> [grown, total, centerX, centerY, z]
            foreach (var nr in _naturalResources.Read)
            {
                if (nr.Cuttable == null) continue;
                if (nr.Living == null || nr.Living.IsDead) continue;
                if (nr.BlockObject == null) continue;

                var c = nr.BlockObject.Coordinates;
                int cx = c.x / cellSize * cellSize + cellSize / 2;
                int cy = c.y / cellSize * cellSize + cellSize / 2;
                long key = (long)cx * 100000 + cy;

                if (!cells.ContainsKey(key))
                    cells[key] = new int[] { 0, 0, cx, cy, c.z };

                cells[key][1]++;
                if (nr.Growable != null && nr.Growable.IsGrown)
                    cells[key][0]++;
            }

            var sorted = new List<int[]>(cells.Values);
            sorted.Sort((a, b) => b[0].CompareTo(a[0]));
            var results = new List<object>();
            for (int i = 0; i < System.Math.Min(top, sorted.Count); i++)
            {
                var s = sorted[i];
                results.Add(new { x = s[2], y = s[3], z = s[4], grown = s[0], total = s[1] });
            }
            return results;
        }

        public object CollectTime()
        {
            return new
            {
                dayNumber = _dayNightCycle.DayNumber,
                dayProgress = _dayNightCycle.DayProgress,
                partialDayNumber = _dayNightCycle.PartialDayNumber
            };
        }

        public object CollectWeather()
        {
            return new
            {
                cycle = _gameCycleService.Cycle,
                cycleDay = _gameCycleService.CycleDay,
                isHazardous = _weatherService.IsHazardousWeather,
                temperateWeatherDuration = _weatherService.TemperateWeatherDuration,
                hazardousWeatherDuration = _weatherService.HazardousWeatherDuration,
                cycleLengthInDays = _weatherService.CycleLengthInDays
            };
        }

        public object CollectDistricts(string format = "toon")
        {
            var goods = _goodService.Goods;
            var results = new List<object>();

            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var counter = dc.GetComponent<DistrictResourceCounter>();
                var pop = dc.DistrictPopulation;

                if (format == "toon")
                {
                    var row = new Dictionary<string, object>
                    {
                        ["name"] = dc.DistrictName,
                        ["adults"] = pop != null ? pop.NumberOfAdults : 0,
                        ["children"] = pop != null ? pop.NumberOfChildren : 0,
                        ["bots"] = pop != null ? pop.NumberOfBots : 0
                    };
                    if (counter != null)
                    {
                        foreach (var goodId in goods)
                        {
                            var rc = counter.GetResourceCount(goodId);
                            if (rc.AllStock > 0)
                                row[goodId] = rc.AvailableStock;
                        }
                    }
                    results.Add(row);
                }
                else
                {
                    var resources = new Dictionary<string, object>();
                    if (counter != null)
                    {
                        foreach (var goodId in goods)
                        {
                            var rc = counter.GetResourceCount(goodId);
                            if (rc.AllStock > 0)
                                resources[goodId] = new { available = rc.AvailableStock, all = rc.AllStock };
                        }
                    }
                    results.Add(new
                    {
                        name = dc.DistrictName,
                        population = new
                        {
                            adults = pop != null ? pop.NumberOfAdults : 0,
                            children = pop != null ? pop.NumberOfChildren : 0,
                            bots = pop != null ? pop.NumberOfBots : 0
                        },
                        resources
                    });
                }
            }

            return results;
        }

        public object CollectResources(string format = "toon")
        {
            var goods = _goodService.Goods;

            if (format == "toon")
            {
                var flat = new List<object>();
                foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    var counter = dc.GetComponent<DistrictResourceCounter>();
                    if (counter == null) continue;
                    foreach (var goodId in goods)
                    {
                        var rc = counter.GetResourceCount(goodId);
                        if (rc.AllStock > 0)
                            flat.Add(new { district = dc.DistrictName, good = goodId, available = rc.AvailableStock, all = rc.AllStock });
                    }
                }
                return flat;
            }

            var results = new Dictionary<string, object>();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var counter = dc.GetComponent<DistrictResourceCounter>();
                if (counter == null) continue;
                var distResources = new Dictionary<string, object>();
                foreach (var goodId in goods)
                {
                    var rc = counter.GetResourceCount(goodId);
                    if (rc.AllStock > 0)
                        distResources[goodId] = new { available = rc.AvailableStock, all = rc.AllStock };
                }
                results[dc.DistrictName] = distResources;
            }
            return results;
        }

        public object CollectPopulation()
        {
            var results = new List<object>();
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                var pop = dc.DistrictPopulation;
                results.Add(new
                {
                    district = dc.DistrictName,
                    adults = pop != null ? pop.NumberOfAdults : 0,
                    children = pop != null ? pop.NumberOfChildren : 0,
                    bots = pop != null ? pop.NumberOfBots : 0
                });
            }
            return results;
        }

        // PERF: StringBuilder serialization for buildings. Zero Dictionary alloc.
        public object CollectBuildings(string format = "toon", string detail = "basic")
        {
            int? singleId = null;
            if (detail != null && detail.StartsWith("id:"))
            {
                if (int.TryParse(detail.Substring(3), out int parsed))
                    singleId = parsed;
            }
            bool fullDetail = detail == "full" || singleId.HasValue;

            var sb = _sbBuildings;
            sb.Clear();
            Jw.OpenArr(sb);
            bool first = true;
            foreach (var c in _buildings.Read)
            {
                if (singleId.HasValue && c.Id != singleId.Value)
                    continue;
                if (!first) Jw.Sep(sb);
                first = false;

                Jw.Open(sb);
                Jw.KeyFirst(sb, "id"); Jw.Int(sb, c.Id);
                Jw.Key(sb, "name"); Jw.Str(sb, c.Name);
                Jw.Key(sb, "x"); Jw.Int(sb, c.X);
                Jw.Key(sb, "y"); Jw.Int(sb, c.Y);
                Jw.Key(sb, "z"); Jw.Int(sb, c.Z);
                Jw.Key(sb, "orientation"); Jw.Str(sb, c.Orientation ?? "");
                Jw.Key(sb, "finished"); Jw.Bool(sb, c.Finished);
                Jw.Key(sb, "paused"); Jw.Bool(sb, c.Paused);

                if (!fullDetail)
                {
                    Jw.Key(sb, "priority"); Jw.Str(sb, c.ConstructionPriority ?? "");
                    Jw.Key(sb, "workers"); Jw.Str(sb, c.Workplace != null ? $"{c.AssignedWorkers}/{c.DesiredWorkers}" : "");
                    Jw.Close(sb);
                    continue;
                }

                // full detail
                if (c.Pausable != null) { Jw.Key(sb, "pausable"); Jw.Bool(sb, true); }
                if (c.HasFloodgate) { Jw.Key(sb, "floodgate"); Jw.Bool(sb, true); Jw.Key(sb, "height"); Jw.Float(sb, c.FloodgateHeight, "F1"); Jw.Key(sb, "maxHeight"); Jw.Float(sb, c.FloodgateMaxHeight, "F1"); }
                if (c.ConstructionPriority != null) { Jw.Key(sb, "constructionPriority"); Jw.Str(sb, c.ConstructionPriority); }
                if (c.WorkplacePriorityStr != null) { Jw.Key(sb, "workplacePriority"); Jw.Str(sb, c.WorkplacePriorityStr); }
                if (c.Workplace != null) { Jw.Key(sb, "maxWorkers"); Jw.Int(sb, c.MaxWorkers); Jw.Key(sb, "desiredWorkers"); Jw.Int(sb, c.DesiredWorkers); Jw.Key(sb, "assignedWorkers"); Jw.Int(sb, c.AssignedWorkers); }
                if (c.Reachability != null) { Jw.Key(sb, "reachable"); Jw.Bool(sb, !c.Unreachable); }
                if (c.Mechanical != null) { Jw.Key(sb, "powered"); Jw.Bool(sb, c.Powered); }
                if (c.PowerNode != null)
                {
                    Jw.Key(sb, "isGenerator"); Jw.Bool(sb, c.IsGenerator);
                    Jw.Key(sb, "isConsumer"); Jw.Bool(sb, c.IsConsumer);
                    Jw.Key(sb, "nominalPowerInput"); Jw.Int(sb, c.NominalPowerInput);
                    Jw.Key(sb, "nominalPowerOutput"); Jw.Int(sb, c.NominalPowerOutput);
                    if (c.PowerDemand > 0 || c.PowerSupply > 0) { Jw.Key(sb, "powerDemand"); Jw.Int(sb, c.PowerDemand); Jw.Key(sb, "powerSupply"); Jw.Int(sb, c.PowerSupply); }
                }
                if (c.Site != null) { Jw.Key(sb, "buildProgress"); Jw.Float(sb, c.BuildProgress); Jw.Key(sb, "materialProgress"); Jw.Float(sb, c.MaterialProgress); Jw.Key(sb, "hasMaterials"); Jw.Bool(sb, c.HasMaterials); }
                if (c.Capacity > 0)
                {
                    Jw.Key(sb, "stock"); Jw.Int(sb, c.Stock);
                    Jw.Key(sb, "capacity"); Jw.Int(sb, c.Capacity);
                    if (c.Inventory != null && c.Inventory.Count > 0)
                    {
                        Jw.Key(sb, "inventory"); Jw.Open(sb);
                        bool ifirst = true;
                        foreach (var kvp in c.Inventory)
                        {
                            if (!ifirst) Jw.Sep(sb);
                            else { Jw.KeyFirst(sb, kvp.Key); ifirst = false; Jw.Int(sb, kvp.Value); continue; }
                            Jw.KeyFirst(sb, kvp.Key); Jw.Int(sb, kvp.Value);
                        }
                        Jw.Close(sb);
                    }
                }
                if (c.HasWonder) { Jw.Key(sb, "isWonder"); Jw.Bool(sb, true); Jw.Key(sb, "wonderActive"); Jw.Bool(sb, c.WonderActive); }
                if (c.Dwelling != null) { Jw.Key(sb, "dwellers"); Jw.Int(sb, c.Dwellers); Jw.Key(sb, "maxDwellers"); Jw.Int(sb, c.MaxDwellers); }
                if (c.HasClutch) { Jw.Key(sb, "isClutch"); Jw.Bool(sb, true); Jw.Key(sb, "clutchEngaged"); Jw.Bool(sb, c.ClutchEngaged); }
                if (c.Manufactory != null)
                {
                    if (c.Recipes != null && c.Recipes.Count > 0)
                    {
                        Jw.Key(sb, "recipes"); Jw.OpenArr(sb);
                        for (int ri = 0; ri < c.Recipes.Count; ri++)
                        {
                            if (ri > 0) Jw.Sep(sb);
                            Jw.Str(sb, c.Recipes[ri]);
                        }
                        Jw.CloseArr(sb);
                    }
                    Jw.Key(sb, "currentRecipe"); Jw.Str(sb, c.CurrentRecipe ?? "");
                    Jw.Key(sb, "productionProgress"); Jw.Float(sb, c.ProductionProgress);
                    Jw.Key(sb, "readyToProduce"); Jw.Bool(sb, c.ReadyToProduce);
                }
                if (c.BreedingPod != null)
                {
                    Jw.Key(sb, "needsNutrients"); Jw.Bool(sb, c.NeedsNutrients);
                    if (c.NutrientStock != null && c.NutrientStock.Count > 0)
                    {
                        Jw.Key(sb, "nutrients"); Jw.Open(sb);
                        bool nfirst = true;
                        foreach (var kvp in c.NutrientStock)
                        {
                            if (!nfirst) Jw.Sep(sb);
                            else { Jw.KeyFirst(sb, kvp.Key); nfirst = false; Jw.Int(sb, kvp.Value); continue; }
                            Jw.KeyFirst(sb, kvp.Key); Jw.Int(sb, kvp.Value);
                        }
                        Jw.Close(sb);
                    }
                }
                if (c.EffectRadius > 0) { Jw.Key(sb, "effectRadius"); Jw.Int(sb, c.EffectRadius); }
                Jw.Close(sb);
            }
            Jw.CloseArr(sb);
            return sb.ToString();
        }

        // PERF: cached component refs -- zero GetComponent per item.
        // serial param: dict (default), anon, sb -- for A/B testing serialization methods
        // PERF: StringBuilder serialization -- 2ms for 3000 trees. No Dictionary, no Newtonsoft.
        private object CollectNaturalResourcesSb(System.Text.StringBuilder sb, System.Collections.Generic.HashSet<string> species)
        {
            sb.Clear();
            Jw.OpenArr(sb);
            bool first = true;
            foreach (var c in _naturalResources.Read)
            {
                if (c.Cuttable == null) continue;
                if (!species.Contains(c.Name)) continue;
                if (!first) Jw.Sep(sb);
                first = false;
                Jw.Open(sb);
                Jw.KeyFirst(sb, "id"); Jw.Int(sb, c.Id);
                Jw.Key(sb, "name"); Jw.Str(sb, c.Name);
                Jw.Key(sb, "x"); Jw.Int(sb, c.X);
                Jw.Key(sb, "y"); Jw.Int(sb, c.Y);
                Jw.Key(sb, "z"); Jw.Int(sb, c.Z);
                Jw.Key(sb, "marked"); Jw.Bool(sb, c.Marked);
                Jw.Key(sb, "alive"); Jw.Bool(sb, c.Alive);
                Jw.Key(sb, "grown"); Jw.Bool(sb, c.Grown);
                Jw.Key(sb, "growth"); Jw.Float(sb, c.Growth);
                Jw.Close(sb);
            }
            Jw.CloseArr(sb);
            return sb.ToString();
        }

        public object CollectTrees() => CollectNaturalResourcesSb(_sbTrees, _treeSpecies);
        public object CollectCrops() => CollectNaturalResourcesSb(_sbCrops, _cropSpecies);

        public object CollectGatherables()
        {
            var results = new List<object>();
            foreach (var c in _naturalResources.Read)
            {
                if (c.Gatherable == null) continue;
                results.Add(new Dictionary<string, object>
                {
                    ["id"] = c.Id, ["name"] = c.Name,
                    ["x"] = c.X, ["y"] = c.Y, ["z"] = c.Z,
                    ["alive"] = c.Alive
                });
            }
            return results;
        }

        // PERF: reads cached beaver data only. Zero GetComponent from background thread.
        private readonly System.Text.StringBuilder _sbBeavers = new System.Text.StringBuilder(50000);

        public object CollectBeavers(string format = "toon", string detail = "basic")
        {
            int? singleId = null;
            if (detail != null && detail.StartsWith("id:"))
            {
                if (int.TryParse(detail.Substring(3), out int parsed))
                    singleId = parsed;
            }
            bool fullDetail = detail == "full" || singleId.HasValue;

            var sb = _sbBeavers;
            sb.Clear();
            Jw.OpenArr(sb);
            bool first = true;
            foreach (var c in _beavers.Read)
            {
                if (singleId.HasValue && c.Id != singleId.Value)
                    continue;
                if (!first) Jw.Sep(sb);
                first = false;

                Jw.Open(sb);
                Jw.KeyFirst(sb, "id"); Jw.Int(sb, c.Id);
                Jw.Key(sb, "name"); Jw.Str(sb, c.Name);
                Jw.Key(sb, "x"); Jw.Int(sb, c.X);
                Jw.Key(sb, "y"); Jw.Int(sb, c.Y);
                Jw.Key(sb, "z"); Jw.Int(sb, c.Z);
                Jw.Key(sb, "wellbeing"); Jw.Float(sb, c.Wellbeing, "F1");
                Jw.Key(sb, "isBot"); Jw.Bool(sb, c.IsBot);

                if (!fullDetail)
                {
                    float wb = c.Wellbeing;
                    string tier = wb >= 16 ? "ecstatic" : wb >= 12 ? "happy" : wb >= 8 ? "okay" : wb >= 4 ? "unhappy" : "miserable";
                    Jw.Key(sb, "tier"); Jw.Str(sb, tier);
                    Jw.Key(sb, "workplace"); Jw.Str(sb, c.Workplace ?? "");

                    // critical + unmet need summaries
                    sb.Append(",\"critical\":\"");
                    bool cfirst = true;
                    if (c.Needs != null)
                        foreach (var n in c.Needs)
                            if (n.Critical) { if (!cfirst) sb.Append('+'); cfirst = false; sb.Append(n.Id); }
                    sb.Append("\",\"unmet\":\"");
                    bool ufirst = true;
                    if (c.Needs != null)
                        foreach (var n in c.Needs)
                            if (!n.Favorable && !n.Critical && n.Active) { if (!ufirst) sb.Append('+'); ufirst = false; sb.Append(n.Id); }
                    sb.Append("\"}");
                    continue;
                }

                // full detail
                Jw.Key(sb, "anyCritical"); Jw.Bool(sb, c.AnyCritical);
                if (c.Workplace != null) { Jw.Key(sb, "workplace"); Jw.Str(sb, c.Workplace); }
                if (c.District != null) { Jw.Key(sb, "district"); Jw.Str(sb, c.District); }
                Jw.Key(sb, "hasHome"); Jw.Bool(sb, c.HasHome);
                Jw.Key(sb, "contaminated"); Jw.Bool(sb, c.Contaminated);
                if (c.Life != null) { Jw.Key(sb, "lifeProgress"); Jw.Float(sb, c.LifeProgress); }
                if (c.Deteriorable != null) { Jw.Key(sb, "deterioration"); Jw.Float(sb, c.DeteriorationProgress, "F3"); }
                if (c.Carrier != null) { Jw.Key(sb, "liftingCapacity"); Jw.Int(sb, c.LiftingCapacity); if (c.Overburdened) { Jw.Key(sb, "overburdened"); Jw.Bool(sb, true); } }
                if (c.IsCarrying) { Jw.Key(sb, "carrying"); Jw.Str(sb, c.CarryingGood); Jw.Key(sb, "carryAmount"); Jw.Int(sb, c.CarryAmount); }

                // needs array
                Jw.Key(sb, "needs"); Jw.OpenArr(sb);
                if (c.Needs != null)
                {
                    bool nfirst = true;
                    foreach (var n in c.Needs)
                    {
                        if (!fullDetail && !c.IsBot && !n.Active) continue;
                        if (!nfirst) Jw.Sep(sb);
                        nfirst = false;
                        Jw.Open(sb);
                        Jw.KeyFirst(sb, "id"); Jw.Str(sb, n.Id);
                        Jw.Key(sb, "points"); Jw.Float(sb, n.Points);
                        Jw.Key(sb, "wellbeing"); Jw.Int(sb, n.Wellbeing);
                        Jw.Key(sb, "favorable"); Jw.Bool(sb, n.Favorable);
                        Jw.Key(sb, "critical"); Jw.Bool(sb, n.Critical);
                        Jw.Key(sb, "group"); Jw.Str(sb, n.Group);
                        Jw.Close(sb);
                    }
                }
                Jw.CloseArr(sb);
                Jw.Close(sb);
            }
            Jw.CloseArr(sb);
            return sb.ToString();
        }

        public object CollectPowerNetworks()
        {
            // group buildings by power network using cached PowerNetworkId
            var networks = new Dictionary<int, Dictionary<string, object>>();
            var buildings = _buildings.Read;
            for (int i = 0; i < buildings.Count; i++)
            {
                var c = buildings[i];
                if (c.PowerNode == null || c.PowerNetworkId == 0) continue;
                int netId = c.PowerNetworkId;
                if (!networks.ContainsKey(netId))
                {
                    networks[netId] = new Dictionary<string, object>
                    {
                        ["id"] = netId,
                        ["supply"] = c.PowerSupply,
                        ["demand"] = c.PowerDemand,
                        ["buildings"] = new List<object>()
                    };
                }
                var list = (List<object>)networks[netId]["buildings"];
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = c.Name,
                    ["id"] = c.Id,
                    ["isGenerator"] = c.IsGenerator,
                    ["nominalOutput"] = c.NominalPowerOutput,
                    ["nominalInput"] = c.NominalPowerInput
                });
            }
            return networks.Values.ToList();
        }

        public object CollectSpeed()
        {
            var raw = _speedManager.CurrentSpeed;
            int level = System.Array.IndexOf(SpeedScale, raw);
            if (level < 0) level = 0;
            return new { speed = level };
        }

        public object CollectWorkHours()
        {
            return new
            {
                endHours = _workingHoursManager.EndHours,
                areWorkingHours = _workingHoursManager.AreWorkingHours
            };
        }

        // set when beavers stop working (1-24 hours)
        public object SetWorkHours(int endHours)
        {
            if (endHours < 1 || endHours > 24)
                return new { error = "endHours must be 1-24" };
            _workingHoursManager.EndHours = endHours;
            return new { endHours = _workingHoursManager.EndHours };
        }

        // move adult beavers between districts
        public object MigratePopulation(string fromDistrict, string toDistrict, int count)
        {
            Timberborn.GameDistricts.DistrictCenter fromDc = null, toDc = null;
            foreach (var dc in _districtCenterRegistry.FinishedDistrictCenters)
            {
                if (dc.DistrictName == fromDistrict) fromDc = dc;
                if (dc.DistrictName == toDistrict) toDc = dc;
            }
            if (fromDc == null) return new { error = "from district not found", from = fromDistrict };
            if (toDc == null) return new { error = "to district not found", to = toDistrict };

            try
            {
                var distributor = _populationDistributorRetriever.GetPopulationDistributor<AdultsDistributorTemplate>(fromDc);
                if (distributor == null)
                    return new { error = "no population distributor", from = fromDistrict };

                var available = distributor.Current;
                var toMove = System.Math.Min(count, available);
                if (toMove <= 0)
                    return new { error = "no population to migrate", from = fromDistrict, available };

                distributor.MigrateTo(toDc, toMove);
                return new { from = fromDistrict, to = toDistrict, migrated = toMove };
            }
            catch (System.Exception ex)
            {
                return new { error = ex.Message, from = fromDistrict, to = toDistrict };
            }
        }

        // PERF: O(n) entity scan to build occupant lookup, then O(region) tile iteration.
        // Region-bounded so cost depends on area size, not map size. Called occasionally.
    }
}
