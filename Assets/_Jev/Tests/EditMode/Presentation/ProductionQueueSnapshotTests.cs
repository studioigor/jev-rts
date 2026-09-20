using Jev.Gameplay.Simulation;
using Jev.Gameplay.UI;
using NUnit.Framework;

namespace Jev.Gameplay.Presentation.Tests
{
    public sealed class ProductionQueueSnapshotTests
    {
        [Test]
        public void QueueTracksRealProductionHeadAndReturnsToIdleAfterSpawns()
        {
            var world = new RtsWorld { Width = 32, Height = 24 };
            world.Human.Gold = 100;
            var hall = world.AddBuilding(FactionId.Human, BuildingKind.TownHall, new Cell(8, 8));
            Assert.That(world.EnqueueTraining(hall.Id, UnitKind.Worker, FactionId.Human).Ok, Is.True);
            Assert.That(world.EnqueueTraining(hall.Id, UnitKind.Worker, FactionId.Human).Ok, Is.True);
            float duration = world.Definitions.Unit(UnitKind.Worker).TrainingSeconds;
            world.Tick(duration / 2);
            var snapshot = Snapshot(world, hall);
            Assert.That(snapshot.HasProduction, Is.True);
            Assert.That(snapshot.ProductionQueue.Length, Is.EqualTo(2));
            Assert.That(snapshot.ProductionProgress, Is.EqualTo(.5f).Within(.001f));
            Assert.That(snapshot.ProductionQueue[0].IconKey, Is.EqualTo("worker-human"));
            Assert.That(hall.TrainingQueue[1].RemainingSeconds, Is.EqualTo(duration));
            Assert.That(snapshot.ProductionProgressLabel, Does.Contain("50%"));
            world.Tick(duration / 2);
            snapshot = Snapshot(world, hall);
            Assert.That(snapshot.ProductionQueue.Length, Is.EqualTo(1));
            Assert.That(snapshot.ProductionProgress, Is.Zero);
            world.Tick(duration);
            snapshot = Snapshot(world, hall);
            Assert.That(snapshot.ProductionQueue, Is.Empty);
            Assert.That(snapshot.ProductionLabel, Is.EqualTo("Очередь свободна"));
            Assert.That(world.Units.Count, Is.EqualTo(2));
        }

        [Test]
        public void SnapshotPreservesQueueOrderAndDoesNotRevealEnemyProduction()
        {
            var world = new RtsWorld();
            var building = new BuildingState { Kind = BuildingKind.Barracks, Faction = FactionId.Undead, Complete = true };
            building.TrainingQueue.Add(new TrainingJob { Kind = UnitKind.Warrior, RemainingSeconds = 8 });
            building.TrainingQueue.Add(new TrainingJob { Kind = UnitKind.Archer, RemainingSeconds = 12 });
            var own = new UiSnapshot();
            ProductionQueueSnapshot.Apply(own, building, world.Definitions, FactionId.Undead);
            Assert.That(own.ProductionQueue[0].Kind, Is.EqualTo(UnitKind.Warrior));
            Assert.That(own.ProductionQueue[1].Kind, Is.EqualTo(UnitKind.Archer));
            Assert.That(own.ProductionQueue[0].IconKey, Is.EqualTo("swordsman-undead"));
            Assert.That(building.TrainingQueue[0].RemainingSeconds, Is.EqualTo(8), "Reading presentation must not advance production.");
            var enemy = new UiSnapshot();
            ProductionQueueSnapshot.Apply(enemy, building, world.Definitions, FactionId.Human);
            Assert.That(enemy.HasProduction, Is.False);
            Assert.That(enemy.ProductionQueue, Is.Empty);
        }

        [Test]
        public void UnfinishedAndReadyButBlockedProductionHaveExplicitStatuses()
        {
            var world = new RtsWorld();
            var building = new BuildingState { Kind = BuildingKind.TownHall, Faction = FactionId.Human };
            var snapshot = Snapshot(world, building);
            Assert.That(snapshot.HasProduction, Is.True);
            Assert.That(snapshot.ProductionLabel, Does.Contain("завершения строительства"));
            building.Complete = true;
            building.TrainingQueue.Add(new TrainingJob { Kind = UnitKind.Worker, RemainingSeconds = 0 });
            snapshot = Snapshot(world, building);
            Assert.That(snapshot.ProductionProgress, Is.EqualTo(1));
            Assert.That(snapshot.ProductionProgressLabel, Is.EqualTo("Ожидает места для юнита"));
        }

        static UiSnapshot Snapshot(RtsWorld world, BuildingState building)
        {
            var snapshot = new UiSnapshot { Phase = MatchPhase.Playing };
            ProductionQueueSnapshot.Apply(snapshot, building, world.Definitions, FactionId.Human);
            return snapshot;
        }
    }
}
