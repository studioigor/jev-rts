using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Jev
{
    /// <summary>A frozen catalogue for one genuine JEV planning decision. Contains no selected route.</summary>
    public sealed class JevNavigationPlan
    {
        public JObject State { get; internal set; }
        public Dictionary<string,string> Criteria { get; internal set; }
        internal readonly Dictionary<string,JObject> Targets=new Dictionary<string,JObject>();
        internal string UnitId,OrderKey;
        internal int OrderRevision,StartX,StartY;
    }

    /// <summary>Remembers a provider-selected transit point. Never selects, executes or filters physical actions.</summary>
    public sealed class JevNavigationIntent
    {
        readonly int reviewAfterPhysicalDecisions;
        string unitId,orderKey,selectedChoice;
        int orderRevision,physicalDecisions;
        long requestId;
        JObject waypoint;
        bool rejected;

        public JevNavigationIntent(int reviewAfterPhysicalDecisions=8)
        {this.reviewAfterPhysicalDecisions=Math.Max(1,reviewAfterPhysicalDecisions);}

        public void Reset()
        {
            unitId=null;orderKey=null;selectedChoice=null;waypoint=null;orderRevision=0;
            physicalDecisions=0;requestId=0;rejected=false;
        }

        public void RecordPhysicalDecision(bool rejected=false)
        {
            if(selectedChoice==null)return;
            physicalDecisions++;this.rejected|=rejected;
        }

        public bool NeedsPlanning(JObject observation) => PlanIfNeeded(observation)!=null;

        public JevNavigationPlan PlanIfNeeded(JObject observation)
        {
            Synchronize(observation);
            if(!Eligible(observation)||AtOriginalGoalOrWork(observation))return null;
            if(selectedChoice!=null&&!rejected&&physicalDecisions<reviewAfterPhysicalDecisions)return null;
            var plan=BuildPlan(observation);
            if(plan.Criteria.Count>1)return plan;
            if(selectedChoice!=null&&(rejected||physicalDecisions>=reviewAfterPhysicalDecisions))Reset();
            return null;
        }

        public JObject ObservationFacts(JObject observation)
        {
            Synchronize(observation);
            if(selectedChoice==null||!Eligible(observation)||AtOriginalGoalOrWork(observation))return null;
            return new JObject
            {
                ["mode"]=waypoint==null?"direct_order":"waypoint",["waypoint"]=waypoint?.DeepClone(),
                ["selectedChoice"]=selectedChoice,["requestId"]=requestId,["physicalDecisionsSinceReview"]=physicalDecisions,
                ["orderRevision"]=orderRevision,
                ["contract"]="This transit intention was selected by JEV, not by route search. The original order remains authoritative. JEV still selects every offered physical action; no point executes movement or work automatically. Reaching this waypoint completes only this transit intention. Original task completion and observed safety take precedence."
            };
        }

        public JevNavigationPlan BuildPlan(JObject observation)
        {
            if(observation==null)throw new ArgumentNullException(nameof(observation));
            Synchronize(observation);
            var self=observation["self"] as JObject;var order=self?["order"] as JObject;
            var plan=new JevNavigationPlan
            {
                State=(JObject)observation.DeepClone(),Criteria=new Dictionary<string,string>(),
                UnitId=(string)self?["id"],OrderRevision=(int?)self?["orderRevision"]??0,OrderKey=OrderKey(observation),
                StartX=X(self),StartY=Y(self)
            };
            plan.State["navigationIntent"]=ObservationFacts(observation);
            plan.Criteria.Add("direct_order","Continue considering the original order directly, with no intermediate point. JEV still chooses every physical step and all physical action options remain available.");
            plan.Targets.Add("direct_order",null);
            if(!Eligible(observation))return plan;

            var map=observation["map"] as JObject;
            int width=(int?)map?["width"]??0,height=(int?)map?["height"]??0;
            var terrain=new Dictionary<string,bool>();var occupied=new HashSet<string>();
            AddTiles(observation["memory"] as JObject,"terrain",terrain,null);
            AddTiles(observation["publicTerrain"] as JObject,"tiles",terrain,null);
            AddTiles(observation["visible"] as JObject,"tiles",terrain,occupied,(string)self?["id"]);
            foreach(string group in new[]{"units","resources"})
            foreach(var entity in (observation["visible"]?[group] as JArray??new JArray()).OfType<JObject>())
                if((string)entity["id"]!=(string)self?["id"]&&((int?)entity["hp"]??1)>0&&((int?)entity["remaining"]??(int?)entity["amount"]??1)>0)
                    occupied.Add(Key(X(entity),Y(entity)));

            var rectangles=new List<Tuple<string,int,int,int,int>>();var seenBuildings=new HashSet<string>();
            foreach(string group in new[]{"visible","memory"})
            foreach(var building in (observation[group]?["buildings"] as JArray??new JArray()).OfType<JObject>())
            {
                string id=(string)building["id"];
                if(id==null||!seenBuildings.Add(id)||((int?)building["hp"]??1)<=0||!(building["footprint"] is JArray cells)||cells.Count==0)continue;
                foreach(var cell in cells)occupied.Add(Key(X(cell),Y(cell)));
                rectangles.Add(Tuple.Create(id,cells.Min(X),cells.Min(Y),cells.Max(X),cells.Max(Y)));
            }
            var contacts=BuildingContacts(rectangles);
            string contactText=contacts.Count==0?"No cardinal edge contact between the known building rectangles.":
                string.Join(" ",contacts.OfType<JObject>().Select(c=>(string)c["description"]));
            string orderedRelations=string.Join("; ",rectangles.Select(r=>$"{r.Item1} x[{r.Item2}..{r.Item4}] y[{r.Item3}..{r.Item5}]: self {Sides(X(self),Y(self),r)} -> ordered goal {Sides(X(order),Y(order),r)}"));
            string kind=(string)order["kind"],target=(string)order["targetId"];
            var sharedGroup=kind=="move"?order["sharedGroup"] as JObject:null;
            string task=kind=="gather"?$"Approach ordered resource {target} at ({X(order)},{Y(order)}) to legal gathering reach and gather; the resource's occupied cell is not a movement destination.":
                kind=="attack"?$"Approach ordered target {target} at its ordered coordinate ({X(order)},{Y(order)}) to legal attack reach and attack using current observed target facts.":
                kind=="hold"?$"Reach the ordered hold coordinate ({X(order)},{Y(order)}) and defend there.":
                sharedGroup!=null?$"Reach the shared arrival area within Chebyshev radius {sharedGroup["arrivalChebyshevRadius"]} of ({X(order)},{Y(order)}); choose your own parking or yielding from observed traffic, with no allocated slot.":
                $"Reach the original move destination ({X(order)},{Y(order)}).";
            plan.Criteria["direct_order"]=$"Act toward the original {kind} order from current ({X(self)},{Y(self)}) to ordered goal ({X(order)},{Y(order)}), with no intermediate point. {task} Relations to ALL known building rectangles (north means smaller y): {orderedRelations}. Contacts: {contactText} Self to ordered goal Manhattan distance={Math.Abs(X(order)-X(self))+Math.Abs(Y(order)-Y(self))}; ordered goal to original ordered coordinate Manhattan distance=0. This is an active intention to fulfill the original task, not WAIT. Distances and sides are geometric facts, not priorities or a route. JEV still chooses every physical step and all physical action options remain available.";
            var candidates=new Dictionary<string,JObject>();
            foreach(var rectangle in rectangles)
            foreach(int x in new[]{rectangle.Item2-1,rectangle.Item4+1})
            foreach(int y in new[]{rectangle.Item3-1,rectangle.Item5+1})
            {
                string key=Key(x,y);
                if(x<0||y<0||x>=width||y>=height||occupied.Contains(key)||terrain.TryGetValue(key,out bool blocked)&&blocked)continue;
                if(x==X(self)&&y==Y(self)||x==X(order)&&y==Y(order))continue;
                if(!candidates.TryGetValue(key,out var candidate))
                {
                    candidate=new JObject{["x"]=x,["y"]=y,["sourceBuildingIds"]=new JArray(),
                        ["staticTerrainEvidence"]=terrain.ContainsKey(key)?"known clear static terrain":"not observed; coordinate derived only from known building bounds"};
                    candidates.Add(key,candidate);
                }
                ((JArray)candidate["sourceBuildingIds"]).Add(rectangle.Item1);
            }
            foreach(var candidate in candidates.Values)
            {
                int x=X(candidate),y=Y(candidate);string id="corner_"+x+"_"+y;
                candidate["buildingRelations"]=new JArray(rectangles.Select(r=>new JObject
                {
                    ["buildingId"]=r.Item1,["bounds"]=Bounds(r),
                    ["selfSides"]=Sides(X(self),Y(self),r),["pointSides"]=Sides(x,y,r)
                }));
                string relationText=string.Join("; ",rectangles.Select(r=>$"{r.Item1} x[{r.Item2}..{r.Item4}] y[{r.Item3}..{r.Item5}]: self {Sides(X(self),Y(self),r)} -> point {Sides(x,y,r)}"));
                plan.Targets.Add(id,new JObject{["x"]=x,["y"]=y});
                plan.Criteria.Add(id,$"Keep ({x},{y}) as an intermediate transit point outside known building bounds {string.Join(",",candidate["sourceBuildingIds"].Values<string>())}. Relations to ALL known building rectangles (north means smaller y): {relationText}. Contacts: {contactText} Self Manhattan distance={Math.Abs(x-X(self))+Math.Abs(y-Y(self))}; point to original ordered coordinate Manhattan distance={Math.Abs(x-X(order))+Math.Abs(y-Y(order))}. {candidate["staticTerrainEvidence"]}. Distances and sides are geometric facts, not priorities or a route. JEV must select every physical step.");
            }
            plan.State["navigationPlanning"]=new JObject
            {
                ["candidateSource"]="All unique diagonal exterior corners (minX-1/maxX+1, minY-1/maxY+1) of this unit's currently observed or personally remembered building footprints.",
                ["candidates"]=new JArray(candidates.Values),["originalOrder"]=order?.DeepClone(),
                ["buildingContacts"]=contacts,
                ["geometryLegend"]="Coordinates are grid cells; x increases east, y increases south. Sides describe each point relative to each inclusive occupied building rectangle. Cardinal edge contacts mean no free grid cell between the reported occupied edges over the exact overlap interval. This describes geometry only, not reachability or a route.",
                ["reviewReason"]=selectedChoice==null?"no active transit intention":rejected?"physical action rejected":"bounded physical-decision review",
                ["contract"]="Choose direct_order or one offered transit point. No shortest path, priority, preferred side or reachability search was computed. Outside-map points and points known blocked/occupied are excluded; unknown terrain is not assumed clear. The selected point persists until arrival, order change or a planning review. All physical actions in legalActions remain unchanged."
            };
            return plan;
        }

        static JObject Bounds(Tuple<string,int,int,int,int> r)=>new JObject
        {["minX"]=r.Item2,["minY"]=r.Item3,["maxX"]=r.Item4,["maxY"]=r.Item5};

        static string Sides(int x,int y,Tuple<string,int,int,int,int> r)
        {
            string horizontal=x<r.Item2?"west":x>r.Item4?"east":null;
            string vertical=y<r.Item3?"north":y>r.Item5?"south":null;
            return horizontal==null?(vertical??"inside"):vertical==null?horizontal:horizontal+"+"+vertical;
        }

        static JArray BuildingContacts(List<Tuple<string,int,int,int,int>> rectangles)
        {
            var contacts=new JArray();
            for(int i=0;i<rectangles.Count;i++)for(int j=i+1;j<rectangles.Count;j++)
            {
                var a=rectangles[i];var b=rectangles[j];
                int minY=Math.Max(a.Item3,b.Item3),maxY=Math.Min(a.Item5,b.Item5);
                if(minY<=maxY&&(a.Item4+1==b.Item2||b.Item4+1==a.Item2))
                {
                    var west=a.Item4+1==b.Item2?a:b;var east=ReferenceEquals(west,a)?b:a;
                    contacts.Add(new JObject{["axis"]="x",["firstBuildingId"]=west.Item1,["secondBuildingId"]=east.Item1,
                        ["firstEdgeCell"]=west.Item4,["secondEdgeCell"]=east.Item2,["overlapMin"]=minY,["overlapMax"]=maxY,
                        ["description"]=$"{west.Item1} east occupied edge x={west.Item4} directly touches {east.Item1} west occupied edge x={east.Item2} for y={minY}..{maxY}; no free grid cell between these edges."});
                }
                int minX=Math.Max(a.Item2,b.Item2),maxX=Math.Min(a.Item4,b.Item4);
                if(minX<=maxX&&(a.Item5+1==b.Item3||b.Item5+1==a.Item3))
                {
                    var north=a.Item5+1==b.Item3?a:b;var south=ReferenceEquals(north,a)?b:a;
                    contacts.Add(new JObject{["axis"]="y",["firstBuildingId"]=north.Item1,["secondBuildingId"]=south.Item1,
                        ["firstEdgeCell"]=north.Item5,["secondEdgeCell"]=south.Item3,["overlapMin"]=minX,["overlapMax"]=maxX,
                        ["description"]=$"{north.Item1} south occupied edge y={north.Item5} directly touches {south.Item1} north occupied edge y={south.Item3} for x={minX}..{maxX}; no free grid cell between these edges."});
                }
            }
            return contacts;
        }

        public bool ApplyChoice(JevNavigationPlan plan,string choice,JObject currentObservation,long requestId,out string reason)
        {
            reason=null;
            if(plan==null||choice==null||!plan.Targets.TryGetValue(choice,out var target))
            {reason="invalid_navigation_choice";return false;}
            var self=currentObservation?["self"] as JObject;
            if(!Eligible(currentObservation)||plan.UnitId!=(string)self?["id"]||plan.OrderRevision!=((int?)self?["orderRevision"]??0)||
                plan.OrderKey!=OrderKey(currentObservation)||plan.StartX!=X(self)||plan.StartY!=Y(self))
            {reason="stale_navigation_plan";return false;}
            if(AtOriginalGoalOrWork(currentObservation)){reason="navigation_no_longer_needed";return false;}
            unitId=plan.UnitId;orderRevision=plan.OrderRevision;orderKey=plan.OrderKey;selectedChoice=choice;
            waypoint=target==null?null:(JObject)target.DeepClone();this.requestId=requestId;physicalDecisions=0;rejected=false;
            return true;
        }

        void Synchronize(JObject observation)
        {
            if(selectedChoice==null)return;
            var self=observation?["self"] as JObject;
            if(!Eligible(observation)||unitId!=(string)self?["id"]||orderRevision!=((int?)self?["orderRevision"]??0)||orderKey!=OrderKey(observation))
            {Reset();return;}
            if(AtOriginalGoalOrWork(observation)||waypoint!=null&&X(self)==X(waypoint)&&Y(self)==Y(waypoint))Reset();
        }

        static bool Eligible(JObject observation)
        {
            var order=(observation?["self"] as JObject)?["order"] as JObject;
            string kind=(string)order?["kind"];
            return kind=="move"||kind=="gather"||kind=="attack"||kind=="hold";
        }

        static bool AtOriginalGoalOrWork(JObject observation)
        {
            var self=observation?["self"] as JObject;var order=self?["order"] as JObject;
            string kind=(string)order?["kind"],target=(string)order?["targetId"];
            if(kind=="move"&&order?["sharedGroup"] is JObject shared&&
                Math.Max(Math.Abs(X(self)-X(order)),Math.Abs(Y(self)-Y(order)))<=Math.Max(0,(int?)shared["arrivalChebyshevRadius"]??0))return true;
            if((kind=="move"||kind=="hold")&&X(self)==X(order)&&Y(self)==Y(order))return true;
            var actions=observation?["legalActions"] as JArray??new JArray();
            return (kind=="gather"||kind=="attack")&&actions.Any(a=>(string)a["kind"]==kind&&(string)a["targetId"]==target&&target!=null);
        }

        static string OrderKey(JObject observation)
        {
            var order=(observation?["self"] as JObject)?["order"] as JObject;
            return order==null?null:order.ToString(Formatting.None);
        }

        static void AddTiles(JObject group,string name,Dictionary<string,bool> terrain,HashSet<string> occupied,string selfId=null)
        {
            if(!(group?[name] is JArray tiles))return;
            foreach(var tile in tiles)
            {
                if(tile is JObject point)
                {
                    string key=Key(X(point),Y(point));terrain[key]=(bool?)point["blocked"]??false;
                    string occupant=(string)point["occupiedBy"];
                    if(occupied!=null&&!string.IsNullOrEmpty(occupant)&&occupant!=selfId)occupied.Add(key);
                }
                else if(tile is JArray run&&run.Count>=5)
                {
                    int y=(int)run[0],first=(int)run[1],last=(int)run[2];bool blocked=(int)run[3]!=0;string occupant=(string)run[4];
                    for(int x=first;x<=last;x++)
                    {string key=Key(x,y);terrain[key]=blocked;if(occupied!=null&&!string.IsNullOrEmpty(occupant)&&occupant!=selfId)occupied.Add(key);}
                }
            }
        }

        static int X(JToken point)=>point is JArray pair&&pair.Count>=2?(int)pair[0]:(int?)point?["x"]??0;
        static int Y(JToken point)=>point is JArray pair&&pair.Count>=2?(int)pair[1]:(int?)point?["y"]??0;
        static string Key(int x,int y)=>x+","+y;
    }
}
