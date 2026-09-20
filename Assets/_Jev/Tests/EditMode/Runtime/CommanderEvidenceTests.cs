using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Jev.Gameplay.Authoring;
using UnityEngine;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Jev.Gameplay.Simulation;

namespace Jev.Gameplay.Runtime.Tests
{
    public sealed class CommanderEvidenceTests
    {
        [Test]
        public void AssignmentCoverageDistinguishesReplacingOnlyProducerFromUsingFinishedBuilder()
        {
            var target=new Cell(3,3);
            var producer=new UnitState{Id="producer",Faction=FactionId.Undead,Kind=UnitKind.Worker,Hp=40,Order=Order.Gather("wood",new Cell(1,1))};
            var builder=new UnitState{Id="builder",Faction=FactionId.Undead,Kind=UnitKind.Worker,Hp=40,Order=Order.Build(BuildingKind.House,target)};
            var idle=new UnitState{Id="idle",Faction=FactionId.Undead,Kind=UnitKind.Worker,Hp=40};
            var ownBuildings=new[]{new BuildingState{Faction=FactionId.Undead,Kind=BuildingKind.House,Cell=target,Hp=100,Complete=true}};
            var resources=new Dictionary<string,JObject>{{"wood",new JObject{["kind"]="Wood"}}};
            var economy=new JObject{["journalCoversInterval"]=true,["workerCredits"]=new JArray(new JObject{["workerId"]="producer",["woodCredited"]=15,["goldCredited"]=0,["gatherActions"]=3})};
            var facts=new[]{producer,builder,idle}.Select(w=>CommanderEvidence.WorkerFacts(w,ownBuildings,resources,economy)).ToArray();
            var replaceProducer=CommanderEvidence.AssignmentCoverage(facts,"producer","Gold");
            var assignBuilder=CommanderEvidence.AssignmentCoverage(facts,"builder","Gold");
            Assert.That((int)replaceProducer["after"]["wood"],Is.Zero);
            Assert.That((int)replaceProducer["after"]["gold"],Is.EqualTo(1));
            Assert.That((int)assignBuilder["after"]["wood"],Is.EqualTo(1));
            Assert.That((int)assignBuilder["after"]["gold"],Is.EqualTo(1));
            Assert.That((string)facts[1]["orderStatus"],Is.EqualTo("completed_build"));
            Assert.That((bool)facts[1]["orderFulfilled"],Is.True);
            Assert.That((int)facts[0]["creditedWood"],Is.EqualTo(15));
            Assert.That((int)facts[1]["creditedWood"],Is.Zero);
            Assert.That(producer.Order.TargetId,Is.EqualTo("wood"),"Hypothetical coverage must not change any actual order.");
            Assert.That(builder.Order.Kind,Is.EqualTo(OrderKind.Build));
            var staff=CommanderEvidence.Staffing(new[]{producer,builder,idle,new UnitState{Kind=UnitKind.Warrior,Hp=100}},facts);
            Assert.That((int)staff["workers"],Is.EqualTo(3));
            Assert.That((int)staff["army"],Is.EqualTo(1));
            Assert.That((int)staff["withoutOrder"],Is.EqualTo(1));
            Assert.That((int)staff["completedBuild"],Is.EqualTo(1));
        }

