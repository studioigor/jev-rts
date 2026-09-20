using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jev.Gameplay.Jev;
using Jev.Gameplay.Runtime;
using Jev.Gameplay.Simulation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Jev.Gameplay.Diagnostics
{
    /// <summary>
    /// Opt-in live API acceptance checks. Small isolated authoritative worlds run alongside rendering;
    /// no fake replies, local policy, route search or changes to the authored match are involved.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JevIntegrationProbe : MonoBehaviour
    {
        [SerializeField] JevTransport transport;
        [SerializeField, Min(.1f)] float minimumDecisionGap = .75f;
        [SerializeField, Min(.02f)] float approvedStepSeconds = .1f;

        sealed class ProbeCase
        {
            public string Id;
            public RtsWorld World;
            public string ActorId, TargetId;
            public JevBrainRole Role;
            public Cell Goal, InitialCell;
            public int Limit = 12, Calls, ArrivalDepartures, ArrivalWaits;
            public bool Arrived, StaleOrder;
            public ResourceCost Conservation;
            public JObject Record;
            public JArray Decisions = new JArray();
            public readonly JevNavigationIntent Navigation = new JevNavigationIntent();
        }

        readonly Queue<Action> replies = new Queue<Action>();
        readonly Queue<Cell> approvedCells = new Queue<Cell>();
        readonly List<string> caseIds = new List<string>();
        readonly List<JObject> results = new List<JObject>();
        ProbeCase current;
        int caseIndex, generation, routeRevision;
        bool pending, running;
        bool backgroundExecutionOwned, previousRunInBackground;
        long activeTicket;
        double nextActionAt, startedAt, lastClock;
        string reportPath, runId, promptVersion, modelAtStart, state = "idle", lastFailure;

        static readonly string[] AllCases =
        {
            "no_order", "open_move", "wall_detour", "gather_wood", "gather_gold", "build_large_inside", "build_missing_wood",
            "build_missing_gold", "battlefield_gold", "battlefield_human_gold", "battlefield_building_detour", "battlefield_bridge", "combat", "tower", "commander_train", "commander_gather", "stale_order"
        };

        public bool Running => running;
        public string ReportPath => reportPath;
        public string Status => Summary().ToString(Formatting.None);

        /// <param name="onlyCases">Optional comma-separated names from CaseNames; empty runs every case.</param>
        public void StartProbe(JevTransport sharedTransport, string onlyCases = null)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("JEV integration checks require Play mode.");
            if (running) throw new InvalidOperationException("A JEV integration check is already running.");
            if (sharedTransport == null) throw new ArgumentNullException(nameof(sharedTransport));
            if (sharedTransport.Pending != 0 || sharedTransport.InFlight != 0)
                throw new InvalidOperationException("Run the probe from the quiet match setup screen, before starting a match.");
            transport = sharedTransport;
            promptVersion = JevPromptBuilder.PromptVersion;
            modelAtStart = transport.Settings.Model;
            caseIds.Clear();
            foreach (string id in string.IsNullOrWhiteSpace(onlyCases) ? AllCases : onlyCases.Split(',').Select(id => id.Trim()).ToArray())
            {
                if (!AllCases.Contains(id)) throw new ArgumentException("Unknown probe case: " + id);
                caseIds.Add(id);
            }
            if (caseIds.Count == 0) throw new ArgumentException("No integration cases were selected.");
            results.Clear(); replies.Clear(); approvedCells.Clear(); current = null; caseIndex = 0;
            generation++; pending = false; activeTicket = 0; lastFailure = null;
            startedAt = lastClock = Time.realtimeSinceStartupAsDouble;
            runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
#if UNITY_EDITOR
            string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Documentation", "Validation", "Runtime");
#else
            string folder = Path.Combine(Application.persistentDataPath, "Validation", "Runtime");
#endif
            Directory.CreateDirectory(folder);
            reportPath = Path.Combine(folder, "jev-integration-" + runId + ".json");
            if (!transport.IsConfigured)
            {
                RestoreBackgroundExecution();
                state = "blocked"; lastFailure = "missing_credentials"; WriteReport(); return;
            }
            previousRunInBackground = Application.runInBackground;
            backgroundExecutionOwned = true;
            try
            {
                Application.runInBackground = true;
                running = true; state = "running";
                BeginNextCase();
            }
            catch (Exception exception)
            {
                running = false; state = "blocked"; lastFailure = "probe_start_exception_" + exception.GetType().Name;
                RestoreBackgroundExecution();
                WriteReport();
                throw;
            }
        }

        public static string[] CaseNames => (string[])AllCases.Clone();

        public void StopProbe()
        {
            try
            {
                if (!running) return;
                generation++;
                if (pending && transport != null) transport.Cancel(activeTicket);
                pending = false; replies.Clear(); approvedCells.Clear(); running = false; state = "cancelled";
                if (current?.Record != null) current.Record["outcome"] = "cancelled";
                WriteReport();
            }
            finally { RestoreBackgroundExecution(); }
        }

        void OnDisable() => StopProbe();

        void RestoreBackgroundExecution()
        {
            if (!backgroundExecutionOwned) return;
            Application.runInBackground = previousRunInBackground;
            backgroundExecutionOwned = false;
        }

        void Update()
        {
            if (!running) return;
            double now = Time.realtimeSinceStartupAsDouble;
            double elapsed = Math.Max(0, now - lastClock); lastClock = now;
            current?.World.Tick(elapsed);
            while (replies.Count > 0) replies.Dequeue()();
            if (!running || current == null || pending || now < nextActionAt) return;
            try
            {
                if (approvedCells.Count > 0)
                {
                    Cell next = approvedCells.Dequeue();
                    var step = current.World.TryExecuteStep(current.ActorId, next, routeRevision);
                    if (!step.Ok) {approvedCells.Clear();current.Navigation.RecordPhysicalDecision(true);}
                    current.World.AssertInvariants(current.Conservation);
                    nextActionAt = now + approvedStepSeconds;
                    ObserveArrival();
                    return;
                }
                if (Passed()) { FinishCase(true, "objective_reached"); return; }
                if (current.Calls >= current.Limit) { FinishCase(false, "request_budget_exhausted"); return; }
                Request();
            }
            catch (Exception exception)
            {
                FinishCase(false, "probe_exception_" + exception.GetType().Name);
            }
        }

        void BeginNextCase()
        {
            approvedCells.Clear();
            if (caseIndex >= caseIds.Count)
            {
                current = null; running = false;
                state = results.All(r => (bool?)r["passed"] == true) ? "passed" : "completed_with_failures";
                RestoreBackgroundExecution();
                WriteReport(); return;
            }
            current = CreateCase(caseIds[caseIndex++]);
            current.World.Definitions.DecisionIntervalSeconds = minimumDecisionGap;
            current.Conservation = current.World.TotalAccountedResources();
            current.World.AssertInvariants(current.Conservation);
            current.Record = new JObject
            {
                ["case"] = current.Id, ["outcome"] = "running", ["passed"] = false,
                ["requestBudget"] = current.Limit, ["genuineApiRequests"] = 0,
                ["navigationConfiguration"]=new JObject{["publicStaticTerrain"]=current.World.Definitions.PublicLocalTerrain,
                    ["publicTerrainRadius"]=current.World.Definitions.PublicTerrainRadius,["extendedLiteralVocabulary"]=current.World.Definitions.ExtendedMovementVocabulary},
                ["initialWorld"] = Snapshot(current.World), ["decisions"] = current.Decisions
            };
            if (current.Id == "battlefield_bridge")
            {
                current.Record["objective"] = "Cross the Old Bridge and reach the far-bank destination five grid rows beyond its center; reaching the bridge center alone does not pass.";
                current.Record["destination"] = CellJson(current.Goal);
            }
            results.Add(current.Record);
            pending = false; nextActionAt = Time.realtimeSinceStartupAsDouble;
            WriteReport();
        }

        static ProbeCase CreateCase(string id)
        {
            var test = new ProbeCase { Id = id, World = new RtsWorld { Width = 12, Height = 12 }, Role = JevBrainRole.Unit };
            var world = test.World;
            // The small fixtures exercise the same navigation configuration as the authored game.
            // Reference-mode (vision-only, 48-option) physics remains covered by EditMode tests.
            var configuredMatch=FindAnyObjectByType<MatchController>();
            if(configuredMatch!=null)
            {
                world.Definitions.PublicLocalTerrain=configuredMatch.Settings.PublicTerrainNavigation;
                world.Definitions.PublicTerrainRadius=configuredMatch.Settings.PublicTerrainRadius;
                world.Definitions.ExtendedMovementVocabulary=configuredMatch.Settings.ExtendedMovementVocabulary;
            }
            world.Human.BasePopulationCapacity = 10;
            world.Undead.BasePopulationCapacity = 10;
            UnitState actor;
            if (id.StartsWith("battlefield_",StringComparison.Ordinal))
            {
                var match = FindAnyObjectByType<MatchController>();
                if (match == null) throw new InvalidOperationException("The authored match scene must be open.");
                world = test.World = MatchWorldFactory.Create(match.Battlefield, match.Catalog, match.Settings, FactionId.Human, 20);
                test.Limit = 40;
                if (id == "battlefield_gold" || id == "battlefield_human_gold")
                {
                    actor = world.Unit(id=="battlefield_gold"?"undead-worker":"human-worker");
                    var resource = world.Resource(id=="battlefield_gold"?"gold-02":"gold-human-start");
                    if(resource==null)throw new InvalidOperationException("Authored starting gold resource missing.");
                    test.TargetId = resource.Id; test.Goal = resource.Cell;
                    world.SetOrder(actor.Id, Order.Gather(resource.Id, resource.Cell));
                }
                else if(id=="battlefield_building_detour")
                {
                    var site=match.Battlefield.BuildSites.First(s=>s.Faction==FactionId.Human&&s.Kind==BuildingKind.Barracks);
                    world.AddBuilding(FactionId.Human,BuildingKind.Barracks,site.Cell,true,"probe-completed-barracks");
                    actor=world.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(10,8),"probe-new-worker");
                    test.Goal=new Cell(10,22);
                    if(!world.IsFree(test.Goal))throw new InvalidOperationException("Building detour destination is occupied.");
                    world.SetOrder(actor.Id,Order.Move(test.Goal));
                }
                else
                {
                    actor = world.Unit("human-worker");
                    test.Goal = match.Battlefield.Landmarks.First(l => l.Name == "Старый мост").Cell + new Cell(0, 5);
                    if (!world.IsFree(test.Goal)) throw new InvalidOperationException("The authored Old Bridge far-bank destination must be free.");
                    world.SetOrder(actor.Id, Order.Move(test.Goal));
                }
            }
            else if(id == "no_order")
            {
                test.Goal=new Cell(4,4);
                actor=world.AddUnit(FactionId.Human,UnitKind.Worker,test.Goal,"probe-unassigned");
                test.Limit=4;
            }
            else if (id == "build_large_inside")
            {
                world.Width=world.Height=18;
                world.Definitions.Building(BuildingKind.Barracks).FootprintSizeX=7;
                world.Definitions.Building(BuildingKind.Barracks).FootprintSizeY=7;
                test.Goal=new Cell(8,8);
                actor=world.AddUnit(FactionId.Human,UnitKind.Worker,test.Goal,"probe-large-builder");
                world.Human.Wood=40;world.Human.Gold=30;
                world.SetOrder(actor.Id,Order.Build(BuildingKind.Barracks,test.Goal));test.Limit=18;
            }
            else if (id == "open_move" || id == "wall_detour" || id == "stale_order")
            {
                Cell start = id == "wall_detour" ? new Cell(2, 5) : new Cell(2, 3);
                test.Goal = id == "wall_detour" ? new Cell(8, 5) : new Cell(8, 3);
                actor = world.AddUnit(FactionId.Human, UnitKind.Warrior, start, "probe-mover");
                actor.Vision = 8;
                if (id == "wall_detour")
                {
                    for (int y = 2; y <= 6; y++) world.Blocked.Add(new Cell(5, y));
                    test.Limit = 18;
                }
                world.SetOrder(actor.Id, Order.Move(test.Goal));
                test.StaleOrder = id == "stale_order";
                if (test.StaleOrder) test.Limit = 1;
            }
            else if (id.StartsWith("gather_", StringComparison.Ordinal))
            {
                actor = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(3, 4), "probe-worker");
                var resource = world.AddResource(id == "gather_wood" ? ResourceKind.Wood : ResourceKind.Gold, new Cell(4, 4), 25, "probe-resource");
                test.TargetId = resource.Id; world.SetOrder(actor.Id, Order.Gather(resource.Id, resource.Cell));
            }
            else if (id.StartsWith("build_missing_", StringComparison.Ordinal))
            {
                actor = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(3, 5), "probe-builder");
                bool missingWood = id == "build_missing_wood";
                world.Human.Wood = missingWood ? 10 : 20;
                world.Human.Gold = missingWood ? 10 : 0;
                world.AddResource(missingWood ? ResourceKind.Wood : ResourceKind.Gold, new Cell(2, 5), 40, "probe-prerequisite");
                test.Goal = new Cell(4, 5); world.SetOrder(actor.Id, Order.Build(BuildingKind.Barracks, test.Goal));
                test.Limit = 18;
            }
            else if (id == "combat")
            {
                actor = world.AddUnit(FactionId.Human, UnitKind.Warrior, new Cell(4, 4), "probe-warrior");
                var enemy = world.AddUnit(FactionId.Undead, UnitKind.Worker, new Cell(5, 4), "probe-enemy");
                enemy.Hp = 24; test.TargetId = enemy.Id;
                world.SetOrder(actor.Id, Order.Attack(enemy.Id, enemy.Cell));
            }
            else if (id == "tower")
            {
                var tower = world.AddBuilding(FactionId.Human, BuildingKind.Tower, new Cell(4, 4), true, "probe-tower");
                var enemy = world.AddUnit(FactionId.Undead, UnitKind.Worker, new Cell(7, 4), "probe-enemy");
                enemy.Hp = 20; test.ActorId = tower.Id; test.TargetId = enemy.Id; test.Role = JevBrainRole.Tower;
                world.RefreshPopulation(); return test;
            }
            else
            {
                var hall = world.AddBuilding(FactionId.Human, BuildingKind.TownHall, new Cell(3, 3), true, "probe-townhall");
                actor = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(3, 5), "probe-worker");
                world.AddResource(ResourceKind.Wood, new Cell(4, 5), 40, "probe-wood");
                world.AddResource(ResourceKind.Gold, new Cell(2, 5), 40, "probe-gold");
                if (id == "commander_train")
                {
                    world.Human.Wood = 20; world.Human.Gold = 20;
                    world.SetOrder(actor.Id, Order.Gather("probe-wood", new Cell(4, 5)));
                }
                test.TargetId = hall.Id; test.Role = JevBrainRole.Commander;
            }
            test.ActorId = actor.Id; test.InitialCell = actor.Cell;
            world.RefreshPopulation(); return test;
        }

        void Request()
        {
            ProbeCase test = current;
            int run = generation;
            int revision = test.World.Unit(test.ActorId)?.OrderRevision ?? 0;
            Cell start = test.World.Unit(test.ActorId)?.Cell ?? default;
            var options = new Dictionary<string, string>();
            JevNavigationPlan navigationPlan=null;
            JObject request;
            if (test.Role == JevBrainRole.Unit)
            {
                var observation = test.World.Observe(test.ActorId);
                foreach (var action in observation.LegalActions) options.Add(action.Id, action.Description);
                var facts = observation.ToJObject();
                if (test.Id.StartsWith("battlefield_", StringComparison.Ordinal))
                    facts["navigationAtlas"] = MatchWorldFactory.NavigationAtlas(FindAnyObjectByType<MatchController>().Battlefield,test.World.Unit(test.ActorId));
                facts["idleReviewSeconds"] = 10; facts["movementStepSeconds"] = approvedStepSeconds;
                if(!test.StaleOrder&&test.Navigation.NeedsPlanning(facts))navigationPlan=test.Navigation.BuildPlan(facts);
                facts["navigationIntent"]=test.Navigation.ObservationFacts(facts);
                if(navigationPlan!=null)
                {
                    options.Clear();foreach(var option in navigationPlan.Criteria)options.Add(option.Key,option.Value);
                    request=JevPromptBuilder.BuildNavigation(navigationPlan.State,options);
                }
                else request = JevPromptBuilder.BuildUnit(facts, options);
            }
            else if (test.Role == JevBrainRole.Tower)
            {
                foreach (var action in test.World.TowerActions(test.ActorId)) options.Add(action.Id, action.Description);
                var tower = test.World.Building(test.ActorId);
                var target = test.World.Unit(test.TargetId);
                request = JevPromptBuilder.BuildTower(new JObject
                {
                    ["self"] = new JObject { ["id"] = tower.Id, ["hp"] = tower.Hp, ["damage"] = tower.Damage, ["attackRange"] = tower.Range },
                    ["visibleEnemies"] = target.Alive ? new JArray(UnitJson(target)) : new JArray(),
                    ["rules"] = "Each choice permits one shot or an explicit wait; no unchosen shots or automatic retargeting execute."
                }, options);
            }
            else
            {
                request = CommanderRequest(test, options);
            }
            test.Calls++; test.Record["genuineApiRequests"] = test.Calls;
            pending = true;
            activeTicket = transport.Evaluate(request, result => replies.Enqueue(() =>
            {
                if (!running || generation != run || current != test) return;
                pending = false;
                var entry = new JObject
                {
                    ["requestId"] = result.RequestId, ["choice"] = result.Choice, ["success"] = result.Success,
                    ["promptVersion"] = promptVersion, ["providerModel"] = result.Model,
                    ["httpStatus"] = result.HttpStatus, ["attempts"] = result.Attempts,
                    ["inputTokens"] = result.InputTokens, ["role"] = navigationPlan!=null?"Navigation":test.Role.ToString(),
                    ["submittedOrderRevision"] = revision, ["submittedCell"] = CellJson(start),
                    ["request"] = request.DeepClone(), ["response"] = result.Raw?.DeepClone()
                };
                test.Decisions.Add(entry);
                if (!result.Success)
                {
                    entry["error"] = result.ErrorCode;
                    FinishCase(false, "api_" + result.ErrorCode); return;
                }
                if (!options.ContainsKey(result.Choice)) { FinishCase(false, "unoffered_choice"); return; }
                if(navigationPlan!=null)
                {
                    bool accepted=test.Navigation.ApplyChoice(navigationPlan,result.Choice,test.World.Observe(test.ActorId,false).ToJObject(),result.RequestId,out string reason);
                    entry["execution"]=accepted?"navigation_intent_selected":reason;entry["physicallyApplied"]=false;
                    entry["navigationIntent"]=test.Navigation.ObservationFacts(test.World.Observe(test.ActorId,false).ToJObject());
                    if(!accepted){FinishCase(false,"navigation_intent_"+reason);return;}
                }
                else ApplyResult(test, result, revision, start, entry);
                nextActionAt = Time.realtimeSinceStartupAsDouble + minimumDecisionGap;
                WriteReport();
            }));
            if (test.StaleOrder)
            {
                // Deliberately change the command while the genuine request is queued/in flight.
                test.World.SetOrder(test.ActorId, Order.Hold(start));
            }
        }

        static JObject CommanderRequest(ProbeCase test, Dictionary<string, string> options)
        {
            var world = test.World; var worker = world.Unit(test.ActorId); var hall = world.Building(test.TargetId);
            options.Add("wait", "Keep existing assignments and wait for changed conditions or the next strategic review.");
            foreach (var resource in world.Observe(worker.Id, false).VisibleResources)
                options.Add("assign_" + resource.Id, "Order worker " + worker.Id + " to gather " + resource.KindLabel + " from " + resource.Id + ". Its individual JEV brain must choose movement and each gathering action.");
            var faction = world.Human; var cost = world.Definitions.Unit(UnitKind.Worker).Cost;
            if (faction.Wood >= cost.Wood && faction.Gold >= cost.Gold && faction.Population.Used + faction.Population.Reserved < faction.Population.Capacity)
                options.Add("train_worker", "Queue one worker at " + hall.Id + "; pay " + cost.Wood + " wood and " + cost.Gold + " gold, reserve one population slot. A queued worker finishes after 12 seconds.");
            var facts = new JObject
            {
                ["faction"] = new JObject { ["id"] = "human", ["wood"] = faction.Wood, ["gold"] = faction.Gold,
                    ["population"] = new JObject { ["used"] = faction.Population.Used, ["reserved"] = faction.Population.Reserved, ["capacity"] = faction.Population.Capacity, ["hardCap"] = world.Definitions.PerFactionUnitLimit } },
                ["units"] = new JArray(world.Units.Where(u => u.Alive && u.Faction == FactionId.Human).Select(UnitJson)),
                ["buildings"] = new JArray(new JObject { ["id"] = hall.Id, ["type"] = "townhall", ["complete"] = true, ["workerQueue"] = hall.TrainingQueue.Count }),
                ["knownResources"] = world.Observe(worker.Id, false).ToJObject()["visible"]["resources"].DeepClone(),
                ["timeSeconds"] = world.TimeSeconds
            };
            return JevPromptBuilder.BuildCommander(facts, options);
        }

        void ApplyResult(ProbeCase test, JevResult result, int revision, Cell submittedCell, JObject entry)
        {
            ActionResult outcome;
            if (test.Role == JevBrainRole.Unit)
            {
                outcome = test.World.Execute(test.ActorId, result.Choice, revision);
                if (test.StaleOrder)
                {
                    bool rejected = !outcome.Ok && outcome.Reason == "stale_order" && test.World.Unit(test.ActorId).Cell == submittedCell;
                    entry["execution"] = outcome.Reason;
                    test.World.AssertInvariants(test.Conservation);
                    FinishCase(rejected, rejected ? "genuine_stale_reply_rejected" : "stale_reply_was_not_rejected"); return;
                }
                if (outcome.Ok && outcome.ApprovedCells.Count > 0)
                {
                    test.Navigation.RecordPhysicalDecision();
                    routeRevision = revision;
                    foreach (Cell cell in outcome.ApprovedCells) approvedCells.Enqueue(cell);
                }
                if(!outcome.Ok)test.Navigation.RecordPhysicalDecision(true);
                if (result.Choice == "wait" && test.World.Unit(test.ActorId).Cell == test.Goal) test.ArrivalWaits++;
                ObserveArrival();
            }
            else if (test.Role == JevBrainRole.Tower)
                outcome = test.World.ExecuteTowerAction(test.ActorId, result.Choice);
            else if (result.Choice == "train_worker")
                outcome = test.World.EnqueueTraining(test.TargetId, UnitKind.Worker, FactionId.Human);
            else if (result.Choice.StartsWith("assign_", StringComparison.Ordinal))
            {
                var resource = test.World.Resource(result.Choice.Substring("assign_".Length));
                bool ordered = test.World.SetOrder(test.ActorId, Order.Gather(resource.Id, resource.Cell));
                outcome = ordered ? ActionResult.Success("commander_gather_order", test.ActorId, resource.Id) : ActionResult.Fail("order_rejected");
            }
            else outcome = ActionResult.Success("commander_wait");
            entry["execution"] = outcome.Reason; entry["physicallyApplied"] = outcome.Ok;
            test.World.AssertInvariants(test.Conservation);
        }

        void ObserveArrival()
        {
            if (current == null || (current.Id != "open_move" && current.Id != "wall_detour")) return;
            var unit = current.World.Unit(current.ActorId);
            if (unit.Cell == current.Goal) current.Arrived = true;
            else if (current.Arrived) current.ArrivalDepartures++;
        }

        bool Passed()
        {
            var test = current; var world = test.World;
            switch (test.Id)
            {
                case "battlefield_gold": return world.Events.Count(e => e.Type == "gather") >= 3 && world.Undead.Gold >= 55;
                case "battlefield_human_gold": return world.Events.Count(e => e.Type == "gather") >= 3 && world.Human.Gold >= 55;
                case "battlefield_building_detour": return world.Unit(test.ActorId).Cell == test.Goal;
                case "battlefield_bridge": return world.Unit(test.ActorId).Cell == test.Goal;
                case "no_order": return test.ArrivalWaits>=2&&world.Unit(test.ActorId).Cell==test.InitialCell;
                case "build_large_inside": return world.Buildings.Any(b=>b.Cell==test.Goal&&b.Complete);
                case "open_move": return test.Arrived && test.ArrivalDepartures == 0 && test.ArrivalWaits >= 2;
                case "wall_detour": return world.Unit(test.ActorId).Cell == test.Goal;
                case "gather_wood": return world.Human.Wood >= 15 && world.Events.Count(e => e.Type == "gather") >= 3;
                case "gather_gold": return world.Human.Gold >= 15 && world.Events.Count(e => e.Type == "gather") >= 3;
                case "build_missing_wood":
                case "build_missing_gold": return world.Buildings.Any(b => b.Cell == test.Goal && b.Complete) && world.Events.Count(e => e.Type == "gather") >= 2;
                case "combat":
                case "tower": return !world.Unit(test.TargetId).Alive && world.Events.Any(e => e.Type == "attack");
                case "commander_train": return world.Events.Any(e => e.Type == "training_queued");
                case "commander_gather": return world.Unit(test.ActorId).Order?.Kind == OrderKind.Gather;
                default: return false;
            }
        }

        void FinishCase(bool passed, string reason)
        {
            if (current == null) return;
            current.Record["outcome"] = reason; current.Record["passed"] = passed;
            current.Record["finalWorld"] = Snapshot(current.World);
            current.Record["events"] = JArray.FromObject(current.World.Events);
            current.Record["arrivalDepartures"] = current.ArrivalDepartures;
            current.Record["arrivalWaits"] = current.ArrivalWaits;
            if (!passed) lastFailure = current.Id + ": " + reason;
            BeginNextCase();
        }

        JObject Summary()
        {
            return new JObject
            {
                ["runId"] = runId, ["state"] = state, ["running"] = running,
                ["promptVersion"] = promptVersion, ["configuredModel"] = modelAtStart,
                ["currentCase"] = current?.Id, ["currentRequests"] = current?.Calls ?? 0,
                ["caseIndex"] = caseIndex, ["caseCount"] = caseIds.Count,
                ["pending"] = pending, ["activeRequestId"] = activeTicket,
                ["elapsedSeconds"] = startedAt <= 0 ? 0 : Math.Round(Time.realtimeSinceStartupAsDouble - startedAt, 2),
                ["passed"] = results.Count(r => (bool?)r["passed"] == true),
                ["lastFailure"] = lastFailure, ["reportPath"] = reportPath,
                ["transportLedger"] = transport != null ? transport.LedgerPath : null,
                ["methodology"] = "Live UnityWebRequest + production prompt builder + isolated RtsWorld fixtures. Passive enemy targets isolate attack legality. All selected actions come from genuine JEV replies. This does not replace full-match playtesting."
            };
        }

        JObject Report()
        {
            JObject report = Summary();
            report["cases"] = new JArray(results.Select(r => r.DeepClone()));
            return report;
        }

        void WriteReport()
        {
            if (string.IsNullOrEmpty(reportPath)) return;
            try { File.WriteAllText(reportPath, Report().ToString(Formatting.Indented)); }
            catch (IOException) { lastFailure = "report_io_error"; }
            catch (UnauthorizedAccessException) { lastFailure = "report_io_error"; }
        }

        static JObject CellJson(Cell cell) => new JObject { ["x"] = cell.X, ["y"] = cell.Y };
        static JObject UnitJson(UnitState unit) => new JObject
        {
            ["id"] = unit.Id, ["type"] = unit.Kind.ToString().ToLowerInvariant(),
            ["faction"] = unit.Faction.ToString().ToLowerInvariant(), ["position"] = CellJson(unit.Cell),
            ["hp"] = unit.Hp, ["damage"] = unit.Damage, ["range"] = unit.Range,
            ["order"] = unit.Order == null ? null : new JObject { ["kind"] = unit.Order.Kind.ToString().ToLowerInvariant(), ["targetId"] = unit.Order.TargetId }
        };
        static JObject Snapshot(RtsWorld world) => new JObject
        {
            ["timeSeconds"] = world.TimeSeconds, ["width"] = world.Width, ["height"] = world.Height,
            ["blocked"] = new JArray(world.Blocked.Select(CellJson)), ["units"] = new JArray(world.Units.Select(UnitJson)),
            ["buildings"] = JArray.FromObject(world.Buildings), ["resources"] = JArray.FromObject(world.Resources),
            ["human"] = JObject.FromObject(world.Human), ["undead"] = JObject.FromObject(world.Undead)
        };
    }
}
