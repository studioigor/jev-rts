using System;
using System.Collections.Generic;
using System.Linq;

namespace Jev.Gameplay.Simulation
{
    public enum FactionId { Human, Undead }
    public enum UnitKind { Worker, Warrior, Archer }
    public enum BuildingKind { TownHall, House, Barracks, ArcheryRange, Tower }
    public enum ResourceKind { Wood, Gold }
    public enum OrderKind { Move, Gather, Build, Attack, Hold }
    public enum ActionKind { Wait, Move, Attack, Gather, Build }

    [Serializable]
    public struct Cell : IEquatable<Cell>
    {
        public int X, Y;
        public Cell(int x, int y) { X = x; Y = y; }
        public bool Equals(Cell other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Cell other && Equals(other);
        public override int GetHashCode() => unchecked(X * 397 ^ Y);
        public override string ToString() => $"({X},{Y})";
        public static Cell operator +(Cell a, Cell b) => new Cell(a.X + b.X, a.Y + b.Y);
        public static Cell operator -(Cell a, Cell b) => new Cell(a.X - b.X, a.Y - b.Y);
        public static bool operator ==(Cell a, Cell b) => a.Equals(b);
        public static bool operator !=(Cell a, Cell b) => !a.Equals(b);
        public static int Manhattan(Cell a, Cell b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        public static int Chebyshev(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    [Serializable] public sealed class ResourceCost
    {
        public int Wood, Gold;
        public ResourceCost() { }
        public ResourceCost(int wood, int gold) { Wood = wood; Gold = gold; }
        public ResourceCost Copy() => new ResourceCost(Wood, Gold);
    }
    [Serializable] public sealed class PopulationState { public int Used, Capacity, Reserved; }
    [Serializable] public sealed class FactionTraits
    {
        public float HealthMultiplier = 1, Intimidation = 1, FearSusceptibility = 1;
        public static FactionTraits For(FactionId faction) => faction == FactionId.Human ? new FactionTraits() :
            new FactionTraits { HealthMultiplier = .9f, Intimidation = 1.5f, FearSusceptibility = .25f };
    }
    [Serializable] public sealed class FactionState
    {
        public FactionId Id;
        public int Wood, Gold, BasePopulationCapacity;
        public FactionTraits Traits = new FactionTraits();
        public PopulationState Population = new PopulationState();
    }
    [Serializable] public sealed class UnitDefinition
    {
        public UnitKind Kind;
        public int MaxHp, Damage, Range = 1, Vision = 5;
        public ResourceCost Cost = new ResourceCost();
        public float TrainingSeconds = 12;
    }
    [Serializable] public sealed class BuildingDefinition
    {
        public BuildingKind Kind;
        public int MaxHp, RequiredWork, PopulationProvided, Damage, Range, Vision = 6;
        public int FootprintSizeX = 1, FootprintSizeY = 1;
        public ResourceCost Cost = new ResourceCost();
        public UnitKind[] TrainableUnits = Array.Empty<UnitKind>();
    }
    [Serializable] public sealed class GameRules
    {
        public int PerFactionUnitLimit = 20, WoodPerGather = 5, GoldPerGather = 5;
        public float DecisionIntervalSeconds = 2;
        public bool RotatingMovementPriority = true;
        // Optional authored geography only. This does not reveal entities or bypass per-step collision checks.
        public bool PublicLocalTerrain = false;
        // Zero retains the actor's vision radius. Static geography never enlarges entity sensors.
        public int PublicTerrainRadius = 0;
        public bool ExtendedMovementVocabulary = false;
        // RTS exploration reveals the whole sensor disk. Attacks still require a clear physical line of sight.
        public bool CircularUnitVision = false;
        public List<UnitDefinition> Units = new List<UnitDefinition>
        {
            new UnitDefinition { Kind=UnitKind.Worker,MaxHp=40,Damage=4,Cost=new ResourceCost(0,10) },
            new UnitDefinition { Kind=UnitKind.Warrior,MaxHp=100,Damage=12,Cost=new ResourceCost(0,15) },
            new UnitDefinition { Kind=UnitKind.Archer,MaxHp=60,Damage=8,Range=3,Cost=new ResourceCost(5,10) }
        };
        public List<BuildingDefinition> Buildings = new List<BuildingDefinition>
        {
            new BuildingDefinition { Kind=BuildingKind.TownHall,Cost=new ResourceCost(60,30),RequiredWork=4,MaxHp=300,PopulationProvided=10,TrainableUnits=new[]{UnitKind.Worker} },
            new BuildingDefinition { Kind=BuildingKind.House,Cost=new ResourceCost(15,0),RequiredWork=2,MaxHp=100,PopulationProvided=5 },
            new BuildingDefinition { Kind=BuildingKind.Barracks,Cost=new ResourceCost(20,10),RequiredWork=3,MaxHp=180,TrainableUnits=new[]{UnitKind.Warrior} },
            new BuildingDefinition { Kind=BuildingKind.ArcheryRange,Cost=new ResourceCost(25,10),RequiredWork=3,MaxHp=150,TrainableUnits=new[]{UnitKind.Archer} },
            new BuildingDefinition { Kind=BuildingKind.Tower,Cost=new ResourceCost(20,15),RequiredWork=3,MaxHp=160,Damage=10,Range=4 }
        };
        public UnitDefinition Unit(UnitKind kind) => Units.First(d => d.Kind == kind);
        public BuildingDefinition Building(BuildingKind kind) => Buildings.First(d => d.Kind == kind);
    }
    [Serializable] public sealed class Order
    {
        public OrderKind Kind;
        public Cell Cell;
        public string TargetId;
        // A gather order is an ongoing assignment to this resource type. The clicked
        // node is its first target; only a later JEV choice can name a replacement.
        public ResourceKind? GatherResourceKind;
        public BuildingKind BuildingKind;
        public string GroupId;
        public List<string> GroupMembers = new List<string>();
        public int GroupArrivalRadius;
        public Cell FormationAnchor;
        public List<FormationSlot> Formation = new List<FormationSlot>();
        public Order Copy() => new Order { Kind=Kind,Cell=Cell,TargetId=TargetId,GatherResourceKind=GatherResourceKind,BuildingKind=BuildingKind,GroupId=GroupId,FormationAnchor=FormationAnchor,
            GroupMembers=new List<string>(GroupMembers),GroupArrivalRadius=GroupArrivalRadius,
            Formation=Formation.Select(s=>new FormationSlot { UnitId=s.UnitId,Cell=s.Cell }).ToList() };
        public static Order Move(Cell cell) => new Order { Kind=OrderKind.Move,Cell=cell };
        public static Order Gather(string targetId, Cell cell, ResourceKind? resourceKind=null) => new Order { Kind=OrderKind.Gather,TargetId=targetId,Cell=cell,GatherResourceKind=resourceKind };
        public static Order Build(BuildingKind kind, Cell cell) => new Order { Kind=OrderKind.Build,BuildingKind=kind,Cell=cell };
        public static Order Attack(string targetId, Cell lastKnownCell) => new Order { Kind=OrderKind.Attack,TargetId=targetId,Cell=lastKnownCell };
        public static Order Hold(Cell cell) => new Order { Kind=OrderKind.Hold,Cell=cell };
    }
    [Serializable] public sealed class FormationSlot { public string UnitId; public Cell Cell; }
    [Serializable] public sealed class LastAction
    {
        public double TimeSeconds;
        public ActionKind Kind;
        public string ActionId, TargetId, Reason;
        public bool Ok;
    }
    [Serializable] public sealed class UnitState
    {
        public string Id;
        public FactionId Faction;
        public UnitKind Kind;
        public Cell Cell;
        public int Hp, MaxHp, Damage, Range, Vision, OrderRevision;
        public Order Order;
        public LastAction LastAction;
        public UnitMemory Memory = new UnitMemory();
        public ResourceCost TrainingCost = new ResourceCost();
        public bool Alive => Hp > 0;
    }
    [Serializable] public sealed class TrainingJob
    {
        public UnitKind Kind;
        public double RemainingSeconds;
        public ResourceCost Cost = new ResourceCost();
    }
    [Serializable] public sealed class BuildingState
    {
        public string Id;
        public FactionId Faction;
        public BuildingKind Kind;
        public Cell Cell;
        public List<Cell> Footprint = new List<Cell>();
        public int Hp, MaxHp, Progress, RequiredWork, PopulationProvided, Damage, Range, Vision;
        public bool Complete;
        public ResourceCost Cost = new ResourceCost();
        public List<TrainingJob> TrainingQueue = new List<TrainingJob>();
        public LastAction LastAction;
        public bool Alive => Hp > 0;
    }
    [Serializable] public sealed class ResourceState
    {
        public string Id;
        public ResourceKind Kind;
        public Cell Cell;
        public int Remaining;
    }
    [Serializable] public sealed class WorldEvent
    {
        public long Sequence;
        public double TimeSeconds;
        public string Type, ActorId, TargetId, Reason;
        public FactionId Faction;
        public Cell From, To;
        public int Amount;
        public ResourceKind ResourceKind;
    }
    [Serializable] public sealed class LegalAction
    {
        public string Id, TargetId, Description;
        public ActionKind Kind;
        public Cell Cell;
        public BuildingKind BuildingKind;
        public List<Cell> Cells = new List<Cell>();
    }
    public sealed class ActionChoice
    {
        public string UnitId, ActionId;
        public int OrderRevision;
        public ActionChoice() { }
        public ActionChoice(string unitId, string actionId, int revision) { UnitId=unitId;ActionId=actionId;OrderRevision=revision; }
    }
    public sealed class StepChoice
    {
        public string UnitId;
        public Cell Destination;
        public int OrderRevision;
        public StepChoice(string unitId,Cell destination,int revision) { UnitId=unitId;Destination=destination;OrderRevision=revision; }
    }
    public sealed class ActionResult
    {
        public bool Ok, Approved;
        public string Reason, UnitId, TargetId;
        public List<Cell> ApprovedCells = new List<Cell>();
        public static ActionResult Fail(string reason, string unitId = null) => new ActionResult { Reason=reason,UnitId=unitId };
        public static ActionResult Success(string reason, string unitId = null, string targetId = null) => new ActionResult { Ok=true,Approved=true,Reason=reason,UnitId=unitId,TargetId=targetId };
    }
}
