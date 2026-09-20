using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.Jev;
using Jev.Gameplay.UI;

namespace Jev.Gameplay.Runtime
{
    public sealed partial class MatchController
    {
        sealed class Macro { public string Id,Description; public Func<bool> Apply; }
        float nextCommanderAt;
        bool commanderPending;
        long commanderTicket;
        string commanderWorker;
        readonly Dictionary<string,JObject> commanderEnemyMemory = new Dictionary<string,JObject>();
        readonly Dictionary<string,JObject> commanderResourceMemory = new Dictionary<string,JObject>();
        static readonly JsonSerializer CommanderSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = new List<JsonConverter> { new StringEnumConverter() }
        });
        sealed class CommanderUnitReview { public Cell Cell; public int OrderRevision, Hp; public double At, LastObservedMovementAt; }
        sealed class CommanderBuildingReview { public int Progress, Hp; public bool Complete; public double At; }
        readonly Dictionary<string,CommanderUnitReview> commanderUnitReviews = new Dictionary<string,CommanderUnitReview>();
        readonly Dictionary<string,CommanderBuildingReview> commanderBuildingReviews = new Dictionary<string,CommanderBuildingReview>();
        string commanderReviewSession;
        JObject lastCommanderMacro;
        readonly Dictionary<string,TowerBrain> towerBrains = new Dictionary<string,TowerBrain>();
        sealed class TowerBrain { public bool Pending;public float NextAt;public string Signature; }
        sealed class CommanderGoal { public string Id,Description; public Order Order; public bool HoldCurrent; public JObject Facts; }
        sealed class CommanderBankSample { public double At; public long Sequence; public int Wood,Gold; }
        CommanderGoal commanderGoal;
        int commanderPage=-1;
        readonly List<CommanderBankSample> commanderBankSamples=new List<CommanderBankSample>();
        JObject lastMeaningfulCommanderMacro;
        JObject lastCommanderTaskResolution;

        void UpdateCommander()
        {
            if(!Settings.EnableEnemyCommander||commanderPending||Time.time<nextCommanderAt)return;
            if(commanderReviewSession!=SessionId)
            {
                commanderReviewSession=SessionId;commanderUnitReviews.Clear();commanderBuildingReviews.Clear();lastCommanderMacro=null;
                commanderGoal=null;commanderPage=-1;commanderWorker=null;commanderBankSamples.Clear();lastMeaningfulCommanderMacro=null;lastCommanderTaskResolution=null;
            }
            var faction=PlayerFaction==FactionId.Human?FactionId.Undead:FactionId.Human;
            var options=BuildCommanderReview(faction,out var state);
            int session=sessionSerial;commanderPending=true;
            var criteria=options.ToDictionary(o=>o.Id,o=>o.Description);
            commanderTicket=Transport.EvaluatePrepared(()=>JevPromptBuilder.BuildCommander(state,criteria),result=>completedDecisions.Enqueue(()=>
            {
                if(session!=sessionSerial||World==null)return;commanderPending=false;
                var chosen=options.FirstOrDefault(o=>o.Id==result.Choice);
                bool applied=result.Success&&chosen?.Apply()==true;
                bool browsing=applied&&(result.Choice.StartsWith("page_",StringComparison.Ordinal)||result.Choice=="all_pages");
                if(applied&&!browsing&&result.Choice!="wait")commanderPage=-1;
                lastCommanderMacro=new JObject{["choice"]=result.Choice,["description"]=chosen?.Description,["requestId"]=result.RequestId,
                    ["issuedAtSeconds"]=(double)state["timeSeconds"],["resolvedAtSeconds"]=World.TimeSeconds,["providerSuccess"]=result.Success,
                    ["outcome"]=applied?"accepted":result.Success?"rejected":"provider_error",["errorCode"]=result.ErrorCode,
                    ["meaning"]="Accepted means the described command or task selection was applied. Choosing a task only opens a worker choice; an assignment does not complete physical work."};
                if(applied&&!browsing&&result.Choice!="wait")lastMeaningfulCommanderMacro=(JObject)lastCommanderMacro.DeepClone();
                RecordDecision("commander_"+faction,result,applied?"applied":result.Success?"rejected":"error",state);
                bool selectedNextStage=applied&&result.Choice!="wait"&&(commanderGoal!=null||browsing);
                nextCommanderAt=Time.time+(selectedNextStage?.2f:Settings.CommanderInterval);
            }));
        }

        // Preparing a review only exposes current factual options. Execution is exclusively
        // through the macro selected by a validated JEV response in UpdateCommander.
        List<Macro> BuildCommanderReview(FactionId faction,out JObject state)
        {
            var own=World.Units.Where(u=>u.Alive&&u.Faction==faction).ToList();
            var workers=own.Where(u=>u.Kind==UnitKind.Worker).ToList();
            var ownBuildings=World.Buildings.Where(b=>b.Alive&&b.Faction==faction).ToList();
            var visible=FactionVision(faction);
            foreach(var resource in World.Resources.Where(r=>r.Remaining>0&&visible.Contains(r.Cell)))
                commanderResourceMemory[resource.Id]=new JObject{["id"]=resource.Id,["kind"]=resource.Kind.ToString(),["cell"]=CommanderJson(resource.Cell),["remaining"]=resource.Remaining,["lastSeen"]=World.TimeSeconds};
            foreach(var unit in World.Units.Where(u=>u.Alive&&u.Faction!=faction&&visible.Contains(u.Cell)))
                commanderEnemyMemory[unit.Id]=new JObject{["id"]=unit.Id,["kind"]=unit.Kind.ToString(),["cell"]=CommanderJson(unit.Cell),["hp"]=unit.Hp,["maxHp"]=unit.MaxHp,["damage"]=unit.Damage,["range"]=unit.Range,["lastSeen"]=World.TimeSeconds};
            foreach(var b in World.Buildings.Where(b=>b.Alive&&b.Faction!=faction&&World.Footprint(b).Any(visible.Contains)))
                commanderEnemyMemory[b.Id]=new JObject{["id"]=b.Id,["kind"]=b.Kind.ToString(),["cell"]=CommanderJson(b.Cell),["hp"]=b.Hp,["maxHp"]=b.MaxHp,["damage"]=b.Damage,["range"]=b.Range,["lastSeen"]=World.TimeSeconds};
            foreach(var key in commanderResourceMemory.Keys.ToArray())
            {var cell=CommanderReadCell(commanderResourceMemory[key]["cell"]);if(visible.Contains(cell)&&!World.Resources.Any(r=>r.Id==key&&r.Remaining>0))commanderResourceMemory.Remove(key);}
            foreach(var key in commanderEnemyMemory.Keys.ToArray())
            {var cell=CommanderReadCell(commanderEnemyMemory[key]["cell"]);if(visible.Contains(cell)&&!World.Units.Any(u=>u.Id==key&&u.Alive&&u.Cell==cell)&&!World.Buildings.Any(b=>b.Id==key&&b.Alive&&World.Footprint(b).Contains(cell)))commanderEnemyMemory.Remove(key);}
            var bank=World.Faction(faction);
            var sites=Battlefield.BuildSites.Where(s=>s.Faction==faction).Select(s=>CommanderSiteState(s,faction,own,visible)).ToArray();
            // A task selection is only an unissued menu choice. Retire selections whose work
            // is already complete, unavailable, or assigned to every eligible worker. This
            // never chooses a replacement order: the next JEV request sees all strategies.
            JObject selectedGoalState=commanderGoal==null?null:CommanderGoalState(commanderGoal,workers,sites,visible);
            string taskResolution=CommanderEvidence.PendingTaskExpirationReason(selectedGoalState);
            if(taskResolution!=null)
            {
                CloseCommanderTaskSelection(commanderGoal,taskResolution);selectedGoalState=null;
            }
            var economy=CommanderEconomyState(faction);
            var workerFacts=workers.ToDictionary(w=>w.Id,w=>CommanderEvidence.WorkerFacts(w,ownBuildings,commanderResourceMemory,economy));
            var staffing=CommanderEvidence.Staffing(own,workerFacts.Values);
            state=new JObject{["role"]="faction_commander",["decisionStage"]=commanderGoal==null?"choose concrete strategic command or worker task":"choose worker for the previously JEV-selected task",
                ["selectedGoal"]=selectedGoalState,["lastTaskSelectionResolution"]=lastCommanderTaskResolution?.DeepClone(),["faction"]=faction.ToString(),["timeSeconds"]=World.TimeSeconds,
                ["staffing"]=staffing,["stock"]=new JObject{["wood"]=bank.Wood,["gold"]=bank.Gold},["economyEvidence20s"]=economy,
                ["population"]=CommanderJson(bank.Population),["hardUnitCap"]=UnitCap,["factionTraits"]=CommanderJson(bank.Traits),
                ["units"]=new JArray(own.Select(u=>{var unit=CommanderUnitState(u);if(workerFacts.TryGetValue(u.Id,out var facts))unit["assignmentFacts"]=facts.DeepClone();return unit;})),
                ["buildings"]=new JArray(ownBuildings.Select(b=>CommanderBuildingState(b,own))),
                ["readyMilitaryProduction"]=new JArray(ownBuildings.Where(b=>b.Complete&&World.Definitions.Building(b.Kind).TrainableUnits.Any(k=>k!=UnitKind.Worker)).Select(b=>b.Id)),
                ["availableBuildSites"]=new JArray(sites),["buildingCatalog"]=CommanderJson(World.Definitions.Buildings),["unitCatalog"]=CommanderJson(World.Definitions.Units),
                ["gatherYield"]=new JObject{["wood"]=World.Definitions.WoodPerGather,["gold"]=World.Definitions.GoldPerGather},
                ["knownResources"]=new JArray(commanderResourceMemory.Values.Select(v=>v.DeepClone())),["knownEnemies"]=new JArray(commanderEnemyMemory.Values.Select(v=>v.DeepClone())),
                ["mapLandmarks"]=CommanderJson(Battlefield.Landmarks),["selectedWorker"]=commanderWorker,
                ["priorChoice"]=lastCommanderMacro?["choice"]?.DeepClone(),["lastMacroExecution"]=lastCommanderMacro?.DeepClone(),["lastMeaningfulMacro"]=lastMeaningfulCommanderMacro?.DeepClone(),
                ["progressNote"]="Worker tasks and assignments are intentions. First choose the concrete task; the following independent JEV decision chooses its worker. No assignment or work happens automatically. economyEvidence20s reports actual credited resources and bank deductions over its stated measured interval; moving does not prove resource income or completed construction."};
            var options=new List<Macro>{new Macro{Id="wait",Description=commanderGoal==null?"Keep current assignments and review strategy later.":
                "Leave current unit orders unchanged and defer this unissued task. Close worker selection and review all strategic commands next time; no order or work is issued.",
                Apply=()=>{commanderGoal=null;commanderPage=-1;return true;}}};
            if(commanderGoal!=null)
            {
                var goal=commanderGoal;
                foreach(var worker in workers)
                {
                    var order=goal.HoldCurrent?Order.Hold(worker.Cell):goal.Order.Copy();
                    if(SameCommanderOrder(worker.Order,order))continue;
                    string resourceKind=order.Kind==OrderKind.Gather?(string)goal.Facts["resourceKind"]:null;
                    var coverage=CommanderEvidence.AssignmentCoverage(workerFacts.Values,worker.Id,resourceKind);
                    var facts=workerFacts[worker.Id];
                    string previousResource=(string)facts["assignedResourceKind"]??"none";
                    string credit=(bool)facts["creditWindowKnown"]?$"actual credited wood={facts["creditedWood"]}, gold={facts["creditedGold"]} in {Math.Round((double)economy["measuredSeconds"],1)}s":"actual recent resource credits unknown";
                    options.Add(new Macro{Id="assign_"+worker.Id,Description=$"Assign worker {worker.Id} at {worker.Cell} to {goal.Description}. Current order status={facts["orderStatus"]}, resource={previousResource}; {credit}. Replacing this order changes faction gather assignments: Wood {coverage["before"]["wood"]}→{coverage["after"]["wood"]}, Gold {coverage["before"]["gold"]}→{coverage["after"]["gold"]}, unknown resource {coverage["before"]["unknown"]}→{coverage["after"]["unknown"]}. These are order counts, not guaranteed income; JEV still decides all physical work and movement.",Apply=()=>
                    {return ApplyCommanderAssignment(worker,goal,faction);}});
                }
                options.Add(new Macro{Id="cancel_task",Description="Cancel this pending task selection and return to all strategic commands, without changing any unit orders.",Apply=()=>{commanderGoal=null;return true;}});
            }
            else
            {
                if(workers.Count>0)
                {
                    foreach(var known in commanderResourceMemory.Values)
                    {
                        string resourceId=(string)known["id"];var cell=CommanderReadCell(known["cell"]);
                        ResourceKind? resourceKind=Enum.TryParse((string)known["kind"],true,out ResourceKind knownKind)?knownKind:(ResourceKind?)null;
                        var order=Order.Gather(resourceId,cell,resourceKind);
                        if(workers.All(w=>SameCommanderOrder(w.Order,order)))continue;
                        AddCommanderGoal(options,new CommanderGoal{Id="gather_"+resourceId,Order=order,
                            Description=$"gather {known["kind"]} from {resourceId} at {cell}",Facts=new JObject{["kind"]="gather",["targetId"]=resourceId,["resourceKind"]=known["kind"].DeepClone(),["cell"]=CommanderJson(cell),["lastSeen"]=known["lastSeen"].DeepClone()}},
                            $"Select task: gather {known["kind"]} at {cell}; known remaining {known["remaining"]}, currently assigned workers={workers.Count(w=>SameCommanderOrder(w.Order,order))}. Next JEV choice selects the worker; no gathering happens yet.");
                    }
                    foreach(var site in Battlefield.BuildSites.Where(s=>s.Faction==faction))
                    {
                        var facts=sites.First(s=>(string)s["id"]==site.Id);
                        if((bool)facts["completedOwnBuilding"]||(bool)facts["otherBuildingAtCenter"])continue;
                        var order=Order.Build(site.Kind,site.Cell);if(workers.All(w=>SameCommanderOrder(w.Order,order)))continue;
                        AddCommanderGoal(options,new CommanderGoal{Id="build_"+site.Id,Order=order,Description=$"{((bool)facts["alreadyPaid"]?"finish":"start")} {site.Kind} at {site.Cell}",Facts=(JObject)facts.DeepClone()},
                            $"Select construction task: {site.Kind} at {site.Cell}; costs {facts["cost"]["wood"]} wood/{facts["cost"]["gold"]} gold, alreadyPaid={facts["alreadyPaid"]}, actual work {facts["progress"]}/{facts["requiredWork"]}, assigned workers={facts["assignedWorkers"]}, effective population gain={facts["effectivePopulationGain"]}, trains={string.Join(",",World.Definitions.Building(site.Kind).TrainableUnits)}, observed space={facts["spaceStatus"]}. Next JEV choice selects the worker. A selected task does not create a building.");
                    }
                    for(int index=0;index<Battlefield.Landmarks.Count;index++)
                    {
                        var landmark=Battlefield.Landmarks[index];var order=Order.Move(landmark.Cell);if(workers.All(w=>SameCommanderOrder(w.Order,order)))continue;
                        AddCommanderGoal(options,new CommanderGoal{Id="scout_"+index,Order=order,Description=$"explore {landmark.Name} at {landmark.Cell}",Facts=new JObject{["kind"]="scout",["landmark"]=landmark.Name,["cell"]=CommanderJson(landmark.Cell)}},
                            $"Select scouting task: explore {landmark.Name} at {landmark.Cell}; next JEV choice selects the worker. Unseen enemies remain unknown.");
                    }
                    AddCommanderGoal(options,new CommanderGoal{Id="hold_worker",HoldCurrent=true,Description="hold its current position and defend locally",Facts=new JObject{["kind"]="hold_current"}},
                        "Select task: hold one worker at its current position and defend locally. Next JEV choice selects which worker.");
                }
                foreach(var b in ownBuildings.Where(b=>b.Complete))
                    foreach(var kind in World.Definitions.Building(b.Kind).TrainableUnits)
                    {
                        var cost=World.Definitions.Unit(kind).Cost;
                        if(bank.Wood<cost.Wood||bank.Gold<cost.Gold||bank.Population.Used+bank.Population.Reserved>=bank.Population.Capacity||bank.Population.Used+bank.Population.Reserved>=UnitCap)continue;
                        string labor=kind==UnitKind.Worker?$" New worker starts without an order; existing workers without orders={staffing["withoutOrder"]}, with completed construction orders={staffing["completedBuild"]}. Recruitment itself produces no resources.":"";
                        options.Add(new Macro{Id="train_"+b.Id+"_"+kind,Description=$"Queue {kind} at {b.Kind} {b.Id}; cost {cost.Wood} wood/{cost.Gold} gold, bank afterward {bank.Wood-cost.Wood} wood/{bank.Gold-cost.Gold} gold; {World.Definitions.Unit(kind).TrainingSeconds} seconds, queue length now {b.TrainingQueue.Count}, population afterward {bank.Population.Used+bank.Population.Reserved+1}/{bank.Population.Capacity}.{labor}",Apply=()=>World.EnqueueTraining(b.Id,kind,faction).Ok});
                    }
                var army=own.Where(u=>u.Kind!=UnitKind.Worker).ToArray();
                if(army.Length>0)
                {
                    foreach(var enemy in commanderEnemyMemory.Values)
                    {
                        string id=(string)enemy["id"];var cell=CommanderReadCell(enemy["cell"]);
                        options.Add(new Macro{Id="army_attack_"+id,Description=$"Order the entire current army ({army.Length} units) to attack last-observed {enemy["kind"]} {id} at {cell}, seen at {enemy["lastSeen"]}; each unit decides its actions and route.",Apply=()=>{bool ok=false;foreach(var u in army)ok|=IssueOrder(u,Order.Attack(id,cell));return ok;}});
                    }
                    for(int index=0;index<Battlefield.Landmarks.Count;index++)
                    {
                        var landmark=Battlefield.Landmarks[index];var cell=landmark.Cell;
                        options.Add(new Macro{Id="army_move_"+index,Description=$"Order all {army.Length} army units to scout/advance toward {landmark.Name} {cell} with one shared destination and no assigned slots. JEV chooses every individual route and reacts to observed enemies.",Apply=()=>IssueArmyMove(army,cell,faction)});
                    }
                    var enemyStart=faction==FactionId.Human?Battlefield.Undead:Battlefield.Human;
                    string initialHallId=faction==FactionId.Human?"undead-townhall":"human-townhall";
                    options.Add(new Macro{Id="assault_enemy_start",Description=$"Order the army to assault the opposing starting town hall location {enemyStart.TownHall}, public match-setup information. Current defenders are unknown unless observed. Each unit chooses its route and attacks.",Apply=()=>{bool ok=false;foreach(var u in army)ok|=IssueOrder(u,Order.Attack(initialHallId,enemyStart.TownHall));return ok;}});
                    options.Add(new Macro{Id="army_hold",Description="Hold all current military positions and defend locally.",Apply=()=>{foreach(var u in army)IssueOrder(u,Order.Hold(u.Cell));return true;}});
                }
            }
            return BoundCommanderChoices(options,state);
        }

        bool ApplyCommanderAssignment(UnitState worker,CommanderGoal goal,FactionId faction)
        {
            // The response may arrive after another unit finishes the work. Revalidate only
            // established completion/depletion facts; never replace a worker's useful order
            // with work that disappeared while JEV was evaluating the assignment.
            string expired=null;
            if(!goal.HoldCurrent&&goal.Order.Kind==OrderKind.Build&&World.Buildings.Any(b=>b.Alive&&b.Faction==faction&&
                b.Kind==goal.Order.BuildingKind&&b.Cell==goal.Order.Cell&&b.Complete))expired="construction_already_complete";
            else if(!goal.HoldCurrent&&goal.Order.Kind==OrderKind.Gather&&FactionVision(faction).Contains(goal.Order.Cell)&&
                !World.Resources.Any(r=>r.Id==goal.Order.TargetId&&r.Cell==goal.Order.Cell&&r.Remaining>0))expired="resource_observed_depleted";
            if(expired!=null){CloseCommanderTaskSelection(goal,expired);return false;}
            bool applied=worker.Alive&&worker.Faction==faction&&IssueOrder(worker,goal.HoldCurrent?Order.Hold(worker.Cell):goal.Order.Copy());
            if(applied)commanderGoal=null;
            return applied;
        }

        void CloseCommanderTaskSelection(CommanderGoal goal,string reason)
        {
            lastCommanderTaskResolution=new JObject{["taskId"]=goal.Id,["reason"]=reason,["atSeconds"]=World.TimeSeconds,
                ["meaning"]="The unissued task selection closed. Existing unit orders are unchanged; JEV chooses the next strategic command."};
            if(ReferenceEquals(commanderGoal,goal)){commanderGoal=null;commanderPage=-1;}
        }

        static bool SameCommanderOrder(Order a,Order b) => a!=null&&b!=null&&a.Kind==b.Kind&&a.Cell==b.Cell&&a.TargetId==b.TargetId&&a.BuildingKind==b.BuildingKind&&a.GatherResourceKind==b.GatherResourceKind;
        void AddCommanderGoal(List<Macro> options,CommanderGoal goal,string description) => options.Add(new Macro{Id=goal.Id,Description=description,Apply=()=>{commanderGoal=goal;commanderPage=-1;return true;}});

        JObject CommanderGoalState(CommanderGoal goal,List<UnitState> workers,JObject[] sites,HashSet<Cell> visible)
        {
            string kind=goal.HoldCurrent?"hold_current":goal.Order.Kind.ToString().ToLowerInvariant();
            JObject current=null;
            bool targetVisible=!goal.HoldCurrent&&visible.Contains(goal.Order.Cell);
            if(kind=="build")current=sites.FirstOrDefault(s=>(string)s["id"]==(string)goal.Facts["id"]);
            else if(kind=="gather")commanderResourceMemory.TryGetValue(goal.Order.TargetId,out current);
            int eligible=workers.Count(worker=>!SameCommanderOrder(worker.Order,goal.HoldCurrent?Order.Hold(worker.Cell):goal.Order));
            bool? fulfilled=kind=="build"?current==null?(bool?)null:(bool?)current["completedOwnBuilding"]:
                kind=="move"?workers.Any(worker=>worker.Cell==goal.Order.Cell):(bool?)null;
            return CommanderEvidence.RefreshGoalFacts(goal.Id,kind,goal.Description,goal.Facts,current,targetVisible,eligible,fulfilled,World.TimeSeconds);
        }

        List<Macro> BoundCommanderChoices(List<Macro> all,JObject state)
        {
            if(all.Count<=JevResponseValidator.MaximumChoices){commanderPage=-1;return all;}
            var pages=CommanderEvidence.PageIds(all.Skip(1).Select(o=>o.Id).ToArray());
            state["choicePages"]=new JArray(pages.Select((ids,index)=>new JObject{["index"]=index,["optionIds"]=new JArray(ids)}));
            state["choicePagingNote"]="Every command remains present on a numbered page. Page selection only browses options; it does not execute a command. Page order is not priority.";
            if(commanderPage>=0&&commanderPage<pages.Count)
            {
                var ids=new HashSet<string>(pages[commanderPage]);
                var page=all.Where(o=>o.Id=="wait"||ids.Contains(o.Id)).ToList();
                page.Add(new Macro{Id="all_pages",Description="Return to all command pages without executing a command.",Apply=()=>{commanderPage=-1;return true;}});
                state["selectedChoicePage"]=commanderPage;return page;
            }
            var result=new List<Macro>{all[0]};
            for(int index=0;index<pages.Count;index++)
            {int pageIndex=index;result.Add(new Macro{Id="page_"+index,Description=$"Browse command page {index}: {string.Join(", ",pages[index])}. A later JEV choice selects the command.",Apply=()=>{commanderPage=pageIndex;return true;}});}
            return result;
        }

        JObject CommanderSiteState(global::Jev.Gameplay.Authoring.BuildSite site,FactionId faction,List<UnitState> own,HashSet<Cell> visible)
        {
            var footprint=World.Footprint(site.Kind,site.Cell);var cells=new HashSet<Cell>(footprint);var definition=World.Definitions.Building(site.Kind);
            var existing=World.Buildings.FirstOrDefault(b=>b.Alive&&b.Cell==site.Cell&&(b.Faction==faction||World.Footprint(b).Any(visible.Contains)));
            var knownTerrain=new HashSet<Cell>(own.SelectMany(u=>u.Memory.Terrain).Select(t=>t.Cell));
            var knownBlockers=new JArray();
            foreach(var cell in footprint.Where(c=>World.Blocked.Contains(c)&&(visible.Contains(c)||knownTerrain.Contains(c))))knownBlockers.Add(new JObject{["kind"]="terrain",["cell"]=CommanderJson(cell)});
            foreach(var unit in World.Units.Where(u=>u.Alive&&cells.Contains(u.Cell)&&(u.Faction==faction||visible.Contains(u.Cell))))knownBlockers.Add(new JObject{["kind"]="unit",["id"]=unit.Id,["faction"]=unit.Faction.ToString(),["cell"]=CommanderJson(unit.Cell)});
            foreach(var resource in World.Resources.Where(r=>r.Remaining>0&&cells.Contains(r.Cell)&&visible.Contains(r.Cell)))knownBlockers.Add(new JObject{["kind"]="resource",["id"]=resource.Id,["cell"]=CommanderJson(resource.Cell)});
            foreach(var building in World.Buildings.Where(b=>b.Alive&&World.Footprint(b).Any(cells.Contains)&&(b.Faction==faction||World.Footprint(b).Any(visible.Contains))))knownBlockers.Add(new JObject{["kind"]="building",["id"]=building.Id,["faction"]=building.Faction.ToString(),["cell"]=CommanderJson(building.Cell)});
            bool paid=existing!=null&&existing.Faction==faction&&existing.Kind==site.Kind;
            int unknown=footprint.Count(c=>!visible.Contains(c));
            return new JObject{["id"]=site.Id,["kind"]=site.Kind.ToString(),["owner"]=site.Faction.ToString(),["cell"]=CommanderJson(site.Cell),
                ["footprintBounds"]=new JObject{["minX"]=footprint.Min(c=>c.X),["minY"]=footprint.Min(c=>c.Y),["maxX"]=footprint.Max(c=>c.X),["maxY"]=footprint.Max(c=>c.Y)},
                ["cost"]=new JObject{["wood"]=definition.Cost.Wood,["gold"]=definition.Cost.Gold},["alreadyPaid"]=paid,
                ["populationProvided"]=definition.PopulationProvided,["effectivePopulationGain"]=paid&&existing.Complete?0:Math.Max(0,Math.Min(UnitCap,World.Faction(faction).Population.Capacity+definition.PopulationProvided)-World.Faction(faction).Population.Capacity),
                ["progress"]=paid?existing.Progress:0,["requiredWork"]=definition.RequiredWork,["completedOwnBuilding"]=paid&&existing.Complete,
                ["otherBuildingAtCenter"]=existing!=null&&!paid,["assignedWorkers"]=own.Count(u=>u.Kind==UnitKind.Worker&&u.Order?.Kind==OrderKind.Build&&u.Order.Cell==site.Cell&&u.Order.BuildingKind==site.Kind),
                ["currentlyUnseenFootprintCells"]=unknown,["knownOccupantsAndBlockers"]=knownBlockers,
                ["spaceStatus"]=paid?"own construction footprint":knownBlockers.Count>0?"observed obstruction":unknown>0?"partly unobserved; no known obstruction":"currently observed clear",
                ["spaceNote"]="Own entities are known; enemy entities and neutral occupancy are included only when visible. Unseen space is not guaranteed clear. Unit occupants may move. Assignment does not reserve, clear or construct this footprint."};
        }

        JObject CommanderEconomyState(FactionId faction)
        {
            var bank=World.Faction(faction);
            commanderBankSamples.Add(new CommanderBankSample{At=World.TimeSeconds,Sequence=World.TotalEvents,Wood=bank.Wood,Gold=bank.Gold});
            while(commanderBankSamples.Count>1&&commanderBankSamples[1].At<=World.TimeSeconds-20)commanderBankSamples.RemoveAt(0);
            var before=commanderBankSamples[0];
            return CommanderEvidence.Economy(World.Events,faction,before.Sequence,before.At,World.TimeSeconds,before.Wood,before.Gold,bank.Wood,bank.Gold);
        }

        static JToken CommanderJson(object value) => value==null ? JValue.CreateNull() : JToken.FromObject(value,CommanderSerializer);
        static Cell CommanderReadCell(JToken value) => new Cell((int)value["x"],(int)value["y"]);

        JObject CommanderUnitState(UnitState unit)
        {
            var state=(JObject)CommanderJson(new { unit.Id,unit.Kind,unit.Faction,unit.Cell,unit.Hp,unit.MaxHp,unit.Damage,unit.Range,
                unit.Order,unit.OrderRevision,unit.LastAction,recentPositions=unit.Memory.RecentPositions });
            commanderUnitReviews.TryGetValue(unit.Id,out var previous);
            double movementAt=previous==null||previous.Cell!=unit.Cell?World.TimeSeconds:previous.LastObservedMovementAt;
            state["progressSincePreviousReview"]=previous==null?null:new JObject
            {
                ["previousReviewTimeSeconds"]=previous.At,["previousCell"]=CommanderJson(previous.Cell),["cellChanged"]=previous.Cell!=unit.Cell,
                ["previousOrderRevision"]=previous.OrderRevision,["orderChanged"]=previous.OrderRevision!=unit.OrderRevision,
                ["damageTaken"]=Math.Max(0,previous.Hp-unit.Hp),
                ["lastObservedCellChangeTimeSeconds"]=movementAt
            };
            if(brains.TryGetValue(unit.Id,out var brain))
                state["executionState"]=new JObject
                {
                    ["pendingDecision"]=brain.Pending,["waitingAfterWait"]=brain.Waiting,["moving"]=brain.Moving,
                    ["approvedStepsRemaining"]=brain.ApprovedRoute.Count,["workAnimationRemainingSeconds"]=Mathf.Max(0,brain.WorkUntil-Time.time),
                    ["animationState"]=brain.Action,["lastDecisionSecondsAgo"]=Mathf.Max(0,Time.time-brain.LastDecisionAt),
                    ["wakeReasons"]=new JArray(brain.WakeReasons)
                };
            commanderUnitReviews[unit.Id]=new CommanderUnitReview{Cell=unit.Cell,OrderRevision=unit.OrderRevision,Hp=unit.Hp,At=World.TimeSeconds,LastObservedMovementAt=movementAt};
            return state;
        }

        JObject CommanderBuildingState(BuildingState building,List<UnitState> own)
        {
            var state=(JObject)CommanderJson(new { building.Id,building.Kind,building.Cell,building.Hp,building.MaxHp,building.Complete,
                building.Progress,building.RequiredWork,building.PopulationProvided,building.TrainingQueue,building.LastAction });
            state["assignedWorkers"]=CommanderJson(own.Where(u=>u.Kind==UnitKind.Worker&&u.Order?.Kind==OrderKind.Build&&
                u.Order.Cell==building.Cell&&u.Order.BuildingKind==building.Kind).Select(u=>u.Id));
            state["remainingWork"]=Math.Max(0,building.RequiredWork-building.Progress);
            state["alreadyPaid"]=true;
            state["progressSincePreviousReview"]=commanderBuildingReviews.TryGetValue(building.Id,out var previous)?new JObject
            {
                ["previousReviewTimeSeconds"]=previous.At,["workCompleted"]=building.Progress-previous.Progress,["completePreviously"]=previous.Complete,
                ["damageTaken"]=Math.Max(0,previous.Hp-building.Hp)
            }:null;
            commanderBuildingReviews[building.Id]=new CommanderBuildingReview{Progress=building.Progress,Complete=building.Complete,Hp=building.Hp,At=World.TimeSeconds};
            return state;
        }
        HashSet<Cell> FactionVision(FactionId faction)
        {
            var visible=new HashSet<Cell>();
            foreach(var u in World.Units.Where(u=>u.Alive&&u.Faction==faction))visible.UnionWith(World.VisibleCellsForUnit(u.Id));
            foreach(var b in World.Buildings.Where(b=>b.Alive&&b.Complete&&b.Faction==faction))
                visible.UnionWith(World.VisibleCellsForBuilding(b.Id));
            return visible;
        }
        bool IssueArmyMove(UnitState[] army,Cell target,FactionId faction)
        {
            if(!World.IssueGroupMove(army.Select(u=>u.Id).ToArray(),target,faction).Ok)return false;
            foreach(var u in army)if(brains.TryGetValue(u.Id,out var brain))RequestChangedOrder(u,brain);
            return true;
        }
        void UpdateTowers()
        {
            foreach(var tower in World.Buildings.Where(b=>b.Alive&&b.Complete&&b.Kind==BuildingKind.Tower))
            {
                if(!towerBrains.TryGetValue(tower.Id,out var brain)){brain=new TowerBrain();towerBrains.Add(tower.Id,brain);}
                if(brain.Pending||Time.time<brain.NextAt)continue;
                var actions=World.TowerActions(tower.Id);if(actions.Count==0)continue;
                string signature=string.Join("|",actions.Select(a=>a.Id));
                // No-target WAIT is still a model decision, then remains asleep until change/heartbeat.
                if(actions.Count==1&&signature==brain.Signature&&Time.time<brain.NextAt+Settings.IdleHeartbeat)continue;
                var state=new JObject{["self"]=CommanderJson(new{tower.Id,tower.Faction,tower.Hp,tower.MaxHp,tower.Damage,tower.Range,tower.Cell}),
                    ["targets"]=CommanderJson(actions.Where(a=>a.Kind==ActionKind.Attack).Select(a=>new{a.TargetId,a.Cell,a.Description}))};
                int session=sessionSerial;brain.Pending=true;brain.Signature=signature;
                var criteria=actions.ToDictionary(a=>a.Id,a=>a.Description);
                Transport.EvaluatePrepared(()=>JevPromptBuilder.BuildTower(state,criteria),result=>completedDecisions.Enqueue(()=>
                {
                    if(session!=sessionSerial||World==null)return;brain.Pending=false;brain.NextAt=Time.time+Settings.DecisionInterval;
                    var outcome=result.Success?World.ExecuteTowerAction(tower.Id,result.Choice):ActionResult.Fail(result.ErrorCode);
                    RecordDecision(tower.Id,result,outcome.Ok?"applied":outcome.Reason,state);
                }));
            }
        }
    }

    /// <summary>Lossless choice paging and factual accounting; neither method chooses a strategy.</summary>
    public static class CommanderEvidence
    {
        /// <summary>Lifecycle of an unissued task selector, not strategic or unit policy.</summary>
        public static string PendingTaskExpirationReason(JObject facts)
        {
            if(facts==null)return null;
            if((string)facts["taskKind"]=="build"&&(bool?)facts["completedOwnBuilding"]==true)return "construction_already_complete";
            if((string)facts["taskKind"]=="gather"&&(bool?)facts["targetVisibleDepleted"]==true)return "resource_observed_depleted";
            if((string)facts["taskKind"]=="build"&&(bool?)facts["otherBuildingAtCenter"]==true)return "site_occupied_by_another_building";
            if((int?)facts["eligibleWorkerCount"]==0)return "no_unassigned_eligible_workers";
            return null;
        }

        public static JObject WorkerFacts(UnitState worker,IEnumerable<BuildingState> ownBuildings,
            IReadOnlyDictionary<string,JObject> knownResources,JObject economy)
        {
            string status="without_order",resourceKind=null;bool? fulfilled=null;
            var order=worker.Order;
            if(order!=null)
            {
                status=order.Kind.ToString().ToLowerInvariant()+"_order";
                if(order.Kind==OrderKind.Build)
                {
                    var building=ownBuildings.FirstOrDefault(b=>b.Alive&&b.Faction==worker.Faction&&b.Cell==order.Cell&&b.Kind==order.BuildingKind);
                    fulfilled=building?.Complete??false;
                    status=fulfilled==true?"completed_build":building==null?"construction_not_started":"construction_in_progress";
                }
                else if(order.Kind==OrderKind.Move)
                {fulfilled=worker.Cell==order.Cell;status=fulfilled==true?"completed_move":"move_in_progress";}
                else if(order.Kind==OrderKind.Gather)
                {
                    resourceKind=order.TargetId!=null&&knownResources.TryGetValue(order.TargetId,out var resource)?(string)resource["kind"]:order.GatherResourceKind?.ToString()??"Unknown";
                    if(string.IsNullOrEmpty(resourceKind))resourceKind="Unknown";
                }
            }
            bool known=(bool?)economy?["journalCoversInterval"]==true&&economy?["workerCredits"] is JArray;
            var credits=(economy?["workerCredits"] as JArray)?.OfType<JObject>().FirstOrDefault(c=>(string)c["workerId"]==worker.Id);
            return new JObject
            {
                ["workerId"]=worker.Id,["orderStatus"]=status,["orderFulfilled"]=fulfilled.HasValue?new JValue(fulfilled.Value):JValue.CreateNull(),
                ["assignedResourceKind"]=resourceKind,["creditWindowKnown"]=known,
                ["creditedWood"]=known?new JValue((int?)credits?["woodCredited"]??0):JValue.CreateNull(),
                ["creditedGold"]=known?new JValue((int?)credits?["goldCredited"]??0):JValue.CreateNull(),
                ["gatherActionsInWindow"]=known?new JValue((int?)credits?["gatherActions"]??0):JValue.CreateNull()
            };
        }

        public static JObject Staffing(IEnumerable<UnitState> own,IEnumerable<JObject> workerFacts)
        {
            var facts=workerFacts.ToArray();var units=own.Where(u=>u.Alive).ToArray();
            return new JObject
            {
                ["workers"]=units.Count(u=>u.Kind==UnitKind.Worker),["army"]=units.Count(u=>u.Kind!=UnitKind.Worker),
                ["withoutOrder"]=facts.Count(f=>(string)f["orderStatus"]=="without_order"),
                ["completedBuild"]=facts.Count(f=>(string)f["orderStatus"]=="completed_build"),
                ["completedMove"]=facts.Count(f=>(string)f["orderStatus"]=="completed_move"),
                ["unfinishedConstructionOrders"]=facts.Count(f=>(string)f["orderStatus"]=="construction_not_started"||(string)f["orderStatus"]=="construction_in_progress"),
                ["gatherAssignments"]=CountGatherAssignments(facts),
                ["note"]="Counts describe current orders, not guaranteed production. completed_build means that worker's own ordered building is already complete; the Build label is no longer unfinished work. Resource credits report actual completed gathering."
            };
        }

        public static JObject AssignmentCoverage(IEnumerable<JObject> workerFacts,string workerId,string newResourceKind)
        {
            var facts=workerFacts.ToArray();var before=CountGatherAssignments(facts);var after=(JObject)before.DeepClone();
            var current=facts.FirstOrDefault(f=>(string)f["workerId"]==workerId);
            string previous=ResourceBucket((string)current?["assignedResourceKind"]),next=ResourceBucket(newResourceKind);
            if(previous!=null)after[previous]=(int)after[previous]-1;
            if(next!=null)after[next]=(int)after[next]+1;
            return new JObject{["before"]=before,["after"]=after};
        }

        static JObject CountGatherAssignments(IEnumerable<JObject> facts)
        {
            var counts=new JObject{["wood"]=0,["gold"]=0,["unknown"]=0};
            foreach(var worker in facts)
            {string bucket=ResourceBucket((string)worker["assignedResourceKind"]);if(bucket!=null)counts[bucket]=(int)counts[bucket]+1;}
            return counts;
        }

        static string ResourceBucket(string kind)=>string.IsNullOrEmpty(kind)?null:
            string.Equals(kind,"Wood",StringComparison.OrdinalIgnoreCase)?"wood":string.Equals(kind,"Gold",StringComparison.OrdinalIgnoreCase)?"gold":"unknown";

        public static JObject RefreshGoalFacts(string taskId,string taskKind,string description,JObject selectedFacts,
            JObject observedFacts,bool targetVisible,int eligibleWorkers,bool? fulfilled,double observedAt)
        {
            // Keep the task identity, but do not present its old paid/progress/remaining snapshot as current.
            var facts=(JObject)(observedFacts??selectedFacts??new JObject()).DeepClone();
            if(observedFacts==null)
            {
                foreach(string field in new[]{"alreadyPaid","progress","requiredWork","completedOwnBuilding","otherBuildingAtCenter",
                    "assignedWorkers","currentlyUnseenFootprintCells","knownOccupantsAndBlockers","spaceStatus","remaining"})facts.Remove(field);
            }
            bool depleted=taskKind=="gather"&&targetVisible&&(observedFacts==null||(int?)observedFacts["remaining"]==0);
            if(depleted)facts["remaining"]=0;
            facts["taskId"]=taskId;facts["taskKind"]=taskKind;facts["taskDescription"]=description;
            facts["reviewedAtSeconds"]=observedAt;facts["targetCurrentlyVisible"]=targetVisible;
            facts["targetVisibleDepleted"]=depleted;facts["fulfilled"]=fulfilled.HasValue?new JValue(fulfilled.Value):JValue.CreateNull();
            facts["eligibleWorkerCount"]=eligibleWorkers;
            facts["factsSource"]=observedFacts!=null?taskKind=="gather"&&!targetVisible?"last_seen_resource_memory":"current_commander_observation":
                depleted?"visible_resource_absent_or_depleted":"selected_task_identity_without_current_target_facts";
            facts["selectionNote"]="This is still the previously selected task. Facts are refreshed from current authorized commander observations and last-seen resource memory. An unseen missing target is not confirmed depleted. fulfilled=null means no global completion fact is available. Eligible workers are the offered assign options. Terminal selections close without changing any unit order; JEV still chooses the next task. WAIT defers the unissued selection and returns to strategic review. No physical work or unit assignment happens automatically.";
            return facts;
        }

        public static List<string[]> PageIds(IReadOnlyList<string> ids)
        {
            // A command page also needs WAIT and a return-to-pages option.
            int pageSize=JevResponseValidator.MaximumChoices-2;
            var pages=new List<string[]>();
            for(int offset=0;offset<ids.Count;offset+=pageSize)
            {
                var page=new string[Math.Min(pageSize,ids.Count-offset)];
                for(int index=0;index<page.Length;index++)page[index]=ids[offset+index];
                pages.Add(page);
            }
            return pages;
        }

        public static JObject Economy(IReadOnlyList<WorldEvent> events,FactionId faction,long afterSequence,
            double fromSeconds,double nowSeconds,int woodBefore,int goldBefore,int woodNow,int goldNow)
        {
            // The world journal is bounded. Missing old entries must not masquerade as zero income.
            bool complete=events.Count==0?afterSequence==0:events[0].Sequence<=afterSequence+1;
            var gathered=events.Where(e=>e.Sequence>afterSequence&&e.TimeSeconds<=nowSeconds&&
                e.Faction==faction&&e.Type=="gather").ToArray();
            int wood=gathered.Where(e=>e.ResourceKind==ResourceKind.Wood).Sum(e=>e.Amount);
            int gold=gathered.Where(e=>e.ResourceKind==ResourceKind.Gold).Sum(e=>e.Amount);
            var perWorker=new JArray(gathered.GroupBy(e=>e.ActorId).Select(group=>new JObject
            {
                ["workerId"]=group.Key,["woodCredited"]=group.Where(e=>e.ResourceKind==ResourceKind.Wood).Sum(e=>e.Amount),
                ["goldCredited"]=group.Where(e=>e.ResourceKind==ResourceKind.Gold).Sum(e=>e.Amount),["gatherActions"]=group.Count()
            }));
            return new JObject
            {
                ["requestedWindowSeconds"]=20,["fromSeconds"]=fromSeconds,["toSeconds"]=nowSeconds,
                ["measuredSeconds"]=Math.Max(0,nowSeconds-fromSeconds),["journalCoversInterval"]=complete,
                ["bankBefore"]=new JObject{["wood"]=woodBefore,["gold"]=goldBefore},
                ["bankNow"]=new JObject{["wood"]=woodNow,["gold"]=goldNow},
                ["netStockChange"]=new JObject{["wood"]=woodNow-woodBefore,["gold"]=goldNow-goldBefore},
                ["resourcesCredited"]=complete?new JObject{["wood"]=wood,["gold"]=gold}:null,
                ["netResourcesSpent"]=complete?new JObject{["wood"]=woodBefore+wood-woodNow,["gold"]=goldBefore+gold-goldNow}:null,
                ["workerCredits"]=complete?perWorker:null,
                ["accountingNote"]="Own-faction facts only. Credited income counts completed gather actions; movement and gather orders are not income. Net spent = bankBefore + credited - bankNow. Negative net spending means another credit or refund. The measured interval uses the nearest available review at or before 20 seconds ago, or session start; missing journal coverage yields unknown income/spending."
            };
        }
    }
}
