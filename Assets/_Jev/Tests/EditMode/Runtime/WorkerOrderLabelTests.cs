using System.Collections.Generic;
using System.Reflection;
using Jev.Gameplay.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Runtime.Tests
{
    public sealed class WorkerOrderLabelTests
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        GameObject host;
        MatchController match;
        RtsWorld world;
        UnitState worker;

        [SetUp]
        public void SetUp()
        {
            host=new GameObject("Worker order label fixture");host.SetActive(false);
            match=host.AddComponent<MatchController>();
            world=new RtsWorld{Width=24,Height=24,VictoryEnabled=false};
            typeof(MatchController).GetProperty("World").GetSetMethod(true).Invoke(match,new object[]{world});
            worker=world.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(3,5));
        }

        [TearDown]
        public void TearDown(){Object.DestroyImmediate(host);}

        [Test]
        public void CompletedOwnConstructionIsReportedWithoutChangingTheOrder()
        {
            var house=world.AddBuilding(FactionId.Human,BuildingKind.House,new Cell(5,5),false);
            world.SetOrder(worker.Id,Order.Build(house.Kind,house.Cell));
            Assert.That(Label(),Is.EqualTo("Строит: Дом"));
            house.Progress=house.RequiredWork;house.Complete=true;
            Assert.That(Label(),Is.EqualTo("Строительство завершено"));
            Assert.That(worker.Order.Kind,Is.EqualTo(OrderKind.Build));
            Assert.That(worker.Order.Cell,Is.EqualTo(house.Cell));
            Assert.That(worker.OrderRevision,Is.EqualTo(1));
        }

        [Test]
        public void OtherFactionWrongKindOrDestroyedBuildingDoesNotCompleteWorkerOrder()
        {
            var house=world.AddBuilding(FactionId.Undead,BuildingKind.House,new Cell(5,5));
            world.SetOrder(worker.Id,Order.Build(BuildingKind.House,house.Cell));
            Assert.That(Label(),Is.EqualTo("Строит: Дом"));
            house.Faction=FactionId.Human;house.Kind=BuildingKind.Barracks;
            Assert.That(Label(),Is.EqualTo("Строит: Дом"));
            house.Kind=BuildingKind.House;house.Hp=0;
            Assert.That(Label(),Is.EqualTo("Строит: Дом"));
        }

        [Test]
        public void ExhaustionLabelRequiresCurrentPlayerVisibilityAndDoesNotChangeAssignment()
        {
            var resource=world.AddResource(ResourceKind.Wood,new Cell(5,7),5);
            world.SetOrder(worker.Id,Order.Gather(resource.Id,resource.Cell));
            var visible=(HashSet<Cell>)typeof(MatchController).GetField("playerVisible",Private).GetValue(match);
            visible.Add(resource.Cell);
            Assert.That(Label(),Is.EqualTo("Добыча ресурсов"));
            resource.Remaining=0;
            Assert.That(Label(),Is.EqualTo("Ресурс исчерпан"));
            visible.Clear();
            Assert.That(Label(),Is.EqualTo("Добыча ресурсов"),"Hidden current resource state cannot update this UI label.");
            Assert.That(worker.Order.Kind,Is.EqualTo(OrderKind.Gather));
            Assert.That(worker.Order.TargetId,Is.EqualTo(resource.Id));
            Assert.That(worker.OrderRevision,Is.EqualTo(1));
        }

        [Test]
        public void MissingUnseenTargetDoesNotClaimExhaustion()
        {
            world.SetOrder(worker.Id,Order.Gather("unknown-resource",new Cell(5,7)));
            Assert.That(Label(),Is.EqualTo("Добыча ресурсов"));
        }

        string Label()=>(string)typeof(MatchController).GetMethod("OrderLabel",Private).Invoke(match,new object[]{worker});
    }
}
