using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Jev
{
    public sealed class JevGatherPlan
    {
        public JObject State;
        public Dictionary<string,string> Criteria;
        internal readonly Dictionary<string,JObject> Targets=new Dictionary<string,JObject>();
        internal string UnitId,OrderKey;
        internal int OrderRevision,StartX,StartY;
    }

    /// <summary>
    /// Exposes personally known replacement nodes after observed exhaustion. It never
    /// selects a resource, changes an order or supplies a route without a JEV response.
    /// </summary>
    public static class JevGatherIntent
    {
        public const string KeepAssignment="keep_assignment";
        // A continuation is local work around the exhausted node. This circle has
        // 197 grid cells, below JEV's 255-choice limit without ranking/truncating trees.
        public const int MaximumContinuationRadius=8;

        public static JevGatherPlan BuildPlan(JObject observation)
        {
            if(!ObservedExhaustion(observation))return null;
            var self=observation["self"] as JObject;var order=self["order"] as JObject;
            var candidates=KnownCandidates(observation).ToArray();
            if(candidates.Length==0)return null;
            var plan=new JevGatherPlan
            {
                State=(JObject)observation.DeepClone(),Criteria=new Dictionary<string,string>(),
                UnitId=(string)self["id"],OrderRevision=(int?)self["orderRevision"]??0,
                OrderKey=order.ToString(Formatting.None),StartX=X(self),StartY=Y(self)
            };
            plan.Criteria.Add(KeepAssignment,"Keep the gathering assignment without selecting a replacement now; the next physical decision can defend, retreat or wait if no safe productive continuation is appropriate. This option performs no work or movement.");
            plan.Targets.Add(KeepAssignment,null);
            foreach(var candidate in candidates)
            {
                string id="gather_next_"+(string)candidate["id"];
                plan.Targets.Add(id,(JObject)candidate.DeepClone());
                int distance=Math.Abs(X(self)-X(candidate))+Math.Abs(Y(self)-Y(candidate));
                plan.Criteria.Add(id,$"Continue the existing {(string)order["resourceKind"]} assignment at resource {candidate["id"]} ({X(candidate)},{Y(candidate)}). Evidence: {candidate["evidence"]}; last observed remaining={candidate["remaining"]}; cardinal coordinate distance={distance}, ignoring obstacles. JEV must still choose every movement and gathering action; this does not promise reachability or execute work.");
            }
            plan.State["gatherContinuation"]=new JObject
            {
                ["resourceKind"]=order["resourceKind"].DeepClone(),["exhaustedTargetId"]=order["targetId"]?.DeepClone(),
                ["evidence"]="The ordered cell is currently visible and the original resource is absent there.",
                ["workArea"]=new JObject{["centerX"]=X(order),["centerY"]=Y(order),["radiusCells"]=MaximumContinuationRadius},
                ["candidates"]=new JArray(candidates.Select(c=>c.DeepClone())),
                ["contract"]="All replacement nodes come from this worker's current sensors or personal resource memory, have the same resource kind as its ongoing assignment, and lie inside the stated local work area around the exhausted node. Resources outside this local work area are not part of this continuation choice; they are not claimed absent. Memory can be stale. Candidates are in stable ID order, not ranked by distance or usefulness. No nearest-target rule, path search or automatic retargeting runs. Choose a target yourself, or keep_assignment to defer for an actual threat. The assignment continues until a new player or commander order."
            };
            return plan;
        }

        public static bool TryResolveChoice(JevGatherPlan plan,string choice,JObject currentObservation,out JObject nextTarget,out string reason)
        {
            nextTarget=null;reason=null;
            if(plan==null||choice==null||!plan.Targets.TryGetValue(choice,out var submittedTarget))
            {reason="invalid_gather_choice";return false;}
            var self=currentObservation?["self"] as JObject;var order=self?["order"] as JObject;
            if(!ObservedExhaustion(currentObservation)||plan.UnitId!=(string)self?["id"]||
                plan.OrderRevision!=((int?)self?["orderRevision"]??0)||plan.OrderKey!=order?.ToString(Formatting.None)||
                plan.StartX!=X(self)||plan.StartY!=Y(self))
            {reason="stale_gather_plan";return false;}
            if(submittedTarget==null)return true;
            var currentTarget=KnownCandidates(currentObservation).FirstOrDefault(c=>(string)c["id"]==(string)submittedTarget["id"]&&
                X(c)==X(submittedTarget)&&Y(c)==Y(submittedTarget)&&(string)c["kind"]==(string)submittedTarget["kind"]);
            if(currentTarget==null){reason="gather_target_no_longer_known";return false;}
            nextTarget=(JObject)currentTarget.DeepClone();return true;
        }

        public static bool ObservedExhaustion(JObject observation)
        {
            var self=observation?["self"] as JObject;var order=self?["order"] as JObject;
            string kind=(string)order?["resourceKind"],target=(string)order?["targetId"];
            if((string)self?["type"]!="worker"||(string)order?["kind"]!="gather"||
                (kind!="wood"&&kind!="gold")||string.IsNullOrEmpty(target)||!CellVisible(observation,X(order),Y(order)))return false;
            return !Resources(observation,"visible").Any(r=>(string)r["id"]==target&&((int?)r["remaining"]??0)>0);
        }

        static IEnumerable<JObject> KnownCandidates(JObject observation)
        {
            var order=observation?["self"]?["order"] as JObject;string kind=(string)order?["resourceKind"],original=(string)order?["targetId"];
            var known=new Dictionary<string,JObject>();
            foreach(string group in new[]{"visible","memory"})
            foreach(var resource in Resources(observation,group))
            {
                string id=(string)resource["id"];
                if(string.IsNullOrEmpty(id)||id==original||known.ContainsKey(id)||(string)resource["kind"]!=kind||
                    ((int?)resource["remaining"]??0)<=0||resource["x"]?.Type!=JTokenType.Integer||resource["y"]?.Type!=JTokenType.Integer)continue;
                int dx=X(resource)-X(order),dy=Y(resource)-Y(order);
                if(dx*dx+dy*dy>MaximumContinuationRadius*MaximumContinuationRadius)continue;
                // Current absence overrides old memory; do not resurrect a cut tree.
                if(group=="memory"&&CellVisible(observation,X(resource),Y(resource)))continue;
                var candidate=(JObject)resource.DeepClone();candidate["evidence"]=group=="visible"?"currently observed":"personally remembered; may be stale";
                known.Add(id,candidate);
            }
            return known.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>pair.Value);
        }

        static IEnumerable<JObject> Resources(JObject state,string group)=>
            (state?[group]?["resources"] as JArray??new JArray()).OfType<JObject>();

        static bool CellVisible(JObject observation,int x,int y)
        {
            foreach(var tile in observation?["visible"]?["tiles"] as JArray??new JArray())
            {
                if(tile is JObject point&&X(point)==x&&Y(point)==y)return true;
                if(tile is JArray run&&run.Count>=5&&(int)run[0]==y&&(int)run[1]<=x&&(int)run[2]>=x)return true;
            }
            return false;
        }
        static int X(JToken point)=>(int?)point?["x"]??0;
        static int Y(JToken point)=>(int?)point?["y"]??0;
    }
}
