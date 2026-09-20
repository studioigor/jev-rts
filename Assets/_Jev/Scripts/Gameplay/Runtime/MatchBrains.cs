using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Jev.Gameplay.Jev;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.UI;

namespace Jev.Gameplay.Runtime
{
    public sealed partial class MatchController
    {
        public long ApprovedStepCount { get; private set; }
        public long RejectedStepCount { get; private set; }
        readonly Queue<Action> completedDecisions=new Queue<Action>();
        readonly List<ReadyUnitDecision> readyUnitDecisions=new List<ReadyUnitDecision>();
        sealed class ReadyUnitDecision
        {
            public string UnitId;
            public UnitBrain Brain;
            public JevResult Result;
            public JObject Observation;
            public LegalAction Action;
            public Cell Start;
            public int Revision,Session,ContactRevision;
        }
        sealed class DueStep
        {
            public UnitState Unit;
            public UnitBrain Brain;
            public Cell Destination,From;
        }
        void RequestChangedOrder(UnitState unit,UnitBrain brain)
        {
            long previousTicket=brain.Ticket;bool wasPending=brain.Pending;
            brain.ResetForNewOrder(Time.time);
            if(wasPending)Transport.Cancel(previousTicket);
            // First command starts immediately; group preparation shares the frame budget.
            // Every queued review captures fresh state only when its preparation starts.
            RequestUnitDecision(unit,brain,true);
        }
        void UpdateBrains()
        {
            for(int n=0;n<Mathf.Max(1,Settings.DecisionCompletionsPerFrame)&&completedDecisions.Count>0;n++)completedDecisions.Dequeue()();
            DrainDecisionPreparations();
            ApplyReadyUnitDecisions();
            if(World.MatchEnded)return;
            var live=World.Units.Where(u=>u.Alive).ToArray();if(live.Length==0)return;
            nextBrainOffset=(nextBrainOffset+1)%live.Length;
            var dueSteps=new List<DueStep>();
            int sensorsRemaining=Mathf.Max(1,Settings.SensorReviewsPerFrame);
            // Complete presentation for already committed steps before resolving any next step.
            for(int offset=0;offset<live.Length;offset++)
            {
                var unit=live[(offset+nextBrainOffset)%live.Length];if(!brains.TryGetValue(unit.Id,out var brain))continue;
                var view=unitViews[unit.Id];
                // Sense during long approved segments too, so walking past an enemy does
                // not postpone a genuine tactical decision until the entire segment ends.
                if(Time.time>=brain.NextSensorAt&&sensorsRemaining>0)
                {
                    brain.NextSensorAt=Time.time+Settings.SensorInterval;
                    sensorsRemaining--;
                    var sensor=World.SenseUnit(unit.Id,brain.Sensor);
                    if(brain.ObserveEnemyContacts(sensor.VisibleEnemyIds)&&brain.Pending)Transport.Prioritize(brain.Ticket);
                    if(brain.SensedSignature!=sensor.Signature){brain.Wake("observed_change");brain.SensedSignature=sensor.Signature;}
                }
                if(brain.Moving)
                {
                    float progress=Mathf.Clamp01((Time.time-brain.StepStarted)/Settings.StepDuration);
                    var point=Vector3.Lerp(brain.StepFrom,brain.StepTo,progress);point.y=Battlefield.SampleHeight(point);
                    view.SetPose(point,brain.Facing,"Walk");
                    if(progress<1)continue;
                    brain.Moving=false;
                }
                if(brain.ApprovedRoute.Count>0)
                {
                    dueSteps.Add(new DueStep{Unit=unit,Brain=brain,Destination=brain.ApprovedRoute.Peek(),From=unit.Cell});
                    continue;
                }
            }
            if(dueSteps.Count>0)
            {
                // The arbiter receives only provider-approved next cells. A rejected route is stopped, never repaired.
                var outcomes=World.TryExecuteSteps(dueSteps.Select(s=>new StepChoice(s.Unit.Id,s.Destination,s.Brain.RouteRevision)).ToArray());
                for(int index=0;index<dueSteps.Count;index++)
                {
                    var due=dueSteps[index];var brain=due.Brain;var unit=due.Unit;var outcome=outcomes[index];
                    if(!outcome.Ok)
                    {
                        RejectedStepCount++;
                        brain.ApprovedRoute.Clear();brain.Navigation.RecordPhysicalDecision(true);brain.Wake("action_rejected");
                        RecordExecution(unit.Id,"rejected_step",new JObject{["from"]=CellJson(due.From),["requested"]=CellJson(due.Destination),["reason"]=outcome.Reason,["revision"]=brain.RouteRevision});
                        continue;
                    }
                    ApprovedStepCount++;
                    brain.ApprovedRoute.Dequeue();var view=unitViews[unit.Id];
                    brain.StepFrom=view.transform.position;brain.StepTo=Battlefield.ToWorld(unit.Cell);
                    brain.Facing=brain.StepTo-brain.StepFrom;brain.StepStarted=Time.time;brain.Moving=true;brain.Action="Walk";
                    view.SetPose(brain.StepFrom,brain.Facing,"Walk");
                    RecordExecution(unit.Id,"approved_step",new JObject{["from"]=CellJson(due.From),["to"]=CellJson(unit.Cell),["revision"]=unit.OrderRevision});
                }
            }
            for(int offset=0;offset<live.Length;offset++)
            {
                var unit=live[(offset+nextBrainOffset)%live.Length];if(!brains.TryGetValue(unit.Id,out var brain)||brain.ApprovedRoute.Count>0)continue;
                var view=unitViews[unit.Id];
                bool busy=brain.PresentationBusy(Time.time);
                if(!brain.Moving)view.SetPose(Battlefield.ToWorld(unit.Cell),brain.Facing,busy?brain.Action:"Idle");
                if(!busy&&brain.Action!="Idle") {brain.Action="Idle";brain.Wake("action_completed");}
                if(brain.Pending||brain.DecisionQueued)continue;
                if(busy&&!brain.CanPrefetch(Time.time,Settings.StepDuration,Settings.DecisionPrefetchSeconds))continue;
                if(Time.time<brain.NextDecisionAt)continue;
                if(busy)brain.Wake("committed_action_finishing");
                if(brain.WakeReasons.Count==0&&Time.time-brain.LastDecisionAt<Settings.IdleHeartbeat)continue;
                RequestUnitDecision(unit,brain);
            }
        }
        void ApplyReadyUnitDecisions()
        {
            if(readyUnitDecisions.Count==0)return;
            var ready=new List<ReadyUnitDecision>();
            var deferred=new List<ReadyUnitDecision>();
            foreach(var item in readyUnitDecisions)
            {
                if(item.Session!=sessionSerial||World==null||!brains.TryGetValue(item.UnitId,out var brain)||brain!=item.Brain)continue;
                var unit=World.Unit(item.UnitId);
                if(unit==null||!unit.Alive||unit.OrderRevision!=item.Revision||unit.Cell!=item.Start||brain.Ticket!=item.Result.RequestId)
                {
                    RecordDecision(item.UnitId,item.Result,"stale",item.Observation);
                    if(brain.Ticket==item.Result.RequestId){brain.Pending=false;brain.Wake("stale_response");}
                    continue;
                }
                if(brain.PresentationBusy(Time.time)){deferred.Add(item);continue;}
                if(item.Action.Kind==ActionKind.Move&&item.ContactRevision!=brain.EnemyContactRevision)
                {
                    // A newly discovered enemy invalidates travel selected without that
                    // contact. Attacks/work remain legal; repeated damage cannot starve
                    // an in-flight defensive response.
                    RecordDecision(unit.Id,item.Result,"new_contact_requires_movement_review",item.Observation);
                    brain.Pending=false;brain.NextDecisionAt=0;brain.Wake("new_enemy_contact");continue;
                }
                brain.Pending=false;
                ready.Add(item);
            }
            readyUnitDecisions.Clear();readyUnitDecisions.AddRange(deferred);
            if(ready.Count==0)return;
            // All compatible responses available in this frame share physical eligibility and simultaneous damage.
            var outcomes=World.ExecuteBatch(ready.Select(item=>new ActionChoice(item.UnitId,item.Action.Id,item.Revision)).ToArray());
            for(int index=0;index<ready.Count;index++)
            {
                var item=ready[index];var outcome=outcomes[index];var brain=item.Brain;var unit=World.Unit(item.UnitId);var chosen=item.Action;
                RecordDecision(unit.Id,item.Result,outcome.Ok?"applied":outcome.Reason,item.Observation);brain.Fear=item.Result.FearProbability;
                if(!unit.Alive){brain.ApprovedRoute.Clear();continue;}
                if(!outcome.Ok){brain.Navigation.RecordPhysicalDecision(true);brain.Wake("action_rejected");continue;}
                if(chosen.Kind==ActionKind.Move)
                {brain.Navigation.RecordPhysicalDecision();brain.RouteRevision=item.Revision;foreach(var cell in outcome.ApprovedCells)brain.ApprovedRoute.Enqueue(cell);brain.Action="Walk";}
                else if(chosen.Kind==ActionKind.Wait){brain.Waiting=true;brain.Action="Idle";brain.SensedSignature=World.SenseUnit(unit.Id,brain.Sensor).Signature;}
                else
                {
                    float clipDuration=unitViews[unit.Id].ClipDuration("Attack");
                    brain.WorkUntil=Time.time+Mathf.Max(Settings.WorkDuration,clipDuration);brain.Action="Attack";
                    brain.WorkImpactAt=Time.time+clipDuration*(Feedback?Feedback.ContactPhase:.38f);
                    brain.Facing=Battlefield.ToWorld(chosen.Cell)-Battlefield.ToWorld(unit.Cell);
                }
            }
        }
        static JArray CellJson(Cell cell)=>new JArray(cell.X,cell.Y);
        void PrepareUnitDecision(UnitState unit,UnitBrain brain,bool urgent=false,bool skipGatherReview=false)
        {
            var observation=World.Observe(unit.Id,true);
            brain.ObserveEnemyContacts(observation.VisibleUnits.Concat(observation.VisibleBuildings)
                .Where(e=>e.Faction!=unit.Faction&&e.Hp>0).Select(e=>e.Id));
            var legal=Settings.EnableShortSegments?observation.LegalActions:World.LegalActions(unit.Id,false);
            if(legal.Count==0)return;
            var state=observation.ToJObject();
            state["navigationAtlas"]=MatchWorldFactory.NavigationAtlas(Battlefield,unit);
            state["wakeReasons"]=new JArray(brain.WakeReasons);state["idleReviewSeconds"]=Settings.IdleHeartbeat;
            state["movementStepSeconds"]=Settings.StepDuration;
            var gatherPlan=skipGatherReview?null:JevGatherIntent.BuildPlan(state);
            bool tacticalReview=observation.VisibleUnits.Concat(observation.VisibleBuildings).Any(e=>e.Faction!=unit.Faction&&e.Hp>0)
                ||(bool?)state["combat"]?["underAttack"]==true;
            // A tactical review must offer attacks/retreat immediately, not first ask
            // about a transit corner or navigating back to a confirmed empty resource.
            var plan=gatherPlan==null&&!tacticalReview&&!JevGatherIntent.ObservedExhaustion(state)?brain.Navigation.PlanIfNeeded(state):null;
            state["navigationIntent"]=brain.Navigation.ObservationFacts(state);
            // Only detached observation/criteria enter the worker; no live world, brain or Unity APIs.
            var criteria=legal.ToDictionary(a=>a.Id,a=>a.Description);
            Func<JObject> prepareRequest=gatherPlan!=null?()=>JevPromptBuilder.BuildGather(gatherPlan.State,gatherPlan.Criteria):
                plan!=null?()=>JevPromptBuilder.BuildNavigation(plan.State,plan.Criteria):()=>JevPromptBuilder.BuildUnit(state,criteria);
            var start=unit.Cell;int revision=unit.OrderRevision,session=sessionSerial,contactRevision=brain.EnemyContactRevision;
            brain.Pending=true;brain.WakeReasons.Clear();brain.LastDecisionAt=Time.time;brain.NextDecisionAt=Time.time+Settings.DecisionInterval;
            brain.Ticket=Transport.EvaluatePrepared(prepareRequest,result=>completedDecisions.Enqueue(()=>
            {
                if(session!=sessionSerial||World==null||!brains.TryGetValue(unit.Id,out var current)||current!=brain)return;
                if(result.RequestId!=brain.Ticket)return;
                if(!unit.Alive||unit.OrderRevision!=revision||unit.Cell!=start)
                {brain.Pending=false;RecordDecision(unit.Id,result,"stale",state);brain.Wake("stale_response");return;}
                if(!result.Success)
                {brain.Pending=false;World.RecordDecisionFailure(unit.Id,result.ErrorCode);RecordDecision(unit.Id,result,"error",state);brain.Wake("decision_error");brain.NextDecisionAt=Time.time+Mathf.Max(Settings.DecisionInterval,3);return;}
                if(gatherPlan!=null)
                {
                    brain.Pending=false;
                    bool accepted=JevGatherIntent.TryResolveChoice(gatherPlan,result.Choice,World.Observe(unit.Id,false).ToJObject(),out var target,out string reason);
                    RecordDecision(unit.Id,result,accepted?target==null?"gather_continuation_deferred":"gather_target_selected":reason,gatherPlan.State);
                    if(!accepted){brain.Wake("gather_target_rejected");return;}
                    if(target!=null)
                    {
                        var kind=(string)target["kind"]=="wood"?ResourceKind.Wood:ResourceKind.Gold;
                        // Only the exact target selected by this genuine response changes
                        // the assignment. The next provider call chooses physical work.
                        IssueOrder(unit,Order.Gather((string)target["id"],new Cell((int)target["x"],(int)target["y"]),kind));
                    }
                    else RequestUnitDecision(unit,brain,urgent,true);
                    return;
                }
                if(plan!=null)
                {
                    brain.Pending=false;
                    bool accepted=brain.Navigation.ApplyChoice(plan,result.Choice,World.Observe(unit.Id,false).ToJObject(),result.RequestId,out string reason);
                    RecordDecision(unit.Id,result,accepted?"navigation_intent_selected":reason,plan.State);
                    brain.Wake(accepted?"navigation_intent_selected":"navigation_intent_rejected");
                    // An intention is not a physical action. Continue the same decision cycle
                    // immediately instead of paying a second artificial decision interval.
                    if(accepted)RequestUnitDecision(unit,brain,urgent);
                    return;
                }
                var chosen=legal.FirstOrDefault(a=>a.Id==result.Choice);
                if(chosen==null){brain.Pending=false;brain.Wake("illegal_answer");return;}
                // Keep Pending while the real response waits for presentation, preventing
                // duplicate requests and overlapping work/step execution.
                readyUnitDecisions.Add(new ReadyUnitDecision{UnitId=unit.Id,Brain=brain,Result=result,Observation=state,Action=chosen,Start=start,Revision=revision,Session=session,ContactRevision=contactRevision});
            }),urgent);
        }
        void RecordDecision(string actor,JevResult result,string disposition,JObject observation)
        {
            AppendDecision(new JObject{["session"]=SessionId,["time"]=World.TimeSeconds,["actor"]=actor,["requestId"]=result.RequestId,
                ["choice"]=result.Choice,["success"]=result.Success,["disposition"]=disposition,["fear"]=result.FearProbability,["tokens"]=result.InputTokens,
                ["observation"]=observation,["response"]=result.Raw});
        }
        void RecordExecution(string actor,string action,JObject data)
        {AppendDecision(new JObject{["time"]=World.TimeSeconds,["actor"]=actor,["execution"]=action,["data"]=data});}
        void AppendDecision(JObject entry)
        {
            entry["sequence"]=++decisionSequence;
            if(decisions.Count>=MaxRetainedDecisions)decisions.RemoveRange(0,decisions.Count-MaxRetainedDecisions+1);
            decisions.Add(entry);
        }
        public string SaveSessionReport()
        {
            if(World==null||string.IsNullOrEmpty(SessionId))return null;
            try
            {
                string folder=Path.Combine(JevCredentials.LocalSettingsDirectory,"Sessions",SessionId);Directory.CreateDirectory(folder);
                string path=Path.Combine(folder,"gameplay.json");
                File.WriteAllText(path,new JObject{["session"]=SessionId,["phase"]=Phase.ToString(),["simulatedSeconds"]=World.TimeSeconds,["decisions"]=new JArray(decisions),
                    ["decisionWindow"]=new JObject{["capacity"]=MaxRetainedDecisions,["totalEntries"]=decisionSequence,["retainedEntries"]=decisions.Count,
                        ["firstSequence"]=decisions.Count==0?0:(long)decisions[0]["sequence"],["lastSequence"]=decisionSequence,["omittedEntries"]=decisionSequence-decisions.Count},
                    ["events"]=JArray.FromObject(World.Events),
                    ["eventWindow"]=new JObject{["capacity"]=RtsWorld.MaxRetainedEvents,["totalEntries"]=World.TotalEvents,["retainedEntries"]=World.Events.Count,
                        ["firstSequence"]=World.Events.Count==0?0:World.Events[0].Sequence,["lastSequence"]=World.TotalEvents,["omittedEntries"]=World.TotalEvents-World.Events.Count},
                    ["jevCost"]=new JObject{["estimatedUsd"]=Transport.MatchCost.EstimatedUsd,["inputTokens"]=Transport.MatchCost.InputTokens,
                        ["outputTokens"]=Transport.MatchCost.OutputTokens,["attempts"]=Transport.MatchCost.Attempts,["missingUsageAttempts"]=Transport.MatchCost.MissingUsageAttempts,
                        ["label"]=Transport.MatchCost.Label,["isInvoice"]=false},
                    ["transportLedger"]=Transport.LedgerPath}.ToString(Formatting.None));return path;
            }
            catch(Exception)
            {
                if(!reportWarningReported){reportWarningReported=true;Debug.LogWarning("The gameplay session report could not be saved in LocalSettings/Sessions.",this);}
                return null;
            }
        }
    }
}
