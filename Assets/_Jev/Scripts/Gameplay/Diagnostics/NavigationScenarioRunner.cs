using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jev.Gameplay.Jev;
using Jev.Gameplay.Runtime;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Jev.Gameplay.Diagnostics
{
    /// <summary>Measures an ordinary shared group order. It never chooses movement, passage priority or stopping actions.</summary>
    public sealed class NavigationScenarioRunner : MonoBehaviour
    {
        public MatchController Match;
        public NavigationScenarioDefinition Scenario;
        public bool AutoStart=true;
        public float StartDelay=2;
        public string State { get; private set; } = "ready";
        public string ReportPath { get; private set; }
        public int Arrived { get; private set; }
        public int Crossed => crossed.Count;
        public double StableSeconds { get; private set; }
        readonly HashSet<string> crossed = new HashSet<string>();
        readonly Dictionary<string,Cell> previous = new Dictionary<string,Cell>();
        readonly JArray samples = new JArray();
        readonly JArray crossings = new JArray();
        float startAt, nextSample;
        double stableAt = -1;
        bool wasBackground;
        string activeSession;
        int moves, departuresAfterArrival;
        readonly HashSet<string> reached = new HashSet<string>();

        void Awake() { wasBackground=Application.runInBackground; }
        void Start() { startAt=Time.unscaledTime+StartDelay; }
        public bool Begin()
        {
            if(State=="running")return false;
            crossed.Clear();previous.Clear();samples.Clear();crossings.Clear();reached.Clear();
            stableAt=-1;StableSeconds=0;Arrived=0;moves=0;departuresAfterArrival=0;
            Application.runInBackground=true;
            try
            {
                if(!Match.StartScenario(Scenario)){State="missing_configuration";Application.runInBackground=wasBackground;return false;}
            }
            catch { Application.runInBackground=wasBackground;throw; }
            activeSession=Match.SessionId;
            State="running";nextSample=0;ReportPath=null;
            foreach(var u in Match.World.Units)previous[u.Id]=u.Cell;
            return true;
        }
        void Update()
        {
            if(State=="ready"&&AutoStart&&Time.unscaledTime>=startAt)Begin();
            if(State!="running")return;
            var w=Match.World;
            if(w==null||Match.SessionId!=activeSession){Finish("interrupted","Match was cleared or replaced.");return;}
            try { w.AssertInvariants(); }
            catch(Exception e) { Finish("failed_invariant",e.Message);return; }
            bool changed=false;
            foreach(var u in w.Units)
            {
                if(previous[u.Id]!=u.Cell){changed=true;moves++;previous[u.Id]=u.Cell;}
                if(u.Cell.X>Scenario.PassageMaxX&&crossed.Add(u.Id))crossings.Add(new JObject{["unit"]=u.Id,["time"]=w.TimeSeconds});
                bool inside=Cell.Chebyshev(u.Cell,Scenario.Target)<=u.Order.GroupArrivalRadius;
                if(inside)reached.Add(u.Id);
                else if(reached.Remove(u.Id))departuresAfterArrival++;
            }
            Arrived=w.Units.Count(u=>Cell.Chebyshev(u.Cell,Scenario.Target)<=u.Order.GroupArrivalRadius);
            bool allWaiting=w.Units.All(u=>u.LastAction!=null&&u.LastAction.Ok&&u.LastAction.Kind==ActionKind.Wait);
            if(changed||Arrived!=Scenario.Starts.Count||!allWaiting)stableAt=-1;
            else if(stableAt<0)stableAt=w.TimeSeconds;
            StableSeconds=stableAt<0?0:w.TimeSeconds-stableAt;
            if(Time.unscaledTime>=nextSample)
            {
                nextSample=Time.unscaledTime+.5f;
                samples.Add(new JObject{["time"]=w.TimeSeconds,["arrived"]=Arrived,["crossed"]=Crossed,
                    ["units"]=new JArray(w.Units.Select(u=>new JArray(u.Id,u.Cell.X,u.Cell.Y,u.LastAction?.Kind.ToString())))});
            }
            if(Match.Transport.IsBlocked){Finish("blocked",Match.Transport.BlockingErrorMessage);return;}
            if(Crossed==Scenario.Starts.Count&&StableSeconds>=Scenario.RequiredHoldSeconds)Finish("passed",null);
            else if(w.TimeSeconds>=Scenario.TimeoutSeconds)Finish("timeout",null);
        }
        public JObject Status() => new JObject { ["state"]=State,["session"]=activeSession,
            ["time"]=Match?.World?.TimeSeconds??0,["crossed"]=Crossed,["arrived"]=Arrived,["total"]=Scenario?Scenario.Starts.Count:0,
            ["stableSeconds"]=StableSeconds,["moves"]=moves,["departuresAfterArrival"]=departuresAfterArrival,
            ["approvedPhysicalSteps"]=Match?.ApprovedStepCount??0,["rejectedPhysicalSteps"]=Match?.RejectedStepCount??0,
            ["transportErrors"]=Match?.Transport?.LastError,["report"]=ReportPath };
        public void Finish(string state,string error)
        {
            if(State!="running")return;
            State=state;
            var folder=Path.Combine(JevCredentials.LocalSettingsDirectory,"Sessions",activeSession);
            Directory.CreateDirectory(folder);ReportPath=Path.Combine(folder,"chokepoint.json");
            var summary=Status();summary["promptVersion"]=JevPromptBuilder.PromptVersion;
            summary["methodology"]="One shared target, normal MatchController and genuine JEV unit decisions. No assigned destination slots, scripted queue, pathfinder, teleport or arrival auto-stop. Live decisions continue after success.";
            summary["target"]=new JArray(Scenario.Target.X,Scenario.Target.Y);
            summary["error"]=error;summary["crossings"]=crossings;summary["samples"]=samples;
            summary["gameplayReport"]=Match.SessionId==activeSession?Match.SaveSessionReport():null;summary["transportLedger"]=Match.Transport.LedgerPath;
            File.WriteAllText(ReportPath,summary.ToString(Newtonsoft.Json.Formatting.None));
        }
        void OnDestroy()
        {
            if(State=="running")Finish("interrupted",null);
            Application.runInBackground=wasBackground;
        }
    }
}
