using System.Collections.Generic;
using System.Reflection;
using Jev.Gameplay.Authoring;
using Jev.Gameplay.Jev;
using Jev.Gameplay.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Runtime.Tests
{
    /// <summary>Scheduling and command submission only. Disabled transport never sends HTTP or fabricates a choice.</summary>
    public sealed class UnitDecisionSchedulingTests
    {
        [Test]
        public void NewThreatInterruptsOnlyUncommittedTravelWithoutStarvingPendingDecisions()
        {
            var host=new GameObject("Threat scheduling fixture");
            try
            {
                var brain=host.AddComponent<UnitBrain>();brain.Pending=true;brain.Ticket=42;
                brain.Moving=true;brain.StepStarted=10;brain.WorkUntil=12;brain.NextDecisionAt=100;
                brain.ApprovedRoute.Enqueue(new Cell(7,8));
                Assert.That(brain.ObserveEnemyContacts(new[]{"enemy"}),Is.True);
                Assert.That(brain.ApprovedRoute,Is.Empty);Assert.That(brain.NextDecisionAt,Is.Zero);
                Assert.That(brain.Pending,Is.True);Assert.That(brain.Ticket,Is.EqualTo(42));
                Assert.That(brain.Moving,Is.True);Assert.That(brain.WorkUntil,Is.EqualTo(12));
                brain.ApprovedRoute.Enqueue(new Cell(8,8));
                Assert.That(brain.ObserveEnemyContacts(new[]{"enemy"}),Is.False);
                Assert.That(brain.ApprovedRoute.Count,Is.EqualTo(1),"A persistent contact must not repeatedly erase JEV's retreat/approach.");
                brain.InterruptForThreat("damage");
                Assert.That(brain.Pending,Is.True);Assert.That(brain.ApprovedRoute,Is.Empty);
                brain.ObserveEnemyContacts(new string[0]);
                Assert.That(brain.ObserveEnemyContacts(new[]{"enemy"}),Is.True);
            }
            finally{Object.DestroyImmediate(host);}
        }

        [Test]
        public void NewOrderInvalidatesOldChoiceButKeepsCommittedStepAndWorkContact()
        {
            var host=new GameObject("Brain scheduling fixture");
            try
            {
                var brain=host.AddComponent<UnitBrain>();
                brain.Ticket=42;brain.Pending=true;brain.NextDecisionAt=100;brain.Moving=true;
                brain.WorkUntil=12;brain.WorkImpactAt=11;brain.StepStarted=3;brain.ApprovedRoute.Enqueue(new Cell(7,8));
                brain.ResetForNewOrder(10);
                Assert.That(brain.Ticket,Is.Zero);Assert.That(brain.Pending,Is.False);
                Assert.That(brain.NextDecisionAt,Is.Zero);Assert.That(brain.ApprovedRoute,Is.Empty);
                Assert.That(brain.WakeReasons,Does.Contain("new_order"));
                Assert.That(brain.Moving,Is.True);Assert.That(brain.StepStarted,Is.EqualTo(3));
                Assert.That(brain.WorkUntil,Is.EqualTo(11.08f).Within(.0001f));
            }
            finally{Object.DestroyImmediate(host);}
        }

        [TestCase(10f,10.9f,12.3f,10.98f)]
        [TestCase(11.2f,10.9f,12.3f,11.2f)]
        [TestCase(10.94f,10.9f,12.3f,10.98f)]
        [TestCase(10.91f,10.9f,10.93f,10.93f)]
        public void ChangedOrderKeepsImpactRecoveryButNeverExtendsOrWaitsForFullFollowThrough(float now,float impact,float until,float expected)
        {
            var host=new GameObject("Work contact scheduling fixture");
            try
            {
                var brain=host.AddComponent<UnitBrain>();brain.WorkUntil=until;brain.WorkImpactAt=impact;
                brain.ResetForNewOrder(now);
                Assert.That(brain.WorkUntil,Is.EqualTo(expected).Within(.0001f));
                Assert.That(brain.WorkUntil,Is.LessThanOrEqualTo(until));
                Assert.That(brain.PresentationBusy(now),Is.EqualTo(expected>now));
                Assert.That(brain.PresentationBusy(expected+.001f),Is.False);
            }
            finally{Object.DestroyImmediate(host);}
        }

        [Test]
        public void PrefetchUsesOnlyCurrentCommittedCellAndBoundedPresentationWindow()
        {
            var host=new GameObject("Brain prefetch fixture");
            try
            {
                var brain=host.AddComponent<UnitBrain>();brain.Moving=true;brain.StepStarted=10;
                brain.ApprovedRoute.Enqueue(new Cell(7,8));
                Assert.That(brain.CanPrefetch(10.4f,.5f,1),Is.False,"Remaining route cells have not been physically committed.");
                brain.ApprovedRoute.Clear();
                Assert.That(brain.CanPrefetch(10.4f,.5f,1),Is.True);
                Assert.That(brain.PresentationBusy(10.4f),Is.True,"Early replies must wait for presentation.");
                brain.Moving=false;brain.WorkUntil=13;
                Assert.That(brain.CanPrefetch(11,.5f,1),Is.False);
                Assert.That(brain.CanPrefetch(12,.5f,1),Is.True);
                Assert.That(brain.PresentationBusy(12.9f),Is.True);
                Assert.That(brain.PresentationBusy(13),Is.False);
            }
            finally{Object.DestroyImmediate(host);}
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SingleAndGroupOrdersSubmitImmediatelyEvenDuringOldWorkCooldown(bool group)
        {
            var host=new GameObject("Offline command scheduling fixture");host.SetActive(false);
            var settings=ScriptableObject.CreateInstance<MatchSettings>();
            var battlefield=ScriptableObject.CreateInstance<BattlefieldDefinition>();
            settings.DecisionPreparationBudgetMilliseconds=1000;
            try
            {
                var controller=host.AddComponent<MatchController>();
                var transport=host.AddComponent<JevTransport>();
                var brain=host.AddComponent<UnitBrain>();
                controller.Settings=settings;controller.Battlefield=battlefield;controller.Transport=transport;
                var world=new RtsWorld{Width=24,Height=24,VictoryEnabled=false};
                var unit=world.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(5,5));
                typeof(MatchController).GetProperty("World").GetSetMethod(true).Invoke(controller,new object[]{world});
                var brains=(Dictionary<string,UnitBrain>)typeof(MatchController).GetField("brains",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(controller);
                brains.Add(unit.Id,brain);
                brain.NextDecisionAt=Time.time+100;brain.WorkUntil=Time.time+5;brain.WorkImpactAt=Time.time+1;brain.Moving=true;
                if(group)Assert.That(controller.IssueGroupMove(new[]{unit.Id},new Cell(7,7)).Ok,Is.True);
                else Assert.That(controller.IssueOrder(unit,Order.Move(new Cell(7,7))),Is.True);
                Assert.That(transport.Counters.Evaluations,Is.EqualTo(1),"No Update or expired cooldown is needed to submit the new order.");
                Assert.That(transport.Counters.Attempts,Is.Zero,"Disabled transport never launches HTTP.");
                Assert.That(brain.LastDecisionAt,Is.EqualTo(Time.time));
                Assert.That(brain.NextDecisionAt,Is.EqualTo(Time.time+settings.DecisionInterval));
                Assert.That(brain.Pending,Is.True);
                Assert.That(unit.Cell,Is.EqualTo(new Cell(5,5)),"An order submits a request; it cannot choose or execute movement.");
                Assert.That(brain.Moving,Is.True);
                if(!group)
                {
                    int revision=unit.OrderRevision;
                    controller.IssueOrder(unit,Order.Move(new Cell(7,7)));
                    Assert.That(transport.Counters.Evaluations,Is.EqualTo(1),"Do not duplicate an already pending request.");
                    brain.Pending=false;brain.NextDecisionAt=Time.time+100;
                    controller.IssueOrder(unit,Order.Move(new Cell(7,7)));
                    Assert.That(transport.Counters.Evaluations,Is.EqualTo(2),"Repeating the same order explicitly wakes a waiting unit.");
                    Assert.That(unit.OrderRevision,Is.EqualTo(revision));
                }
            }
            finally{Object.DestroyImmediate(host);Object.DestroyImmediate(settings);Object.DestroyImmediate(battlefield);}
        }
        [Test]
        public void TwentyUnitCommandSpreadsSnapshotsAcrossFramesAndCoalescesChangedQueuedOrders()
        {
            var host=new GameObject("Twenty brains offline scheduling fixture");host.SetActive(false);
            var settings=ScriptableObject.CreateInstance<MatchSettings>();
            var field=ScriptableObject.CreateInstance<BattlefieldDefinition>();
            settings.DecisionPreparationsPerFrame=2;settings.DecisionPreparationBudgetMilliseconds=1000;
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            try
            {
                var controller=host.AddComponent<MatchController>();var transport=host.AddComponent<JevTransport>();
                controller.Settings=settings;controller.Battlefield=field;controller.Transport=transport;
                var world=new RtsWorld{Width=40,Height=40,VictoryEnabled=false};
                typeof(MatchController).GetProperty("World").GetSetMethod(true).Invoke(controller,new object[]{world});
                var brains=(Dictionary<string,UnitBrain>)typeof(MatchController).GetField("brains",flags).GetValue(controller);
                var ids=new List<string>();
                for(int i=0;i<20;i++)
                {
                    var unit=world.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(5+i%5,5+i/5));ids.Add(unit.Id);
                    var child=new GameObject("brain");child.transform.SetParent(host.transform);
                    brains.Add(unit.Id,child.AddComponent<UnitBrain>());
                }
                Assert.That(controller.IssueGroupMove(ids,new Cell(20,20)).Ok,Is.True);
                Assert.That(transport.Counters.Evaluations,Is.EqualTo(2));
                Assert.That(controller.QueuedUnitReviews,Is.EqualTo(18));
                var changed=world.Unit(ids[19]);controller.IssueOrder(changed,Order.Move(new Cell(25,25)));
                Assert.That(controller.QueuedUnitReviews,Is.EqualTo(18),"Replace a queued review, never duplicate the unit.");
                var died=world.Unit(ids[18]);died.Hp=0;
                for(int frame=0;frame<10;frame++)
                {
                    long before=transport.Counters.Evaluations;
                    typeof(MatchController).GetField("preparationFrame",flags).SetValue(controller,-1);
                    typeof(MatchController).GetMethod("DrainDecisionPreparations",flags).Invoke(controller,null);
                    Assert.That(transport.Counters.Evaluations-before,Is.LessThanOrEqualTo(2));
                }
                Assert.That(controller.QueuedUnitReviews,Is.Zero);
                Assert.That(transport.Counters.Evaluations,Is.EqualTo(19),"Dead queued units never submit.");
                Assert.That(transport.Counters.Attempts,Is.Zero,"No HTTP or fabricated choices in this fixture.");
                var callbacks=(Queue<System.Action>)typeof(MatchController).GetField("completedDecisions",flags).GetValue(controller);
                while(callbacks.Count>0)callbacks.Dequeue()();
                var decisions=(List<Newtonsoft.Json.Linq.JObject>)typeof(MatchController).GetField("decisions",flags).GetValue(controller);
                var latest=decisions.Find(d=>(string)d["actor"]==changed.Id);
                Assert.That((int)latest["observation"]["self"]["order"]["x"],Is.EqualTo(25),"Capture the latest order when its actual review begins.");
                Assert.That(changed.Cell,Is.EqualTo(new Cell(9,8)),"Preparing an observation cannot choose movement.");
            }
            finally{Object.DestroyImmediate(host);Object.DestroyImmediate(settings);Object.DestroyImmediate(field);}
        }
    }
}
