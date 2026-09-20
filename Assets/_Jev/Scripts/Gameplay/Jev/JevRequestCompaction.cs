using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Jev
{
    /// <summary>
    /// Final, lossless wire encoding. No observation, choice, review or request is omitted.
    /// Canonical observations stay available to gameplay/diagnostics; only repeated syntax travels less.
    /// </summary>
    public static class JevRequestCompaction
    {
        public const string Version = "literal-action-runs-v1";
        const string Legend = "Wire encoding literal-action-runs-v1. In legalActions, an array [id,runs,endX,endY,criterionEncoded] is exactly a MOVE from self.x/y; E/W changes x by +1/-1, S/N changes y by +1/-1 per step. E3 N2 means three east cells THEN two north cells, including every intermediate cell. Endpoint is the final cell. This is a literal offered sequence, never a suggested route; stop on collision/order change, never reroute. All other action objects keep their usual meaning. criterionEncoded=1 means its criterion starts with Move. and refers to these exact cells. Criterion abbreviations are geometric facts current→endpoint: G=gather approach L1, B=build perimeter L1, in=inside footprint, reach=cardinal work reach; L=original goal L1; F=final order L1; I=(JEV waypoint):L1; S=shared-center Chebyshev, r=arrival radius, within=endpoint in arrival area; E=enemy id:Chebyshev; V=recent endpoint visits; R=returns directly to position just left; P=passes exact original destination at step, leave=remaining steps afterward. Distances ignore obstacles and are NOT action scores. Each selected ID executes exactly once. No choices are removed. A footprint rectangle [minX,minY,maxX,maxY] expands inclusive in y-then-x order.";

        // Each substitution has an exact inverse. Unrecognized/custom prose is kept verbatim.
        static readonly (string Pattern, string Replacement, string Inverse, string Original)[] Facts =
        {
            (@" Gather approach L1 (\d+)→(\d+); reach=(true|false)\.", " G:$1→$2,reach:$3.", @" G:(\d+)→(\d+),reach:(true|false)\.", " Gather approach L1 $1→$2; reach=$3."),
            (@" Build perimeter L1 (\d+)→(\d+); inside=(true|false); reach=(true|false)\.", " B:$1→$2,in:$3,reach:$4.", @" B:(\d+)→(\d+),in:(true|false),reach:(true|false)\.", " Build perimeter L1 $1→$2; inside=$3; reach=$4."),
            (@" Goal L1 (\d+)→(\d+)\.", " L:$1→$2.", @" L:(\d+)→(\d+)\.", " Goal L1 $1→$2."),
            (@" Final order L1 (\d+)→(\d+)\.", " F:$1→$2.", @" F:(\d+)→(\d+)\.", " Final order L1 $1→$2."),
            (@" JEV-selected intermediate \((-?\d+),(-?\d+)\) L1 (\d+)→(\d+)\.", " I:($1,$2):$3→$4.", @" I:\((-?\d+),(-?\d+)\):(\d+)→(\d+)\.", " JEV-selected intermediate ($1,$2) L1 $3→$4."),
            (@" Shared arrival Chebyshev (\d+)→(\d+); radius=(\d+); endpointWithin=(true|false)\. No allocated slot\.", " S:$1→$2,r:$3,within:$4.", @" S:(\d+)→(\d+),r:(\d+),within:(true|false)\.", " Shared arrival Chebyshev $1→$2; radius=$3; endpointWithin=$4. No allocated slot."),
            (@" This sequence passes the exact ordered destination at step (\d+), then leaves it for (\d+) more steps\.", " P:$1,leave:$2.", @" P:(\d+),leave:(\d+)\.", " This sequence passes the exact ordered destination at step $1, then leaves it for $2 more steps."),
            (@" Enemy ([^ ]+) Chebyshev (\d+)→(\d+)\.", " E:$1:$2→$3.", @" E:([^ ]+):(\d+)→(\d+)\.", " Enemy $1 Chebyshev $2→$3."),
            (@" Endpoint visited (\d+) times in recent position memory\.", " V:$1.", @" V:(\d+)\.", " Endpoint visited $1 times in recent position memory."),
            (@" Reverses directly to the position just left\.", " R.", @" R\.", " Reverses directly to the position just left.")
        };

        static readonly Regex[] ForwardPatterns = Facts.Select(f=>new Regex(f.Pattern,RegexOptions.CultureInvariant)).ToArray();
        static readonly Regex[] InversePatterns = Facts.Select(f=>new Regex(f.Inverse,RegexOptions.CultureInvariant)).ToArray();

        public static JObject Compact(JObject request)
        {
            if(request==null)throw new ArgumentNullException(nameof(request));
            var result=(JObject)request.DeepClone();
            if(!(result["state"] is JObject state)||state["wireEncoding"]!=null)return result;
            var self=state["self"] as JObject;
            var criteria=result["questions"]?["action"]?["criteria"] as JObject;
            bool changed=false;
            if(self?["x"]?.Type==JTokenType.Integer&&self["y"]?.Type==JTokenType.Integer&&state["legalActions"] is JArray actions)
            {
                int startX=(int)self["x"],startY=(int)self["y"];
                for(int i=0;i<actions.Count;i++)
                {
                    if(!(actions[i] is JObject action)||!TryRuns(action,startX,startY,out string runs))continue;
                    string id=(string)action["id"],prefix=MovePrefix(startX,startY,(int)action["x"],(int)action["y"],runs);
                    string criterion=(string)criteria?[id];bool encodeCriterion=criterion!=null&&criterion.StartsWith(prefix,StringComparison.Ordinal);
                    if(encodeCriterion)
                    {
                        string tail=criterion.Substring(prefix.Length);
                        // An extension that already uses our abbreviations stays verbatim, avoiding ambiguous expansion.
                        if(InversePatterns.Any(pattern=>pattern.IsMatch(tail)))encodeCriterion=false;
                        else
                        {
                            for(int fact=0;fact<Facts.Length;fact++)tail=ForwardPatterns[fact].Replace(tail,Facts[fact].Replacement);
                            criteria[id]="Move."+tail;
                        }
                    }
                    actions[i]=new JArray(id,runs,(int)action["x"],(int)action["y"],encodeCriterion?1:0);changed=true;
                }
            }
            changed|=CompactFootprints(state);
            if(changed)state["wireEncoding"]=new JObject{["version"]=Version,["legend"]=Legend};
            return result;
        }

        static bool TryRuns(JObject action,int x,int y,out string runs)
        {
            runs=null;
            if((string)action["kind"]!="move"||action["id"]?.Type!=JTokenType.String||action["x"]?.Type!=JTokenType.Integer||action["y"]?.Type!=JTokenType.Integer||
                action.Properties().Any(p=>p.Name!="id"&&p.Name!="kind"&&p.Name!="x"&&p.Name!="y"&&p.Name!="cells")||!(action["cells"] is JArray cells)||cells.Count==0)return false;
            var parts=new List<string>();string direction=null;int count=0;
            foreach(var token in cells)
            {
                if(!(token is JArray pair)||pair.Count!=2||pair[0].Type!=JTokenType.Integer||pair[1].Type!=JTokenType.Integer)return false;
                int nextX=(int)pair[0],nextY=(int)pair[1],dx=nextX-x,dy=nextY-y;
                if(Math.Abs(dx)+Math.Abs(dy)!=1)return false;
                string next=dx==1?"E":dx==-1?"W":dy==1?"S":"N";
                if(next!=direction){if(direction!=null)parts.Add(direction+count);direction=next;count=0;}
                count++;x=nextX;y=nextY;
            }
            if(x!=(int)action["x"]||y!=(int)action["y"])return false;
            parts.Add(direction+count);runs=string.Join(" ",parts);return true;
        }

        static string MovePrefix(int x,int y,int endX,int endY,string runs) =>
            $"Move from ({x},{y}) via {string.Join(" → ",runs.Split(' ').Select(r=>Direction(r[0])+" "+r.Substring(1)))} to ({endX},{endY}); stop if obstructed.";
        static string Direction(char c) => c=='E'?"east":c=='W'?"west":c=='S'?"south":"north";

        static bool CompactFootprints(JToken value)
        {
            bool changed=false;
            if(value is JObject obj)
            {
                foreach(var property in obj.Properties().ToArray())
                {
                    if(property.Name=="footprint"&&property.Value is JArray cells&&TryRectangle(cells,out var bounds))
                    {property.Value=new JObject{["rectangle"]=bounds};changed=true;}
                    else changed|=CompactFootprints(property.Value);
                }
            }
            else if(value is JArray array)foreach(var item in array)changed|=CompactFootprints(item);
            return changed;
        }

        static bool TryRectangle(JArray cells,out JArray bounds)
        {
            bounds=null;if(cells.Count<4)return false;
            if(cells.Any(c=>!(c is JObject p)||p.Count!=2||p["x"]?.Type!=JTokenType.Integer||p["y"]?.Type!=JTokenType.Integer))return false;
            int minX=cells.Min(c=>(int)c["x"]),minY=cells.Min(c=>(int)c["y"]),maxX=cells.Max(c=>(int)c["x"]),maxY=cells.Max(c=>(int)c["y"]);
            if((long)(maxX-minX+1)*(maxY-minY+1)!=cells.Count)return false;
            int i=0;for(int y=minY;y<=maxY;y++)for(int x=minX;x<=maxX;x++,i++)if((int)cells[i]["x"]!=x||(int)cells[i]["y"]!=y)return false;
            bounds=new JArray(minX,minY,maxX,maxY);return true;
        }

        /// <summary>Diagnostic inverse: reconstructs the canonical request exactly; never used to choose/execute actions.</summary>
        public static JObject Expand(JObject request)
        {
            if(request==null)throw new ArgumentNullException(nameof(request));
            var result=(JObject)request.DeepClone();
            var state=result["state"] as JObject;
            if((string)state?["wireEncoding"]?["version"]!=Version)return result;
            var self=state["self"] as JObject;var criteria=result["questions"]?["action"]?["criteria"] as JObject;
            if(state["legalActions"] is JArray actions)
            for(int i=0;i<actions.Count;i++)
            {
                if(!(actions[i] is JArray row)||row.Count!=5||row[0].Type!=JTokenType.String||row[1].Type!=JTokenType.String||row[2].Type!=JTokenType.Integer||row[3].Type!=JTokenType.Integer||row[4].Type!=JTokenType.Integer)continue;
                int x=(int)self["x"],y=(int)self["y"];var cells=new JArray();string runs=(string)row[1],id=(string)row[0];
                foreach(var run in runs.Split(' '))
                {
                    int count=int.Parse(run.Substring(1));char d=run[0];
                    for(int step=0;step<count;step++){x+=d=='E'?1:d=='W'?-1:0;y+=d=='S'?1:d=='N'?-1:0;cells.Add(new JArray(x,y));}
                }
                if((int)row[4]==1)
                {
                    string tail=((string)criteria[id]).Substring("Move.".Length);
                    for(int j=Facts.Length-1;j>=0;j--)tail=InversePatterns[j].Replace(tail,Facts[j].Original);
                    criteria[id]=MovePrefix((int)self["x"],(int)self["y"],x,y,runs)+tail;
                }
                actions[i]=new JObject{["id"]=id,["kind"]="move",["x"]=x,["y"]=y,["cells"]=cells};
            }
            ExpandFootprints(state);state.Remove("wireEncoding");return result;
        }

        static void ExpandFootprints(JToken value)
        {
            if(value is JObject obj)
            foreach(var property in obj.Properties().ToArray())
            {
                if(property.Name=="footprint"&&property.Value is JObject marker&&marker.Count==1&&marker["rectangle"] is JArray r&&r.Count==4)
                {
                    var cells=new JArray();for(int y=(int)r[1];y<=(int)r[3];y++)for(int x=(int)r[0];x<=(int)r[2];x++)cells.Add(new JObject{["x"]=x,["y"]=y});property.Value=cells;
                }
                else ExpandFootprints(property.Value);
            }
            else if(value is JArray array)foreach(var item in array)ExpandFootprints(item);
        }
    }
}
