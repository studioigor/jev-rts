using System;
using System.Linq;
using NUnit.Framework;
using Jev.Gameplay.Simulation;

namespace Jev.Gameplay.Simulation.Tests
{
    public sealed class SimulationRulesTests
    {
        private static RtsWorld World(int cap=20)
        {
            var world=new RtsWorld { Width=32,Height=24 };
            world.Definitions.PerFactionUnitLimit=cap;
            world.Human.BasePopulationCapacity=cap;world.Undead.BasePopulationCapacity=cap;
            world.RefreshPopulation();return world;
        }
        [Test] public void ReferenceDefinitionsAndUndeadStatsArePreserved()
        {
            var w=World();var unit=w.AddUnit(FactionId.Undead,UnitKind.Warrior,new Cell(2,2));
            Assert.That(unit.Hp,Is.EqualTo(90));Assert.That(unit.Damage,Is.EqualTo(12));
            Assert.That(w.Definitions.Building(BuildingKind.TownHall).Cost.Wood,Is.EqualTo(60));
            Assert.That(w.Definitions.Building(BuildingKind.ArcheryRange).Cost.Gold,Is.EqualTo(10));
            Assert.That(w.Definitions.Unit(UnitKind.Archer).TrainingSeconds,Is.EqualTo(12));w.AssertInvariants();
        }
        [Test] public void MatchStartsWithExactlyOneHallAndWorkerPerFactionAtConfiguredCap()
        {
            var w=World();w.StartMatch(FactionId.Undead,8,new Cell(2,2),new Cell(2,3),new Cell(25,20),new Cell(25,19));
            Assert.That(w.Units.Count,Is.EqualTo(2));Assert.That(w.Buildings.Count,Is.EqualTo(2));
            Assert.That(w.Human.Population.Capacity,Is.EqualTo(8));Assert.That(w.Undead.Population.Used,Is.EqualTo(1));
            Assert.That(w.PlayerFaction,Is.EqualTo(FactionId.Undead));w.AssertInvariants();
        }
        [Test] public void EventWindowKeepsMonotonicSequenceAndConservationAfterPruning()
        {
            var w=World();var unit=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(3,3));
            var total=w.TotalAccountedResources();long before=w.TotalEvents;
            for(int i=0;i<RtsWorld.MaxRetainedEvents+25;i++)w.SetOrder(unit.Id,Order.Hold(unit.Cell));
            Assert.That(w.Events.Count,Is.EqualTo(RtsWorld.MaxRetainedEvents));
            Assert.That(w.TotalEvents,Is.EqualTo(before+RtsWorld.MaxRetainedEvents+25));
            Assert.That(w.Events.First().Sequence,Is.EqualTo(w.TotalEvents-RtsWorld.MaxRetainedEvents+1));
            Assert.That(w.Events.Last().Sequence,Is.EqualTo(w.TotalEvents));
            Assert.That(w.Events.Select(e=>e.Sequence).Distinct().Count(),Is.EqualTo(w.Events.Count));
            w.AssertInvariants(total);
            w.StartMatch(FactionId.Human,20,new Cell(2,2),new Cell(2,3),new Cell(25,20),new Cell(25,19));
            Assert.That(w.TotalEvents,Is.Zero);Assert.That(w.Events,Is.Empty);
            w.SetOrder("human-worker",Order.Hold(new Cell(2,3)));
            Assert.That(w.Events.First().Sequence,Is.EqualTo(1));
        }
        [Test] public void ConcurrentGatherConservesBothResourcesAndCannotOverdrawNode()
        {
            foreach(ResourceKind kind in Enum.GetValues(typeof(ResourceKind)))
            {
                var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(2,3),"a");var b=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(4,3),"b");
                var node=w.AddResource(kind,new Cell(3,3),7,"node");var total=w.TotalAccountedResources();
                var results=w.ExecuteBatch(new[]{new ActionChoice(a.Id,"gather_node",0),new ActionChoice(b.Id,"gather_node",0)});
                Assert.That(results.All(r=>r.Ok),Is.True);Assert.That(node.Remaining,Is.Zero);
                Assert.That(kind==ResourceKind.Wood?w.Human.Wood:w.Human.Gold,Is.EqualTo(7));w.AssertInvariants(total);
            }
        }
        [Test] public void CooperatingWorkersPayConstructionOnceAndCompleteOneWorkEach()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(2,3),"a");var b=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(4,3),"b");
            w.Human.Wood=15;w.SetOrder(a.Id,Order.Build(BuildingKind.House,new Cell(3,3)));w.SetOrder(b.Id,Order.Build(BuildingKind.House,new Cell(3,3)));
            var total=w.TotalAccountedResources();w.ExecuteBatch(new[]{new ActionChoice(a.Id,"build_order",a.OrderRevision),new ActionChoice(b.Id,"build_order",b.OrderRevision)});
            Assert.That(w.Buildings.Count,Is.EqualTo(1));Assert.That(w.Buildings[0].Complete,Is.True);Assert.That(w.Human.Wood,Is.Zero);w.AssertInvariants(total);
        }
        [Test] public void ParallelBuildsCannotSpendTheSameGoldTwice()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(2,2),"a");var b=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(8,2),"b");
            w.Human.Wood=40;w.Human.Gold=15;w.SetOrder(a.Id,Order.Build(BuildingKind.Tower,new Cell(3,2)));w.SetOrder(b.Id,Order.Build(BuildingKind.Tower,new Cell(9,2)));
            var total=w.TotalAccountedResources();var results=w.ExecuteBatch(new[]{new ActionChoice(a.Id,"build_order",a.OrderRevision),new ActionChoice(b.Id,"build_order",b.OrderRevision)});
            Assert.That(results.Count(r=>r.Ok),Is.EqualTo(1));Assert.That(w.Buildings.Count,Is.EqualTo(1));Assert.That(w.Human.Gold,Is.Zero);w.AssertInvariants(total);
        }
        [Test] public void WaitAndClockDoNotRepeatGatherConstructionOrMovement()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(3,3));w.AddResource(ResourceKind.Wood,new Cell(3,4),50,"tree");
            w.SetOrder(a.Id,Order.Gather("tree",new Cell(3,4)));w.Execute(a.Id,"wait",a.OrderRevision);w.Tick(30);
            Assert.That(w.Human.Wood,Is.Zero);Assert.That(a.Cell,Is.EqualTo(new Cell(3,3)));Assert.That(w.Buildings,Is.Empty);
        }
        [Test] public void SegmentsAreNeutralExplicitVocabularyAndApprovalNeverTeleports()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(8,8));
            var actions=w.LegalActions(a.Id);Assert.That(actions.Count(x=>x.Kind==ActionKind.Move),Is.EqualTo(48));
            var chosen=actions.Single(x=>x.Id=="move_east_2_north_2");var result=w.Execute(a.Id,chosen.Id,0);
            Assert.That(result.ApprovedCells,Is.EqualTo(new[]{new Cell(9,8),new Cell(10,8),new Cell(10,7),new Cell(10,6)}));
            Assert.That(a.Cell,Is.EqualTo(new Cell(8,8)));Assert.That(w.TryExecuteStep(a.Id,result.ApprovedCells[0],0).Ok,Is.True);
            w.Blocked.Add(result.ApprovedCells[1]);Assert.That(w.TryExecuteStep(a.Id,result.ApprovedCells[1],0).Ok,Is.False);
            Assert.That(a.Cell,Is.EqualTo(new Cell(9,8)));
        }
        [Test] public void NewOrdersRejectStaleResponsesAndRemainingSegmentSteps()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(8,8));var result=w.Execute(a.Id,"move_east_2",0);
            w.SetOrder(a.Id,Order.Hold(a.Cell));Assert.That(w.Execute(a.Id,"move_east",0).Reason,Is.EqualTo("stale_order"));
            Assert.That(w.TryExecuteStep(a.Id,result.ApprovedCells[0],0).Reason,Is.EqualTo("stale_order"));Assert.That(a.Cell,Is.EqualTo(new Cell(8,8)));
        }
        [Test] public void MovementCollisionsRotatePriorityWithoutInventingDetours()
        {
            foreach(int round in new[]{0,1})
            {
                var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(2,3),"a");var b=w.AddUnit(FactionId.Undead,UnitKind.Warrior,new Cell(4,3),"b");w.TimeSeconds=round*2;
                var results=w.TryExecuteSteps(new[]{new StepChoice(a.Id,new Cell(3,3),0),new StepChoice(b.Id,new Cell(3,3),0)});
                Assert.That(results.Count(r=>r.Ok),Is.EqualTo(1));Assert.That(results[round].Ok,Is.True);Assert.That(results[1-round].Reason,Is.EqualTo("movement_conflict"));w.AssertInvariants();
            }
        }
        [Test] public void SimultaneousLethalAttacksBothLandAndDeadActorsCannotAct()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(2,2),"a");var b=w.AddUnit(FactionId.Undead,UnitKind.Warrior,new Cell(3,2),"b");a.Hp=b.Hp=12;
            var r=w.ExecuteBatch(new[]{new ActionChoice("a","attack_b",0),new ActionChoice("b","attack_a",0)});
            Assert.That(r.All(x=>x.Ok),Is.True);Assert.That(a.Hp,Is.Zero);Assert.That(b.Hp,Is.Zero);Assert.That(w.Execute("a","move_north",0).Ok,Is.False);w.AssertInvariants();
        }
        [Test] public void ReorderingSameGroupKeepsOneSharedTargetAndStableMembershipWithoutAllocatingSlots()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(3,3),"a");var b=w.AddUnit(FactionId.Human,UnitKind.Archer,new Cell(5,3),"b");
            Assert.That(w.IssueGroupMove(new[]{"a","b"},new Cell(10,10),FactionId.Human).Ok,Is.True);
            string group=a.Order.GroupId;
            a.Cell=new Cell(3,2);var beforeA=a.Cell;var beforeB=b.Cell;
            Assert.That(w.IssueGroupMove(new[]{"b","a"},new Cell(15,10),FactionId.Human).Ok,Is.True);
            Assert.That(a.Order.Cell,Is.EqualTo(new Cell(15,10)));Assert.That(b.Order.Cell,Is.EqualTo(a.Order.Cell));
            Assert.That(a.Order.GroupId,Is.EqualTo(group));Assert.That(b.Order.GroupId,Is.EqualTo(group));
            CollectionAssert.AreEqual(new[]{"a","b"},a.Order.GroupMembers);CollectionAssert.AreEqual(a.Order.GroupMembers,b.Order.GroupMembers);
            Assert.That(a.Order.Formation,Is.Empty);Assert.That(b.Order.Formation,Is.Empty);
            Assert.That(a.Cell,Is.EqualTo(beforeA));Assert.That(b.Cell,Is.EqualTo(beforeB));
            var copy=a.Order.Copy();copy.GroupMembers[0]="copy-only";
            var observation=w.Observe(a.Id);observation.Self.Order.GroupMembers[0]="observation-only";
            Assert.That(a.Order.GroupMembers[0],Is.EqualTo("a"));Assert.That(b.Order.GroupMembers[0],Is.EqualTo("a"));
            a.Order.GroupMembers[0]="first-unit-only";Assert.That(b.Order.GroupMembers[0],Is.EqualTo("a"));
        }
        [TestCase(1,0)] [TestCase(2,2)] [TestCase(20,3)] [TestCase(60,5)]
        public void SharedGroupArrivalRadiusAndObservationDescribeNoPersonalDestinations(int count,int radius)
        {
            var w=World(60);for(int i=0;i<count;i++)w.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(1+i%10,1+i/10),"u"+i);
            var ids=w.Units.Select(u=>u.Id).ToArray();var target=new Cell(25,18);var origins=w.Units.Select(u=>u.Cell).ToArray();
            Assert.That(w.IssueGroupMove(ids,target,FactionId.Human).Ok,Is.True);
            Assert.That(w.Units.All(u=>u.Order.Cell==target&&u.Order.GroupArrivalRadius==radius&&u.Order.Formation.Count==0),Is.True);
            CollectionAssert.AreEqual(origins,w.Units.Select(u=>u.Cell));
            var order=w.Observe(ids[0]).ToJObject()["self"]["order"];
            Assert.That((int)order["x"],Is.EqualTo(target.X));Assert.That((int)order["y"],Is.EqualTo(target.Y));
            Assert.That((int)order["sharedGroup"]["arrivalChebyshevRadius"],Is.EqualTo(radius));
            CollectionAssert.AreEqual(ids,order["sharedGroup"]["members"].Select(id=>(string)id));
            Assert.That(order["formation"],Is.Null);Assert.That(order["sharedGroup"]["slots"],Is.Null);
        }
        [Test] public void GroupDestinationDoesNotRevealHiddenOccupantsButExecutionStillBlocksCollision()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(3,3),"a");var b=w.AddUnit(FactionId.Human,UnitKind.Archer,new Cell(5,3),"b");
            var enemy=w.AddUnit(FactionId.Undead,UnitKind.Worker,new Cell(20,18),"hidden");
            Assert.That(w.Observe(a.Id).VisibleUnits.Any(u=>u.Id==enemy.Id),Is.False);
            Assert.That(w.Observe(b.Id).VisibleUnits.Any(u=>u.Id==enemy.Id),Is.False);
            Assert.That(w.IssueGroupMove(new[]{a.Id,b.Id},new Cell(20,18),FactionId.Human).Ok,Is.True);
            Assert.That(a.Order.Cell,Is.EqualTo(enemy.Cell));Assert.That(b.Order.Cell,Is.EqualTo(enemy.Cell));
            Assert.That(w.Observe(a.Id).ToJObject()["self"]["order"].ToString().Contains("hidden"),Is.False);
            a.Cell=new Cell(19,18);var before=a.Cell;
            Assert.That(w.TryExecuteStep(a.Id,enemy.Cell,a.OrderRevision).Ok,Is.False);
            Assert.That(a.Cell,Is.EqualTo(before));Assert.That(enemy.Cell,Is.EqualTo(new Cell(20,18)));w.AssertInvariants();
        }
        [Test] public void VisionBlocksWallsCornersAndNeverSharesAllyMemory()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Archer,new Cell(2,2),"a");var enemy=w.AddUnit(FactionId.Undead,UnitKind.Worker,new Cell(5,2),"enemy");
            var ally=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(20,20),"ally");w.AddResource(ResourceKind.Gold,new Cell(20,19),50,"hidden");
            w.Blocked.Add(new Cell(3,2));w.Observe(ally.Id);var o=w.Observe(a.Id);
            Assert.That(o.VisibleUnits.Any(e=>e.Id==enemy.Id),Is.False);Assert.That(o.Memory.Resources.Any(r=>r.Id=="hidden"),Is.False);
            Assert.That(w.HasLineOfSight(a.Cell,new Cell(3,3)),Is.False);Assert.That(o.ToJObject()["units"],Is.Null);
            Assert.That(o.ToJObject().ToString().Contains("hidden"),Is.False);
        }
        [Test] public void MemoryPreservesLastSeenFactsAndClearsVisiblyEmptyCell()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(2,2),"a");var b=w.AddUnit(FactionId.Undead,UnitKind.Warrior,new Cell(4,2),"b");
            w.Observe(a.Id);a.Cell=new Cell(20,20);b.Cell=new Cell(5,2);b.Hp=1;w.TimeSeconds=5;var stale=w.Observe(a.Id);
            Assert.That(stale.Memory.Enemies.Single().Hp,Is.EqualTo(90));Assert.That(stale.Memory.Enemies.Single().Cell,Is.EqualTo(new Cell(4,2)));
            a.Cell=new Cell(2,2);b.Cell=new Cell(20,19);Assert.That(w.Observe(a.Id).Memory.Enemies,Is.Empty);
        }
        [Test] public void ObservationCopiesCannotChangeWorldAndReadOnlyObservationDoesNotAppendHistory()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(2,2));w.SetOrder(a.Id,Order.Hold(a.Cell));
            var o=w.Observe(a.Id,false);o.Faction.Wood=99;o.Rules.Buildings[0].Cost.Wood=99;o.Self.Order.Cell=new Cell(9,9);
            Assert.That(w.Human.Wood,Is.Zero);Assert.That(w.Definitions.Buildings[0].Cost.Wood,Is.EqualTo(60));Assert.That(a.Order.Cell,Is.EqualTo(a.Cell));Assert.That(a.Memory.RecentPositions,Is.Empty);
        }
        [Test] public void NavigationHistoryRetainsTravelInsteadOfRepeatedStationarySensorReads()
        {
            var w=World();var a=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(2,2));
            for(int i=0;i<20;i++)w.Observe(a.Id);
            Assert.That(a.Memory.RecentPositions.Count,Is.EqualTo(1));
            for(int i=0;i<40;i++){a.Cell=new Cell(2+i%6,3+i/6);w.Observe(a.Id);}
            Assert.That(a.Memory.RecentPositions.Count,Is.EqualTo(32));
            Assert.That(a.Memory.RecentPositions.Last(),Is.EqualTo(a.Cell));
            var before=a.Memory.RecentPositions.ToArray();w.Observe(a.Id,false);
            CollectionAssert.AreEqual(before,a.Memory.RecentPositions);
        }
        [TestCase(FactionId.Human)]
        [TestCase(FactionId.Undead)]
        public void EachBuildingTrainsOnlyItsAssignedUnitAndInvalidRecruitmentCostsNothing(FactionId faction)
        {
            foreach(BuildingKind buildingKind in Enum.GetValues(typeof(BuildingKind)))
                foreach(UnitKind unitKind in Enum.GetValues(typeof(UnitKind)))
                {
                    var w=World();var bank=w.Faction(faction);bank.Wood=bank.Gold=100;
                    var building=w.AddBuilding(faction,buildingKind,new Cell(8,8));
                    var total=w.TotalAccountedResources();long events=w.TotalEvents;
                    bool allowed=buildingKind==BuildingKind.TownHall&&unitKind==UnitKind.Worker
                        ||buildingKind==BuildingKind.Barracks&&unitKind==UnitKind.Warrior
                        ||buildingKind==BuildingKind.ArcheryRange&&unitKind==UnitKind.Archer;
                    var result=w.EnqueueTraining(building.Id,unitKind,faction);
                    Assert.That(result.Ok,Is.EqualTo(allowed),$"{faction}: {buildingKind} -> {unitKind}");
                    if(allowed)
                    {
                        Assert.That(building.TrainingQueue.Single().Kind,Is.EqualTo(unitKind));
                        Assert.That(bank.Population.Reserved,Is.EqualTo(1));
                    }
                    else
                    {
                        Assert.That(result.Reason,Is.EqualTo("unit_not_trainable_here"));
                        Assert.That(building.TrainingQueue,Is.Empty);
                        Assert.That(bank.Wood,Is.EqualTo(100));Assert.That(bank.Gold,Is.EqualTo(100));
                        Assert.That(bank.Population.Reserved,Is.Zero);
                        Assert.That(w.TrainingSpent.Wood,Is.Zero);Assert.That(w.TrainingSpent.Gold,Is.Zero);
                        Assert.That(w.TotalEvents,Is.EqualTo(events));
                    }
                    w.AssertInvariants(total);
                }
        }
        [Test] public void QueuedProductionReservesConfigurableCapAndChargesAtomically()
        {
            var w=World(3);w.Human.Gold=100;var hall=w.AddBuilding(FactionId.Human,BuildingKind.TownHall,new Cell(8,8));var total=w.TotalAccountedResources();
            for(int i=0;i<3;i++) Assert.That(w.EnqueueTraining(hall.Id,UnitKind.Worker,FactionId.Human).Ok,Is.True);
            Assert.That(w.EnqueueTraining(hall.Id,UnitKind.Worker,FactionId.Human).Reason,Is.EqualTo("faction_unit_limit"));Assert.That(w.Human.Gold,Is.EqualTo(70));
            w.Tick(36);Assert.That(w.Human.Population.Used,Is.EqualTo(3));Assert.That(w.Human.Population.Reserved,Is.Zero);Assert.That(w.Units.All(u=>u.Order==null),Is.True);w.AssertInvariants(total);
        }
        [Test] public void BlockedExitKeepsFinishedTrainingAtZeroWithoutAdditionalPayment()
        {
            var w=World();w.Human.Gold=10;var hall=w.AddBuilding(FactionId.Human,BuildingKind.TownHall,new Cell(8,8));
            var cells=new[]{new Cell(8,7),new Cell(9,8),new Cell(8,9),new Cell(7,8)};w.Blocked.UnionWith(cells);
            w.EnqueueTraining(hall.Id,UnitKind.Worker,FactionId.Human);w.Tick(50);Assert.That(w.Units,Is.Empty);Assert.That(hall.TrainingQueue.Single().RemainingSeconds,Is.Zero);
            w.Blocked.Remove(cells[0]);w.Tick(0);Assert.That(w.Units.Single().Cell,Is.EqualTo(cells[0]));Assert.That(w.TrainingSpent.Gold,Is.EqualTo(10));
        }
        [Test] public void HousingLossKeepsExistingUnitsAndProductionChronologyMatchesSmallTicks()
        {
            Func<RtsWorld> fixture=()=> { var w=World();w.Human.BasePopulationCapacity=1;w.Human.Gold=100;var house=w.AddBuilding(FactionId.Human,BuildingKind.House,new Cell(2,2));
                var z=w.AddBuilding(FactionId.Human,BuildingKind.Barracks,new Cell(8,8),true,"z");var a=w.AddBuilding(FactionId.Human,BuildingKind.Barracks,new Cell(18,8),true,"a");
                w.EnqueueTraining(z.Id,UnitKind.Warrior,FactionId.Human);w.EnqueueTraining(a.Id,UnitKind.Warrior,FactionId.Human);house.Hp=0;w.RefreshPopulation();return w; };
            var big=fixture();var small=fixture();big.Tick(24);small.Tick(12);small.Tick(12);
            Assert.That(big.Units.Count,Is.EqualTo(1));Assert.That(big.Events.Last(e=>e.Type=="training_completed").ActorId,Is.EqualTo("a"));
            Assert.That(big.Units.Single().Cell,Is.EqualTo(small.Units.Single().Cell));Assert.That(big.Building("z").TrainingQueue.Single().RemainingSeconds,Is.Zero);big.AssertInvariants();
        }
        [Test] public void DestroyedProductionCancelsQueueWithoutRefundAndPreservesConservation()
        {
            var w=World();w.Human.Gold=20;var hall=w.AddBuilding(FactionId.Human,BuildingKind.TownHall,new Cell(8,8));var total=w.TotalAccountedResources();
            w.EnqueueTraining(hall.Id,UnitKind.Worker,FactionId.Human);hall.Hp=0;w.Tick(1);
            Assert.That(hall.TrainingQueue,Is.Empty);Assert.That(w.Human.Gold,Is.EqualTo(10));Assert.That(w.Human.Population.Reserved,Is.Zero);w.AssertInvariants(total);
        }
        [Test] public void AuthoredFootprintsBlockMovementAndAllowEdgeConstructionAndSpawn()
        {
            var w=World();var definition=w.Definitions.Building(BuildingKind.House);definition.FootprintSizeX=3;definition.FootprintSizeY=3;
            var worker=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(8,6));w.Human.Wood=15;w.SetOrder(worker.Id,Order.Build(BuildingKind.House,new Cell(8,8)));
            Assert.That(w.Execute(worker.Id,"build_order",worker.OrderRevision).Ok,Is.True);var building=w.Buildings.Single();
            Assert.That(building.Footprint.Count,Is.EqualTo(9));Assert.That(w.IsFree(new Cell(7,7)),Is.False);
            Assert.That(w.SpawnCell(building),Is.EqualTo(new Cell(7,6)));w.AssertInvariants();
        }
        [Test] public void TowerRequiresExplicitJevActionAndCannotAttackThroughWall()
        {
            var w=World();var tower=w.AddBuilding(FactionId.Human,BuildingKind.Tower,new Cell(8,8));var enemy=w.AddUnit(FactionId.Undead,UnitKind.Worker,new Cell(10,8),"enemy");
            w.Tick(100);Assert.That(enemy.Hp,Is.EqualTo(36));Assert.That(w.ExecuteTowerAction(tower.Id,"attack_enemy").Ok,Is.True);Assert.That(enemy.Hp,Is.EqualTo(26));
            w.Blocked.Add(new Cell(9,8));Assert.That(w.TowerActions(tower.Id).Any(a=>a.TargetId==enemy.Id),Is.False);
        }
        [Test] public void DestroyingFinalTownHallEndsMatchAndPreventsFurtherCommands()
        {
            var w=World();w.StartMatch(FactionId.Human,20,new Cell(2,2),new Cell(2,3),new Cell(20,20),new Cell(20,19));
            var attacker=w.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(19,20));w.Building("undead-townhall").Hp=12;
            Assert.That(w.Execute(attacker.Id,"attack_undead-townhall",0).Ok,Is.True);Assert.That(w.MatchEnded,Is.True);Assert.That(w.Winner,Is.EqualTo(FactionId.Human));
            Assert.That(w.Execute(attacker.Id,"wait",0).Reason,Is.EqualTo("match_ended"));w.AssertInvariants();
        }
        [Test] public void SimultaneousFinalTownHallDestructionProducesDraw()
        {
            var w=World();w.StartMatch(FactionId.Human,20,new Cell(2,2),new Cell(2,3),new Cell(20,20),new Cell(20,19));
            var human=w.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(19,20),"human-attacker");
            var undead=w.AddUnit(FactionId.Undead,UnitKind.Warrior,new Cell(3,2),"undead-attacker");
            w.Building("human-townhall").Hp=12;w.Building("undead-townhall").Hp=12;
            var results=w.ExecuteBatch(new[]{new ActionChoice(human.Id,"attack_undead-townhall",0),new ActionChoice(undead.Id,"attack_human-townhall",0)});
            Assert.That(results.All(r=>r.Ok),Is.True);Assert.That(w.MatchEnded,Is.True);Assert.That(w.Draw,Is.True);Assert.That(w.Winner,Is.Null);w.AssertInvariants();
        }
        [Test] public void LargeAuthoredFoundationDoesNotRequireEveryCornerInsideWorkerVision()
        {
            var w=World();var definition=w.Definitions.Building(BuildingKind.TownHall);definition.FootprintSizeX=definition.FootprintSizeY=9;
            var worker=w.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(10,5));w.Human.Wood=60;w.Human.Gold=30;
            w.SetOrder(worker.Id,Order.Build(BuildingKind.TownHall,new Cell(10,10)));
            Assert.That(w.VisibleCellsForUnit(worker.Id).Contains(new Cell(14,14)),Is.False);
            var total=w.TotalAccountedResources();Assert.That(w.Execute(worker.Id,"build_order",worker.OrderRevision).Ok,Is.True);
            var building=w.Buildings.Single();Assert.That(building.Footprint.Count,Is.EqualTo(81));
            for(int n=1;n<definition.RequiredWork;n++)Assert.That(w.Execute(worker.Id,"build_"+building.Id,worker.OrderRevision).Ok,Is.True);
            Assert.That(building.Complete,Is.True);Assert.That(w.Human.Wood,Is.Zero);Assert.That(w.Human.Gold,Is.Zero);w.AssertInvariants(total);
        }
    }
}
