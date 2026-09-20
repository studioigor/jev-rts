using System.Collections.Generic;
using UnityEngine;
using Jev.Gameplay.Simulation;

namespace Jev.Gameplay.Runtime
{
    /// <summary>Individual decision lifecycle. Contains no policy or pathfinder.</summary>
    [DisallowMultipleComponent]
    public sealed class UnitBrain : MonoBehaviour
    {
        public string UnitId;
        [System.NonSerialized] public long Ticket;
        [System.NonSerialized] public bool Pending, Waiting, Moving, DecisionQueued;
        [System.NonSerialized] public float NextDecisionAt, LastDecisionAt = -100, NextSensorAt, WorkUntil, WorkImpactAt;
        [System.NonSerialized] public string Action = "Idle";
        [System.NonSerialized] public ulong SensedSignature;
        public readonly UnitSensingSnapshot Sensor = new UnitSensingSnapshot();
        [System.NonSerialized] public float? Fear;
        [System.NonSerialized] public Vector3 StepFrom, StepTo, Facing = Vector3.forward;
        [System.NonSerialized] public float StepStarted;
        [System.NonSerialized] public int RouteRevision;
        public readonly Queue<Cell> ApprovedRoute = new Queue<Cell>();
        public readonly global::Jev.Gameplay.Jev.JevNavigationIntent Navigation = new global::Jev.Gameplay.Jev.JevNavigationIntent();
        public readonly HashSet<string> WakeReasons = new HashSet<string> { "initial" };
        readonly HashSet<string> visibleEnemies = new HashSet<string>();
        readonly HashSet<string> sensedEnemies = new HashSet<string>();
        public int EnemyContactRevision { get; private set; }
        public void Wake(string reason) { WakeReasons.Add(reason); Waiting = false; }
        public bool ObserveEnemyContacts(IEnumerable<string> ids)
        {
            sensedEnemies.Clear();foreach(var id in ids)sensedEnemies.Add(id);
            bool discovered=false;
            foreach(var id in sensedEnemies)if(!visibleEnemies.Contains(id)){discovered=true;break;}
            visibleEnemies.Clear();visibleEnemies.UnionWith(sensedEnemies);
            if(discovered){EnemyContactRevision++;InterruptForThreat("new_enemy_contact");}
            return discovered;
        }
        public void InterruptForThreat(string reason)
        {
            // Stop only uncommitted travel. The provider chooses whether to fight, flee
            // or continue; preserve an in-flight request so repeated hits cannot starve it.
            ApprovedRoute.Clear();Navigation.Reset();NextDecisionAt=0;Wake(reason);
        }
        public bool PresentationBusy(float now) => Moving || WorkUntil > now;
        public bool CanPrefetch(float now,float stepDuration,float leadSeconds)
        {
            if(ApprovedRoute.Count>0)return false;
            float remaining=Mathf.Max(Moving?StepStarted+stepDuration-now:0,WorkUntil-now);
            return remaining<=Mathf.Max(0,leadSeconds);
        }
        public void ResetForNewOrder(float now)
        {
            // Work is already committed. Preserve the impact plus a brief recovery, but a new
            // order need not wait through the full follow-through. Never interrupt a committed
            // movement interpolation: its authoritative cell is already the destination.
            if(WorkUntil>now)WorkUntil=Mathf.Min(WorkUntil,Mathf.Max(now,WorkImpactAt+.08f));
            Ticket=0;Pending=false;ApprovedRoute.Clear();Navigation.Reset();NextDecisionAt=0;
            Wake("new_order");
        }
    }
}
