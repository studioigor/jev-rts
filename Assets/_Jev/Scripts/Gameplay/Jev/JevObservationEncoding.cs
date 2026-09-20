using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Jev
{
    /// <summary>Lossless row encoding and geometric/arithmetic facts. Never ranks, removes or chooses actions.</summary>
    public static class JevObservationEncoding
    {
        static int X(JToken value) => (int?)(value as JObject)?["x"] ?? 0;
        static int Y(JToken value) => (int?)(value as JObject)?["y"] ?? 0;
        static int L1(JToken a,JToken b) => Math.Abs(X(a)-X(b))+Math.Abs(Y(a)-Y(b));
        static int C(JToken a,JToken b) => Math.Max(Math.Abs(X(a)-X(b)),Math.Abs(Y(a)-Y(b)));

        struct BuildArea
        {
            public int MinX, MinY, MaxX, MaxY;
            public bool Contains(JToken point) => X(point)>=MinX && X(point)<=MaxX && Y(point)>=MinY && Y(point)<=MaxY;
            public int FootprintDistance(JToken point) => Math.Max(0,Math.Max(MinX-X(point),X(point)-MaxX)) + Math.Max(0,Math.Max(MinY-Y(point),Y(point)-MaxY));
            public bool CardinalReach(JToken point) => !Contains(point) && FootprintDistance(point)==1;
            // Exact obstacle-free distance to a cardinal neighbor OUTSIDE the
            // footprint. Inside the site is never a valid construction position.
            public int PerimeterDistance(JToken point) => Contains(point)
                ? Math.Min(Math.Min(X(point)-MinX+1,MaxX-X(point)+1),Math.Min(Y(point)-MinY+1,MaxY-Y(point)+1))
                : Math.Max(0,FootprintDistance(point)-1);
            public JObject Bounds() => new JObject { ["minX"]=MinX,["minY"]=MinY,["maxX"]=MaxX,["maxY"]=MaxY,["width"]=MaxX-MinX+1,["height"]=MaxY-MinY+1 };
        }

        static JObject BuildDefinition(JObject state,JObject order)
        {
            string kind=(string)order?["buildingType"];
            return kind==null ? null : ((state["rules"] as JObject)?["buildingDefinitions"] as JObject)?[kind] as JObject;
        }

        static bool TryBuildArea(JObject state,JObject order,out BuildArea area)
        {
            area=default;
            if((string)order?["kind"]!="build") return false;
            var definition=BuildDefinition(state,order);
            if(definition==null) return false;
            int width=Math.Max(1,(int?)definition["footprintSizeX"]??1),height=Math.Max(1,(int?)definition["footprintSizeY"]??1);
            area.MinX=X(order)-width/2;area.MinY=Y(order)-height/2;
            area.MaxX=area.MinX+width-1;area.MaxY=area.MinY+height-1;
            return true;
        }

        static JObject MatchingBuilding(JObject state,JObject order,string group)
        {
            var buildings=(state[group] as JObject)?["buildings"] as JArray;
            string faction=(string)(state["self"] as JObject)?["factionId"];
            return buildings?.OfType<JObject>().FirstOrDefault(b => X(b)==X(order) && Y(b)==Y(order)
                && (string)b["factionId"]==faction && (string)b["type"]==(string)order["buildingType"] && ((int?)b["hp"]??1)>0);
        }

        public static bool OrderedConstructionAlreadyPaid(JObject state)
        {
            var order=state["self"]?["order"] as JObject;
            return (string)order?["kind"]=="build"&&
                (MatchingBuilding(state,order,"visible")!=null||MatchingBuilding(state,order,"memory")!=null);
        }

        static int GatherApproachDistance(JToken point,JToken resource) => Math.Abs(L1(point,resource)-1);

        static JArray EncodeTiles(JArray tiles)
        {
            var runs=new JArray();JArray last=null;
            foreach(var tile in tiles.OfType<JObject>())
            {
                int x=X(tile),y=Y(tile),blocked=(bool?)tile["blocked"]==true?1:0;
                var occupant=tile["occupiedBy"]?.DeepClone()??JValue.CreateNull();
                var extra=new JObject(tile.Properties().Where(p=>p.Name!="x"&&p.Name!="y"&&p.Name!="blocked"&&p.Name!="occupiedBy")
                    .Select(p=>new JProperty(p.Name,p.Value.DeepClone())));
                JToken extraFields=extra.Count==0?null:extra;
                if(last!=null&&(int)last[0]==y&&(int)last[2]+1==x&&(int)last[3]==blocked
                    &&JToken.DeepEquals(last[4],occupant)&&JToken.DeepEquals(last.Count>5?last[5]:null,extraFields))
                    last[2]=x;
                else
                {
                    last=new JArray(y,x,x,blocked,occupant);
                    if(extraFields!=null)last.Add(extraFields);
                    runs.Add(last);
                }
            }
            return runs;
        }

        static JArray EncodeCells(JArray cells) => new JArray(cells.Select(cell =>
            cell is JObject point&&point.Properties().All(p=>p.Name=="x"||p.Name=="y")&&point["x"]!=null&&point["y"]!=null
                ? (JToken)new JArray(point["x"].DeepClone(),point["y"].DeepClone()) : cell.DeepClone()));

        static JObject Objective(JObject source)
        {
            var self=source["self"] as JObject;var order=self?["order"] as JObject;
            var objective=new JObject { ["unitId"]=self?["id"]?.DeepClone(),["unitType"]=self?["type"]?.DeepClone(),
                ["currentCell"]=new JArray(X(self),Y(self)),["orderKind"]=order?["kind"]?.DeepClone()??new JValue("none") };
            if(order==null)return objective;
            objective["orderedCell"]=new JArray(X(order),Y(order));
            objective["targetOffsetXY"]=new JArray(X(order)-X(self),Y(order)-Y(self));
            foreach(string field in new[]{"targetId","buildingType"})if(order[field]!=null)objective[field]=order[field].DeepClone();
            string targetId=(string)order["targetId"];
            if(targetId!=null)
            {
                bool visible=new[]{"units","resources","buildings"}.Any(k=>((source["visible"] as JObject)?[k] as JArray)?.Any(e=>(string)e["id"]==targetId)==true);
                bool remembered=new[]{"enemies","resources","buildings"}.Any(k=>((source["memory"] as JObject)?[k] as JArray)?.Any(e=>(string)e["id"]==targetId)==true);
                bool targetCellVisible=((source["visible"] as JObject)?["tiles"] as JArray)?.Any(t=>X(t)==X(order)&&Y(t)==Y(order))==true;
                objective["orderedCellCurrentlyVisible"]=targetCellVisible;
                objective["namedTargetCurrentlyVisible"]=visible;
                objective["targetEvidence"]=visible?"currently visible":targetCellVisible?"ordered cell is currently observed; named target is absent there":remembered?"personally remembered; may be stale":"commanded coordinates only; absence from current sensors does not prove depletion or death";
            }
            return objective;
        }

        static bool StandardMoveDescription(JToken action,string description)
        {
            if(!(action["cells"] is JArray cells))return false;
            string expected="Move only through these explicit adjacent cells in order: "+string.Join(" → ",cells.Select(c=>$"({X(c)},{Y(c)})"))+". Stop if obstructed; no rerouting.";
            return description==expected;
        }

        // This is run-length encoding of the ALREADY offered literal cells, not
        // a route planner. Reversals and every turn remain in their original order.
        static string CardinalRunDescription(JToken action,JObject self)
        {
            if(!(action["cells"] is JArray cells)||cells.Count==0||self==null)return null;
            int x=X(self),y=Y(self),count=0;string previousDirection=null;
            var runs=new System.Collections.Generic.List<string>();
            foreach(var cell in cells)
            {
                if(!(cell is JObject point)||point["x"]?.Type!=JTokenType.Integer||point["y"]?.Type!=JTokenType.Integer)return null;
                int nextX=X(cell),nextY=Y(cell),dx=nextX-x,dy=nextY-y;
                if(Math.Abs(dx)+Math.Abs(dy)!=1)return null;
                string direction=dx==1?"east":dx==-1?"west":dy==1?"south":"north";
                if(direction!=previousDirection)
                {
                    if(previousDirection!=null)runs.Add(previousDirection+" "+count);
                    previousDirection=direction;count=0;
                }
                count++;x=nextX;y=nextY;
            }
            if(x!=X(action)||y!=Y(action))return null;
            runs.Add(previousDirection+" "+count);
            return $"Move from ({X(self)},{Y(self)}) via {string.Join(" → ",runs)} to ({x},{y}); stop if obstructed.";
        }

        static JArray OutsideSides(JToken point,BuildArea area)
        {
            var sides=new JArray();
            if(X(point)<area.MinX)sides.Add("west");if(X(point)>area.MaxX)sides.Add("east");
            if(Y(point)<area.MinY)sides.Add("north");if(Y(point)>area.MaxY)sides.Add("south");
            if(sides.Count==0)sides.Add("inside");
            return sides;
        }

        static JObject NavigationGeometry(JObject source)
        {
            var self=source["self"] as JObject;var order=self?["order"] as JObject;
            var rectangles=new JArray();var seen=new System.Collections.Generic.HashSet<string>();
            foreach(string group in new[]{"visible","memory"})
            foreach(var building in (source[group]?["buildings"] as JArray??new JArray()).OfType<JObject>())
            {
                string id=(string)building["id"];
                if(id==null||!seen.Add(id)||!(building["footprint"] is JArray cells)||cells.Count==0)continue;
                var area=new BuildArea{MinX=cells.Min(X),MinY=cells.Min(Y),MaxX=cells.Max(X),MaxY=cells.Max(Y)};
                rectangles.Add(new JObject{["id"]=id,["bounds"]=area.Bounds(),
                    ["evidence"]=group=="visible"?"currently observed building extent":"personally remembered extent; may be stale",
                    ["currentOutsideSides"]=OutsideSides(self,area),["orderedCellOutsideSides"]=order==null?null:OutsideSides(order,area)});
            }
            return new JObject{["knownBuildingRectangles"]=rectangles,
                ["contract"]="Inclusive occupied building bounds from this unit's existing observation and memory. Smaller y is north; smaller x is west. Outside-side labels are coordinate facts only, not a selected bypass or a route. Static terrain being clear does not remove these known building obstacles. JEV selects the side and every offered movement sequence."};
        }

        public static JObject Compact(JObject source)
        {
            var state=(JObject)source.DeepClone();
            foreach(var pair in new[]{("visible","tiles"),("memory","terrain"),("publicTerrain","tiles")})
                if(state[pair.Item1] is JObject group && group[pair.Item2] is JArray tiles)
                {
                    group["tileEncoding"]="horizontal-runs-v1: expand xStart..xEnd inclusive at y; omitted cells unknown; preserve run order; optional extra fields apply to every cell of that run.";
                    group["tileColumns"]="y,xStart,xEnd,blocked(0=false/1=true),occupiedBy(null=none),optionalExtraFields";
                    group[pair.Item2]=EncodeTiles(tiles);
                }
            // Compact structural action facts remain inspectable. Movement criteria
            // repeat lossless cardinal runs so each choice is understood on its own.
            if(state["legalActions"] is JArray actions)
            {
                foreach(var action in actions.OfType<JObject>())
                {
                    action.Remove("description");
                    if(action["cells"] is JArray cells)action["cells"]=EncodeCells(cells);
                }
                state["actionEncoding"]="legalActions retains every offered ID and physical field. cells is an exact ordered sequence of [x,y] pairs (objects retain extra fields). Movement criteria encode those same cells as ordered cardinal runs from the current coordinate: east/west changes x by +1/-1 and south/north changes y by +1/-1 once per listed step. Execute only those cells; stop on collision or changed order, never reroute. Movement does not gather, build or attack. Criterion geometry: Build perimeter L1=distance to an OUTSIDE cardinal build position; inside=endpoint inside footprint; reach=endpoint in cardinal interaction reach. Gather approach L1=distance to a cardinal resource neighbor. Enemy C=Chebyshev range. Every distance is current→endpoint, ignores obstacles/costs, and is not a path or priority.";
            }
            var self=state["self"] as JObject;
            var order=self?["order"] as JObject;
            if(order!=null)
            {
                var facts=new JObject();
                if((string)order["kind"]=="build")
                {
                    var definition=BuildDefinition(state,order);
                    var visible=MatchingBuilding(state,order,"visible");
                    var existing=visible??MatchingBuilding(state,order,"memory");
                    facts["existingBuilding"]=existing?.DeepClone();
                    facts["buildingEvidence"]=visible!=null?"visible":existing!=null?"remembered":"none";
                    facts["materialsAlreadyPaid"]=existing!=null;
                    facts["woodMissing"]=existing!=null?0:Math.Max(0,((int?)definition?["cost"]?["wood"]??0)-((int?)state["faction"]?["wood"]??0));
                    facts["goldMissing"]=existing!=null?0:Math.Max(0,((int?)definition?["cost"]?["gold"]??0)-((int?)state["faction"]?["gold"]??0));
                    bool complete=(bool?)existing?["complete"]==true;
                    facts["constructionComplete"]=complete;
                    facts["remainingWork"]=complete?0:existing!=null
                        ? Math.Max(0,(int?)existing["remainingWork"]??((int?)existing["requiredWork"]??0)-((int?)existing["progress"]??0))
                        : Math.Max(0,(int?)definition?["requiredWork"]??0);
                    if(TryBuildArea(state,order,out var area))
                    {
                        facts["plannedFootprintBounds"]=area.Bounds();
                        facts["currentInsideFootprint"]=area.Contains(self);
                        facts["currentCardinalBuildReach"]=area.CardinalReach(self);
                        facts["currentBuildPerimeterL1"]=area.PerimeterDistance(self);
                        facts["interactionGeometry"]="Build from a cardinal neighbor outside the entire rectangular footprint. Perimeter L1 ignores obstacles, occupancy and costs; it is not a route or an available build action. The center is not an approach destination.";
                    }
                }
                else if((string)order["kind"]=="gather")
                {
                    facts["resourceCenterL1"]=L1(self,order);
                    facts["currentCardinalGatherReach"]=L1(self,order)==1;
                    facts["currentGatherApproachL1"]=GatherApproachDistance(self,order);
                    facts["interactionGeometry"]="Gather from a cardinal neighbor of the resource cell. Do not enter the occupied resource cell. Approach L1 ignores obstacles and does not imply the resource is still present or currently visible.";
                }
                else
                {
                    facts["goalManhattanDistance"]=L1(self,order);
                    if((string)order["kind"]=="move"&&order["sharedGroup"] is JObject shared)
                    {
                        int radius=Math.Max(0,(int?)shared["arrivalChebyshevRadius"]??0);
                        facts["sharedArrivalChebyshevRadius"]=radius;
                        facts["currentSharedCenterChebyshev"]=C(self,order);
                        facts["withinSharedArrivalArea"]=C(self,order)<=radius;
                        facts["interactionGeometry"]="All group members share this center and arrival radius. No individual destination is allocated. Being within the area permits JEV to choose WAIT or parking/yielding steps based only on observed traffic.";
                    }
                }
                state["orderFacts"]=facts;
            }
            // Put the unchanged current task before large sensor tables so distant
            // commanded targets cannot be mistaken for absent or completed tasks.
            state.Remove("currentObjective");
            state.Remove("navigationGeometry");
            state.AddFirst(new JProperty("navigationGeometry",NavigationGeometry(source)));
            state.AddFirst(new JProperty("currentObjective",Objective(source)));
            if(state["navigationIntent"] is JObject intent)
            { state.Remove("navigationIntent");state.AddFirst(new JProperty("navigationIntent",intent)); }
            return state;
        }
        public static void AnnotateCriteria(JObject state,JObject criteria)
        {
            if(!(state["legalActions"] is JArray actions))return;
            var self=state["self"] as JObject;var goal=self?["order"] as JObject;
            var intention=state["navigationIntent"] as JObject;
            var waypoint=(string)intention?["mode"]=="waypoint"?intention?["waypoint"] as JObject:null;
            bool build=TryBuildArea(state,goal,out var area);
            bool gather=(string)goal?["kind"]=="gather";
            var sharedGroup=(string)goal?["kind"]=="move"?goal?["sharedGroup"] as JObject:null;
            int sharedRadius=Math.Max(0,(int?)sharedGroup?["arrivalChebyshevRadius"]??0);
            var enemies=(state["visible"]?["units"] as JArray??new JArray()).Where(e=>(string)e["factionId"]!=(string)self?["factionId"]).ToArray();
            foreach(var action in actions)
            {
                string id=(string)action["id"],kind=(string)action["kind"];
                if(id==null||criteria[id]==null)continue;
                if(kind=="wait")criteria[id]=$"Remain at ({X(self)},{Y(self)}). No movement, gathering, construction, healing or attack. WAIT does not block incoming hits or withdraw from an enemy. Suitable when there is no unfinished useful task or local defensive response.";
                if(kind=="wait"&&sharedGroup!=null)criteria[id]=$"Remain at ({X(self)},{Y(self)}); shared arrival radius={sharedRadius}, within={(C(self,goal)<=sharedRadius).ToString().ToLowerInvariant()}. No movement or work. Consider whether staying blocks observed group traffic; yielding or parking requires choosing its own offered movement.";
                if(kind!="move")continue;
                string facts=build
                    ? $" Build perimeter L1 {area.PerimeterDistance(self)}→{area.PerimeterDistance(action)}; inside={area.Contains(action).ToString().ToLowerInvariant()}; reach={area.CardinalReach(action).ToString().ToLowerInvariant()}."
                    : gather
                        ? $" Gather approach L1 {GatherApproachDistance(self,goal)}→{GatherApproachDistance(action,goal)}; reach={(L1(action,goal)==1).ToString().ToLowerInvariant()}."
                        : goal!=null?$" {(waypoint==null?"Goal":"Final order")} L1 {L1(self,goal)}→{L1(action,goal)}.":"";
                if(waypoint!=null)facts=$" JEV-selected intermediate ({X(waypoint)},{Y(waypoint)}) L1 {L1(self,waypoint)}→{L1(action,waypoint)}."+facts;
                if(sharedGroup!=null)facts+=$" Shared arrival Chebyshev {C(self,goal)}→{C(action,goal)}; radius={sharedRadius}; endpointWithin={(C(action,goal)<=sharedRadius).ToString().ToLowerInvariant()}. No allocated slot.";
                if((string)goal?["kind"]=="move"&&sharedGroup==null&&action["cells"] is JArray path)
                {
                    int goalIndex=path.ToList().FindIndex(p=>X(p)==X(goal)&&Y(p)==Y(goal));
                    if(goalIndex>=0&&goalIndex<path.Count-1)facts+=$" This sequence passes the exact ordered destination at step {goalIndex+1}, then leaves it for {path.Count-goalIndex-1} more steps.";
                }
                foreach(var enemy in enemies)facts+=$" Enemy {enemy["id"]} Chebyshev {C(self,enemy)}→{C(action,enemy)}.";
                string description=(string)criteria[id];
                if(StandardMoveDescription(action,description))description=CardinalRunDescription(action,self)??description;
                var recent=state["memory"]?["recentPositions"] as JArray;
                if(recent!=null)
                {
                    int visits=recent.Count(p=>p is JArray pair&&pair.Count>=2&&(int)pair[0]==X(action)&&(int)pair[1]==Y(action));
                    facts+=$" Endpoint visited {visits} times in recent position memory.";
                    var previous=recent.LastOrDefault(p=>p is JArray pair&&pair.Count>=2&&((int)pair[0]!=X(self)||(int)pair[1]!=Y(self))) as JArray;
                    if(previous!=null&&(int)previous[0]==X(action)&&(int)previous[1]==Y(action))facts+=" Reverses directly to the position just left.";
                }
                criteria[id]=description+facts;
            }
        }
    }
}
