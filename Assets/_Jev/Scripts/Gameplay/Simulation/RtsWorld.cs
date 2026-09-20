using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Jev.Gameplay.Simulation
{
    /// <summary>Authoritative physical rules. This class never chooses a route, target, order or strategy.</summary>
    [Serializable]
    public sealed partial class RtsWorld
    {
        public int Width = 80, Height = 68;
        public float CellSize = 1.2f;
        public Vector2 Origin;
        public GameRules Definitions = new GameRules();
        public HashSet<Cell> Blocked = new HashSet<Cell>();
        // Null preserves the compact test-world convention: movement walls also block sight.
        // Authored battlefields provide a separate set so water and slopes never become opaque walls.
        public HashSet<Cell> SightBlocked;
        public List<UnitState> Units = new List<UnitState>();
        public List<BuildingState> Buildings = new List<BuildingState>();
        public List<ResourceState> Resources = new List<ResourceState>();
        public List<WorldEvent> Events = new List<WorldEvent>();
        public const int MaxRetainedEvents = 4096;
        public long TotalEvents => eventSequence;
        public FactionState Human = new FactionState { Id=FactionId.Human,Traits=FactionTraits.For(FactionId.Human) };
        public FactionState Undead = new FactionState { Id=FactionId.Undead,Traits=FactionTraits.For(FactionId.Undead) };
        public double TimeSeconds;
        public FactionId PlayerFaction;
        public bool VictoryEnabled, MatchEnded, Draw;
        public FactionId? Winner;
        public ResourceCost TrainingSpent = new ResourceCost();
        private long nextId = 1, eventSequence;
        private static readonly Cell[] Directions = { new Cell(0,-1),new Cell(1,0),new Cell(0,1),new Cell(-1,0) };
        private static readonly string[] DirectionNames = { "north","east","south","west" };
        public FactionState Faction(FactionId faction) => faction == FactionId.Human ? Human : Undead;
        public UnitState Unit(string id)
        {
            for (int i = 0; i < Units.Count; i++) if (Units[i].Id == id) return Units[i];
            return null;
        }
        public BuildingState Building(string id)
        {
            for (int i = 0; i < Buildings.Count; i++) if (Buildings[i].Id == id) return Buildings[i];
            return null;
        }
        public ResourceState Resource(string id)
        {
            for (int i = 0; i < Resources.Count; i++) if (Resources[i].Id == id) return Resources[i];
            return null;
        }
        public bool InBounds(Cell cell) => cell.X >= 0 && cell.Y >= 0 && cell.X < Width && cell.Y < Height;
        public IEnumerable<Cell> Footprint(BuildingState building) => building.Footprint.Count == 0 ? new[] {building.Cell} : building.Footprint;
        public List<Cell> Footprint(BuildingKind kind, Cell center)
        {
            var definition = Definitions.Building(kind);
            var result = new List<Cell>();
            int sx=Math.Max(1,definition.FootprintSizeX), sy=Math.Max(1,definition.FootprintSizeY);
            for(int y=0;y<sy;y++) for(int x=0;x<sx;x++) result.Add(new Cell(center.X+x-sx/2,center.Y+y-sy/2));
            return result;
        }
        public bool IsOccupied(Cell cell, string exceptId = null) =>
            Units.Any(u=>u.Alive && u.Id!=exceptId && u.Cell==cell) ||
            Resources.Any(r=>r.Remaining>0 && r.Id!=exceptId && r.Cell==cell) ||
            Buildings.Any(b=>b.Alive && b.Id!=exceptId && Footprint(b).Contains(cell));
        public bool IsFree(Cell cell, string exceptId=null) => InBounds(cell) && !Blocked.Contains(cell) && !IsOccupied(cell,exceptId);
        public bool CanPlaceBuilding(BuildingKind kind, Cell cell) => Footprint(kind,cell).All(c=>IsFree(c));
        private string NewId(string prefix)
        {
            string id;
            do { id=prefix+"-"+nextId++; } while(Units.Any(u=>u.Id==id)||Buildings.Any(b=>b.Id==id)||Resources.Any(r=>r.Id==id));
            return id;
        }
        private void CheckNewId(string id)
        {
            if(string.IsNullOrWhiteSpace(id)||Units.Any(u=>u.Id==id)||Buildings.Any(b=>b.Id==id)||Resources.Any(r=>r.Id==id))
                throw new ArgumentException("Entity ID must be nonempty and unique.");
        }
        public UnitState AddUnit(FactionId faction, UnitKind kind, Cell cell, string id=null)
        {
            if(!IsFree(cell)) throw new InvalidOperationException("Unit spawn cell is unavailable: "+cell);
            id=id??NewId(faction.ToString().ToLowerInvariant()+"-"+kind.ToString().ToLowerInvariant()); CheckNewId(id);
            var spec=Definitions.Unit(kind);
            int hp=Math.Max(1,(int)Math.Floor(spec.MaxHp*Faction(faction).Traits.HealthMultiplier+.5));
            var unit=new UnitState { Id=id,Faction=faction,Kind=kind,Cell=cell,Hp=hp,MaxHp=hp,Damage=spec.Damage,Range=spec.Range,Vision=spec.Vision };
            Units.Add(unit); RefreshPopulation(); return unit;
        }
        public BuildingState AddBuilding(FactionId faction, BuildingKind kind, Cell cell, bool complete=true, string id=null)
        {
            if(!CanPlaceBuilding(kind,cell)) throw new InvalidOperationException("Building footprint is unavailable: "+cell);
            id=id??NewId(faction.ToString().ToLowerInvariant()+"-"+kind.ToString().ToLowerInvariant()); CheckNewId(id);
            var spec=Definitions.Building(kind);
            var building=new BuildingState { Id=id,Faction=faction,Kind=kind,Cell=cell,Footprint=Footprint(kind,cell),Hp=spec.MaxHp,MaxHp=spec.MaxHp,
                Progress=complete?spec.RequiredWork:0,RequiredWork=spec.RequiredWork,Complete=complete,PopulationProvided=spec.PopulationProvided,
                Damage=spec.Damage,Range=spec.Range,Vision=spec.Vision };
            Buildings.Add(building); RefreshPopulation(); return building;
        }
        public ResourceState AddResource(ResourceKind kind, Cell cell, int remaining, string id=null)
        {
            if(remaining<0 || !IsFree(cell)) throw new InvalidOperationException("Resource cell or amount is invalid: "+cell);
            id=id??NewId(kind.ToString().ToLowerInvariant()); CheckNewId(id);
            var resource=new ResourceState { Id=id,Kind=kind,Cell=cell,Remaining=remaining }; Resources.Add(resource); return resource;
        }
        public void StartMatch(FactionId playerFaction, int cap, Cell humanHall, Cell humanWorker, Cell undeadHall, Cell undeadWorker, int startWood=60, int startGold=40)
        {
            if(cap<1 || startWood<0 || startGold<0) throw new ArgumentOutOfRangeException(nameof(cap));
            // Authored terrain and resource nodes survive match initialization.
            Units.Clear(); Buildings.Clear(); Events.Clear(); nextId=1;eventSequence=0;TimeSeconds=0;
            Definitions.PerFactionUnitLimit=cap; PlayerFaction=playerFaction; MatchEnded=false;Draw=false;Winner=null;VictoryEnabled=false;
            TrainingSpent=new ResourceCost();
            foreach(var faction in new[]{Human,Undead}) { faction.Wood=startWood;faction.Gold=startGold;faction.BasePopulationCapacity=0;faction.Traits=FactionTraits.For(faction.Id); }
            AddBuilding(FactionId.Human,BuildingKind.TownHall,humanHall,true,"human-townhall");
            AddBuilding(FactionId.Undead,BuildingKind.TownHall,undeadHall,true,"undead-townhall");
            AddUnit(FactionId.Human,UnitKind.Worker,humanWorker,"human-worker");
            AddUnit(FactionId.Undead,UnitKind.Worker,undeadWorker,"undead-worker");
            VictoryEnabled=true;RefreshPopulation();AssertInvariants();
        }
        public bool SetOrder(string unitId, Order order)
        {
            var unit=Unit(unitId);
            if(unit==null||!unit.Alive||MatchEnded||order==null||!InBounds(order.Cell)) return false;
            if((order.Kind==OrderKind.Build||order.Kind==OrderKind.Gather)&&unit.Kind!=UnitKind.Worker) return false;
            unit.Order=order.Copy();
            // Preserve the resource type carried by the command after its initial node
            // disappears from sensors. This captures no replacement or hidden location.
            if(unit.Order.Kind==OrderKind.Gather&&!unit.Order.GatherResourceKind.HasValue)
            {
                var initialResource=Resource(unit.Order.TargetId);
                if(initialResource!=null&&initialResource.Cell==unit.Order.Cell)unit.Order.GatherResourceKind=initialResource.Kind;
            }
            unit.OrderRevision++;
            Emit("order_changed",unit.Id,unit.Faction,null,unit.Cell,order.Cell);
            return true;
        }
        /// <summary>Give every selected unit the same clicked target and shared arrival area. Never allocate positions or routes.</summary>
        public ActionResult IssueGroupMove(IReadOnlyList<string> unitIds, Cell target, FactionId owner)
        {
            if(MatchEnded)return ActionResult.Fail("match_ended");
            if(unitIds==null||unitIds.Count==0||unitIds.Count>Definitions.PerFactionUnitLimit||unitIds.Distinct().Count()!=unitIds.Count)
                return ActionResult.Fail("invalid_selection");
            var selected=unitIds.Select(Unit).ToList();
            if(selected.Any(u=>u==null||!u.Alive||u.Faction!=owner)) return ActionResult.Fail("not_unit_owner");
            // A command is not an occupancy query. Hidden dynamic occupants do
            // not affect acceptance; actual chosen steps still check collisions.
            if(!InBounds(target)||Blocked.Contains(target))return ActionResult.Fail("destination_blocked");
            var prior=selected[0].Order;
            bool preserve=prior!=null&&prior.Kind==OrderKind.Move&&!string.IsNullOrEmpty(prior.GroupId)&&prior.GroupMembers.Count==selected.Count&&
                prior.GroupMembers.All(unitIds.Contains)&&selected.All(u=>u.Order?.GroupId==prior.GroupId);
            var members=preserve?new List<string>(prior.GroupMembers):unitIds.ToList();
            string group=preserve?prior.GroupId:NewId("selection");
            int radius=members.Count>1?(int)Math.Ceiling((Math.Sqrt(members.Count)-1)/2)+1:0;
            foreach(var unit in selected)SetOrder(unit.Id,new Order { Kind=OrderKind.Move,Cell=target,GroupId=group,GroupMembers=members,GroupArrivalRadius=radius });
            return ActionResult.Success("group_ordered");
        }
        public void RefreshPopulation()
        {
            foreach(var faction in new[]{Human,Undead})
            {
                var buildings=Buildings.Where(b=>b.Faction==faction.Id&&b.Alive).ToList();
                faction.Population.Used=Units.Count(u=>u.Faction==faction.Id&&u.Alive);
                faction.Population.Capacity=Math.Min(Definitions.PerFactionUnitLimit,Math.Max(0,faction.BasePopulationCapacity+buildings.Where(b=>b.Complete).Sum(b=>b.PopulationProvided)));
                faction.Population.Reserved=buildings.Sum(b=>b.TrainingQueue.Count);
            }
        }
        public ActionResult EnqueueTraining(string buildingId, UnitKind kind, FactionId requester)
        {
            if(MatchEnded) return ActionResult.Fail("match_ended");
            var building=Building(buildingId);
            if(building==null||!building.Alive) return ActionResult.Fail("building_unavailable");
            if(building.Faction!=requester) return ActionResult.Fail("not_building_owner");
            if(!building.Complete) return ActionResult.Fail("building_incomplete");
            if(!Definitions.Building(building.Kind).TrainableUnits.Contains(kind)) return ActionResult.Fail("unit_not_trainable_here");
            RefreshPopulation();
            var faction=Faction(requester);var spec=Definitions.Unit(kind);var pop=faction.Population;
            if(pop.Used+pop.Reserved>=Definitions.PerFactionUnitLimit) return ActionResult.Fail("faction_unit_limit");
            if(pop.Used+pop.Reserved>=pop.Capacity) return ActionResult.Fail("population_capacity");
            if(faction.Wood<spec.Cost.Wood) return ActionResult.Fail("insufficient_wood");
            if(faction.Gold<spec.Cost.Gold) return ActionResult.Fail("insufficient_gold");
            Pay(faction,spec.Cost); TrainingSpent.Wood+=spec.Cost.Wood;TrainingSpent.Gold+=spec.Cost.Gold;
            building.TrainingQueue.Add(new TrainingJob { Kind=kind,RemainingSeconds=spec.TrainingSeconds,Cost=spec.Cost.Copy() });
            RefreshPopulation();Emit("training_queued",building.Id,requester,null,building.Cell,building.Cell);
            return ActionResult.Success("queued",building.Id);
        }
        /// <summary>Advance explicit production and the clock only. Never repeats a unit action.</summary>
        public void Tick(double seconds)
        {
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<0) throw new ArgumentOutOfRangeException(nameof(seconds));
            if(MatchEnded) return;
            double start=TimeSeconds,budget=seconds,elapsedTotal=0;
            var blocked=new HashSet<string>();
            CancelDestroyedQueues();
            while(true)
            {
                var active=Buildings.Where(b=>b.Alive&&b.Complete&&b.TrainingQueue.Count>0&&!blocked.Contains(b.Id)).OrderBy(b=>b.Id,StringComparer.Ordinal).ToList();
                if(active.Count==0) break;
                double next=active.Min(b=>b.TrainingQueue[0].RemainingSeconds);
                double elapsed=Math.Min(budget,next);
                foreach(var building in active) building.TrainingQueue[0].RemainingSeconds=Math.Max(0,building.TrainingQueue[0].RemainingSeconds-elapsed);
                budget-=elapsed;elapsedTotal+=elapsed;TimeSeconds=start+elapsedTotal;
                if(elapsed+1e-8<next) break;
                foreach(var building in active.Where(b=>b.TrainingQueue[0].RemainingSeconds<=1e-8))
                {
                    RefreshPopulation();var faction=Faction(building.Faction);Cell? exit=SpawnCell(building);
                    if(!exit.HasValue||faction.Population.Used>=faction.Population.Capacity||faction.Population.Used>=Definitions.PerFactionUnitLimit)
                    { blocked.Add(building.Id);continue; }
                    var job=building.TrainingQueue[0];building.TrainingQueue.RemoveAt(0);
                    var unit=AddUnit(building.Faction,job.Kind,exit.Value);unit.TrainingCost=job.Cost.Copy();
                    Emit("training_completed",building.Id,building.Faction,unit.Id,building.Cell,unit.Cell);
                }
            }
            TimeSeconds=start+seconds;RefreshPopulation();CheckVictory();
        }
        public Cell? SpawnCell(BuildingState building)
        {
            var footprint=Footprint(building).ToList();int minX=footprint.Min(c=>c.X),maxX=footprint.Max(c=>c.X),minY=footprint.Min(c=>c.Y),maxY=footprint.Max(c=>c.Y);
            // Each side starts at the authored center, then visits its remaining edge cells in stable coordinate order.
            var candidates=new List<Cell>();
            candidates.Add(new Cell(building.Cell.X,minY-1)); for(int x=minX;x<=maxX;x++) candidates.Add(new Cell(x,minY-1));
            candidates.Add(new Cell(maxX+1,building.Cell.Y)); for(int y=minY;y<=maxY;y++) candidates.Add(new Cell(maxX+1,y));
            candidates.Add(new Cell(building.Cell.X,maxY+1)); for(int x=minX;x<=maxX;x++) candidates.Add(new Cell(x,maxY+1));
            candidates.Add(new Cell(minX-1,building.Cell.Y)); for(int y=minY;y<=maxY;y++) candidates.Add(new Cell(minX-1,y));
            foreach(var cell in candidates.Distinct()) if(IsFree(cell)) return cell;
            return null;
        }
        private void CancelDestroyedQueues()
        {
            foreach(var building in Buildings.Where(b=>!b.Alive&&b.TrainingQueue.Count>0))
            { int count=building.TrainingQueue.Count;building.TrainingQueue.Clear();Emit("training_cancelled",building.Id,building.Faction,null,building.Cell,building.Cell,count,"building_destroyed"); }
        }
        private static bool Affordable(FactionState faction,ResourceCost cost) => faction.Wood>=cost.Wood&&faction.Gold>=cost.Gold;
        private static void Pay(FactionState faction,ResourceCost cost) { faction.Wood-=cost.Wood;faction.Gold-=cost.Gold; }
        private void Emit(string type,string actor,FactionId faction,string target,Cell from,Cell to,int amount=0,string reason=null,ResourceKind resourceKind=ResourceKind.Wood)
        {
            if(Events.Count>=MaxRetainedEvents)Events.RemoveRange(0,Events.Count-MaxRetainedEvents+1);
            Events.Add(new WorldEvent { Sequence=++eventSequence,TimeSeconds=TimeSeconds,Type=type,ActorId=actor,Faction=faction,TargetId=target,From=from,To=to,Amount=amount,Reason=reason,ResourceKind=resourceKind });
        }
        private ActionResult Finish(UnitState unit,LegalAction action,bool ok,string reason,string target=null)
        {
            unit.LastAction=new LastAction { TimeSeconds=TimeSeconds,Kind=action.Kind,ActionId=action.Id,TargetId=target??action.TargetId,Ok=ok,Reason=reason };
            if(!ok) Emit("action_rejected",unit.Id,unit.Faction,action.TargetId,unit.Cell,unit.Cell,0,reason);
            return ok?ActionResult.Success(reason,unit.Id,target??action.TargetId):ActionResult.Fail(reason,unit.Id);
        }
        public ActionResult Execute(string unitId,string actionId,int expectedRevision)
            => ExecuteBatch(new[]{new ActionChoice(unitId,actionId,expectedRevision)})[0];

        /// <summary>Canonical IDs are resolved against one common snapshot; supplied effect fields are never trusted.</summary>
        public List<ActionResult> ExecuteBatch(IReadOnlyList<ActionChoice> choices)
        {
            var results=new ActionResult[choices.Count];var prepared=new List<PreparedAction>();var decided=new HashSet<string>();
            for(int i=0;i<choices.Count;i++)
            {
                var choice=choices[i];var unit=Unit(choice.UnitId);
                if(MatchEnded) { results[i]=ActionResult.Fail("match_ended",choice.UnitId);continue; }
                if(unit==null||!unit.Alive) { results[i]=ActionResult.Fail("unit_unavailable",choice.UnitId);continue; }
                if(!decided.Add(unit.Id)) { results[i]=ActionResult.Fail("duplicate_decision",unit.Id);continue; }
                if(unit.OrderRevision!=choice.OrderRevision) { results[i]=ActionResult.Fail("stale_order",unit.Id);continue; }
                var action=LegalActions(unit.Id,true).Find(a=>a.Id==choice.ActionId);
                if(action==null) { results[i]=Finish(unit,new LegalAction { Id=choice.ActionId },false,"illegal_action");continue; }
                prepared.Add(new PreparedAction { Unit=unit,Action=action,Index=i });
            }
            prepared=prepared.OrderBy(p=>p.Unit.Id,StringComparer.Ordinal).ToList();
            if(prepared.Count>0)
            {
                int shift=(int)Math.Floor(TimeSeconds/Math.Max(.01,Definitions.DecisionIntervalSeconds))%prepared.Count;
                prepared=prepared.Skip(shift).Concat(prepared.Take(shift)).ToList();
            }
            var damages=new Dictionary<string,int>();
            foreach(var ready in prepared)
            {
                var unit=ready.Unit;var action=ready.Action;ActionResult result;
                if(action.Kind==ActionKind.Move)
                {
                    // Runtime presents the approved route and consumes these exact cells with TryExecuteStep.
                    result=ActionResult.Success("segment_approved",unit.Id);result.ApprovedCells=new List<Cell>(action.Cells);
                    unit.LastAction=new LastAction { TimeSeconds=TimeSeconds,Kind=ActionKind.Move,ActionId=action.Id,Ok=true,Reason="segment_approved" };
                    Emit("segment_approved",unit.Id,unit.Faction,null,unit.Cell,action.Cell,action.Cells.Count);
                }
                else if(action.Kind==ActionKind.Attack)
                {
                    damages.TryGetValue(action.TargetId,out int amount);damages[action.TargetId]=amount+unit.Damage;
                    Emit("attack",unit.Id,unit.Faction,action.TargetId,unit.Cell,action.Cell,unit.Damage);
                    result=Finish(unit,action,true,"executed");
                }
                else if(action.Kind==ActionKind.Gather)
                {
                    var resource=Resource(action.TargetId);int amount=Math.Min(resource.Remaining,resource.Kind==ResourceKind.Wood?Definitions.WoodPerGather:Definitions.GoldPerGather);
                    resource.Remaining-=amount;var faction=Faction(unit.Faction);
                    if(resource.Kind==ResourceKind.Wood) faction.Wood+=amount;else faction.Gold+=amount;
                    Emit("gather",unit.Id,unit.Faction,resource.Id,unit.Cell,resource.Cell,amount,null,resource.Kind);
                    result=Finish(unit,action,amount>0,amount>0?"executed":"resource_depleted_by_other_unit");
                }
                else if(action.Kind==ActionKind.Build) result=Build(unit,action);
                else { Emit("wait",unit.Id,unit.Faction,null,unit.Cell,unit.Cell);result=Finish(unit,action,true,"executed"); }
                results[ready.Index]=result;
            }
            ApplyDamage(damages);RefreshPopulation();CheckVictory();return results.ToList();
        }
        private sealed class PreparedAction { public UnitState Unit;public LegalAction Action;public int Index; }
        private ActionResult Build(UnitState unit,LegalAction action)
        {
            var building=Buildings.Find(b=>b.Alive&&b.Cell==action.Cell);
            if(building!=null&&(building.Faction!=unit.Faction||building.Kind!=action.BuildingKind||building.Complete)) return Finish(unit,action,false,"build_site_unavailable");
            if(building==null)
            {
                var spec=Definitions.Building(action.BuildingKind);var faction=Faction(unit.Faction);
                if(!Affordable(faction,spec.Cost)) return Finish(unit,action,false,"resources_spent_by_other_worker");
                if(!CanPlaceBuilding(action.BuildingKind,action.Cell)) return Finish(unit,action,false,"build_site_occupied");
                Pay(faction,spec.Cost);building=AddBuilding(unit.Faction,action.BuildingKind,action.Cell,false);building.Cost=spec.Cost.Copy();
                Emit("build_started",unit.Id,unit.Faction,building.Id,unit.Cell,building.Cell);
            }
            building.Progress=Math.Min(building.RequiredWork,building.Progress+1);
            Emit("build_progress",unit.Id,unit.Faction,building.Id,unit.Cell,building.Cell,building.Progress);
            if(building.Progress==building.RequiredWork) { building.Complete=true;Emit("build_completed",unit.Id,unit.Faction,building.Id,unit.Cell,building.Cell); }
            return Finish(unit,action,true,"executed",building.Id);
        }
        /// <summary>Execute one already JEV-approved cardinal cell. A collision rejects; it never substitutes a step.</summary>
        public ActionResult TryExecuteStep(string unitId,Cell destination,int expectedRevision)
        {
            var unit=Unit(unitId);if(unit==null||!unit.Alive) return ActionResult.Fail("unit_unavailable",unitId);
            if(MatchEnded) return ActionResult.Fail("match_ended",unitId);
            if(unit.OrderRevision!=expectedRevision) return ActionResult.Fail("stale_order",unitId);
            var action=new LegalAction { Kind=ActionKind.Move,Id="approved_step",Cell=destination };
            if(Cell.Manhattan(unit.Cell,destination)!=1) return Finish(unit,action,false,"nonadjacent_step");
            if(!IsFree(destination)) return Finish(unit,action,false,"destination_occupied_during_resolution");
            var from=unit.Cell;unit.Cell=destination;Emit("move",unit.Id,unit.Faction,null,from,destination);
            return Finish(unit,action,true,"executed");
        }
        /// <summary>Optional common-snapshot collision arbitration for runtime steps becoming due together.</summary>
        public List<ActionResult> TryExecuteSteps(IReadOnlyList<StepChoice> choices)
        {
            var results=new ActionResult[choices.Count];var eligible=new List<int>();var seen=new HashSet<string>();
            for(int i=0;i<choices.Count;i++)
            {
                var step=choices[i];var unit=Unit(step.UnitId);
                if(unit==null||!unit.Alive) results[i]=ActionResult.Fail("unit_unavailable",step.UnitId);
                else if(!seen.Add(unit.Id)) results[i]=ActionResult.Fail("duplicate_decision",unit.Id);
                else if(unit.OrderRevision!=step.OrderRevision) results[i]=ActionResult.Fail("stale_order",unit.Id);
                else if(Cell.Manhattan(unit.Cell,step.Destination)!=1||!IsFree(step.Destination)) results[i]=ActionResult.Fail("destination_unavailable_in_snapshot",unit.Id);
                else eligible.Add(i);
            }
            eligible=eligible.OrderBy(i=>choices[i].UnitId,StringComparer.Ordinal).ToList();
            if(eligible.Count>0) { int shift=(int)Math.Floor(TimeSeconds/Math.Max(.01,Definitions.DecisionIntervalSeconds))%eligible.Count;eligible=eligible.Skip(shift).Concat(eligible.Take(shift)).ToList(); }
            var claimed=new HashSet<Cell>();
            foreach(int index in eligible)
            {
                var choice=choices[index];var unit=Unit(choice.UnitId);
                bool conflict=Definitions.RotatingMovementPriority?!claimed.Add(choice.Destination):eligible.Count(i=>choices[i].Destination==choice.Destination)>1;
                results[index]=conflict?Finish(unit,new LegalAction { Kind=ActionKind.Move,Id="approved_step" },false,"movement_conflict"):
                    TryExecuteStep(choice.UnitId,choice.Destination,choice.OrderRevision);
            }
            return results.ToList();
        }
        public void RecordDecisionFailure(string unitId,string reason)
        {
            var unit=Unit(unitId);if(unit==null||!unit.Alive) return;
            unit.LastAction=new LastAction { TimeSeconds=TimeSeconds,Ok=false,Reason=reason,ActionId="none" };
            Emit("decision_error",unit.Id,unit.Faction,null,unit.Cell,unit.Cell,0,reason);
        }
        public List<LegalAction> TowerActions(string buildingId)
        {
            var building=Building(buildingId);var result=new List<LegalAction>();
            if(building==null||!building.Alive||!building.Complete||building.Damage<=0||MatchEnded) return result;
            result.Add(new LegalAction { Id="wait",Kind=ActionKind.Wait,Description="Wait for a visible change or the next review." });
            foreach(var enemy in Units.Where(u=>u.Alive&&u.Faction!=building.Faction))
                if(Footprint(building).Any(c=>Cell.Chebyshev(c,enemy.Cell)<=building.Range&&HasLineOfSight(c,enemy.Cell,building.Id)))
                    result.Add(new LegalAction { Id="attack_"+enemy.Id,Kind=ActionKind.Attack,TargetId=enemy.Id,Cell=enemy.Cell,Description=$"Attack visible enemy {enemy.Kind} {enemy.Id} for {building.Damage} damage." });
            foreach(var enemy in Buildings.Where(b=>b.Alive&&b.Faction!=building.Faction))
                if(Footprint(building).Any(c=>Footprint(enemy).Any(e=>Cell.Chebyshev(c,e)<=building.Range&&HasLineOfSight(c,e,building.Id))))
                    result.Add(new LegalAction { Id="attack_"+enemy.Id,Kind=ActionKind.Attack,TargetId=enemy.Id,Cell=enemy.Cell,Description=$"Attack visible enemy building {enemy.Id} for {building.Damage} damage." });
            return result;
        }
        public ActionResult ExecuteTowerAction(string buildingId,string actionId)
        {
            var building=Building(buildingId);var action=TowerActions(buildingId).Find(a=>a.Id==actionId);
            if(action==null) return ActionResult.Fail("illegal_tower_action",buildingId);
            building.LastAction=new LastAction { TimeSeconds=TimeSeconds,Kind=action.Kind,ActionId=action.Id,TargetId=action.TargetId,Ok=true,Reason="executed" };
            Emit(action.Kind==ActionKind.Attack?"attack":"wait",building.Id,building.Faction,action.TargetId,building.Cell,action.Cell,action.Kind==ActionKind.Attack?building.Damage:0);
            if(action.Kind==ActionKind.Attack) ApplyDamage(new Dictionary<string,int> { [action.TargetId]=building.Damage });
            RefreshPopulation();CheckVictory();return ActionResult.Success("executed",buildingId,action.TargetId);
        }
        private void ApplyDamage(Dictionary<string,int> damage)
        {
            foreach(var item in damage)
            {
                var unit=Unit(item.Key);
                if(unit!=null) { unit.Hp=Math.Max(0,unit.Hp-item.Value);if(!unit.Alive) Emit("unit_died",unit.Id,unit.Faction,unit.Id,unit.Cell,unit.Cell); }
                else { var building=Building(item.Key);if(building==null) continue;building.Hp=Math.Max(0,building.Hp-item.Value);if(!building.Alive) Emit("building_destroyed",building.Id,building.Faction,building.Id,building.Cell,building.Cell); }
            }
            CancelDestroyedQueues();
        }
        public void CheckVictory()
        {
            if(!VictoryEnabled||MatchEnded) return;
            bool human=Buildings.Any(b=>b.Alive&&b.Kind==BuildingKind.TownHall&&b.Faction==FactionId.Human);
            bool undead=Buildings.Any(b=>b.Alive&&b.Kind==BuildingKind.TownHall&&b.Faction==FactionId.Undead);
            if(human&&undead) return;
            MatchEnded=true;Draw=!human&&!undead;Winner=Draw?(FactionId?)null:human?FactionId.Human:FactionId.Undead;
            Emit("match_ended",null,Winner??PlayerFaction,null,default,default,0,Draw?"draw":"last_townhall_destroyed");
        }
        public ResourceCost TotalAccountedResources() => new ResourceCost(
            Human.Wood+Undead.Wood+Resources.Where(r=>r.Kind==ResourceKind.Wood).Sum(r=>r.Remaining)+Buildings.Sum(b=>b.Cost.Wood)+TrainingSpent.Wood,
            Human.Gold+Undead.Gold+Resources.Where(r=>r.Kind==ResourceKind.Gold).Sum(r=>r.Remaining)+Buildings.Sum(b=>b.Cost.Gold)+TrainingSpent.Gold);
        public void AssertInvariants(ResourceCost expectedConservation=null)
        {
            var errors=new List<string>();var ids=new HashSet<string>();var occupied=new HashSet<Cell>();
            if(Width<1||Height<1||Definitions.PerFactionUnitLimit<1) errors.Add("Invalid map or cap");
            Action<string,Cell,bool> check=(id,cell,alive)=> { if(!InBounds(cell)||Blocked.Contains(cell)) errors.Add("Invalid cell "+id);if(alive&&!occupied.Add(cell)) errors.Add("Occupied overlap "+cell); };
            Action<string> idCheck=id=> { if(string.IsNullOrWhiteSpace(id)||!ids.Add(id)) errors.Add("Duplicate or empty ID "+id); };
            foreach(var unit in Units) { idCheck(unit.Id);check(unit.Id,unit.Cell,unit.Alive);if(unit.Hp<0||unit.Hp>unit.MaxHp||unit.MaxHp<1) errors.Add("Invalid unit HP "+unit.Id); }
            foreach(var resource in Resources) { idCheck(resource.Id);check(resource.Id,resource.Cell,resource.Remaining>0);if(resource.Remaining<0) errors.Add("Negative resource "+resource.Id); }
            foreach(var building in Buildings)
            {
                idCheck(building.Id);foreach(var cell in Footprint(building)) check(building.Id,cell,building.Alive);
                if(building.Hp<0||building.Hp>building.MaxHp||building.MaxHp<1) errors.Add("Invalid building HP "+building.Id);
                if(building.Progress<0||building.Progress>building.RequiredWork||building.RequiredWork<1||building.Complete!=(building.Progress==building.RequiredWork)) errors.Add("Invalid construction "+building.Id);
                if(building.Cost.Wood<0||building.Cost.Gold<0||building.TrainingQueue.Any(j=>j.RemainingSeconds<0||double.IsNaN(j.RemainingSeconds)||double.IsInfinity(j.RemainingSeconds))) errors.Add("Invalid economy "+building.Id);
            }
            foreach(var faction in new[]{Human,Undead})
            {
                var alive=Buildings.Where(b=>b.Alive&&b.Faction==faction.Id).ToList();
                int used=Units.Count(u=>u.Alive&&u.Faction==faction.Id),reserved=alive.Sum(b=>b.TrainingQueue.Count),capacity=Math.Min(Definitions.PerFactionUnitLimit,faction.BasePopulationCapacity+alive.Where(b=>b.Complete).Sum(b=>b.PopulationProvided));
                if(used>Definitions.PerFactionUnitLimit||used+reserved>Definitions.PerFactionUnitLimit) errors.Add("Faction unit limit "+faction.Id);
                if(faction.Wood<0||faction.Gold<0) errors.Add("Negative faction stock "+faction.Id);
                if(faction.Population.Used!=used||faction.Population.Reserved!=reserved||faction.Population.Capacity!=capacity) errors.Add("Invalid population "+faction.Id);
            }
            if(expectedConservation!=null) { var total=TotalAccountedResources();if(total.Wood!=expectedConservation.Wood||total.Gold!=expectedConservation.Gold) errors.Add("Resource conservation violated"); }
            if(errors.Count>0) throw new InvalidOperationException(string.Join("; ",errors));
        }
    }
}
