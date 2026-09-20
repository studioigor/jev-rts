using System.Collections.Generic;
using System.Reflection;
using Jev.Gameplay.Authoring;
using Jev.Gameplay.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Runtime.Tests
{
    /// <summary>Real physics picking and order dispatch, without a transport or an AI substitute.</summary>
    public sealed class ContextCommandTests
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        readonly List<Object> owned=new List<Object>();
        MatchController match;
        RtsWorld world;
        BuildingState house;
        ResourceState wood;
        UnitState worker;
        HashSet<Cell> visible;
        Vector2 pointer;

        [SetUp]
        public void SetUp()
        {
            var host=Own(new GameObject("Context command fixture"));host.SetActive(false);
            match=host.AddComponent<MatchController>();
            match.Battlefield=Own(ScriptableObject.CreateInstance<BattlefieldDefinition>());
            match.Battlefield.Origin=Vector2.zero;match.Battlefield.CellSize=1;
            world=new RtsWorld{Width=24,Height=24,VictoryEnabled=false};
            typeof(MatchController).GetProperty("World").GetSetMethod(true).Invoke(match,new object[]{world});
            worker=world.AddUnit(FactionId.Human,UnitKind.Worker,new Cell(3,5));
            house=world.AddBuilding(FactionId.Human,BuildingKind.House,new Cell(5,5));
            wood=world.AddResource(ResourceKind.Wood,new Cell(5,7),100);
            world.SetOrder(worker.Id,Order.Build(house.Kind,house.Cell));
            Field<HashSet<string>>("selection").Add(worker.Id);
            visible=Field<HashSet<Cell>>("playerVisible");visible.Add(wood.Cell);visible.Add(house.Cell);

            var camera=Own(new GameObject("Context camera")).AddComponent<UnityEngine.Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(8000,2,-10),Quaternion.identity);
            // Keep picking independent of the batch runner's display and Game View size.
            camera.targetTexture=Own(new RenderTexture(800,600,0));
            camera.rect=new Rect(0,0,1,1);match.ViewCamera=camera;
            pointer=camera.ViewportToScreenPoint(new Vector3(.5f,.5f,0));
            AddProxy(house.Id,EntityViewKind.Building,new Vector3(8000,2,2),new Vector3(4,4,2));
            AddProxy(wood.Id,EntityViewKind.Resource,new Vector3(8000,2,6),new Vector3(1,4,1));
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            if(match && match.ViewCamera)match.ViewCamera.targetTexture=null;
            for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
            owned.Clear();
        }

        [Test]
        public void FinishedHouseRemainsSelectableButCannotInterceptTreeCommand()
        {
            Assert.That(Pick(false,true),Is.EqualTo(house.Id),"Ordinary selection should still select the frontmost house.");
            Assert.That(Pick(true,true),Is.EqualTo(wood.Id),"A finished house has no worker action, so its proxy must not consume a tree click.");
            Command();
            Assert.That(worker.Order.Kind,Is.EqualTo(OrderKind.Gather));
            Assert.That(worker.Order.TargetId,Is.EqualTo(wood.Id));
            Assert.That(Field<string>("status"),Is.EqualTo("Приказ добывать дерево."));
            Assert.That(worker.Cell,Is.EqualTo(new Cell(3,5)),"Input dispatch changes orders; only JEV can choose the next action.");
        }

        [Test]
        public void UnfinishedHouseCanStillBeResumedAndCompletionImmediatelyChangesPicking()
        {
            house.Complete=false;house.Progress=1;
            Assert.That(Pick(true,true),Is.EqualTo(house.Id));
            Command();
            Assert.That(worker.Order.Kind,Is.EqualTo(OrderKind.Build));
            Assert.That(Field<string>("status"),Is.EqualTo("Приказ продолжить строительство."));
            house.Complete=true;house.Progress=house.RequiredWork;
            Command();
            Assert.That(worker.Order.Kind,Is.EqualTo(OrderKind.Gather));
            Assert.That(worker.Order.TargetId,Is.EqualTo(wood.Id));
        }

        [Test]
        public void CompletedHouseWithoutActionBehindItFallsBackToMoveWithoutConstructionMessage()
        {
            wood.Remaining=0;
            Assert.That(Pick(true,true),Is.Null);
            Command();
            Assert.That(worker.Order.Kind,Is.EqualTo(OrderKind.Move));
            Assert.That(Field<string>("status"),Is.EqualTo("Приказ двигаться."));
        }

        [Test]
        public void FilteringFriendlyHouseDoesNotExposeHiddenOrExhaustedResource()
        {
            visible.Remove(wood.Cell);
            Assert.That(Pick(true,true),Is.Null);
            visible.Add(wood.Cell);wood.Remaining=0;
            Assert.That(Pick(true,true),Is.Null);
        }

        [Test]
        public void EnemyHouseRemainsAttackTargetEvenWhenComplete()
        {
            house.Faction=FactionId.Undead;
            Assert.That(Pick(true,true),Is.EqualTo(house.Id));
            Command();
            Assert.That(worker.Order.Kind,Is.EqualTo(OrderKind.Attack));
            Assert.That(worker.Order.TargetId,Is.EqualTo(house.Id));
            Assert.That(Field<string>("status"),Is.EqualTo("Приказ атаковать здание."));
        }

        [Test]
        public void SoldiersCannotConsumeHarvestOrFriendlyConstructionCommands()
        {
            worker.Kind=UnitKind.Warrior;house.Complete=false;
            Assert.That(Pick(true,false),Is.Null);
            Command();
            Assert.That(worker.Order.Kind,Is.EqualTo(OrderKind.Move));
            Assert.That(Field<string>("status"),Is.EqualTo("Приказ двигаться."));
        }

        void AddProxy(string id,EntityViewKind kind,Vector3 position,Vector3 size)
        {
            var go=Own(new GameObject(id+" picking proxy"));go.transform.position=position;
            var view=go.AddComponent<EntityView>();view.Id=id;view.Kind=kind;
            var box=go.AddComponent<BoxCollider>();box.size=size;box.isTrigger=true;
        }
        T Own<T>(T value) where T:Object {owned.Add(value);return value;}
        T Field<T>(string name)=>(T)typeof(MatchController).GetField(name,Private).GetValue(match);
        string Pick(bool command,bool canWork)=>(string)typeof(MatchController).GetMethod("EntityAt",Private).Invoke(match,new object[]{pointer,command,canWork});
        void Command()=>typeof(MatchController).GetMethod("IssueContextCommand",Private).Invoke(match,new object[]{pointer,new Vector3(9.5f,0,9.5f)});
    }
}
