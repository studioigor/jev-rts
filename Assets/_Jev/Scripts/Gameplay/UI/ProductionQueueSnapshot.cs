using Jev.Gameplay.Simulation;
using UnityEngine;

namespace Jev.Gameplay.UI
{
    /// <summary>Projects the real FIFO production queue into display data without advancing it.</summary>
    public static class ProductionQueueSnapshot
    {
        public static void Apply(UiSnapshot snapshot, BuildingState building, GameRules rules, FactionId player)
        {
            snapshot.HasProduction = building.Faction == player && rules.Building(building.Kind).TrainableUnits.Length > 0;
            if (!snapshot.HasProduction) return;
            if (!building.Complete)
            {
                snapshot.ProductionLabel = "Найм после завершения строительства";
                snapshot.QueueLabel = "Производство ещё недоступно";
                return;
            }
            int count = building.TrainingQueue.Count;
            if (count == 0)
            {
                snapshot.ProductionLabel = "Очередь свободна";
                snapshot.ProductionProgressLabel = "Выберите юнита для найма";
                snapshot.QueueLabel = "Готово к найму";
                return;
            }
            snapshot.ProductionQueue = new ProductionQueueItem[count];
            string suffix = building.Faction == FactionId.Human ? "-human" : "-undead";
            for (int i = 0; i < count; i++)
            {
                var kind = building.TrainingQueue[i].Kind;
                snapshot.ProductionQueue[i] = new ProductionQueueItem { Kind = kind,
                    IconKey = (kind == UnitKind.Warrior ? "swordsman" : kind.ToString().ToLowerInvariant()) + suffix };
            }
            var first = building.TrainingQueue[0];
            float duration = rules.Unit(first.Kind).TrainingSeconds;
            snapshot.ProductionProgress = duration > 0 ? Mathf.Clamp01(1 - (float)first.RemainingSeconds / duration) : 1;
            snapshot.ProductionLabel = "Найм: " + UnitName(first.Kind);
            snapshot.QueueLabel = "В очереди: " + count + (count > 1 ? " · " + (count - 1) + " ожидают" : "");
            snapshot.ProductionProgressLabel = first.RemainingSeconds <= .001 ? "Ожидает места для юнита"
                : Mathf.CeilToInt((float)first.RemainingSeconds) + " с  ·  " + Mathf.FloorToInt(snapshot.ProductionProgress * 100) + "%";
            if (snapshot.Phase == MatchPhase.Paused) snapshot.ProductionProgressLabel = "Пауза · " + snapshot.ProductionProgressLabel;
        }

        static string UnitName(UnitKind kind) => kind == UnitKind.Worker ? "Рабочий" : kind == UnitKind.Warrior ? "Мечник" : "Лучник";
    }
}
