using System.Collections.Generic;
using System.Linq;
using Jev.Gameplay.Authoring;
using Jev.Gameplay.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Presentation.Tests
{
    public sealed class FogOfWarSightTests
    {
        static RtsWorld AuthoredWorld() => new RtsWorld
        {
            Width = 16, Height = 12, SightBlocked = new HashSet<Cell>()
        };

        [Test]
        public void ImpassableRiverDoesNotHideTheOppositeBankOrAllowWalkingOnWater()
        {
            var world = AuthoredWorld();
            for (int y = 0; y < world.Height; y++) world.Blocked.Add(new Cell(4, y));
            var observer = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(2, 3), "observer");
            var enemy = world.AddUnit(FactionId.Undead, UnitKind.Warrior, new Cell(6, 3), "enemy");

            Assert.That(world.VisibleCellsForUnit(observer.Id), Does.Contain(enemy.Cell));
            Assert.That(world.Observe(observer.Id).VisibleUnits.Select(u => u.Id), Does.Contain(enemy.Id));
            Assert.That(world.IsFree(new Cell(4, 3)), Is.False, "Sight must not remove movement restrictions.");
        }

        [Test]
        public void AuthoredOpaqueWallStillHidesEnemiesAndBlocksDiagonalCorners()
        {
            var world = AuthoredWorld();
            var observer = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(2, 3));
            var enemy = world.AddUnit(FactionId.Undead, UnitKind.Warrior, new Cell(6, 3));
            world.SightBlocked.Add(new Cell(3, 3));
            world.Blocked.Add(new Cell(3, 3));

            Assert.That(world.VisibleCellsForUnit(observer.Id).Contains(enemy.Cell), Is.False);
            Assert.That(world.Observe(observer.Id).VisibleUnits, Is.Empty);
            Assert.That(world.HasLineOfSight(observer.Cell, new Cell(3, 4)), Is.False);
            Assert.That(world.VisibleCellsForUnit(observer.Id), Does.Contain(new Cell(3, 3)), "The wall itself remains visible.");
        }

        [Test]
        public void CuttingDownTreeOpensSightWithoutRebakingTheBattlefield()
        {
            var world = AuthoredWorld();
            var observer = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(2, 3));
            var enemy = world.AddUnit(FactionId.Undead, UnitKind.Warrior, new Cell(6, 3));
            var tree = world.AddResource(ResourceKind.Wood, new Cell(4, 3), 100);

            Assert.That(world.VisibleCellsForUnit(observer.Id).Contains(enemy.Cell), Is.False);
            Assert.That(world.Observe(observer.Id).VisibleResources.Select(r => r.Id), Does.Contain(tree.Id));
            tree.Remaining = 0;
            Assert.That(world.VisibleCellsForUnit(observer.Id), Does.Contain(enemy.Cell));
            Assert.That(world.IsFree(tree.Cell), Is.True);
        }

        [Test]
        public void StandingBuildingOccludesButDestroyedBuildingDoesNot()
        {
            var world = AuthoredWorld();
            var observer = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(2, 3));
            var enemy = world.AddUnit(FactionId.Undead, UnitKind.Warrior, new Cell(6, 3));
            var building = world.AddBuilding(FactionId.Human, BuildingKind.House, new Cell(4, 3));

            Assert.That(world.VisibleCellsForUnit(observer.Id).Contains(enemy.Cell), Is.False);
            Assert.That(world.VisibleCellsForBuilding(building.Id), Does.Contain(enemy.Cell), "A structure must not occlude its own perimeter sensors.");
            building.Hp = 0;
            Assert.That(world.VisibleCellsForUnit(observer.Id), Does.Contain(enemy.Cell));
        }

        [Test]
        public void AuthoredEmptySightMaskIsDistinctFromLegacyMovementWalls()
        {
            var field = ScriptableObject.CreateInstance<BattlefieldDefinition>();
            try
            {
                field.Blocked.Add(new Cell(3, 3));
                Assert.That(field.CreateSightBlockers(), Is.Null);
                field.HasAuthoredSightBlockers = true;
                Assert.That(field.CreateSightBlockers(), Is.Empty);
                field.SightBlocked.Add(new Cell(5, 5));
                var mask = field.CreateSightBlockers();
                mask.Clear();
                Assert.That(field.SightBlocked.Count, Is.EqualTo(1), "Runtime changes must not mutate the authoring asset.");
            }
            finally { Object.DestroyImmediate(field); }
        }

        [Test]
        public void LegacySmallWorldWallsContinueToBlockSight()
        {
            var world = new RtsWorld { Width = 12, Height = 12 };
            world.Blocked.Add(new Cell(3, 3));
            Assert.That(world.HasLineOfSight(new Cell(2, 3), new Cell(6, 3)), Is.False);
        }

        [Test]
        public void CircularSensorsRevealEveryCellInRadiusBehindTreesWallsAndBuildings()
        {
            var world = AuthoredWorld();
            world.Definitions.CircularUnitVision = true;
            var observer = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(7, 6));
            world.AddResource(ResourceKind.Wood, new Cell(8, 6), 100);
            world.AddBuilding(FactionId.Human, BuildingKind.House, new Cell(7, 7));
            world.Blocked.Add(new Cell(6, 6)); world.SightBlocked.Add(new Cell(6, 6));
            var sight = world.VisibleCellsForUnit(observer.Id);

            for (int y = 0; y < world.Height; y++) for (int x = 0; x < world.Width; x++)
            {
                var point = new Cell(x, y);
                int dx = x - observer.Cell.X, dy = y - observer.Cell.Y;
                bool inside = dx * dx + dy * dy <= observer.Vision * observer.Vision;
                Assert.That(sight.Contains(point), Is.EqualTo(inside), "Circular reveal at " + point);
                Assert.That(world.CanSee(observer, point), Is.EqualTo(inside));
            }
            var observation = world.Observe(observer.Id);
            Assert.That(observation.Rules.CircularUnitVision, Is.True);
            Assert.That(observation.Memory.Terrain.Select(t => t.Cell), Is.EquivalentTo(sight));
        }

        [TestCase("wall")]
        [TestCase("tree")]
        [TestCase("building")]
        public void CircularVisionSeesEnemyButCannotAttackThroughPhysicalObstacle(string blocker)
        {
            var world = AuthoredWorld();
            world.Definitions.CircularUnitVision = true;
            var observer = world.AddUnit(FactionId.Human, UnitKind.Archer, new Cell(2, 3));
            var enemy = world.AddUnit(FactionId.Undead, UnitKind.Warrior, new Cell(4, 3));
            ResourceState tree = null; BuildingState structure = null;
            var blockedCell = new Cell(3, 3);
            if (blocker == "wall") { world.Blocked.Add(blockedCell); world.SightBlocked.Add(blockedCell); }
            else if (blocker == "tree") tree = world.AddResource(ResourceKind.Wood, blockedCell, 100);
            else structure = world.AddBuilding(FactionId.Human, BuildingKind.House, blockedCell);
            var observation = world.Observe(observer.Id);

            Assert.That(observation.VisibleUnits.Select(u => u.Id), Does.Contain(enemy.Id));
            Assert.That(observation.LegalActions.Any(a => a.TargetId == enemy.Id && a.Kind == ActionKind.Attack), Is.False);
            int hp = enemy.Hp;
            Assert.That(world.Execute(observer.Id, "attack_" + enemy.Id, observer.OrderRevision).Ok, Is.False);
            Assert.That(enemy.Hp, Is.EqualTo(hp), "An invented action ID cannot bypass physical attack checks.");

            world.Blocked.Remove(blockedCell); world.SightBlocked.Remove(blockedCell);
            if (tree != null) tree.Remaining = 0;
            if (structure != null) structure.Hp = 0;
            Assert.That(world.Observe(observer.Id).LegalActions.Any(a => a.TargetId == enemy.Id && a.Kind == ActionKind.Attack), Is.True);
        }

        [Test]
        public void CircularVisionCannotAttackFarBuildingCellWhenOnlyAnotherFootprintCellIsInRange()
        {
            var world = AuthoredWorld();
            world.Definitions.CircularUnitVision = true;
            world.Definitions.Building(BuildingKind.House).FootprintSizeX = 3;
            var observer = world.AddUnit(FactionId.Human, UnitKind.Archer, new Cell(2, 3));
            var target = world.AddBuilding(FactionId.Undead, BuildingKind.House, new Cell(6, 3));
            world.SightBlocked.Add(new Cell(4, 3));

            Assert.That(world.Observe(observer.Id).VisibleBuildings.Select(b => b.Id), Does.Contain(target.Id));
            Assert.That(world.Observe(observer.Id).LegalActions.Any(a => a.TargetId == target.Id && a.Kind == ActionKind.Attack), Is.False);
            world.SightBlocked.Clear();
            Assert.That(world.Observe(observer.Id).LegalActions.Any(a => a.TargetId == target.Id && a.Kind == ActionKind.Attack), Is.True,
                "A clear near edge is attackable even when the building center is out of range.");
        }
    }
}
