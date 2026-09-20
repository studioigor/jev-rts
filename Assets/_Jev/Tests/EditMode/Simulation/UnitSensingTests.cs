using System;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Simulation.Tests
{
    public sealed class UnitSensingTests
    {
        static RtsWorld World() => new RtsWorld
        {
            Width = 80, Height = 68, VictoryEnabled = false,
            Definitions = new GameRules { CircularUnitVision = true, PublicLocalTerrain = true, PublicTerrainRadius = 8 }
        };

        [TestCase(false)]
        [TestCase(true)]
        public void ContactsMatchFullObservationWithoutMutatingMemoryOrChoosingActions(bool circular)
        {
            var world = World(); world.Definitions.CircularUnitVision = circular;
            var self = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(10, 10), "self");
            world.AddUnit(FactionId.Human, UnitKind.Warrior, new Cell(10, 11), "friend");
            world.AddUnit(FactionId.Undead, UnitKind.Warrior, new Cell(12, 10), "enemy");
            world.AddUnit(FactionId.Undead, UnitKind.Warrior, new Cell(50, 10), "hidden");
            world.AddBuilding(FactionId.Undead, BuildingKind.House, new Cell(13, 10), true, "enemy-house");
            world.Blocked.Add(new Cell(11, 10));
            var memory = self.Memory;
            var originalEvents = world.TotalEvents;
            var result = world.SenseUnit(self.Id);
            var observation = world.Observe(self.Id, false);
            var contacts = observation.VisibleUnits.Concat(observation.VisibleBuildings)
                .Where(e => e.Faction != self.Faction && e.Hp > 0).Select(e => e.Id);
            Assert.That(result.VisibleEnemyIds, Is.EquivalentTo(contacts));
            Assert.That(result.VisibleEnemyIds, Does.Not.Contain("hidden"));
            Assert.That(self.Memory, Is.SameAs(memory));
            Assert.That(self.Memory.Terrain, Is.Empty);
            Assert.That(self.LastAction, Is.Null);
            Assert.That(world.TotalEvents, Is.EqualTo(originalEvents));
        }

        [Test]
        public void StableWaitIgnoresClockAndRemoteEconomyButSeesPersonalChanges()
        {
            var world = World();
            var self = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(10, 10), "self");
            var tree = world.AddResource(ResourceKind.Wood, new Cell(11, 10), 100, "tree");
            var hidden = world.AddResource(ResourceKind.Gold, new Cell(60, 10), 10000, "hidden");
            world.SetOrder(self.Id, Order.Gather(tree.Id, tree.Cell));
            var sensor = new UnitSensingSnapshot();
            ulong before = world.SenseUnit(self.Id, sensor).Signature;
            world.TimeSeconds += 1; world.Human.Gold += 10; world.Human.Wood += 5; world.Human.Population.Reserved++;
            hidden.Remaining -= 50;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.EqualTo(before));
            tree.Remaining -= 5;
            ulong lessWood = world.SenseUnit(self.Id, sensor).Signature;
            Assert.That(lessWood, Is.Not.EqualTo(before));
            tree.Remaining = 0;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(lessWood));
            before = sensor.Signature; self.Hp--;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
            before = sensor.Signature; world.Human.Traits.FearSusceptibility = .4f;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
        }

        [Test]
        public void OnlyAnUnpaidFoundationDependsOnStocksIncludingRememberedAndDestroyedSites()
        {
            var world = World();
            var self = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(10, 10), "self");
            world.SetOrder(self.Id, Order.Build(BuildingKind.House, new Cell(11, 10)));
            var sensor = new UnitSensingSnapshot();
            ulong before = world.SenseUnit(self.Id, sensor).Signature;
            world.Human.Wood++;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
            var house = world.AddBuilding(FactionId.Human, BuildingKind.House, new Cell(11, 10), false, "house");
            world.Observe(self.Id);
            before = world.SenseUnit(self.Id, sensor).Signature; world.Human.Gold++;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.EqualTo(before));
            self.Cell = new Cell(40, 40);
            before = world.SenseUnit(self.Id, sensor).Signature; world.Human.Wood++;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.EqualTo(before), "A personally remembered paid site must not be paid again.");
            house.Hp = 0; self.Cell = new Cell(10, 10);
            before = world.SenseUnit(self.Id, sensor).Signature; world.Human.Wood++;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before), "A visible empty site supersedes stale building memory.");
        }

        [Test]
        public void MovementOccupancyAndAttackGeometryWakeWithoutBuildingTheActionVocabulary()
        {
            var world = World();
            var self = world.AddUnit(FactionId.Human, UnitKind.Warrior, new Cell(10, 10), "self");
            var other = world.AddUnit(FactionId.Undead, UnitKind.Warrior, new Cell(15, 11), "enemy");
            var sensor = new UnitSensingSnapshot();
            ulong before = world.SenseUnit(self.Id, sensor).Signature;
            Assert.That(sensor.VisibleEnemyIds, Is.Empty);
            other.Cell = new Cell(11, 10);
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
            Assert.That(sensor.VisibleEnemyIds, Is.EqualTo(new[] { "enemy" }));
            before = sensor.Signature; other.Cell = new Cell(12, 10);
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
            before = sensor.Signature; world.Blocked.Add(new Cell(10, 12));
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
            before = sensor.Signature; self.Range++;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
            before = sensor.Signature; other.Hp = 0;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
            Assert.That(sensor.VisibleEnemyIds, Is.Empty);
        }

        [Test]
        public void DamageWindowMatchesCombatObservationWithoutRevealingHiddenAttacker()
        {
            var world = World();
            var self = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(10, 10), "self"); self.Vision = 1;
            var other = world.AddUnit(FactionId.Undead, UnitKind.Archer, new Cell(13, 10), "hidden-enemy");
            var sensor = new UnitSensingSnapshot();
            ulong before = world.SenseUnit(self.Id, sensor).Signature;
            Assert.That(world.Execute(other.Id, "attack_self", 0).Ok, Is.True);
            world.SenseUnit(self.Id, sensor);
            Assert.That(sensor.Signature, Is.Not.EqualTo(before));
            Assert.That(sensor.VisibleEnemyIds, Is.Empty);
            Assert.That(sensor.UnderAttack, Is.True);
            var full = world.Observe(self.Id, false).Combat;
            Assert.That(sensor.ReceivedDamage, Is.EqualTo((int)full["receivedDamage"]));
            Assert.That(sensor.ReceivedHitCount, Is.EqualTo((int)full["receivedHitCount"]));
            before = sensor.Signature; world.TimeSeconds += 1;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.EqualTo(before));
            world.TimeSeconds += 6;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
            Assert.That(sensor.UnderAttack, Is.False);
        }

        [Test]
        public void FortyUnitRepeatedSensorPassesAllocateNoDecisionDocuments()
        {
            var world = World();
            var units = new UnitState[40]; var sensors = new UnitSensingSnapshot[40];
            for (int i = 0; i < units.Length; i++)
            {
                units[i] = world.AddUnit(i < 20 ? FactionId.Human : FactionId.Undead, UnitKind.Warrior,
                    new Cell(20 + i % 10 * 2, 20 + i / 10 * 2), "unit-" + i);
                units[i].Vision = 14; sensors[i] = new UnitSensingSnapshot();
            }
            for (int y = 0; y < 10; y++) for (int x = 0; x < 80; x++)
                world.AddResource(ResourceKind.Wood, new Cell(x, y), 100);
            for (int i = 0; i < units.Length; i++) world.SenseUnit(units[i].Id, sensors[i]);
            var watch = Stopwatch.StartNew();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int pass = 0; pass < 10; pass++)
                for (int i = 0; i < units.Length; i++) world.SenseUnit(units[i].Id, sensors[i]);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            watch.Stop();
            Console.WriteLine($"40 units × 10 sensor passes: {watch.Elapsed.TotalMilliseconds:F2} ms; {bytes} allocated bytes.");
            Assert.That(bytes, Is.LessThan(1024), "Steady circular sensing must not construct observations, tile lists, actions, JSON, or memory copies.");
            Assert.That(units.All(u => u.Memory.Terrain.Count == 0 && u.LastAction == null), Is.True);
        }
    }
}
