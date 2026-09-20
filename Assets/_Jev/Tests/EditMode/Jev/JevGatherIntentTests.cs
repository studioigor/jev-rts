using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Jev.Tests
{
    public sealed class JevGatherIntentTests
    {
        [Test]
        public void ObservedExhaustionOffersEveryKnownSameKindNodeWithoutChoosingOrChangingTheOrder()
        {
            var state=State();var before=state.DeepClone();var plan=JevGatherIntent.BuildPlan(state);
            CollectionAssert.AreEquivalent(new[]{"keep_assignment","gather_next_tree-near","gather_next_tree-far"},plan.Criteria.Keys);
            Assert.That(JToken.DeepEquals(state,before),Is.True);
            Assert.That(JToken.DeepEquals(plan.State["self"],state["self"]),Is.True);
            Assert.That(JToken.DeepEquals(plan.State["legalActions"],state["legalActions"]),Is.True);
            Assert.That(plan.State["gatherContinuation"]["candidates"].Count(),Is.EqualTo(2));
            Assert.That(JevGatherIntent.TryResolveChoice(plan,"gather_next_tree-far",state,out var target,out var reason),Is.True,reason);
            Assert.That((string)target["id"],Is.EqualTo("tree-far"),"A genuine selected farther target is accepted; no nearest-resource override exists.");
            Assert.That((string)state["self"]["order"]["targetId"],Is.EqualTo("tree-exhausted"),"Resolving evidence itself never issues an order.");
        }

        [Test]
        public void UnseenOriginalTargetMustStillBeApproachedInsteadOfAssumedExhausted()
        {
            var state=State();state["visible"]["tiles"]=new JArray();
            Assert.That(JevGatherIntent.ObservedExhaustion(state),Is.False);
            Assert.That(JevGatherIntent.BuildPlan(state),Is.Null);
        }

        [Test]
        public void LivingOriginalTargetPreventsRetargetEvenWhenOtherTreesAreCloser()
        {
            var state=State();((JArray)state["visible"]["resources"]).Add(Resource("tree-exhausted","wood",4,3));
            Assert.That(JevGatherIntent.BuildPlan(state),Is.Null);
        }

        [Test]
        public void CurrentAbsenceOverridesStaleResourceMemoryAndGlobalDataCannotBecomeKnowledge()
        {
            var state=State();
            ((JArray)state["memory"]["resources"]).Add(Resource("old-tree","wood",5,3));
            ((JArray)state["visible"]["tiles"]).Add(new JObject{["x"]=5,["y"]=3});
            state["globalResources"]=new JArray(Resource("secret-tree","wood",30,30));
            var plan=JevGatherIntent.BuildPlan(state);
            Assert.That(plan.Criteria.ContainsKey("gather_next_old-tree"),Is.False);
            Assert.That(plan.Criteria.ContainsKey("gather_next_secret-tree"),Is.False);
            Assert.That(plan.Criteria.ContainsKey("gather_next_gold"),Is.False);
        }

        [Test]
        public void DeferringToPhysicalSurvivalDecisionDoesNotCreateATargetOrAction()
        {
            var state=State();var plan=JevGatherIntent.BuildPlan(state);
            Assert.That(JevGatherIntent.TryResolveChoice(plan,JevGatherIntent.KeepAssignment,state,out var target,out var reason),Is.True,reason);
            Assert.That(target,Is.Null);
        }

        [Test]
        public void ChangedOrderMovementAndForgedChoiceInvalidateSubmittedRetarget()
        {
            var state=State();var plan=JevGatherIntent.BuildPlan(state);
            plan.Criteria["forged"]="An action not part of the actual offered target snapshot.";
            Assert.That(JevGatherIntent.TryResolveChoice(plan,"forged",state,out _,out _),Is.False);
            var changed=(JObject)state.DeepClone();changed["self"]["orderRevision"]=2;
            Assert.That(JevGatherIntent.TryResolveChoice(plan,"gather_next_tree-near",changed,out _,out _),Is.False);
            changed=(JObject)state.DeepClone();changed["self"]["x"]=2;
            Assert.That(JevGatherIntent.TryResolveChoice(plan,"gather_next_tree-near",changed,out _,out _),Is.False);
            changed=(JObject)state.DeepClone();changed["self"]["order"]["targetId"]="new-command";
            Assert.That(JevGatherIntent.TryResolveChoice(plan,"gather_next_tree-near",changed,out _,out _),Is.False);
        }

        [Test]
        public void AnotherWorkerDepletingSelectedVisibleNodeRejectsItBeforeReassignment()
        {
            var state=State();var plan=JevGatherIntent.BuildPlan(state);
            state["visible"]["resources"]=new JArray(Resource("gold","gold",3,4));
            ((JArray)state["visible"]["tiles"]).Add(new JObject{["x"]=6,["y"]=3});
            Assert.That(JevGatherIntent.TryResolveChoice(plan,"gather_next_tree-near",state,out var target,out var reason),Is.False);
            Assert.That(target,Is.Null);Assert.That(reason,Is.EqualTo("gather_target_no_longer_known"));
        }

        [Test]
        public void NoKnownReplacementAndOtherKindsOfOrdersDoNotCreateExtraPlanningRequests()
        {
            var state=State();state["visible"]["resources"]=new JArray();state["memory"]["resources"]=new JArray();
            Assert.That(JevGatherIntent.BuildPlan(state),Is.Null);
            state=State();state["self"]["order"]["kind"]="move";
            Assert.That(JevGatherIntent.BuildPlan(state),Is.Null);
            state=State();((JObject)state["self"]["order"]).Remove("resourceKind");
            Assert.That(JevGatherIntent.BuildPlan(state),Is.Null,"Unknown order resource kind must not be guessed from unrelated nodes.");
        }

        [Test]
        public void DenseWorkAreaOffersAllNodesWithinExplicitRadiusWithoutRankingOrChoiceOverflow()
        {
            var state=State();var nodes=new JArray();
            int radius=JevGatherIntent.MaximumContinuationRadius;
            for(int y=0;y<24;y++)for(int x=0;x<24;x++)
                if(x!=4||y!=3)nodes.Add(Resource("tree_"+x+"_"+y,"wood",x,y));
            state["visible"]["resources"]=nodes;state["memory"]["resources"]=new JArray();
            var plan=JevGatherIntent.BuildPlan(state);
            int expected=nodes.Count(r=>((int)r["x"]-4)*((int)r["x"]-4)+((int)r["y"]-3)*((int)r["y"]-3)<=radius*radius);
            Assert.That(plan.Criteria.Count,Is.EqualTo(expected+1));
            Assert.That(plan.Criteria.Count,Is.LessThanOrEqualTo(JevResponseValidator.MaximumChoices));
            Assert.That(plan.Criteria.ContainsKey("gather_next_tree_23_23"),Is.False);
            Assert.That((int)plan.State["gatherContinuation"]["workArea"]["radiusCells"],Is.EqualTo(radius));
        }

        [Test]
        public void CompactVisibilityAndWireRoundTripPreserveContinuationEvidenceAndCandidates()
        {
            var state=State();var raw=JevGatherIntent.BuildPlan(state);
            state["visible"]["tiles"]=new JArray{new JArray(3,3,4,0,JValue.CreateNull())};
            var plan=JevGatherIntent.BuildPlan(state);
            CollectionAssert.AreEqual(raw.Criteria.Keys,plan.Criteria.Keys);
            var request=new JObject{["state"]=plan.State,["questions"]=new JObject{["action"]=new JObject{["criteria"]=JObject.FromObject(plan.Criteria)}}};
            Assert.That(JToken.DeepEquals(request,JevRequestCompaction.Expand(JevRequestCompaction.Compact(request))),Is.True);
        }

        static JObject Resource(string id,string kind,int x,int y)=>new JObject{["id"]=id,["kind"]=kind,["x"]=x,["y"]=y,["remaining"]=100};
        static JObject State()=>new JObject
        {
            ["self"]=new JObject{["id"]="worker",["type"]="worker",["x"]=3,["y"]=3,["orderRevision"]=1,
                ["order"]=new JObject{["kind"]="gather",["resourceKind"]="wood",["targetId"]="tree-exhausted",["x"]=4,["y"]=3}},
            ["visible"]=new JObject{["tiles"]=new JArray(new JObject{["x"]=4,["y"]=3}),
                ["resources"]=new JArray(Resource("tree-near","wood",6,3),Resource("gold","gold",3,4))},
            ["memory"]=new JObject{["resources"]=new JArray(Resource("tree-far","wood",11,3),Resource("tree-near","wood",6,3))},
            ["legalActions"]=new JArray(new JObject{["id"]="wait",["kind"]="wait",["x"]=3,["y"]=3})
        };
    }
}
