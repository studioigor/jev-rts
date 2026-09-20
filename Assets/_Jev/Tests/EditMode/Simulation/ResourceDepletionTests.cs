using System.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Simulation.Tests
{
    public sealed class ResourceDepletionTests
    {
        [TestCase(ResourceKind.Wood, 100)]
        [TestCase(ResourceKind.Gold, 10000)]
        public void RepeatedConfirmedGatherExhaustsFiniteStockAndRejectsAnyFurtherGather(ResourceKind kind, int stock)
        {
            var world = new RtsWorld { Width = 12, Height = 12 };
            var worker = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(3, 3), "worker");
            var resource = world.AddResource(kind, new Cell(4, 3), stock, "resource");
            var conserved = world.TotalAccountedResources();
            world.SetOrder(worker.Id, Order.Gather(resource.Id, resource.Cell));
            world.Observe(worker.Id);
            int yield = kind == ResourceKind.Wood ? world.Definitions.WoodPerGather : world.Definitions.GoldPerGather;
            int steps = (stock + yield - 1) / yield;
            for (int i = 0; i < steps; i++) Assert.That(world.Execute(worker.Id, "gather_resource", worker.OrderRevision).Ok, Is.True);
            Assert.That(resource.Remaining, Is.Zero);
            Assert.That(worker.Order.GatherResourceKind,Is.EqualTo(kind),"The ongoing assignment keeps its resource type after exhaustion.");
            Assert.That((string)world.Observe(worker.Id).ToJObject()["self"]["order"]["resourceKind"],Is.EqualTo(kind.ToString().ToLowerInvariant()));
            Assert.That(kind == ResourceKind.Wood ? world.Human.Wood : world.Human.Gold, Is.EqualTo(stock));
            Assert.That(world.Execute(worker.Id, "gather_resource", worker.OrderRevision).Ok, Is.False);
            var observation = world.Observe(worker.Id);
            Assert.That(observation.VisibleResources, Is.Empty);
            Assert.That(observation.Memory.Resources, Is.Empty);
            Assert.That(observation.LegalActions.Any(a => a.Kind == ActionKind.Gather), Is.False);
            Assert.That(world.IsFree(resource.Cell), Is.True, "Exhausted scenery no longer occupies the simulation cell.");
            world.AssertInvariants(conserved);
        }

        [TestCase(ResourceKind.Wood)]
        [TestCase(ResourceKind.Gold)]
        public void ConcurrentLastStrikesDoNotCreateZeroYieldSuccessOrNegativeStock(ResourceKind kind)
        {
            var world = new RtsWorld { Width = 12, Height = 12 };
            var first = world.AddUnit(FactionId.Human, UnitKind.Worker, new Cell(3, 3), "first");
            var second = world.AddUnit(FactionId.Undead, UnitKind.Worker, new Cell(5, 3), "second");
            var node = world.AddResource(kind, new Cell(4, 3), 3, "resource");
            var conserved = world.TotalAccountedResources();
            var replies = world.ExecuteBatch(new[] { new ActionChoice(first.Id, "gather_resource", 0), new ActionChoice(second.Id, "gather_resource", 0) });
            Assert.That(replies.Count(r => r.Ok), Is.EqualTo(1));
            Assert.That(node.Remaining, Is.Zero);
            Assert.That(world.Events.Where(e => e.Type == "gather").Sum(e => e.Amount), Is.EqualTo(3));
            world.AssertInvariants(conserved);
        }
    }
}
