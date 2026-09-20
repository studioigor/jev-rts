using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Jev.Tests
{
    public sealed class JevRequestCompactionTests
    {
        static JObject Request(params int[][] runs)
        {
            var cells=new JArray();int x=10,y=10;
            foreach(var run in runs)for(int i=0;i<run[2];i++){x+=run[0];y+=run[1];cells.Add(new JObject{["x"]=x,["y"]=y});}
            var move=new JObject{["id"]="move_literal",["kind"]="move",["x"]=x,["y"]=y,["cells"]=cells,
                ["description"]="Move only through these explicit adjacent cells in order: "+string.Join(" → ",cells.Select(c=>$"({c["x"]},{c["y"]})"))+". Stop if obstructed; no rerouting."};
            var footprint=new JArray();for(int fy=2;fy<=8;fy++)for(int fx=3;fx<=7;fx++)footprint.Add(new JObject{["x"]=fx,["y"]=fy});
            var observation=new JObject
            {
                ["self"]=new JObject{["id"]="worker",["factionId"]="human",["x"]=10,["y"]=10,["orderRevision"]=4,["order"]=new JObject{["kind"]="gather",["targetId"]="wood",["x"]=11,["y"]=10}},
                ["visible"]=new JObject{["tiles"]=new JArray(new JObject{["x"]=10,["y"]=10,["blocked"]=false,["occupiedBy"]="worker"}),["units"]=new JArray(new JObject{["id"]="enemy",["factionId"]="undead",["x"]=5,["y"]=6}),["buildings"]=new JArray(new JObject{["id"]="hall",["footprint"]=footprint})},
                ["memory"]=new JObject{["terrain"]=new JArray(new JObject{["x"]=90,["y"]=90,["blocked"]=true,["lastSeen"]=1.5}),["recentPositions"]=new JArray(new JArray(11,10),new JArray(10,10)),["resources"]=new JArray(new JObject{["id"]="old-mine",["x"]=89,["y"]=75,["remaining"]=2000,["lastSeen"]=.5})},
                ["legalActions"]=new JArray(new JObject{["id"]="wait",["kind"]="wait",["x"]=10,["y"]=10,["description"]="Wait without work."},move,new JObject{["id"]="gather_wood",["kind"]="gather",["targetId"]="wood",["x"]=11,["y"]=10,["description"]="Gather five wood once."})
            };
            return JevPromptBuilder.BuildUnit(observation,((JArray)observation["legalActions"]).ToDictionary(a=>(string)a["id"],a=>(string)a["description"]));
        }

        [Test]
        public void WireRoundtripPreservesExactChoicesCellsEvidenceFearAndAllCustomFields()
        {
            var request=Request(new[]{0,-1,2},new[]{1,0,4},new[]{-1,0,2},new[]{0,1,3});var before=request.DeepClone();
            var compact=JevRequestCompaction.Compact(request);
            Assert.That(JToken.DeepEquals(request,before),Is.True,"Wire encoding must never mutate the canonical request.");
            Assert.That(JToken.DeepEquals(JevRequestCompaction.Expand(compact),request),Is.True);
            Assert.That(JToken.DeepEquals(JevRequestCompaction.Compact(compact),compact),Is.True);
            CollectionAssert.AreEqual(((JObject)request["questions"]["action"]["criteria"]).Properties().Select(p=>p.Name),((JObject)compact["questions"]["action"]["criteria"]).Properties().Select(p=>p.Name));
            Assert.That(JToken.DeepEquals(request["questions"]["fear"],compact["questions"]["fear"]),Is.True,"Independent fear sensing remains a genuine provider question.");
            var row=(JArray)compact["state"]["legalActions"][1];Assert.That((string)row[1],Is.EqualTo("N2 E4 W2 S3"));Assert.That((int)row[2],Is.EqualTo(12));Assert.That((int)row[3],Is.EqualTo(11));
            // Independently expand the published directions, not the production inverse.
            int x=10,y=10;var expanded=new JArray();foreach(var run in ((string)row[1]).Split(' '))for(int i=0;i<int.Parse(run.Substring(1));i++)
            {x+=run[0]=='E'?1:run[0]=='W'?-1:0;y+=run[0]=='S'?1:run[0]=='N'?-1:0;expanded.Add(new JArray(x,y));}
            Assert.That(JToken.DeepEquals(expanded,request["state"]["legalActions"][1]["cells"]),Is.True);
            Assert.That(JToken.DeepEquals(request["state"]["memory"],compact["state"]["memory"]),Is.True,"Distant/stale evidence must not be pruned.");
        }

        [Test]
        public void AmbiguousCustomMovementProseAndExtraPhysicalFieldsStayVerbatim()
        {
            var request=Request(new[]{1,0,4});request["questions"]["action"]["criteria"]["move_literal"]="Carry quest item. V:0. Do not drop it.";
            var compact=JevRequestCompaction.Compact(request);
            Assert.That((string)compact["questions"]["action"]["criteria"]["move_literal"],Is.EqualTo("Carry quest item. V:0. Do not drop it."));
            Assert.That(JToken.DeepEquals(JevRequestCompaction.Expand(compact),request),Is.True);
            request["state"]["legalActions"][1]["staminaCost"]=3;
            compact=JevRequestCompaction.Compact(request);
            Assert.That(JToken.DeepEquals(compact["state"]["legalActions"][1],request["state"]["legalActions"][1]),Is.True);
        }

        [Test]
        public void NonCardinalOrExtendedCellFactsAreNeverLost()
        {
            var request=Request(new[]{1,0,4});request["state"]["legalActions"][1]["cells"][0]=new JObject{["x"]=11,["y"]=10,["cost"]=2};
            var compact=JevRequestCompaction.Compact(request);
            Assert.That(JToken.DeepEquals(request["state"]["legalActions"][1],compact["state"]["legalActions"][1]),Is.True);
            Assert.That(JToken.DeepEquals(JevRequestCompaction.Expand(compact),request),Is.True);
            request=Request(new[]{1,0,4});request["state"]["legalActions"][1]["cells"][0][0]=12;
            compact=JevRequestCompaction.Compact(request);Assert.That(compact["state"]["legalActions"][1],Is.TypeOf<JObject>());
        }

        [Test]
        public void RectangleEncodingKeepsFullExtentsAndDoesNotFillHolesOrInventCellOrder()
        {
            var request=Request(new[]{1,0,4});var original=(JArray)request["state"]["visible"]["buildings"][0]["footprint"];
            var compact=JevRequestCompaction.Compact(request);var bounds=(JArray)compact["state"]["visible"]["buildings"][0]["footprint"]["rectangle"];
            CollectionAssert.AreEqual(new[]{3,2,7,8},bounds.Values<int>());
            Assert.That(JToken.DeepEquals(JevRequestCompaction.Expand(compact),request),Is.True);
            original.RemoveAt(5);compact=JevRequestCompaction.Compact(request);Assert.That(JToken.DeepEquals(compact["state"]["visible"]["buildings"][0]["footprint"],original),Is.True);
        }

        [Test]
        public void EveryGeneratedGeometricFactHasAnExactInverse()
        {
            var request=Request(new[]{1,0,4});string criterion=(string)request["questions"]["action"]["criteria"]["move_literal"];
            request["questions"]["action"]["criteria"]["move_literal"]=criterion+" Build perimeter L1 4→3; inside=false; reach=false. Goal L1 2→3. Final order L1 9→8. JEV-selected intermediate (4,5) L1 5→6. Shared arrival Chebyshev 4→3; radius=3; endpointWithin=true. No allocated slot. This sequence passes the exact ordered destination at step 2, then leaves it for 2 more steps. Reverses directly to the position just left.";
            Assert.That(JToken.DeepEquals(JevRequestCompaction.Expand(JevRequestCompaction.Compact(request)),request),Is.True);
        }

        [Test]
        public void LargeActionVocabularyKeepsAllOptionsWhileReducingWireBytes()
        {
            var request=Request(new[]{1,0,8});var actions=(JArray)request["state"]["legalActions"];var criteria=(JObject)request["questions"]["action"]["criteria"];
            var original=(JObject)actions[1];string description=(string)criteria["move_literal"];
            for(int i=0;i<120;i++){var action=(JObject)original.DeepClone();string id="literal_variant_"+i;action["id"]=id;actions.Add(action);criteria[id]=description;}
            var compact=JevRequestCompaction.Compact(request);
            Assert.That(((JArray)compact["state"]["legalActions"]).Count,Is.EqualTo(actions.Count));
            Assert.That(JToken.DeepEquals(JevRequestCompaction.Expand(compact),request),Is.True);
            Assert.That(compact.ToString(Formatting.None).Length,Is.LessThan(request.ToString(Formatting.None).Length*.65));
        }
    }
}
