using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Jev.Tests
{
    public sealed class JevObservationEncodingTests
    {
        static JObject Observation() => JObject.Parse(@"{
          'self': {'id':'worker-7','type':'worker','factionId':'human','x':4,'y':5,'hp':31,'maxHp':40,
            'orderRevision':8,'order':{'kind':'build','buildingType':'barracks','x':8,'y':5},
            'lastAction':{'kind':'gather','actionId':'gather_wood-1','ok':true,'timeSeconds':7.25}},
          'faction':{'id':'human','wood':5,'gold':7,'population':{'used':2,'reserved':1,'capacity':10}},
          'rules':{'woodPerGather':5,'goldPerGather':5,'unitLimit':20,
            'buildingDefinitions':{'barracks':{'cost':{'wood':20,'gold':10},'requiredWork':3,'footprintSizeX':3,'footprintSizeY':2}}},
          'map':{'width':96,'height':80,'knownTerrainAscii':'0:?.#\n1:.@o\n'},
          'visible':{
            'tiles':[{'x':4,'y':5,'blocked':false,'occupiedBy':'worker-7'},
                     {'x':3,'y':5,'blocked':true},{'x':4,'y':6,'blocked':false,'occupiedBy':'enemy-2'}],
            'units':[{'id':'enemy-2','type':'warrior','x':4,'y':6,'factionId':'undead','hp':89,'maxHp':125,'damage':12,'attackRange':1}],
            'resources':[{'id':'wood-1','kind':'wood','x':5,'y':5,'remaining':25}],
            'buildings':[{'id':'hall','type':'townhall','factionId':'human','x':1,'y':1,'hp':300,'complete':true,
                          'footprint':[{'x':1,'y':1},{'x':2,'y':1}]}]},
          'memory':{
            'terrain':[{'x':4,'y':5,'blocked':false},{'x':90,'y':77,'blocked':true},{'x':9,'y':8,'blocked':false}],
            'enemies':[{'id':'enemy-old','type':'archer','x':90,'y':77,'hp':40,'factionId':'undead','lastSeenTime':2.1}],
            'resources':[{'id':'gold-old','kind':'gold','x':89,'y':70,'remaining':12,'lastSeenTime':1.25}],
            'buildings':[{'id':'enemy-hall','type':'townhall','x':91,'y':75,'factionId':'undead','complete':true,'lastSeenTime':3.5}],
            'recentPositions':[[3,5],[4,5],[4,5]]},
          'legalActions':[
            {'id':'wait','kind':'wait','x':4,'y':5,'description':'Remain in place without work.'},
            {'id':'move_around','kind':'move','x':6,'y':4,'cells':[{'x':4,'y':4},{'x':5,'y':4},{'x':6,'y':4}],
             'description':'Move exactly through (4,4), (5,4), (6,4); stop on obstruction.'},
            {'id':'gather_wood-1','kind':'gather','targetId':'wood-1','x':5,'y':5,'description':'Gather 5 wood once.'},
            {'id':'attack_enemy-2','kind':'attack','targetId':'enemy-2','x':4,'y':6,'description':'Attack enemy-2 for 4 damage once.'}],
          'navigationAtlas':{'bridges':[{'id':'west','x':12,'y':37}],'source':'authored-public-terrain'}
        }");

        static Dictionary<string, string> Options(JObject observation) => ((JArray)observation["legalActions"])
            .ToDictionary(a => (string)a["id"], a => (string)a["description"]);

        // This decoder deliberately does not use production encoding helpers. It
        // recovers the observed facts according to the published wire legend.
        static JObject DecodeSemanticState(JObject encoded)
        {
            var decoded = (JObject)encoded.DeepClone();
            foreach (var fields in new[] { new[] { "visible", "tiles" }, new[] { "memory", "terrain" } })
            {
                var group = (JObject)decoded[fields[0]];
                Assert.That((string)group["tileColumns"], Is.EqualTo("y,xStart,xEnd,blocked(0=false/1=true),occupiedBy(null=none),optionalExtraFields"));
                Assert.That((string)group["tileEncoding"], Does.StartWith("horizontal-runs-v1:"));
                var tiles=new JArray();
                foreach(var row in (JArray)group[fields[1]])
                {
                    Assert.That(((JArray)row).Count, Is.InRange(5,6));
                    Assert.That((int)row[3], Is.InRange(0, 1));
                    Assert.That((int)row[2],Is.GreaterThanOrEqualTo((int)row[1]));
                    for(int x=(int)row[1];x<=(int)row[2];x++)
                    {
                        var tile=row.Count()>5?(JObject)row[5].DeepClone():new JObject();
                        tile["x"]=x;tile["y"]=row[0].DeepClone();tile["blocked"]=(int)row[3]==1;
                        if(row[4].Type!=JTokenType.Null)tile["occupiedBy"]=row[4].DeepClone();
                        tiles.Add(tile);
                    }
                }
                group[fields[1]]=tiles;
                group.Remove("tileColumns");
                group.Remove("tileEncoding");
            }
            foreach(var action in ((JArray)decoded["legalActions"]).OfType<JObject>())
                if(action["cells"] is JArray cells)
                    action["cells"]=new JArray(cells.Select(c=>c is JArray pair?(JToken)new JObject{["x"]=pair[0].DeepClone(),["y"]=pair[1].DeepClone()}:c.DeepClone()));
            decoded.Remove("orderFacts");
            decoded.Remove("currentObjective");
            decoded.Remove("navigationGeometry");
            decoded.Remove("actionEncoding");
            return decoded;
        }

        [Test]
        public void PartiallySeenLargeBuildingKeepsItsWholeKnownExtentSeparateFromStaticTerrain()
        {
            var source=Observation();source["self"]["x"]=10;source["self"]["y"]=8;
            source["self"]["order"]=new JObject{["kind"]="move",["x"]=10,["y"]=47};
            var cells=new JArray();for(int y=9;y<=15;y++)for(int x=7;x<=13;x++)cells.Add(new JObject{["x"]=x,["y"]=y});
            var hall=new JObject{["id"]="hall",["footprint"]=cells};
            source["visible"]["buildings"]=new JArray(hall);source["memory"]["buildings"]=new JArray(hall.DeepClone());
            var before=source.DeepClone();var request=JevPromptBuilder.BuildUnit(source,Options(source));
            var rectangles=(JArray)request["state"]["navigationGeometry"]["knownBuildingRectangles"];
            Assert.That(rectangles.Count,Is.EqualTo(1));
            Assert.That((int)rectangles[0]["bounds"]["minX"],Is.EqualTo(7));
            Assert.That((int)rectangles[0]["bounds"]["maxY"],Is.EqualTo(15));
            Assert.That((string)rectangles[0]["currentOutsideSides"][0],Is.EqualTo("north"));
            Assert.That((string)rectangles[0]["orderedCellOutsideSides"][0],Is.EqualTo("south"));
            Assert.That(JToken.DeepEquals(source,before),Is.True);
            CollectionAssert.AreEqual(Options(source).Keys,((JObject)request["questions"]["action"]["criteria"]).Properties().Select(p=>p.Name));
        }

        [Test]
        public void CompactWireRoundtripPreservesEveryTerrainRowActionAndEntityFact()
        {
            var source = Observation();
            var immutableSnapshot = source.DeepClone();
            var request = JevPromptBuilder.BuildUnit(source, Options(source));
            Assert.That(JToken.DeepEquals(source, immutableSnapshot), Is.True, "Encoding must not mutate sensors or memory.");

            var expected = (JObject)source.DeepClone();
            foreach (var action in ((JArray)expected["legalActions"]).OfType<JObject>()) action.Remove("description");
            Assert.That(JToken.DeepEquals(DecodeSemanticState((JObject)request["state"]), expected), Is.True,
                "Only duplicate descriptions may leave state; all terrain, stale timestamps, entities, order revisions, atlas and explicit step cells must survive.");

            var criteria = (JObject)request["questions"]["action"]["criteria"];
            CollectionAssert.AreEqual(((JArray)source["legalActions"]).Select(a => (string)a["id"]).ToArray(), criteria.Properties().Select(p => p.Name).ToArray());
            foreach (var action in ((JArray)source["legalActions"]).Where(a => (string)a["kind"] != "wait"))
                Assert.That((string)criteria[(string)action["id"]], Does.StartWith((string)action["description"]));

            source["visible"]["units"][0]["hp"] = 0;
            source["memory"]["terrain"][0]["blocked"] = true;
            Assert.That((int)request["state"]["visible"]["units"][0]["hp"], Is.EqualTo(89));
            Assert.That((int)request["state"]["memory"]["terrain"][0][3], Is.Zero);
        }

        [Test]
        public void FullPersonalMapIsPreservedIncludingDistantRowsAndCurrentOccupancy()
        {
            var observation = Observation();
            var all = new JArray();
            for (int y = 0; y < 80; y++)
                for (int x = 0; x < 96; x++)
                    all.Add(new JObject { ["x"] = x, ["y"] = y, ["blocked"] = (x * 3 + y) % 11 == 0 });
            observation["memory"]["terrain"] = all;
            var compact = JevObservationEncoding.Compact(observation);
            var decoded = DecodeSemanticState(compact);
            Assert.That(((JArray)decoded["memory"]["terrain"]).Count, Is.EqualTo(7680));
            Assert.That(((JArray)compact["memory"]["terrain"]).Count, Is.LessThan(3840));
            Assert.That(JToken.DeepEquals(decoded["memory"]["terrain"], observation["memory"]["terrain"]), Is.True);
            Assert.That(JToken.DeepEquals(decoded["visible"]["tiles"], observation["visible"]["tiles"]), Is.True);
            Assert.That(JToken.DeepEquals(decoded["map"], observation["map"]), Is.True);
        }

        [Test]
        public void ConstructionFactsReportBothDeficitsAndAlreadyPaidWorkWithoutChangingStocks()
        {
            var source = Observation();
            var facts = JevObservationEncoding.Compact(source)["orderFacts"];
            Assert.That((int)facts["woodMissing"], Is.EqualTo(15));
            Assert.That((int)facts["goldMissing"], Is.EqualTo(3));
            Assert.That((bool)facts["materialsAlreadyPaid"], Is.False);

            ((JArray)source["visible"]["buildings"]).Add(JObject.Parse(@"{'id':'work-in-progress','type':'barracks','factionId':'human','x':8,'y':5,'progress':1,'requiredWork':3,'complete':false}"));
            var compact = JevObservationEncoding.Compact(source);
            facts = compact["orderFacts"];
            Assert.That((bool)facts["materialsAlreadyPaid"], Is.True);
            Assert.That((int)facts["woodMissing"], Is.Zero);
            Assert.That((int)facts["goldMissing"], Is.Zero);
            Assert.That((string)facts["existingBuilding"]["id"], Is.EqualTo("work-in-progress"));
            Assert.That(JToken.DeepEquals(compact["faction"], source["faction"]), Is.True);
            Assert.That((int)facts["remainingWork"], Is.EqualTo(2));
            Assert.That((int)facts["currentBuildPerimeterL1"], Is.EqualTo(2));
            Assert.That(facts["goalManhattanDistance"], Is.Null, "Build approach must not treat the site center as the destination.");
        }

        [TestCase(JevBrainRole.Commander)]
        [TestCase(JevBrainRole.Tower)]
        public void NonUnitRolesPreserveTheirWholeIndependentObservation(JevBrainRole role)
        {
            var source = Observation();
            var request = JevPromptBuilder.Build(role, source, Options(source));
            Assert.That(JToken.DeepEquals(request["state"], source), Is.True);
            Assert.That(request["questions"]["fear"], Is.Null);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FreshUnitWithJsonNullOrMissingOrderStillProducesAValidFullChoiceRequest(bool explicitJsonNull)
        {
            var observation = Observation();
            var self = (JObject)observation["self"];
            if (explicitJsonNull) self["order"] = JValue.CreateNull();
            else self.Remove("order");
            self["lastAction"] = JValue.CreateNull();
            var options = Options(observation);

            var request = JevPromptBuilder.BuildUnit(observation, options);
            Assert.That(JevResponseValidator.ValidateRequest(request), Is.Null);
            Assert.That(request["state"]["orderFacts"], Is.Null, "An unassigned unit has no invented destination.");
            Assert.That(JToken.DeepEquals(request["state"]["self"], self), Is.True);
            var criteria = (JObject)request["questions"]["action"]["criteria"];
            CollectionAssert.AreEqual(options.Keys.ToArray(), criteria.Properties().Select(p => p.Name).ToArray());
            Assert.That((string)criteria["move_around"], Does.Not.Contain("Goal L1"));
            Assert.That((string)request["questions"]["action"]["instructions"], Does.StartWith("NO ORDER:"));
        }

        [TestCase(10, 10, 14, 10, true, false, 5, true, false, 1)]
        [TestCase(14, 10, 15, 10, true, false, 1, false, true, 0)]
        [TestCase(15, 10, 14, 10, false, true, 0, true, false, 1)]
        [TestCase(15, 15, 14, 15, false, false, 1, false, true, 0)]
        public void NineByNineBuildSiteUsesOuterCardinalPerimeterInsteadOfCenter(
            int x,int y,int endX,int endY,bool inside,bool reach,int distance,bool endInside,bool endReach,int endDistance)
        {
            var source = Observation();
            source["self"]["x"] = x; source["self"]["y"] = y;
            source["self"]["order"]["x"] = 10; source["self"]["order"]["y"] = 10;
            source["rules"]["buildingDefinitions"]["barracks"]["footprintSizeX"] = 9;
            source["rules"]["buildingDefinitions"]["barracks"]["footprintSizeY"] = 9;
            source["legalActions"] = new JArray(
                new JObject { ["id"]="wait",["kind"]="wait",["x"]=x,["y"]=y,["description"]="Stay still." },
                new JObject { ["id"]="move_exact",["kind"]="move",["x"]=endX,["y"]=endY,["description"]="Execute the exact offered segment." });
            var request = JevPromptBuilder.BuildUnit(source,Options(source));
            var facts = request["state"]["orderFacts"];
            Assert.That(JToken.DeepEquals(facts["plannedFootprintBounds"],JObject.Parse(@"{'minX':6,'minY':6,'maxX':14,'maxY':14,'width':9,'height':9}")),Is.True);
            Assert.That((bool)facts["currentInsideFootprint"],Is.EqualTo(inside));
            Assert.That((bool)facts["currentCardinalBuildReach"],Is.EqualTo(reach));
            Assert.That((int)facts["currentBuildPerimeterL1"],Is.EqualTo(distance));
            string criterion=(string)request["questions"]["action"]["criteria"]["move_exact"];
            Assert.That(criterion,Does.Contain($"Build perimeter L1 {distance}→{endDistance}"));
            Assert.That(criterion,Does.Contain("inside="+endInside.ToString().ToLowerInvariant()));
            Assert.That(criterion,Does.Contain("reach="+endReach.ToString().ToLowerInvariant()));
            Assert.That(criterion,Does.Not.Contain("Goal L1"));
            CollectionAssert.AreEqual(new[]{"wait","move_exact"},((JObject)request["questions"]["action"]["criteria"]).Properties().Select(p=>p.Name).ToArray());
        }

        [Test]
        public void EvenFootprintBoundsAndWrongBuildingTypeDoNotInventPaidConstruction()
        {
            var source = Observation();
            source["self"]["order"]["x"] = 10; source["self"]["order"]["y"] = 10;
            source["rules"]["buildingDefinitions"]["barracks"]["footprintSizeX"] = 4;
            source["rules"]["buildingDefinitions"]["barracks"]["footprintSizeY"] = 2;
            ((JArray)source["visible"]["buildings"]).Add(JObject.Parse(@"{'id':'different-building','type':'house','factionId':'human','x':10,'y':10,'complete':true}"));
            var facts=JevObservationEncoding.Compact(source)["orderFacts"];
            Assert.That(JToken.DeepEquals(facts["plannedFootprintBounds"],JObject.Parse(@"{'minX':8,'minY':9,'maxX':11,'maxY':10,'width':4,'height':2}")),Is.True);
            Assert.That((bool)facts["materialsAlreadyPaid"],Is.False);
            Assert.That((bool)facts["constructionComplete"],Is.False);
            Assert.That((int)facts["remainingWork"],Is.EqualTo(3));
            Assert.That((int)facts["woodMissing"],Is.EqualTo(15));
        }

        [Test]
        public void GatheringApproachesAdjacentRingWithoutRewardingEntryIntoResourceCell()
        {
            var source=Observation();
            source["self"]["x"]=4;source["self"]["y"]=5;
            source["self"]["order"]=JObject.Parse(@"{'kind':'gather','targetId':'wood-1','x':5,'y':5}");
            source["legalActions"]=new JArray(
                JObject.Parse(@"{'id':'wait','kind':'wait','x':4,'y':5,'description':'Remain idle.'}"),
                JObject.Parse(@"{'id':'move_north','kind':'move','x':4,'y':4,'description':'Move exactly to (4,4).'}"),
                JObject.Parse(@"{'id':'gather_wood-1','kind':'gather','targetId':'wood-1','x':5,'y':5,'description':'Gather 5 wood once.'}"));
            var request=JevPromptBuilder.BuildUnit(source,Options(source));
            var facts=request["state"]["orderFacts"];
            Assert.That((int)facts["resourceCenterL1"],Is.EqualTo(1));
            Assert.That((bool)facts["currentCardinalGatherReach"],Is.True);
            Assert.That((int)facts["currentGatherApproachL1"],Is.Zero);
            string criterion=(string)request["questions"]["action"]["criteria"]["move_north"];
            Assert.That(criterion,Does.Contain("Gather approach L1 0→1"));
            Assert.That(criterion,Does.Contain("reach=false"));
            Assert.That((string)request["questions"]["action"]["criteria"]["gather_wood-1"],Is.EqualTo("Gather 5 wood once."));
        }

        [Test]
        public void RunEncodingPreservesOccupantChangesGapsDuplicateCellsOrderAndExtraFields()
        {
            var source=Observation();
            source["visible"]["tiles"]=JArray.Parse(@"[
              {'x':4,'y':1,'blocked':false,'occupiedBy':'a'},
              {'x':5,'y':1,'blocked':false,'occupiedBy':'a'},
              {'x':6,'y':1,'blocked':false,'occupiedBy':'b'},
              {'x':8,'y':1,'blocked':true},
              {'x':7,'y':1,'blocked':false},
              {'x':8,'y':1,'blocked':false,'surface':'mud','revision':7},
              {'x':9,'y':1,'blocked':false,'surface':'mud','revision':7},
              {'x':9,'y':1,'blocked':false},
              {'x':-2,'y':-1,'blocked':false},
              {'x':-1,'y':-1,'blocked':false}]");
            var compact=JevObservationEncoding.Compact(source);
            Assert.That(((JArray)compact["visible"]["tiles"]).Count,Is.EqualTo(7));
            Assert.That(JToken.DeepEquals(DecodeSemanticState(compact)["visible"]["tiles"],source["visible"]["tiles"]),Is.True);
        }

        [Test]
        public void MovementCriteriaRemainSelfContainedAndCustomPhysicalDescriptionsSurvive()
        {
            var source=Observation();
            var move=(JObject)((JArray)source["legalActions"]).First(a=>(string)a["kind"]=="move");
            move["description"]="Move only through these explicit adjacent cells in order: (4,4) → (5,4) → (6,4). Stop if obstructed; no rerouting.";
            var custom=(JObject)move.DeepClone();custom["id"]="custom_move";
            custom["description"]="Execute these cells once; carry the quest item without dropping it.";
            ((JArray)source["legalActions"]).Add(custom);
            var request=JevPromptBuilder.BuildUnit(source,Options(source));
            var criteria=(JObject)request["questions"]["action"]["criteria"];
            Assert.That((string)criteria["move_around"],Does.StartWith("Move from (4,5) via north 1 → east 2 to (6,4); stop if obstructed."));
            Assert.That((string)criteria["custom_move"],Does.StartWith((string)custom["description"]));
            var decoded=DecodeSemanticState((JObject)request["state"]);
            Assert.That(JToken.DeepEquals(decoded["legalActions"][1]["cells"],move["cells"]),Is.True);
            Assert.That(JToken.DeepEquals(decoded["legalActions"][4]["cells"],custom["cells"]),Is.True);
            CollectionAssert.AreEqual(Options(source).Keys.ToArray(),criteria.Properties().Select(p=>p.Name).ToArray());
        }

        [Test]
        public void All160LiteralSegmentsRoundtripFromCardinalRunsWithoutLosingAnyOption()
        {
            var source=Observation();source["self"]["x"]=50;source["self"]["y"]=50;
            source["self"]["order"]=JValue.CreateNull();source["visible"]["units"]=new JArray();
            var actions=new JArray(new JObject{["id"]="wait",["kind"]="wait",["description"]="Wait."});
            var directions=new[]{new[]{0,-1},new[]{1,0},new[]{0,1},new[]{-1,0}};
            foreach(var direction in directions)for(int count=1;count<=8;count++)
                actions.Add(LiteralMove("move_"+actions.Count,50,50,new[]{direction[0],direction[1],count}));
            foreach(var first in directions)foreach(var second in directions.Where(d=>d[0]*first[0]+d[1]*first[1]==0))
                foreach(int a in new[]{1,2,4,8})foreach(int b in new[]{1,2,4,8})
                    actions.Add(LiteralMove("move_"+actions.Count,50,50,new[]{first[0],first[1],a},new[]{second[0],second[1],b}));
            source["legalActions"]=actions;var before=source.DeepClone();
            var request=JevPromptBuilder.BuildUnit(source,Options(source));var criteria=(JObject)request["questions"]["action"]["criteria"];
            Assert.That(criteria.Count,Is.EqualTo(161));
            CollectionAssert.AreEqual(Options(source).Keys,criteria.Properties().Select(p=>p.Name));
            int oldCharacters=0,newCharacters=0;
            foreach(var action in actions.Where(a=>(string)a["kind"]=="move"))
            {
                string criterion=(string)criteria[(string)action["id"]];
                Assert.That(JToken.DeepEquals(DecodeMovementRuns(criterion),action["cells"]),Is.True,(string)action["id"]);
                string verbose=$"Move to ({action["x"]},{action["y"]}) through "+string.Join(" → ",((JArray)action["cells"]).Select(c=>$"({c["x"]},{c["y"]})"))+"; stop if obstructed.";
                string facts=criterion.Substring(criterion.IndexOf("; stop if obstructed.",System.StringComparison.Ordinal)+"; stop if obstructed.".Length);
                oldCharacters+=(verbose+facts).Length;newCharacters+=criterion.Length;
            }
            Assert.That(newCharacters,Is.LessThan(oldCharacters*0.9),"Run descriptions must reduce repeated criterion text, not just move it to another field.");
            Assert.That(JToken.DeepEquals(DecodeSemanticState((JObject)request["state"]),WithoutDescriptions(source)),Is.True);
            Assert.That(JToken.DeepEquals(source,before),Is.True);
        }

        [Test]
        public void LiteralRunsPreserveTurnsReversalsAndDoNotInventStepsForInvalidSequences()
        {
            var source=Observation();var action=LiteralMove("move_turns",4,5,new[]{0,-1,2},new[]{1,0,3},new[]{-1,0,1},new[]{0,1,2},new[]{0,-1,1});
            var invalid=LiteralMove("move_nonadjacent",4,5,new[]{1,0,1});invalid["cells"][0]["x"]=6;invalid["x"]=6;
            invalid["description"]="Move only through these explicit adjacent cells in order: (6,5). Stop if obstructed; no rerouting.";
            source["legalActions"]=new JArray(action,invalid);
            var request=JevPromptBuilder.BuildUnit(source,Options(source));var criteria=request["questions"]["action"]["criteria"];
            StringAssert.Contains("north 2 → east 3 → west 1 → south 2 → north 1",(string)criteria["move_turns"]);
            Assert.That(JToken.DeepEquals(DecodeMovementRuns((string)criteria["move_turns"]),action["cells"]),Is.True);
            Assert.That((string)criteria["move_nonadjacent"],Does.StartWith((string)invalid["description"]));
        }

        [Test]
        public void CardinalRunsKeepEndpointIntermediateFinalDistanceAndPassedGoalWarning()
        {
            var source=Observation();source["self"]["order"]=new JObject{["kind"]="move",["x"]=6,["y"]=5};
            source["navigationIntent"]=new JObject{["mode"]="waypoint",["waypoint"]=new JObject{["x"]=8,["y"]=5}};
            source["legalActions"]=new JArray(LiteralMove("move_east_4",4,5,new[]{1,0,4}));
            var request=JevPromptBuilder.BuildUnit(source,Options(source));string criterion=(string)request["questions"]["action"]["criteria"]["move_east_4"];
            StringAssert.Contains("via east 4 to (8,5)",criterion);
            StringAssert.Contains("intermediate (8,5) L1 4→0",criterion);
            StringAssert.Contains("Final order L1 2→2",criterion);
            StringAssert.Contains("passes the exact ordered destination at step 2, then leaves it for 2 more steps",criterion);
        }

        [Test]
        public void SharedGroupRequestAllowsParkingAndYieldingWithoutAllocatingOrRemovingActions()
        {
            var source=Observation();source["self"]["order"]=new JObject{["kind"]="move",["x"]=6,["y"]=5,
                ["sharedGroup"]=new JObject{["groupId"]="g",["members"]=new JArray("worker-7","other"),["arrivalChebyshevRadius"]=3}};
            source["legalActions"]=new JArray(new JObject{["id"]="wait",["kind"]="wait",["description"]="Wait."},LiteralMove("park",4,5,new[]{1,0,4}),LiteralMove("leave",4,5,new[]{0,-1,4}));
            var before=source.DeepClone();var request=JevPromptBuilder.BuildUnit(source,Options(source));
            StringAssert.StartsWith("GROUP MOVE:",(string)request["questions"]["action"]["instructions"]);
            var facts=request["state"]["orderFacts"];Assert.That((bool)facts["withinSharedArrivalArea"],Is.True);Assert.That((int)facts["currentSharedCenterChebyshev"],Is.EqualTo(2));
            var criteria=(JObject)request["questions"]["action"]["criteria"];
            StringAssert.Contains("Shared arrival Chebyshev 2→2; radius=3; endpointWithin=true",(string)criteria["park"]);
            StringAssert.Contains("endpointWithin=false",(string)criteria["leave"]);
            StringAssert.Contains("staying blocks observed group traffic",(string)criteria["wait"]);
            StringAssert.DoesNotContain("passes the exact ordered destination",(string)criteria["park"]);
            CollectionAssert.AreEqual(Options(source).Keys,criteria.Properties().Select(p=>p.Name));
            Assert.That(JToken.DeepEquals(source,before),Is.True);
        }

        static JObject WithoutDescriptions(JObject source)
        {
            var copy=(JObject)source.DeepClone();foreach(var action in ((JArray)copy["legalActions"]).OfType<JObject>())action.Remove("description");return copy;
        }

        static JObject LiteralMove(string id,int x,int y,params int[][] runs)
        {
            var cells=new JArray();foreach(var run in runs)for(int i=0;i<run[2];i++){x+=run[0];y+=run[1];cells.Add(new JObject{["x"]=x,["y"]=y});}
            return new JObject{["id"]=id,["kind"]="move",["x"]=x,["y"]=y,["cells"]=cells,
                ["description"]="Move only through these explicit adjacent cells in order: "+string.Join(" → ",cells.Select(c=>$"({c["x"]},{c["y"]})"))+". Stop if obstructed; no rerouting."};
        }

        // Independent wire decoder: no production run-encoding helper is used.
        static JArray DecodeMovementRuns(string criterion)
        {
            var match=System.Text.RegularExpressions.Regex.Match(criterion,@"^Move from \((-?\d+),(-?\d+)\) via (.*?) to \((-?\d+),(-?\d+)\);");
            Assert.That(match.Success,Is.True,criterion);int x=int.Parse(match.Groups[1].Value),y=int.Parse(match.Groups[2].Value);var cells=new JArray();
            foreach(string run in match.Groups[3].Value.Split(new[]{" → "},System.StringSplitOptions.None))
            {
                var parts=run.Split(' ');CollectionAssert.Contains(new[]{"north","south","east","west"},parts[0]);int count=int.Parse(parts[1]);Assert.That(count,Is.GreaterThan(0));
                for(int i=0;i<count;i++){x+=parts[0]=="east"?1:parts[0]=="west"?-1:0;y+=parts[0]=="south"?1:parts[0]=="north"?-1:0;cells.Add(new JObject{["x"]=x,["y"]=y});}
            }
            Assert.That(x,Is.EqualTo(int.Parse(match.Groups[4].Value)));Assert.That(y,Is.EqualTo(int.Parse(match.Groups[5].Value)));return cells;
        }

        [Test]
        public void RecentEndpointHistoryDescribesAReversalWithoutRemovingTheOption()
        {
            var source=Observation();
            source["memory"]["recentPositions"]=JArray.Parse("[[6,4],[4,5],[6,4],[4,5]]");
            var request=JevPromptBuilder.BuildUnit(source,Options(source));
            string choice=(string)request["questions"]["action"]["criteria"]["move_around"];
            Assert.That(choice,Does.Contain("Endpoint visited 2 times").And.Contain("Reverses directly"));
            CollectionAssert.AreEqual(Options(source).Keys,((JObject)request["questions"]["action"]["criteria"]).Properties().Select(p=>p.Name));
        }

        [Test]
        public void DistantCommandedObjectiveIsFirstAndRemainsDistinctFromSensorEvidence()
        {
            var source=Observation();
            source["self"]["x"]=64;source["self"]["y"]=46;
            source["self"]["order"]=JObject.Parse(@"{'kind':'gather','targetId':'gold-02','x':64,'y':61}");
            var compact=JevObservationEncoding.Compact(source);
            Assert.That(compact.Properties().First().Name,Is.EqualTo("currentObjective"));
            var objective=compact["currentObjective"];
            Assert.That((string)objective["orderKind"],Is.EqualTo("gather"));
            Assert.That((string)objective["targetId"],Is.EqualTo("gold-02"));
            Assert.That(JToken.DeepEquals(objective["currentCell"],new JArray(64,46)),Is.True);
            Assert.That(JToken.DeepEquals(objective["orderedCell"],new JArray(64,61)),Is.True);
            Assert.That(JToken.DeepEquals(objective["targetOffsetXY"],new JArray(0,15)),Is.True);
            Assert.That((string)objective["targetEvidence"],Does.StartWith("commanded coordinates only"));
            Assert.That(JToken.DeepEquals(compact["self"]["order"],source["self"]["order"]),Is.True);
            Assert.That(JToken.DeepEquals(compact["visible"]["resources"],source["visible"]["resources"]),Is.True);
        }
    }
}
