using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Jev.Gameplay.Simulation;

namespace Jev.Gameplay.Runtime
{
    public sealed partial class MatchController
    {
        sealed class QueuedUnitReview
        {
            public UnitState Unit;
            public UnitBrain Brain;
            public bool Urgent, SkipGatherReview;
        }
        readonly List<QueuedUnitReview> queuedReviews = new List<QueuedUnitReview>();
        int preparationFrame = -1, preparationsThisFrame;
        double preparationMilliseconds;
        public int QueuedUnitReviews => queuedReviews.Count;

        void RefreshPreparationBudget()
        {
            if(preparationFrame==Time.frameCount)return;
            preparationFrame=Time.frameCount;preparationsThisFrame=0;preparationMilliseconds=0;
        }
        bool HasPreparationBudget()
        {
            RefreshPreparationBudget();
            return preparationsThisFrame<Mathf.Max(1,Settings.DecisionPreparationsPerFrame)
                &&(preparationsThisFrame==0||preparationMilliseconds<Mathf.Max(.1f,Settings.DecisionPreparationBudgetMilliseconds));
        }
        void RequestUnitDecision(UnitState unit,UnitBrain brain,bool urgent=false,bool skipGatherReview=false)
        {
            urgent|=brain.WakeReasons.Contains("damage")||brain.WakeReasons.Contains("new_enemy_contact");
            // Queue only identity and intent to review, never an observation that can become
            // stale while twenty selected units wait for their turn on the main thread.
            for(int i=queuedReviews.Count-1;i>=0;i--)
                if(queuedReviews[i].Brain==brain)queuedReviews.RemoveAt(i);
            var item=new QueuedUnitReview{Unit=unit,Brain=brain,Urgent=urgent,SkipGatherReview=skipGatherReview};
            brain.DecisionQueued=true;
            int firstRoutine=urgent?queuedReviews.FindIndex(other=>!other.Urgent):-1;
            if(firstRoutine<0)queuedReviews.Add(item);else queuedReviews.Insert(firstRoutine,item);
            DrainDecisionPreparations();
        }
        void DrainDecisionPreparations()
        {
            while(queuedReviews.Count>0&&HasPreparationBudget())
            {
                var item=queuedReviews[0];queuedReviews.RemoveAt(0);
                if(item.Brain)item.Brain.DecisionQueued=false;
                if(World==null||World.MatchEnded||!item.Brain||!item.Unit.Alive
                    ||!brains.TryGetValue(item.Unit.Id,out var current)||current!=item.Brain||item.Brain.Pending)continue;
                long started=Stopwatch.GetTimestamp();preparationsThisFrame++;
                try{PrepareUnitDecision(item.Unit,item.Brain,item.Urgent,item.SkipGatherReview);}
                finally{preparationMilliseconds+=(Stopwatch.GetTimestamp()-started)*1000d/Stopwatch.Frequency;}
            }
        }
        void ClearDecisionPreparations()
        {
            foreach(var item in queuedReviews)if(item.Brain)item.Brain.DecisionQueued=false;
            queuedReviews.Clear();preparationFrame=-1;preparationsThisFrame=0;preparationMilliseconds=0;
        }
    }
}