        [Test]
        public void CompletedOrderRequiresMatchingLiveOwnedBuildingAndUnknownCreditsRemainUnknown()
        {
            var worker=new UnitState{Id="w",Faction=FactionId.Human,Kind=UnitKind.Worker,Hp=40,Order=Order.Build(BuildingKind.Barracks,new Cell(5,5))};
            var buildings=new[]{
                new BuildingState{Faction=FactionId.Undead,Kind=BuildingKind.Barracks,Cell=new Cell(5,5),Hp=100,Complete=true},
                new BuildingState{Faction=FactionId.Human,Kind=BuildingKind.House,Cell=new Cell(5,5),Hp=100,Complete=true},
                new BuildingState{Faction=FactionId.Human,Kind=BuildingKind.Barracks,Cell=new Cell(5,5),Hp=0,Complete=true}};
            var facts=CommanderEvidence.WorkerFacts(worker,buildings,new Dictionary<string,JObject>(),new JObject{["journalCoversInterval"]=false});
            Assert.That((bool)facts["orderFulfilled"],Is.False);
            Assert.That((string)facts["orderStatus"],Is.EqualTo("construction_not_started"));
            Assert.That((bool)facts["creditWindowKnown"],Is.False);
            Assert.That(facts["creditedWood"].Type,Is.EqualTo(JTokenType.Null));
            Assert.That(facts["creditedGold"].Type,Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void UnknownResourceAssignmentsStayExplicitAndSameResourceReplacementPreservesCoverage()
        {
            var worker=new UnitState{Id="w",Kind=UnitKind.Worker,Hp=40,Order=Order.Gather("hidden-gold-sounding-id",new Cell(1,1))};
            var facts=CommanderEvidence.WorkerFacts(worker,new BuildingState[0],new Dictionary<string,JObject>(),new JObject());
            Assert.That((string)facts["assignedResourceKind"],Is.EqualTo("Unknown"),"Do not infer resource facts from its name.");
            var change=CommanderEvidence.AssignmentCoverage(new[]{facts},"w","Gold");
            Assert.That((int)change["before"]["unknown"],Is.EqualTo(1));
            Assert.That((int)change["after"]["unknown"],Is.Zero);
            Assert.That((int)change["after"]["gold"],Is.EqualTo(1));
            var same=CommanderEvidence.AssignmentCoverage(new[]{facts},"w","Unknown");
            Assert.That(JToken.DeepEquals(same["before"],same["after"]),Is.True);
        }

        [Test]
        public void ResourceAssignmentKindSurvivesDepletionAndRemovalFromCommanderMemory()
        {
            var worker=new UnitState{Id="w",Kind=UnitKind.Worker,Hp=40,Order=Order.Gather("depleted",new Cell(1,1),ResourceKind.Wood)};
            var facts=CommanderEvidence.WorkerFacts(worker,new BuildingState[0],new Dictionary<string,JObject>(),new JObject());
            Assert.That((string)facts["assignedResourceKind"],Is.EqualTo("Wood"));
            var coverage=CommanderEvidence.AssignmentCoverage(new[]{facts},"w","Gold");
            Assert.That((int)coverage["before"]["wood"],Is.EqualTo(1));
            Assert.That((int)coverage["before"]["unknown"],Is.Zero);
            Assert.That((int)coverage["after"]["gold"],Is.EqualTo(1));
        }

        [Test]
        public void SelectedBuildTaskUsesCurrentPaymentProgressAndCompletionWithoutMutatingTheTask()
        {
            var selected=new JObject{["id"]="barracks",["kind"]="Barracks",["alreadyPaid"]=false,["progress"]=0,["completedOwnBuilding"]=false};
            var observed=new JObject{["id"]="barracks",["kind"]="Barracks",["alreadyPaid"]=true,["progress"]=3,["requiredWork"]=3,["completedOwnBuilding"]=true};
            var goal=CommanderEvidence.RefreshGoalFacts("build_barracks","build","build Barracks",selected,observed,true,2,true,45);
            Assert.That((string)goal["taskId"],Is.EqualTo("build_barracks"));
            Assert.That((bool)goal["alreadyPaid"],Is.True);
            Assert.That((int)goal["progress"],Is.EqualTo(3));
            Assert.That((bool)goal["fulfilled"],Is.True);
            Assert.That((int)goal["eligibleWorkerCount"],Is.EqualTo(2));
            Assert.That((bool)selected["alreadyPaid"],Is.False);
            observed["progress"]=1;
            Assert.That((int)goal["progress"],Is.EqualTo(3),"The submitted observation is an independent snapshot.");
        }

        [Test]
        public void MissingUnseenResourceIsUnknownButVisibleMissingResourceIsDepleted()
        {
            var selected=new JObject{["targetId"]="gold-02",["resourceKind"]="Gold",["remaining"]=100,["lastSeen"]=10};
            var unseen=CommanderEvidence.RefreshGoalFacts("gather_gold-02","gather","gather gold",selected,null,false,1,null,30);
            Assert.That((bool)unseen["targetVisibleDepleted"],Is.False);
            Assert.That(unseen["remaining"],Is.Null,"Do not revive an old resource amount as current evidence.");
            Assert.That(unseen["fulfilled"].Type,Is.EqualTo(JTokenType.Null));
            var depleted=CommanderEvidence.RefreshGoalFacts("gather_gold-02","gather","gather gold",selected,null,true,0,null,31);
            Assert.That((bool)depleted["targetVisibleDepleted"],Is.True);
            Assert.That((int)depleted["remaining"],Is.Zero);
            Assert.That((int)depleted["eligibleWorkerCount"],Is.Zero);
            Assert.That((string)depleted["taskId"],Is.EqualTo("gather_gold-02"),"Facts must not automatically cancel or replace the JEV-selected task.");
            Assert.That((int)selected["remaining"],Is.EqualTo(100));
        }

        [Test]
        public void SelectedGatherTaskKeepsCurrentMemoryExplicitlyLastSeen()
        {
            var selected=new JObject{["targetId"]="wood-01",["remaining"]=100};
            var remembered=new JObject{["id"]="wood-01",["remaining"]=25,["lastSeen"]=18};
            var goal=CommanderEvidence.RefreshGoalFacts("gather_wood-01","gather","gather wood",selected,remembered,false,3,null,25);
            Assert.That((int)goal["remaining"],Is.EqualTo(25));
            Assert.That((int)goal["lastSeen"],Is.EqualTo(18));
            Assert.That((string)goal["factsSource"],Is.EqualTo("last_seen_resource_memory"));
            Assert.That((bool)goal["targetVisibleDepleted"],Is.False);
        }

        [Test]
        public void EconomySeparatesRealOwnIncomeFromSpendingAndEnemyActivity()
        {
            var events=new List<WorldEvent>
            {
                Event(8,9,FactionId.Undead,"gather",ResourceKind.Gold,50),
                Event(9,11,FactionId.Undead,"gather",ResourceKind.Wood,10),
                Event(10,12,FactionId.Human,"gather",ResourceKind.Gold,999),
                Event(11,15,FactionId.Undead,"gather",ResourceKind.Gold,5),
                Event(12,19,FactionId.Undead,"move",ResourceKind.Wood,7),
                Event(13,29,FactionId.Undead,"gather",ResourceKind.Wood,5)
            };
            var facts=CommanderEvidence.Economy(events,FactionId.Undead,8,10,30,100,40,85,35);
            Assert.That((bool)facts["journalCoversInterval"],Is.True);
            Assert.That((double)facts["measuredSeconds"],Is.EqualTo(20));
            Assert.That((int)facts["resourcesCredited"]["wood"],Is.EqualTo(15));
            Assert.That((int)facts["resourcesCredited"]["gold"],Is.EqualTo(5));
            Assert.That((int)facts["netResourcesSpent"]["wood"],Is.EqualTo(30));
            Assert.That((int)facts["netResourcesSpent"]["gold"],Is.EqualTo(10));
            Assert.That((int)facts["workerCredits"][0]["gatherActions"],Is.EqualTo(3));
            Assert.That(facts.ToString(),Does.Not.Contain("999"));
        }

        [Test]
        public void LostJournalCoverageDoesNotReportMissingIncomeAsZero()
        {
            var events=new[]{Event(100,20,FactionId.Undead,"gather",ResourceKind.Wood,5)};
            var facts=CommanderEvidence.Economy(events,FactionId.Undead,2,1,22,80,30,110,20);
            Assert.That((bool)facts["journalCoversInterval"],Is.False);
            Assert.That(facts["resourcesCredited"].Type,Is.EqualTo(JTokenType.Null));
            Assert.That(facts["netResourcesSpent"].Type,Is.EqualTo(JTokenType.Null));
            Assert.That(facts["workerCredits"].Type,Is.EqualTo(JTokenType.Null));
            Assert.That((int)facts["netStockChange"]["wood"],Is.EqualTo(30));
            Assert.That((double)facts["measuredSeconds"],Is.EqualTo(21));
        }

        [Test]
        public void InitialReviewAndNonGatherCreditsRemainHonest()
        {
            var initial=CommanderEvidence.Economy(new WorldEvent[0],FactionId.Human,0,0,0,80,40,80,40);
            Assert.That((bool)initial["journalCoversInterval"],Is.True);
            Assert.That((double)initial["measuredSeconds"],Is.Zero);
            Assert.That((int)initial["resourcesCredited"]["gold"],Is.Zero);
            var credit=CommanderEvidence.Economy(new WorldEvent[0],FactionId.Human,0,0,20,80,40,85,40);
            Assert.That((int)credit["netResourcesSpent"]["wood"],Is.EqualTo(-5));
        }

        [Test]
        public void PagingPreservesEveryCommandInOriginalOrderWithinProtocolLimit()
        {
            var ids=Enumerable.Range(0,600).Select(i=>"command_"+i).ToArray();
            var pages=CommanderEvidence.PageIds(ids);
            Assert.That(pages.Count,Is.EqualTo(3));
            Assert.That(pages.All(page=>page.Length+2<=255),Is.True);
            CollectionAssert.AreEqual(ids,pages.SelectMany(page=>page).ToArray());
            Assert.That(CommanderEvidence.PageIds(new string[0]),Is.Empty);
            Assert.That(CommanderEvidence.PageIds(ids.Take(253).ToArray()).Count,Is.EqualTo(1));
            Assert.That(CommanderEvidence.PageIds(ids.Take(254).ToArray()).Count,Is.EqualTo(2));
        }

        [Test]
        public void CompletedPendingHouseReopensProductionAndDefenseWithoutIssuingAnOrder()
        {
            using(var fixture=new CommanderReviewFixture())
            {
                fixture.Review();fixture.Choose("build_house");
                fixture.World.SetOrder(fixture.Worker.Id,Order.Build(BuildingKind.House,fixture.Site.Cell));
                var house=fixture.World.AddBuilding(FactionId.Undead,BuildingKind.House,fixture.Site.Cell,false);
                fixture.Review();
                Assert.That(fixture.State["selectedGoal"].Type,Is.EqualTo(JTokenType.Object));
                Assert.That(fixture.Options.Keys,Does.Contain("assign_backup"));
                house.Complete=true;house.Progress=house.RequiredWork;
                int revision=fixture.Worker.OrderRevision;
                fixture.Review();
                Assert.That(fixture.State["selectedGoal"].Type,Is.EqualTo(JTokenType.Null));
                Assert.That((string)fixture.State["lastTaskSelectionResolution"]["reason"],Is.EqualTo("construction_already_complete"));
                Assert.That(fixture.Options.Keys,Does.Contain("train_undead-townhall_Worker"));
                Assert.That(fixture.Options.Keys,Does.Contain("army_attack_intruder"));
                Assert.That(fixture.Options.Keys,Does.Not.Contain("build_house"));
                Assert.That(fixture.Worker.OrderRevision,Is.EqualTo(revision),"Opening strategic choices must not issue a local AI order.");
                Assert.That(fixture.Backup.Order,Is.Null);
                Assert.That(fixture.Army.Order,Is.Null);
            }
        }

        [Test]
        public void DelayedConstructionAssignmentCannotReplaceWorkAfterAnotherWorkerFinishedHouse()
        {
            using(var fixture=new CommanderReviewFixture())
            {
                fixture.World.SetOrder(fixture.Backup.Id,Order.Gather("tree",new Cell(1,3),ResourceKind.Wood));
                int revision=fixture.Backup.OrderRevision;
                fixture.Review();fixture.Choose("build_house");fixture.Review();
                var submittedAssignment=fixture.Options["assign_backup"];
                fixture.World.AddBuilding(FactionId.Undead,BuildingKind.House,fixture.Site.Cell,true);
                Assert.That(submittedAssignment(),Is.False,"A response to an earlier menu must recheck completion before changing any order.");
                Assert.That(fixture.Backup.OrderRevision,Is.EqualTo(revision));
                Assert.That(fixture.Backup.Order.TargetId,Is.EqualTo("tree"));
                fixture.Review();
                Assert.That(fixture.State["selectedGoal"].Type,Is.EqualTo(JTokenType.Null));
                Assert.That((string)fixture.State["lastTaskSelectionResolution"]["reason"],Is.EqualTo("construction_already_complete"));
                Assert.That(fixture.Options.Keys,Does.Contain("army_attack_intruder"));
            }
        }

        [Test]
        public void DelayedGatherAssignmentRejectsObservedDepletionWithoutChangingExistingOrder()
        {
            using(var fixture=new CommanderReviewFixture())
            {
                fixture.World.SetOrder(fixture.Backup.Id,Order.Hold(fixture.Backup.Cell));
                int revision=fixture.Backup.OrderRevision;
                fixture.Review();fixture.Choose("gather_tree");fixture.Review();
                var submittedAssignment=fixture.Options["assign_backup"];
                fixture.World.Resource("tree").Remaining=0;
                Assert.That(submittedAssignment(),Is.False);
                Assert.That(fixture.Backup.OrderRevision,Is.EqualTo(revision));
                Assert.That(fixture.Backup.Order.Kind,Is.EqualTo(OrderKind.Hold));
                fixture.Review();
                Assert.That(fixture.State["selectedGoal"].Type,Is.EqualTo(JTokenType.Null));
                Assert.That((string)fixture.State["lastTaskSelectionResolution"]["reason"],Is.EqualTo("resource_observed_depleted"));
            }
        }

        [Test]
        public void DelayedGatherAssignmentDoesNotInferDepletionOutsideFactionVision()
        {
            using(var fixture=new CommanderReviewFixture())
            {
                fixture.Review();fixture.Choose("gather_tree");fixture.Review();
                var submittedAssignment=fixture.Options["assign_backup"];
                fixture.World.Resource("tree").Remaining=0;
                fixture.Worker.Cell=new Cell(20,20);fixture.Backup.Cell=new Cell(21,20);fixture.Army.Cell=new Cell(22,20);
                Assert.That(submittedAssignment(),Is.True,"Without current sight the commander only knows the earlier resource observation.");
                Assert.That(fixture.Backup.Order.Kind,Is.EqualTo(OrderKind.Gather));
                Assert.That(fixture.Backup.Order.TargetId,Is.EqualTo("tree"));
            }
        }

        [Test]
        public void WaitingInWorkerSelectionReturnsToStrategyAndKeepsExistingOrders()
        {
            using(var fixture=new CommanderReviewFixture())
            {
                fixture.World.SetOrder(fixture.Worker.Id,Order.Gather("tree",new Cell(1,3)));
                int revision=fixture.Worker.OrderRevision;
                fixture.Review();fixture.Choose("build_house");fixture.Review();
                Assert.That(fixture.State["selectedGoal"].Type,Is.EqualTo(JTokenType.Object));
                fixture.Choose("wait");fixture.Review();
                Assert.That(fixture.State["selectedGoal"].Type,Is.EqualTo(JTokenType.Null));
                Assert.That(fixture.Options.Keys,Does.Contain("army_attack_intruder"));
                Assert.That(fixture.Worker.OrderRevision,Is.EqualTo(revision));
                Assert.That(fixture.Worker.Order.TargetId,Is.EqualTo("tree"));
                Assert.That(fixture.Backup.Order,Is.Null);
            }
        }

        [Test]
        public void PendingSelectorClosesOnlyForEstablishedTerminalFacts()
        {
            var ongoing=new JObject{["taskKind"]="build",["completedOwnBuilding"]=false,["eligibleWorkerCount"]=2};
            Assert.That(CommanderEvidence.PendingTaskExpirationReason(ongoing),Is.Null);
            var unseen=new JObject{["taskKind"]="gather",["targetVisibleDepleted"]=false,["eligibleWorkerCount"]=1};
            Assert.That(CommanderEvidence.PendingTaskExpirationReason(unseen),Is.Null,"An unseen resource is not confirmed depleted.");
            unseen["targetVisibleDepleted"]=true;
            Assert.That(CommanderEvidence.PendingTaskExpirationReason(unseen),Is.EqualTo("resource_observed_depleted"));
            ongoing["eligibleWorkerCount"]=0;
            Assert.That(CommanderEvidence.PendingTaskExpirationReason(ongoing),Is.EqualTo("no_unassigned_eligible_workers"));
            Assert.That(CommanderEvidence.PendingTaskExpirationReason(null),Is.Null);
        }

        [Test]
        public void CommanderReviewReportsActualDamageWithoutInventingAnAttackOrder()
        {
            using(var fixture=new CommanderReviewFixture())
            {
                fixture.Review();fixture.Army.Hp-=12;fixture.World.Buildings.Single(b=>b.Id=="undead-townhall").Hp-=9;
                fixture.Review();
                var army=fixture.State["units"].Single(u=>(string)u["id"]==fixture.Army.Id);
                var hall=fixture.State["buildings"].Single(b=>(string)b["id"]=="undead-townhall");
                Assert.That((int)army["progressSincePreviousReview"]["damageTaken"],Is.EqualTo(12));
                Assert.That((int)hall["progressSincePreviousReview"]["damageTaken"],Is.EqualTo(9));
                Assert.That(fixture.Army.Order,Is.Null);
            }
        }

        // Only prepares menus and invokes named lifecycle operations. There is no transport,
        // provider response, selected combat/economic action, or substitute AI in this fixture.
        sealed class CommanderReviewFixture : IDisposable
        {
            readonly GameObject host;
            readonly MatchController match;
            public readonly RtsWorld World;
            public readonly UnitState Worker,Backup,Army;
            public readonly BuildSite Site;
            public JObject State;
            public Dictionary<string,Func<bool>> Options;
            public CommanderReviewFixture()
            {
                host=new GameObject("Commander review fixture");host.SetActive(false);
                match=host.AddComponent<MatchController>();
                match.Battlefield=ScriptableObject.CreateInstance<BattlefieldDefinition>();
                match.Settings=ScriptableObject.CreateInstance<MatchSettings>();
                match.Battlefield.Human=new FactionSpawn{TownHall=new Cell(25,25)};
                match.Battlefield.Undead=new FactionSpawn{TownHall=new Cell(10,10)};
                Site=new BuildSite{Id="house",Faction=FactionId.Undead,Kind=BuildingKind.House,Cell=new Cell(6,3)};
                match.Battlefield.BuildSites.Add(Site);
                World=new RtsWorld{Width=30,Height=30,VictoryEnabled=false};
                typeof(MatchController).GetProperty("World").GetSetMethod(true).Invoke(match,new object[]{World});
                World.Undead.Wood=100;World.Undead.Gold=100;
                World.AddBuilding(FactionId.Undead,BuildingKind.TownHall,new Cell(10,10),true,"undead-townhall");
                Worker=World.AddUnit(FactionId.Undead,UnitKind.Worker,new Cell(3,3),"worker");
                Backup=World.AddUnit(FactionId.Undead,UnitKind.Worker,new Cell(4,3),"backup");
                Army=World.AddUnit(FactionId.Undead,UnitKind.Warrior,new Cell(3,5),"defender");
                World.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(3,6),"intruder");
                World.AddResource(ResourceKind.Wood,new Cell(1,3),100,"tree");
            }
            public void Review()
            {
                var args=new object[]{FactionId.Undead,null};
                var macros=(IEnumerable)typeof(MatchController).GetMethod("BuildCommanderReview",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(match,args);
                State=(JObject)args[1];Options=new Dictionary<string,Func<bool>>();
                foreach(var macro in macros)
                {
                    var type=macro.GetType();
                    Options.Add((string)type.GetField("Id").GetValue(macro),(Func<bool>)type.GetField("Apply").GetValue(macro));
                }
            }
            public void Choose(string id)=>Assert.That(Options[id](),Is.True);
            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(match.Settings);
                UnityEngine.Object.DestroyImmediate(match.Battlefield);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        static WorldEvent Event(long sequence,double at,FactionId faction,string type,ResourceKind kind,int amount) =>
            new WorldEvent{Sequence=sequence,TimeSeconds=at,Faction=faction,Type=type,ResourceKind=kind,Amount=amount,ActorId="worker_"+faction};
    }
}
