using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Simulation.Tests
{
    public sealed class PublicTerrainObservationTests
    {
        static readonly Cell Start = new Cell(2, 3), HiddenCorner = new Cell(3, 2);
        const string CornerRoute = "move_north_1_east_2";

        static RtsWorld CornerWorld(bool publicTerrain = true)
        {
            var world = new RtsWorld { Width = 10, Height = 10 };
            world.Definitions.PublicLocalTerrain = publicTerrain;
            world.Human.BasePopulationCapacity = world.Undead.BasePopulationCapacity = 20;
            world.AddUnit(FactionId.Human, UnitKind.Warrior, Start, "actor");
            world.Blocked.Add(new Cell(3, 3));
            return world;
        }

        static string[] Routes(UnitObservation observation) => observation.LegalActions.Where(a => a.Kind == ActionKind.Move)
            .Select(a => a.Id + ":" + string.Join(";", a.Cells)).ToArray();

        static void AddOccupant(RtsWorld world, string kind, Cell cell)
        {
            switch (kind)
            {
                case "Enemy": world.AddUnit(FactionId.Undead, UnitKind.Worker, cell, "occupant"); break;
                case "Ally": world.AddUnit(FactionId.Human, UnitKind.Worker, cell, "occupant"); break;
                case "Resource": world.AddResource(ResourceKind.Gold, cell, 100, "occupant"); break;
                case "Building": world.AddBuilding(FactionId.Undead, BuildingKind.House, cell, true, "occupant"); break;
            }
        }

        [Test]
        public void PublicStaticCornerRouteIsOptInAndDoesNotChangeReferenceDefault()
        {
            Assert.That(new GameRules().PublicLocalTerrain, Is.False);
            Assert.That(new GameRules().ExtendedMovementVocabulary, Is.False);
            Assert.That(new GameRules().PublicTerrainRadius, Is.Zero);
            var old = CornerWorld(false).Observe("actor", false);
            Assert.That(old.LegalActions.Any(a => a.Id == CornerRoute), Is.False);
            Assert.That(old.PublicTerrain, Is.Empty);
            Assert.That(old.ToJObject()["publicTerrain"], Is.Null);

            var world = CornerWorld(); var observation = world.Observe("actor", false);
            Assert.That(observation.Rules.PublicLocalTerrain, Is.True);
            Assert.That(observation.VisibleCells.Contains(HiddenCorner), Is.False);
            Assert.That(observation.PublicTerrain.Any(t => t.Cell == HiddenCorner && !t.Blocked), Is.True);
            Assert.That(observation.LegalActions.Single(a => a.Id == CornerRoute).Cells,
                Is.EqualTo(new[] { new Cell(2, 2), HiddenCorner, new Cell(4, 2) }));
            var facts = observation.ToJObject()["publicTerrain"];
            Assert.That((string)facts["source"], Is.EqualTo("authored_static_no_dynamicIntel"));
            Assert.That(facts["tiles"].Any(t => t["occupiedBy"] != null), Is.False);
            foreach (var tile in observation.PublicTerrain)
            {
                int dx = tile.Cell.X - Start.X, dy = tile.Cell.Y - Start.Y;
                Assert.That(world.InBounds(tile.Cell), Is.True);
                Assert.That(dx * dx + dy * dy, Is.LessThanOrEqualTo(observation.Self.Vision * observation.Self.Vision));
                Assert.That(tile.Blocked, Is.EqualTo(world.Blocked.Contains(tile.Cell)));
            }
        }

        [TestCase("Enemy")]
        [TestCase("Resource")]
        [TestCase("Building")]
        public void UnseenDynamicOccupantCannotChangeOfferedRoutesButStopsActualMovement(string kind)
        {
            var expected = CornerWorld().Observe("actor", false);
            var world = CornerWorld(); AddOccupant(world, kind, HiddenCorner);
            var observation = world.Observe("actor", false);
            Assert.That(observation.VisibleCells.Contains(HiddenCorner), Is.False);
            Assert.That(observation.VisibleUnits.Concat(observation.VisibleResources).Concat(observation.VisibleBuildings), Is.Empty);
            Assert.That(observation.Memory.Enemies.Concat(observation.Memory.Resources).Concat(observation.Memory.Buildings), Is.Empty);
            Assert.That(Routes(observation), Is.EqualTo(Routes(expected)));
            Assert.That(JToken.DeepEquals(observation.ToJObject()["publicTerrain"], expected.ToJObject()["publicTerrain"]), Is.True);
            var approved = world.Execute("actor", CornerRoute, 0);
            Assert.That(approved.Ok, Is.True);
            Assert.That(world.TryExecuteStep("actor", approved.ApprovedCells[0], 0).Ok, Is.True);
            var blocked = world.TryExecuteStep("actor", approved.ApprovedCells[1], 0);
            Assert.That(blocked.Ok, Is.False);
            Assert.That(blocked.Reason, Is.EqualTo("destination_occupied_during_resolution"));
            Assert.That(world.Unit("actor").Cell, Is.EqualTo(new Cell(2, 2)));
        }

        [TestCase("Enemy")]
        [TestCase("Ally")]
        [TestCase("Resource")]
        [TestCase("Building")]
        public void CurrentlyVisibleOccupantsStillExcludeTheirCells(string kind)
        {
            var world = CornerWorld(); var occupied = new Cell(2, 2); AddOccupant(world, kind, occupied);
            var observation = world.Observe("actor", false);
            Assert.That(observation.VisibleTiles.Single(t => t.Cell == occupied).OccupiedBy, Is.EqualTo("occupant"));
            Assert.That(observation.LegalActions.Where(a => a.Kind == ActionKind.Move).Any(a => a.Cells.Contains(occupied)), Is.False);
            Assert.That(observation.LegalActions.Any(a => a.Id == "move_south"), Is.True);
        }

        [Test]
        public void PersonalBuildingMemoryExcludesWholeFootprintWithoutReadingHiddenDestruction()
        {
            var world = CornerWorld(); var actor = world.Unit("actor");
            world.Definitions.Building(BuildingKind.House).FootprintSizeX = 3;
            var building = world.AddBuilding(FactionId.Undead, BuildingKind.House, new Cell(4, 2), true, "known-house");
            actor.Cell = new Cell(4, 1); world.Observe(actor.Id);
            actor.Cell = Start; var remembered = world.Observe(actor.Id);
            Assert.That(remembered.VisibleBuildings, Is.Empty);
            Assert.That(remembered.Memory.Buildings.Single().Footprint.Count, Is.EqualTo(3));
            Assert.That(remembered.KnownMapAscii.Split('\n').Skip(1).Sum(row => row.Count(c => c == 'b')), Is.EqualTo(3));
            Assert.That(remembered.LegalActions.Where(a => a.Kind == ActionKind.Move).Any(a => a.Cells.Any(building.Footprint.Contains)), Is.False);

            building.Hp = 0; var unseenDestruction = world.Observe(actor.Id);
            Assert.That(Routes(unseenDestruction), Is.EqualTo(Routes(remembered)));
            Assert.That(unseenDestruction.Memory.Buildings.Single().Hp, Is.GreaterThan(0));
            actor.Cell = new Cell(4, 1); world.Observe(actor.Id);
            actor.Cell = Start; var cleared = world.Observe(actor.Id);
            Assert.That(cleared.Memory.Buildings, Is.Empty);
            Assert.That(cleared.LegalActions.Any(a => a.Id == CornerRoute), Is.True);
        }

        [Test]
        public void AuthoredGeographyDoesNotExpandPersonalVisionOrMemory()
        {
            var world = CornerWorld(false); var actor = world.Unit("actor");
            var before = world.Observe(actor.Id, false);
            var visibleBefore = world.VisibleCellsForUnit(actor.Id);
            world.Definitions.PublicLocalTerrain = true;
            world.Definitions.PublicTerrainRadius = 12;
            var after = world.Observe(actor.Id, false);
            Assert.That(JToken.DeepEquals(before.ToJObject()["visible"], after.ToJObject()["visible"]), Is.True);
            Assert.That(JToken.DeepEquals(before.ToJObject()["memory"], after.ToJObject()["memory"]), Is.True);
            Assert.That(after.KnownMapAscii, Does.Contain("authored static terrain"));
            Assert.That(world.VisibleCellsForUnit(actor.Id), Is.EquivalentTo(visibleBefore));
            Assert.That(world.CanSee(actor, HiddenCorner), Is.False);
            Assert.That(actor.Memory.Terrain, Is.Empty);
            Assert.That(actor.Memory.RecentPositions, Is.Empty);
            world.Observe(actor.Id);
            Assert.That(actor.Memory.Terrain.Any(t => t.Cell == HiddenCorner), Is.False);
            Assert.That(actor.Memory.Terrain.Select(t => t.Cell), Is.EquivalentTo(visibleBefore));
        }

        [Test]
        public void ExtendedVocabularyIsBoundedAndCopiesItsRulesWithoutEnlargingSensors()
        {
            var world = new RtsWorld { Width = 40, Height = 40 };
            var actor = world.AddUnit(FactionId.Human, UnitKind.Warrior, new Cell(20, 20), "actor");
            Assert.That(world.Observe(actor.Id, false).LegalActions.Count(a => a.Kind == ActionKind.Move), Is.EqualTo(48));
            world.Definitions.PublicLocalTerrain = true;
            world.Definitions.PublicTerrainRadius = 12;
            world.Definitions.ExtendedMovementVocabulary = true;
            var observation = world.Observe(actor.Id, false);
            var moves = observation.LegalActions.Where(a => a.Kind == ActionKind.Move).ToArray();
            Assert.That(moves.Length, Is.EqualTo(160));
            Assert.That(moves.Select(a => a.Id).Distinct().Count(), Is.EqualTo(160));
            Assert.That(moves.Max(a => a.Cells.Count), Is.EqualTo(16));
            Assert.That(moves.Any(a => a.Id == "move_north_8"), Is.True);
            for(int length=2;length<=8;length++)
                Assert.That(moves.Any(a => a.Id == "move_north_"+length), Is.True,"Every straight length is offered so an exact destination need not be overshot.");
            Assert.That(world.LegalActions(actor.Id, false).Count(a => a.Kind == ActionKind.Move), Is.EqualTo(4));
            Assert.That(observation.Self.Vision, Is.EqualTo(5));
            Assert.That(observation.VisibleCells.Contains(new Cell(28, 20)), Is.False);
            Assert.That(observation.PublicTerrain.Any(t => t.Cell == new Cell(32, 20)), Is.True);
            Assert.That(observation.PublicTerrain.Any(t => t.Cell == new Cell(33, 20)), Is.False);
            var facts = observation.ToJObject();
            Assert.That((int)facts["publicTerrain"]["radius"], Is.EqualTo(12));
            Assert.That((int)facts["rules"]["publicTerrainRadius"], Is.EqualTo(12));
            Assert.That((bool)facts["rules"]["extendedMovementVocabulary"], Is.True);
            observation.Rules.PublicTerrainRadius = 1; observation.Rules.ExtendedMovementVocabulary = false;
            Assert.That(world.Definitions.PublicTerrainRadius, Is.EqualTo(12));
            Assert.That(world.Definitions.ExtendedMovementVocabulary, Is.True);
        }

        [Test]
        public void ExtendedLiteralRouteAroundLargeBuildingsIsGoalIndependentAndStopsAtAnUnseenEnemy()
        {
            var world = new RtsWorld { Width = 40, Height = 40 };
            world.Definitions.PublicLocalTerrain = true;
            world.Definitions.PublicTerrainRadius = 12;
            world.Definitions.ExtendedMovementVocabulary = true;
            foreach (var kind in new[] { BuildingKind.TownHall, BuildingKind.Barracks })
            {
                var definition = world.Definitions.Building(kind);
                definition.FootprintSizeX = definition.FootprintSizeY = 7;
            }
            var actor = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(10, 8), "actor");
            var hall = world.AddBuilding(FactionId.Human, BuildingKind.TownHall, new Cell(10, 12), true, "hall");
            var barracks = world.AddBuilding(FactionId.Human, BuildingKind.Barracks, new Cell(17, 8), true, "barracks");
            world.SetOrder(actor.Id, Order.Move(new Cell(10, 24)));
            var first = world.Observe(actor.Id, false);
            const string routeId = "move_west_4_south_8";
            var route = first.LegalActions.Single(a => a.Id == routeId);
            Assert.That(route.Cell, Is.EqualTo(new Cell(6, 16)));
            Assert.That(route.Cells.Count, Is.EqualTo(12));
            Assert.That(route.Cells.Any(c => hall.Footprint.Contains(c) || barracks.Footprint.Contains(c)), Is.False);
            var prior = actor.Cell;
            foreach (var cell in route.Cells) { Assert.That(Cell.Manhattan(prior, cell), Is.EqualTo(1)); prior = cell; }
            world.SetOrder(actor.Id, Order.Move(new Cell(25, 2)));
            var changedGoal = world.Observe(actor.Id, false);
            Assert.That(Routes(changedGoal), Is.EqualTo(Routes(first)), "The goal must not rank or change the fixed vocabulary.");
            var hiddenCell = new Cell(6, 14);
            Assert.That(changedGoal.VisibleCells.Contains(hiddenCell), Is.False);
            world.AddUnit(FactionId.Undead, UnitKind.Worker, hiddenCell, "unseen-enemy");
            var occupied = world.Observe(actor.Id, false);
            Assert.That(Routes(occupied), Is.EqualTo(Routes(changedGoal)));
            Assert.That(occupied.VisibleUnits, Is.Empty);
            Assert.That(JToken.DeepEquals(occupied.ToJObject()["publicTerrain"], changedGoal.ToJObject()["publicTerrain"]), Is.True);
            var approved = world.Execute(actor.Id, routeId, actor.OrderRevision);
            Assert.That(approved.Ok, Is.True);
            foreach (var cell in approved.ApprovedCells)
            {
                var result = world.TryExecuteStep(actor.Id, cell, actor.OrderRevision);
                if (cell == hiddenCell)
                {
                    Assert.That(result.Ok, Is.False);
                    Assert.That(result.Reason, Is.EqualTo("destination_occupied_during_resolution"));
                    break;
                }
                Assert.That(result.Ok, Is.True);
            }
            Assert.That(actor.Cell, Is.EqualTo(new Cell(6, 13)));
            world.AssertInvariants();
        }

        [Test]
        public void KnownMapShowsEntireSevenBySevenHallBeyondItsVisibleEdgeWithoutRevealingUnknownBuildings()
        {
            var world = new RtsWorld { Width = 32, Height = 32 };
            world.Definitions.PublicLocalTerrain = true;
            var definition = world.Definitions.Building(BuildingKind.TownHall);
            definition.FootprintSizeX = definition.FootprintSizeY = 7;
            var actor = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(10, 8), "actor");
            var hall = world.AddBuilding(FactionId.Human, BuildingKind.TownHall, new Cell(10, 12), true, "hall");
            world.AddBuilding(FactionId.Undead, BuildingKind.House, new Cell(15, 14), true, "unseen-house");
            var observation = world.Observe(actor.Id, false);
            Assert.That(observation.VisibleBuildings.Select(b => b.Id), Is.EqualTo(new[] { hall.Id }));
            Assert.That(observation.VisibleCells.Count(hall.Footprint.Contains), Is.LessThan(49));
            Assert.That(observation.Memory.Terrain.Any(t => t.Cell == new Cell(10, 15)), Is.False);
            var rows = observation.KnownMapAscii.Split('\n').Skip(1).Where(row => row.Contains(":")).ToArray();
            Assert.That(rows.Sum(row => row.Count(c => c == 'B')), Is.EqualTo(49));
            Assert.That(rows.Single(row => row.StartsWith("15:")).Contains("BBBBBBB"), Is.True);
            // This coordinate lies in the expanded map, outside both sensors and public radius.
            Assert.That(rows.Single(row => row.StartsWith("14:")).Split(':')[1][10], Is.EqualTo('?'));
            Assert.That(observation.Memory.Buildings.Any(b => b.Id == "unseen-house"), Is.False);
        }
    }
}
