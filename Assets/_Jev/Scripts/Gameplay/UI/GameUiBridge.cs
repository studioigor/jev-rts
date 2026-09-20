using Jev.Gameplay.Simulation;
using UnityEngine;

namespace Jev.Gameplay.UI
{
    public enum MatchPhase { Setup, Playing, Paused, Victory, Defeat, Draw }

    /// <summary>A presentation-only snapshot. The authoritative economy stays in the simulation.</summary>
    public sealed class UiSnapshot
    {
        public MatchPhase Phase;
        public FactionId PlayerFaction;
        public int Wood, Gold, Used, Cap = 20, Reserved;
        public string SelectedIconKey = "", SelectedName = "", Description = "", QueueLabel = "", Status = "", JevStatus = "";
        public string JevCostLabel = "", JevCostTooltip = "";
        public float Hp, HpMax;
        public bool CanTrainWorker, CanTrainWarrior, CanTrainArcher, CanBuild, HasSelection, HasArmySelection;
        public bool HasProduction;
        public string ProductionLabel = "", ProductionProgressLabel = "";
        public float ProductionProgress;
        public ProductionQueueItem[] ProductionQueue = System.Array.Empty<ProductionQueueItem>();
    }

    public sealed class ProductionQueueItem
    {
        public UnitKind Kind;
        public string IconKey = "";
    }

    public abstract class GameUiBridge : MonoBehaviour
    {
        public abstract UiSnapshot Snapshot { get; }
        public virtual GameRules DisplayRules => null;
        public virtual int DefaultUnitCap => 20;
        public abstract void StartMatch(FactionId faction, int cap);
        public abstract void Train(UnitKind kind);
        public abstract void ChooseBuild(BuildingKind kind);
        public abstract void CommandHold();
        public abstract void CommandStop();
        public abstract void Pause();
        public abstract void Resume();
        public abstract void Restart();
        public abstract void SelectAllArmy();
        public abstract void FocusSelection();
    }
}
