using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Jev.Tests
{
    public sealed class JevNavigationIntentTests
    {
        [Test]
        public void BuildingCornersUseOnlyPersonalFactsAndKeepEveryPhysicalActionUntouched()
        {
            var state=State();var before=state.DeepClone();var intent=new JevNavigationIntent();
            var plan=intent.BuildPlan(state);
            Assert.That(plan.Criteria.ContainsKey("direct_order"),Is.True);
            Assert.That(plan.Criteria.ContainsKey("corner_6_16"),Is.True,"A far corner may be selected from a full observed footprint without assuming a route to it.");
            Assert.That(plan.Criteria.ContainsKey("corner_14_8"),Is.False,"This hall corner lies in the remembered barracks footprint.");
            Assert.That(plan.Criteria.ContainsKey("corner_21_12"),Is.True);
            Assert.That(plan.Criteria.ContainsKey("corner_2_2"),Is.False,"Global or unrelated data must not become personal map knowledge.");
            Assert.That(JToken.DeepEquals(state,before),Is.True);
            Assert.That(JToken.DeepEquals(plan.State["legalActions"],state["legalActions"]),Is.True);
            Assert.That(JToken.DeepEquals(plan.State["visible"],state["visible"]),Is.True);
            Assert.That(JToken.DeepEquals(plan.State["memory"],state["memory"]),Is.True);
            Assert.That(intent.NeedsPlanning(state),Is.True);
        }

        [Test]
        public void CandidateValidationRemovesOutOfBoundsKnownWallsOccupantsAndDuplicatesOnly()
        {
            var state=State();
            state["visible"]["buildings"]=new JArray(Building("a",4,4,4,4),Building("b",4,6,4,6),Building("edge",0,0,0,0));
            state["memory"]["buildings"]=new JArray();
            state["visible"]["tiles"]=new JArray(new JObject{["x"]=3,["y"]=3,["blocked"]=true});
            state["visible"]["units"]=new JArray(new JObject{["id"]="visible-other",["x"]=5,["y"]=3,["hp"]=40});
            var plan=new JevNavigationIntent().BuildPlan(state);
            Assert.That(plan.Criteria.ContainsKey("corner_3_3"),Is.False);
            Assert.That(plan.Criteria.ContainsKey("corner_5_3"),Is.False);
            Assert.That(plan.Criteria.Keys.Any(k=>k.Contains("_-1")),Is.False);
            Assert.That(plan.Criteria.ContainsKey("corner_3_5"),Is.True);
            var shared=((JArray)plan.State["navigationPlanning"]["candidates"]).Single(c=>(int)c["x"]==3&&(int)c["y"]==5);
            CollectionAssert.AreEquivalent(new[]{"a","b"},((JArray)shared["sourceBuildingIds"]).Values<string>());
        }

        [Test]
        public void ArrivalAndChangedOrderInvalidateIntentWithoutInventingAnAction()
        {
            var state=State();var intent=new JevNavigationIntent();
            Assert.That(intent.ApplyChoice(intent.BuildPlan(state),"corner_6_16",state,123,out var reason),Is.True,reason);
            var facts=intent.ObservationFacts(state);
            Assert.That((string)facts["mode"],Is.EqualTo("waypoint"));
            Assert.That((long)facts["requestId"],Is.EqualTo(123));
            Assert.That((int)facts["waypoint"]["x"],Is.EqualTo(6));
            facts["waypoint"]["x"]=999;
            Assert.That((int)intent.ObservationFacts(state)["waypoint"]["x"],Is.EqualTo(6));
            state["self"]["x"]=6;state["self"]["y"]=16;
            Assert.That(intent.ObservationFacts(state),Is.Null);
            state=State();Assert.That(intent.ApplyChoice(intent.BuildPlan(state),"corner_6_16",state,124,out reason),Is.True);
            state["self"]["orderRevision"]=2;
            Assert.That(intent.ObservationFacts(state),Is.Null);
            Assert.That(JToken.DeepEquals(state["legalActions"],State()["legalActions"]),Is.True);
        }

        [Test]
        public void ReviewAndRejectionRequestNewJevPlanningWithoutSelectingAnotherPoint()
        {
            var state=State();var intent=new JevNavigationIntent();
            Assert.That(intent.ApplyChoice(intent.BuildPlan(state),"corner_6_16",state,1,out _),Is.True);
            for(int i=0;i<7;i++)intent.RecordPhysicalDecision();
            Assert.That(intent.NeedsPlanning(state),Is.False);
            intent.RecordPhysicalDecision();
            Assert.That(intent.NeedsPlanning(state),Is.True);
            Assert.That((string)intent.ObservationFacts(state)["selectedChoice"],Is.EqualTo("corner_6_16"));
            var review=intent.BuildPlan(state);
            Assert.That((long)review.State["navigationIntent"]["requestId"],Is.EqualTo(1));
            Assert.That(intent.ApplyChoice(review,"direct_order",state,2,out _),Is.True);
            Assert.That((string)intent.ObservationFacts(state)["mode"],Is.EqualTo("direct_order"));
            Assert.That(intent.NeedsPlanning(state),Is.False);
            intent.RecordPhysicalDecision(true);
            Assert.That(intent.NeedsPlanning(state),Is.True);
            Assert.That((string)intent.ObservationFacts(state)["selectedChoice"],Is.EqualTo("direct_order"));
        }

        [Test]
        public void PlanRequiresTheExactSubmittedChoiceAndUnchangedRequestOrigin()
        {
            var state=State();var intent=new JevNavigationIntent();var plan=intent.BuildPlan(state);
            plan.Criteria["forged"]="This criterion was added after the snapshot.";
            Assert.That(intent.ApplyChoice(plan,"forged",state,1,out var reason),Is.False);
            Assert.That(reason,Is.EqualTo("invalid_navigation_choice"));
            var moved=(JObject)state.DeepClone();moved["self"]["x"]=11;
            Assert.That(intent.ApplyChoice(plan,"corner_6_16",moved,2,out reason),Is.False);
            Assert.That(reason,Is.EqualTo("stale_navigation_plan"));
            var changed=(JObject)state.DeepClone();changed["self"]["order"]["y"]=21;
            Assert.That(intent.ApplyChoice(plan,"corner_6_16",changed,3,out _),Is.False);
            Assert.That(intent.ObservationFacts(state),Is.Null);
        }

        [Test]
        public void NoOrderBuildArrivedAndWorkInReachDoNotCausePointlessPlanning()
        {
            var intent=new JevNavigationIntent();var state=State();state["self"]["order"]=JValue.CreateNull();
            Assert.That(intent.NeedsPlanning(state),Is.False);
            state=State();state["self"]["order"]["kind"]="build";
            Assert.That(intent.NeedsPlanning(state),Is.False);
            state=State();state["self"]["order"]["x"]=10;state["self"]["order"]["y"]=8;
            Assert.That(intent.NeedsPlanning(state),Is.False);
            foreach(string kind in new[]{"gather","attack"})
            {
                state=State();state["self"]["order"]["kind"]=kind;state["self"]["order"]["targetId"]="target";
                ((JArray)state["legalActions"]).Add(new JObject{["id"]=kind+"_target",["kind"]=kind,["targetId"]="target"});
                Assert.That(intent.NeedsPlanning(state),Is.False);
            }
            state=State();state["visible"]["buildings"]=new JArray();state["memory"]["buildings"]=new JArray();
            Assert.That(intent.NeedsPlanning(state),Is.False,"direct_order alone requires no planning request; physical choices still belong to JEV.");
        }

        [Test]
        public void CompactTileRunsApplyTheSameKnownBlockAndOccupancyChecks()
        {
            var raw=State();
            raw["publicTerrain"]=new JObject{["tiles"]=new JArray(new JObject{["x"]=6,["y"]=16,["blocked"]=true})};
            var compact=(JObject)raw.DeepClone();compact["publicTerrain"]["tiles"]=new JArray{new JArray(16,6,6,1,JValue.CreateNull())};
            var intent=new JevNavigationIntent();
            CollectionAssert.AreEqual(intent.BuildPlan(raw).Criteria.Keys,intent.BuildPlan(compact).Criteria.Keys);
            Assert.That(intent.BuildPlan(compact).Criteria.ContainsKey("corner_6_16"),Is.False);
        }

        [Test]
        public void EveryCandidateDescribesBothBuildingsAndTheirExactSharedOccupiedEdges()
        {
            var state=State();var before=state.DeepClone();var plan=new JevNavigationIntent().BuildPlan(state);
            var candidates=(JArray)plan.State["navigationPlanning"]["candidates"];
            foreach(var candidate in candidates)
                CollectionAssert.AreEqual(new[]{"hall","barracks"},((JArray)candidate["buildingRelations"]).Select(r=>(string)r["buildingId"]));
            var west=candidates.Single(c=>(int)c["x"]==6&&(int)c["y"]==16);
            var east=candidates.Single(c=>(int)c["x"]==14&&(int)c["y"]==16);
            Assert.That((string)west["buildingRelations"][0]["selfSides"],Is.EqualTo("north"));
            Assert.That((string)west["buildingRelations"][0]["pointSides"],Is.EqualTo("west+south"));
            Assert.That((string)west["buildingRelations"][1]["selfSides"],Is.EqualTo("west"));
            Assert.That((string)west["buildingRelations"][1]["pointSides"],Is.EqualTo("west+south"));
            Assert.That((string)east["buildingRelations"][0]["pointSides"],Is.EqualTo("east+south"));
            Assert.That((string)east["buildingRelations"][1]["pointSides"],Is.EqualTo("south"));
            var contact=((JArray)plan.State["navigationPlanning"]["buildingContacts"]).Single();
            Assert.That((string)contact["axis"],Is.EqualTo("x"));
            Assert.That((string)contact["firstBuildingId"],Is.EqualTo("hall"));
            Assert.That((string)contact["secondBuildingId"],Is.EqualTo("barracks"));
            Assert.That((int)contact["firstEdgeCell"],Is.EqualTo(13));
            Assert.That((int)contact["secondEdgeCell"],Is.EqualTo(14));
            Assert.That((int)contact["overlapMin"],Is.EqualTo(9));
            Assert.That((int)contact["overlapMax"],Is.EqualTo(11));
            StringAssert.Contains("barracks x[14..20] y[5..11]: self west -> point south",plan.Criteria["corner_14_16"]);
            StringAssert.Contains("no free grid cell between these edges",plan.Criteria["corner_6_16"]);
            Assert.That(JToken.DeepEquals(state,before),Is.True);
            Assert.That(JToken.DeepEquals(plan.State["legalActions"],state["legalActions"]),Is.True);
        }

        [Test]
        public void EdgeContactFactsDoNotInventContactAcrossAFreeRowOrDiagonalCorner()
        {
            var state=State();state["memory"]["buildings"]=new JArray();
            state["visible"]["buildings"]=new JArray(Building("south",5,5,8,7),Building("north",4,2,6,4),Building("gap",10,2,11,4),Building("diagonal",9,8,9,8));
            var contacts=(JArray)new JevNavigationIntent().BuildPlan(state).State["navigationPlanning"]["buildingContacts"];
            Assert.That(contacts.Count,Is.EqualTo(1));
            Assert.That((string)contacts[0]["axis"],Is.EqualTo("y"));
            Assert.That((string)contacts[0]["firstBuildingId"],Is.EqualTo("north"));
            Assert.That((string)contacts[0]["secondBuildingId"],Is.EqualTo("south"));
            Assert.That((int)contacts[0]["firstEdgeCell"],Is.EqualTo(4));
            Assert.That((int)contacts[0]["secondEdgeCell"],Is.EqualTo(5));
            Assert.That((int)contacts[0]["overlapMin"],Is.EqualTo(5));
            Assert.That((int)contacts[0]["overlapMax"],Is.EqualTo(6));
        }

        [Test]
        public void UnitPromptAcceptsJsonNullIntentAndKeepsOriginalOrder()
        {
            var state=State();state["navigationIntent"]=JValue.CreateNull();var before=state.DeepClone();
            var request=JevPromptBuilder.BuildUnit(state,Options(state));
            StringAssert.Contains("Goal L1 14→18",(string)request["questions"]["action"]["criteria"]["move_west_4"]);
            Assert.That(JToken.DeepEquals(state,before),Is.True);
            Assert.That(JToken.DeepEquals(request["state"]["self"]["order"],state["self"]["order"]),Is.True);
        }

        [Test]
        public void UnitPromptSeparatesIntermediateProgressFromUnchangedFinalOrder()
        {
            var state=State();var intent=new JevNavigationIntent();
            Assert.That(intent.ApplyChoice(intent.BuildPlan(state),"corner_6_16",state,123,out _),Is.True);
            state["navigationIntent"]=intent.ObservationFacts(state);var before=state.DeepClone();
            var request=JevPromptBuilder.BuildUnit(state,Options(state));
            string move=(string)request["questions"]["action"]["criteria"]["move_west_4"];
            StringAssert.Contains("JEV-selected intermediate (6,16) L1 12→8",move);
            StringAssert.Contains("Final order L1 14→18",move);
            Assert.That(JToken.DeepEquals(request["state"]["self"]["order"],state["self"]["order"]),Is.True);
            Assert.That(JToken.DeepEquals(state,before),Is.True);
            CollectionAssert.AreEqual(Options(state).Keys,((JObject)request["questions"]["action"]["criteria"]).Properties().Select(p=>p.Name));
        }

        [Test]
        public void DirectOrderDescribesTheActualGoalAsFullyAsCornersAfterAnObstacleBypass()
        {
            var state=State();state["self"]["x"]=14;state["self"]["y"]=16;var before=state.DeepClone();
            var plan=new JevNavigationIntent().BuildPlan(state);string direct=plan.Criteria["direct_order"];
            StringAssert.Contains("from current (14,16) to ordered goal (10,22)",direct);
            StringAssert.Contains("Reach the original move destination (10,22)",direct);
            StringAssert.Contains("hall x[7..13] y[9..15]: self east+south -> ordered goal south",direct);
            StringAssert.Contains("barracks x[14..20] y[5..11]: self south -> ordered goal west+south",direct);
            StringAssert.Contains("Self to ordered goal Manhattan distance=10",direct);
            StringAssert.Contains("ordered goal to original ordered coordinate Manhattan distance=0",direct);
            StringAssert.Contains("no free grid cell between these edges",direct);
            Assert.That(plan.Criteria.ContainsKey("corner_6_16"),Is.True,"The competing corner remains a genuine available choice.");
            Assert.That(plan.Criteria.Keys.First(),Is.EqualTo("direct_order"));
            Assert.That(JToken.DeepEquals(state,before),Is.True);
            Assert.That(JToken.DeepEquals(plan.State["legalActions"],state["legalActions"]),Is.True);
        }

        [Test]
        public void DirectResourceAndCombatIntentUsesInteractionReachRatherThanOccupiedDestination()
        {
            var state=State();state["self"]["order"]["kind"]="gather";state["self"]["order"]["targetId"]="gold-2";
            string gather=new JevNavigationIntent().BuildPlan(state).Criteria["direct_order"];
            StringAssert.Contains("resource gold-2 at (10,22) to legal gathering reach and gather",gather);
            StringAssert.Contains("occupied cell is not a movement destination",gather);
            state["self"]["order"]["kind"]="attack";state["self"]["order"]["targetId"]="enemy-3";
            StringAssert.Contains("target enemy-3 at its ordered coordinate (10,22) to legal attack reach and attack",new JevNavigationIntent().BuildPlan(state).Criteria["direct_order"]);
            state["self"]["order"]["kind"]="hold";
            StringAssert.Contains("Reach the ordered hold coordinate (10,22) and defend there",new JevNavigationIntent().BuildPlan(state).Criteria["direct_order"]);
        }

        [Test]
        public void EnteringSharedArrivalAreaEndsTransitPlanningButLeavesPhysicalYieldingChoices()
        {
            var state=State();state["self"]["order"]["sharedGroup"]=new JObject{["groupId"]="g",["members"]=new JArray("u","friend"),["arrivalChebyshevRadius"]=3};
            var intent=new JevNavigationIntent();Assert.That(intent.ApplyChoice(intent.BuildPlan(state),"corner_6_16",state,1,out _),Is.True);
            state["self"]["x"]=13;state["self"]["y"]=19;var actions=state["legalActions"].DeepClone();
            Assert.That(intent.NeedsPlanning(state),Is.False);Assert.That(intent.ObservationFacts(state),Is.Null);
            Assert.That(JToken.DeepEquals(actions,state["legalActions"]),Is.True);
            state["self"]["x"]=14;Assert.That(intent.NeedsPlanning(state),Is.True);
            StringAssert.Contains("shared arrival area within Chebyshev radius 3",intent.BuildPlan(state).Criteria["direct_order"]);
        }

        static System.Collections.Generic.Dictionary<string,string> Options(JObject state)=>
            ((JArray)state["legalActions"]).ToDictionary(a=>(string)a["id"],a=>"Execute the exact offered "+(string)a["kind"]+" action.");

        static JObject State()=>new JObject
        {
            ["self"]=new JObject{["id"]="u",["x"]=10,["y"]=8,["orderRevision"]=1,["order"]=new JObject{["kind"]="move",["x"]=10,["y"]=22}},
            ["map"]=new JObject{["width"]=30,["height"]=30,["knownTerrainAscii"]="unchanged personal map"},
            ["visible"]=new JObject{["buildings"]=new JArray(Building("hall",7,9,13,15)),["tiles"]=new JArray(),["units"]=new JArray()},
            ["memory"]=new JObject{["buildings"]=new JArray(Building("barracks",14,5,20,11)),["terrain"]=new JArray()},
            ["globalBuildings"]=new JArray(Building("must-not-be-read",3,3,3,3)),
            ["legalActions"]=new JArray(new JObject{["id"]="wait",["kind"]="wait"},new JObject{["id"]="move_west_4",["kind"]="move",["x"]=6,["y"]=8,["cells"]=new JArray(new JArray(9,8),new JArray(8,8),new JArray(7,8),new JArray(6,8))})
        };

        static JObject Building(string id,int minX,int minY,int maxX,int maxY)
        {
            var footprint=new JArray();for(int y=minY;y<=maxY;y++)for(int x=minX;x<=maxX;x++)footprint.Add(new JObject{["x"]=x,["y"]=y});
            return new JObject{["id"]=id,["hp"]=100,["footprint"]=footprint};
        }
    }
}
