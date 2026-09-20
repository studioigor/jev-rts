using Jev.Gameplay.Simulation;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Runtime.Tests
{
    public sealed class UnitWakeSignatureTests
    {
        [Test]
        public void LightweightWakeReadLeavesCompleteProviderFactsAndMemoryUntouched()
        {
            var world = new RtsWorld { Width = 30, Height = 30 };
            world.Definitions.CircularUnitVision = true;
            var self = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(10, 10), "worker");
            var tree = world.AddResource(ResourceKind.Wood, new Cell(11, 10), 100, "tree");
            world.SetOrder(self.Id, Order.Gather(tree.Id, tree.Cell));
            world.Human.Wood = 24; world.Human.Gold = 18;
            var before = world.Observe(self.Id, false).ToJObject();
            var memory = self.Memory;
            var sensor = new UnitSensingSnapshot();
            for (int i = 0; i < 10; i++) world.SenseUnit(self.Id, sensor);
            var after = world.Observe(self.Id, false).ToJObject();
            Assert.That(JToken.DeepEquals(before, after), Is.True);
            Assert.That(self.Memory, Is.SameAs(memory));
            Assert.That((int)after["faction"]["wood"], Is.EqualTo(24));
            Assert.That((int)after["faction"]["gold"], Is.EqualTo(18));
            Assert.That(after["legalActions"].HasValues, Is.True);
            Assert.That(after["rules"]["buildingDefinitions"].HasValues, Is.True);
        }

        [Test]
        public void EveryNonConstructionOrderIgnoresRemoteStockChanges()
        {
            var world = new RtsWorld { Width = 30, Height = 30 };
            world.Definitions.CircularUnitVision = true;
            var self = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(10, 10), "worker");
            var sensor = new UnitSensingSnapshot();
            foreach (Order order in new[] { null, Order.Move(new Cell(12, 12)), Order.Hold(self.Cell),
                Order.Gather("tree", new Cell(11, 10)), Order.Attack("enemy", new Cell(12, 10)) })
            {
                self.Order = order;
                ulong before = world.SenseUnit(self.Id, sensor).Signature;
                world.Human.Wood += 5; world.Human.Gold += 5; world.Human.Population.Used++;
                Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.EqualTo(before), order?.Kind.ToString() ?? "none");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PaidFoundationWakeDoesNotDependOnAnotherWorkersBankButStillDetectsDamage(bool complete)
        {
            var world = new RtsWorld { Width = 30, Height = 30 };
            world.Definitions.CircularUnitVision = true;
            var self = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(10, 10), "worker");
            var house = world.AddBuilding(FactionId.Human, BuildingKind.House, new Cell(11, 10), complete, "house");
            world.SetOrder(self.Id, Order.Build(house.Kind, house.Cell));
            var sensor = new UnitSensingSnapshot();
            ulong before = world.SenseUnit(self.Id, sensor).Signature;
            world.Human.Gold += 5;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.EqualTo(before));
            house.Hp--;
            Assert.That(world.SenseUnit(self.Id, sensor).Signature, Is.Not.EqualTo(before));
        }
    }
}
