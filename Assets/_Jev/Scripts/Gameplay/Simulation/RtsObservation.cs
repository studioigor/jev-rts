using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Simulation
{
    [Serializable] public sealed class TileSnapshot
    {
        public Cell Cell;
        public bool Blocked;
        public string OccupiedBy;
        public TileSnapshot Copy() => new TileSnapshot { Cell=Cell,Blocked=Blocked,OccupiedBy=OccupiedBy };
    }
    [Serializable] public class EntitySnapshot
    {
        public string Id, KindLabel;
        public FactionId Faction;
        public Cell Cell;
        public int Hp,MaxHp,Damage,Range,Remaining,Progress,RequiredWork,PopulationProvided;
        public double LastSeenTime;
        public float Intimidation;
        public bool Complete, IsBuilding, IsResource;
        public BuildingKind BuildingKind;
        public ResourceKind ResourceKind;
        public List<Cell> Footprint=new List<Cell>();
        public ResourceCost Cost=new ResourceCost();
        public EntitySnapshot Copy() => new EntitySnapshot { Id=Id,KindLabel=KindLabel,Faction=Faction,Cell=Cell,Hp=Hp,MaxHp=MaxHp,Damage=Damage,Range=Range,
            Remaining=Remaining,Progress=Progress,RequiredWork=RequiredWork,PopulationProvided=PopulationProvided,LastSeenTime=LastSeenTime,Intimidation=Intimidation,
            Complete=Complete,IsBuilding=IsBuilding,IsResource=IsResource,BuildingKind=BuildingKind,ResourceKind=ResourceKind,Footprint=new List<Cell>(Footprint),Cost=Cost.Copy() };
    }
    [Serializable] public sealed class ActorSnapshot : EntitySnapshot
    {
        public UnitKind Kind;
        public int Vision,OrderRevision;
        public Order Order;
        public LastAction LastAction;
    }
    [Serializable] public sealed class UnitMemory
    {
        public List<TileSnapshot> Terrain=new List<TileSnapshot>();
        public List<EntitySnapshot> Resources=new List<EntitySnapshot>(),Buildings=new List<EntitySnapshot>(),Enemies=new List<EntitySnapshot>();
        public List<Cell> RecentPositions=new List<Cell>();
        public UnitMemory Copy() => new UnitMemory { Terrain=Terrain.Select(t=>t.Copy()).ToList(),Resources=Resources.Select(e=>e.Copy()).ToList(),
            Buildings=Buildings.Select(e=>e.Copy()).ToList(),Enemies=Enemies.Select(e=>e.Copy()).ToList(),RecentPositions=new List<Cell>(RecentPositions) };
    }
    [Serializable] public sealed class UnitObservation
    {
        public double TimeSeconds;
        public int Width,Height;
        public ActorSnapshot Self;
        public FactionState Faction;
        public GameRules Rules;
        public UnitMemory Memory;
        public List<TileSnapshot> VisibleTiles=new List<TileSnapshot>();
        public List<TileSnapshot> PublicTerrain=new List<TileSnapshot>();
        public List<EntitySnapshot> VisibleUnits=new List<EntitySnapshot>(),VisibleBuildings=new List<EntitySnapshot>(),VisibleResources=new List<EntitySnapshot>();
        public List<LegalAction> LegalActions=new List<LegalAction>();
        public string KnownMapAscii;
        public JObject Combat;
        public IEnumerable<Cell> VisibleCells => VisibleTiles.Select(t=>t.Cell);
        public static string BuildingName(BuildingKind kind) => kind==BuildingKind.TownHall?"townhall":kind==BuildingKind.ArcheryRange?"archery_range":kind.ToString().ToLowerInvariant();
        private static JObject Position(Cell cell) => new JObject { ["x"]=cell.X,["y"]=cell.Y };
        private static JObject EntityJson(EntitySnapshot entity,bool remembered=false)
        {
            var json=Position(entity.Cell);json["id"]=entity.Id;
            if(entity.IsResource) { json["kind"]=entity.ResourceKind.ToString().ToLowerInvariant();json["remaining"]=entity.Remaining; }
            else
            {
                json["type"]=entity.KindLabel;json["factionId"]=entity.Faction.ToString().ToLowerInvariant();json["hp"]=entity.Hp;json["maxHp"]=entity.MaxHp;
                if(entity.IsBuilding) { json["complete"]=entity.Complete;json["progress"]=entity.Progress;json["requiredWork"]=entity.RequiredWork;json["remainingWork"]=Math.Max(0,entity.RequiredWork-entity.Progress);
                    json["footprint"]=new JArray(entity.Footprint.Select(Position));json["cost"]=new JObject { ["wood"]=entity.Cost.Wood,["gold"]=entity.Cost.Gold }; }
                else { json["damage"]=entity.Damage;json["attackRange"]=entity.Range;json["factionArchetype"]=entity.Faction.ToString().ToLowerInvariant();json["intimidation"]=entity.Intimidation; }
            }
            if(remembered) json["lastSeenTime"]=entity.LastSeenTime;
            return json;
        }
        private static JObject TileJson(TileSnapshot tile)
        {
            var json=Position(tile.Cell);json["blocked"]=tile.Blocked;if(tile.OccupiedBy!=null) json["occupiedBy"]=tile.OccupiedBy;return json;
        }
        private static JObject OrderJson(Order order)
        {
            if(order==null) return null;
            var json=Position(order.Cell);json["kind"]=order.Kind.ToString().ToLowerInvariant();
            if(order.TargetId!=null) json["targetId"]=order.TargetId;
            if(order.Kind==OrderKind.Gather&&order.GatherResourceKind.HasValue) json["resourceKind"]=order.GatherResourceKind.Value.ToString().ToLowerInvariant();
            if(order.Kind==OrderKind.Build) json["buildingType"]=BuildingName(order.BuildingKind);
            if(order.GroupMembers.Count>0)json["sharedGroup"]=new JObject { ["groupId"]=order.GroupId,["members"]=new JArray(order.GroupMembers),
                ["arrivalChebyshevRadius"]=order.GroupArrivalRadius };
            if(order.Formation.Count>0) json["formation"]=new JObject { ["groupId"]=order.GroupId,["targetAnchor"]=Position(order.FormationAnchor),["offset"]=Position(order.Cell-order.FormationAnchor),
                ["slots"]=new JArray(order.Formation.Select(s=>{ var p=Position(s.Cell);p["unitId"]=s.UnitId;return p; })) };
            return json;
        }
        public JObject ToJObject()
        {
            var self=EntityJson(Self);self["visionRadius"]=Self.Vision;self["orderRevision"]=Self.OrderRevision;self["order"]=OrderJson(Self.Order);
            self["lastAction"]=Self.LastAction==null?null:new JObject { ["timeSeconds"]=Self.LastAction.TimeSeconds,["kind"]=Self.LastAction.Kind.ToString().ToLowerInvariant(),
                ["actionId"]=Self.LastAction.ActionId,["targetId"]=Self.LastAction.TargetId,["ok"]=Self.LastAction.Ok,["reason"]=Self.LastAction.Reason };
            var definitions=new JObject();foreach(var b in Rules.Buildings) definitions[BuildingName(b.Kind)]=new JObject { ["cost"]=new JObject { ["wood"]=b.Cost.Wood,["gold"]=b.Cost.Gold },
                ["requiredWork"]=b.RequiredWork,["maxHp"]=b.MaxHp,["populationProvided"]=b.PopulationProvided,["footprintSizeX"]=b.FootprintSizeX,["footprintSizeY"]=b.FootprintSizeY };
            var observation=new JObject
            {
                ["tick"]=TimeSeconds,["timeSeconds"]=TimeSeconds,["decisionIntervalSeconds"]=Rules.DecisionIntervalSeconds,["self"]=self,["combat"]=Combat?.DeepClone(),
                ["faction"]=new JObject { ["id"]=Faction.Id.ToString().ToLowerInvariant(),["wood"]=Faction.Wood,["gold"]=Faction.Gold,
                    ["population"]=new JObject { ["used"]=Faction.Population.Used,["capacity"]=Faction.Population.Capacity,["reserved"]=Faction.Population.Reserved },
                    ["archetype"]=Faction.Id.ToString().ToLowerInvariant(),["traits"]=new JObject { ["healthMultiplier"]=Faction.Traits.HealthMultiplier,["intimidation"]=Faction.Traits.Intimidation,["fearSusceptibility"]=Faction.Traits.FearSusceptibility } },
                ["map"]=new JObject { ["width"]=Width,["height"]=Height,["knownTerrainAscii"]=KnownMapAscii },
                ["rules"]=new JObject { ["woodPerGather"]=Rules.WoodPerGather,["goldPerGather"]=Rules.GoldPerGather,["unitLimit"]=Rules.PerFactionUnitLimit,["buildingDefinitions"]=definitions,
                    ["extendedMovementVocabulary"]=Rules.ExtendedMovementVocabulary,["publicTerrainRadius"]=Rules.PublicTerrainRadius,["circularUnitVision"]=Rules.CircularUnitVision,
                    ["navigationContract"]="Each listed move contains an explicit cell sequence. JEV selects the whole sequence. The engine executes only those cells, stops on a collision or new order, and never searches or reroutes. Gather/build/attack occur exactly once per selected action." },
                ["visible"]=new JObject { ["tiles"]=new JArray(VisibleTiles.Select(TileJson)),["units"]=new JArray(VisibleUnits.Select(e=>EntityJson(e))),["resources"]=new JArray(VisibleResources.Select(e=>EntityJson(e))),["buildings"]=new JArray(VisibleBuildings.Select(e=>EntityJson(e))) },
                ["memory"]=new JObject { ["terrain"]=new JArray(Memory.Terrain.Select(TileJson)),["resources"]=new JArray(Memory.Resources.Select(e=>EntityJson(e,true))),["buildings"]=new JArray(Memory.Buildings.Select(e=>EntityJson(e,true))),
                    ["enemies"]=new JArray(Memory.Enemies.Select(e=>EntityJson(e,true))),["recentPositions"]=new JArray(Memory.RecentPositions.Select(c=>new JArray(c.X,c.Y))) },
                ["legalActions"]=new JArray(LegalActions.Select(a=> { var j=new JObject { ["id"]=a.Id,["kind"]=a.Kind.ToString().ToLowerInvariant(),["description"]=a.Description,["x"]=a.Cell.X,["y"]=a.Cell.Y };if(a.TargetId!=null)j["targetId"]=a.TargetId;if(a.Kind==ActionKind.Move)j["cells"]=new JArray(a.Cells.Select(Position));if(a.Kind==ActionKind.Build)j["buildingType"]=BuildingName(a.BuildingKind);return j; }))
            };
            if(Rules.PublicLocalTerrain)
                observation["publicTerrain"]=new JObject
                {
                    ["source"]="authored_static_no_dynamicIntel",["radius"]=Math.Max(0,Rules.PublicTerrainRadius>0?Rules.PublicTerrainRadius:Self.Vision),
                    ["contract"]="Only authored static terrain within the local radius, independent of line of sight. No current units, resources or buildings are revealed here. A listed route may meet an unseen occupant; each physical step still checks collisions and stops without rerouting.",
                    ["tiles"]=new JArray(PublicTerrain.Select(TileJson))
                };
            return observation;
        }
    }

    public sealed partial class RtsWorld
    {
        private static readonly int[] ShortTurnLegLengths = { 1, 2 };
        private static readonly int[] ExtendedTurnLegLengths = { 1, 2, 4, 8 };
        public bool HasLineOfSight(Cell from,Cell to,string ignoreBuildingId=null)
        {
            return HasLineOfSight(from,to,SightBlockers(ignoreBuildingId));
        }
        private HashSet<Cell> SightBlockers(string ignoreBuildingId=null)
        {
            var cells=new HashSet<Cell>(SightBlocked??Blocked);
            // Harvestable trees are dynamic occluders; cutting one down opens its sight line.
            // Legacy laboratory maps keep their original visibility contract.
            if(SightBlocked!=null)
                foreach(var resource in Resources.Where(r=>r.Remaining>0&&r.Kind==ResourceKind.Wood))cells.Add(resource.Cell);
            foreach(var building in Buildings.Where(b=>b.Alive&&b.Id!=ignoreBuildingId)) cells.UnionWith(Footprint(building));
            return cells;
        }
        private static bool HasLineOfSight(Cell from,Cell to,HashSet<Cell> opaque)
        {
            bool Blocks(int x,int y)
            {
                var point=new Cell(x,y);if(point==to) return false;
                return opaque.Contains(point);
            }
            int x0=from.X,y0=from.Y,ix=0,iy=0,nx=Math.Abs(to.X-x0),ny=Math.Abs(to.Y-y0),sx=Math.Sign(to.X-x0),sy=Math.Sign(to.Y-y0);
            while(ix<nx||iy<ny)
            {
                int a=(1+2*ix)*ny,b=(1+2*iy)*nx;
                if(a==b) { if(Blocks(x0+sx,y0)||Blocks(x0,y0+sy))return false;x0+=sx;y0+=sy;ix++;iy++; }
                else if(a<b) { x0+=sx;ix++; } else { y0+=sy;iy++; }
                if(Blocks(x0,y0)) return false;
            }
            return true;
        }
        public bool CanSee(UnitState unit,Cell point) =>
            (unit.Cell.X-point.X)*(unit.Cell.X-point.X)+(unit.Cell.Y-point.Y)*(unit.Cell.Y-point.Y)<=unit.Vision*unit.Vision &&
            (Definitions.CircularUnitVision||HasLineOfSight(unit.Cell,point));
        /// <summary>Geometry-only vision for presentation/commander aggregation; no memory, choices or JSON allocation.</summary>
        public HashSet<Cell> VisibleCellsForUnit(string unitId)
        {
            var visible=new HashSet<Cell>();var unit=Unit(unitId);if(unit==null||!unit.Alive)return visible;
            var opaque=Definitions.CircularUnitVision?null:SightBlockers();
            for(int y=Math.Max(0,unit.Cell.Y-unit.Vision);y<=Math.Min(Height-1,unit.Cell.Y+unit.Vision);y++)
                for(int x=Math.Max(0,unit.Cell.X-unit.Vision);x<=Math.Min(Width-1,unit.Cell.X+unit.Vision);x++)
                { int dx=unit.Cell.X-x,dy=unit.Cell.Y-y;var point=new Cell(x,y);if(dx*dx+dy*dy<=unit.Vision*unit.Vision&&(Definitions.CircularUnitVision||HasLineOfSight(unit.Cell,point,opaque)))visible.Add(point); }
            return visible;
        }
        /// <summary>Completed structures see from their footprint perimeter. Their own mesh does not occlude their sensors.</summary>
        public HashSet<Cell> VisibleCellsForBuilding(string buildingId)
        {
            var visible=new HashSet<Cell>();var building=Building(buildingId);if(building==null||!building.Alive||!building.Complete)return visible;
            var footprint=new HashSet<Cell>(Footprint(building));var boundary=footprint.Where(c=>Directions.Any(d=>!footprint.Contains(c+d))).ToArray();
            var opaque=SightBlockers(buildingId);int radius=building.Vision;
            int minX=Math.Max(0,footprint.Min(c=>c.X)-radius),maxX=Math.Min(Width-1,footprint.Max(c=>c.X)+radius);
            int minY=Math.Max(0,footprint.Min(c=>c.Y)-radius),maxY=Math.Min(Height-1,footprint.Max(c=>c.Y)+radius);
            for(int y=minY;y<=maxY;y++)for(int x=minX;x<=maxX;x++)
            {
                var point=new Cell(x,y);if(footprint.Contains(point)){visible.Add(point);continue;}
                foreach(var origin in boundary)if(Cell.Chebyshev(origin,point)<=radius&&HasLineOfSight(origin,point,opaque)){visible.Add(point);break;}
            }
            return visible;
        }
        private EntitySnapshot Snapshot(UnitState unit) => new EntitySnapshot { Id=unit.Id,KindLabel=unit.Kind.ToString().ToLowerInvariant(),Faction=unit.Faction,
            Cell=unit.Cell,Hp=unit.Hp,MaxHp=unit.MaxHp,Damage=unit.Damage,Range=unit.Range,Intimidation=Faction(unit.Faction).Traits.Intimidation };
        private EntitySnapshot Snapshot(BuildingState building) => new EntitySnapshot { Id=building.Id,KindLabel=UnitObservation.BuildingName(building.Kind),Faction=building.Faction,Cell=building.Cell,
            Hp=building.Hp,MaxHp=building.MaxHp,Damage=building.Damage,Range=building.Range,Progress=building.Progress,RequiredWork=building.RequiredWork,Complete=building.Complete,
            PopulationProvided=building.PopulationProvided,Cost=building.Cost.Copy(),IsBuilding=true,BuildingKind=building.Kind,Footprint=Footprint(building).ToList() };
        private static EntitySnapshot Snapshot(ResourceState resource) => new EntitySnapshot { Id=resource.Id,KindLabel=resource.Kind.ToString().ToLowerInvariant(),Cell=resource.Cell,
            IsResource=true,ResourceKind=resource.Kind,Remaining=resource.Remaining };
        private static FactionState CopyFaction(FactionState faction) => new FactionState { Id=faction.Id,Wood=faction.Wood,Gold=faction.Gold,BasePopulationCapacity=faction.BasePopulationCapacity,
            Population=new PopulationState { Used=faction.Population.Used,Capacity=faction.Population.Capacity,Reserved=faction.Population.Reserved },
            Traits=new FactionTraits { HealthMultiplier=faction.Traits.HealthMultiplier,Intimidation=faction.Traits.Intimidation,FearSusceptibility=faction.Traits.FearSusceptibility } };
        public UnitObservation Observe(string unitId,bool updateMemory=true)
        {
            var unit=Unit(unitId);if(unit==null) throw new ArgumentException("Unknown unit "+unitId);
            var o=new UnitObservation { TimeSeconds=TimeSeconds,Width=Width,Height=Height,Faction=CopyFaction(Faction(unit.Faction)),Rules=CopyRules(Definitions),
                Self=new ActorSnapshot { Id=unit.Id,Faction=unit.Faction,Kind=unit.Kind,KindLabel=unit.Kind.ToString().ToLowerInvariant(),Cell=unit.Cell,Hp=unit.Hp,MaxHp=unit.MaxHp,
                    Damage=unit.Damage,Range=unit.Range,Vision=unit.Vision,Order=unit.Order?.Copy(),OrderRevision=unit.OrderRevision,Intimidation=Faction(unit.Faction).Traits.Intimidation,
                    LastAction=unit.LastAction==null?null:new LastAction { TimeSeconds=unit.LastAction.TimeSeconds,Kind=unit.LastAction.Kind,ActionId=unit.LastAction.ActionId,TargetId=unit.LastAction.TargetId,Ok=unit.LastAction.Ok,Reason=unit.LastAction.Reason } } };
            var occupancy=new Dictionary<Cell,string>();
            foreach(var b in Buildings.Where(b=>b.Alive)) foreach(var c in Footprint(b)) occupancy[c]=b.Id;
            foreach(var r in Resources.Where(r=>r.Remaining>0)) occupancy[r.Cell]=r.Id;
            foreach(var u in Units.Where(u=>u.Alive)) occupancy[u.Cell]=u.Id;
            var visibleCells=VisibleCellsForUnit(unitId);
            foreach(var cell in visibleCells.OrderBy(c=>c.Y).ThenBy(c=>c.X))
            { occupancy.TryGetValue(cell,out string occupant);o.VisibleTiles.Add(new TileSnapshot { Cell=cell,Blocked=Blocked.Contains(cell),OccupiedBy=occupant }); }
            if(Definitions.PublicLocalTerrain)
            {
                // Public authored geography is separate from sensors and personal memory. Never consult occupancy here.
                int radius=Math.Max(0,Definitions.PublicTerrainRadius>0?Definitions.PublicTerrainRadius:unit.Vision);
                for(int y=Math.Max(0,unit.Cell.Y-radius);y<=Math.Min(Height-1,unit.Cell.Y+radius);y++)
                    for(int x=Math.Max(0,unit.Cell.X-radius);x<=Math.Min(Width-1,unit.Cell.X+radius);x++)
                    {
                        int dx=x-unit.Cell.X,dy=y-unit.Cell.Y;var cell=new Cell(x,y);
                        if(dx*dx+dy*dy<=radius*radius)o.PublicTerrain.Add(new TileSnapshot{Cell=cell,Blocked=Blocked.Contains(cell)});
                    }
            }
            o.VisibleUnits=Units.Where(u=>u.Alive&&u.Id!=unit.Id&&visibleCells.Contains(u.Cell)).Select(Snapshot).ToList();
            o.VisibleResources=Resources.Where(r=>r.Remaining>0&&visibleCells.Contains(r.Cell)).Select(Snapshot).ToList();
            o.VisibleBuildings=Buildings.Where(b=>b.Alive&&Footprint(b).Any(visibleCells.Contains)).Select(Snapshot).ToList();
            var memory=unit.Memory.Copy();var terrain=memory.Terrain.ToDictionary(t=>t.Cell);
            foreach(var tile in o.VisibleTiles) terrain[tile.Cell]=new TileSnapshot { Cell=tile.Cell,Blocked=tile.Blocked };
            memory.Terrain=terrain.Values.OrderBy(t=>t.Cell.Y).ThenBy(t=>t.Cell.X).ToList();
            memory.Resources=Remember(memory.Resources,o.VisibleResources,visibleCells);
            memory.Buildings=Remember(memory.Buildings,o.VisibleBuildings,visibleCells);
            memory.Enemies=Remember(memory.Enemies,o.VisibleUnits.Where(u=>u.Faction!=unit.Faction),visibleCells);
            // A position trail records travel, not repeated sensor reads while standing still.
            // Keep enough personal history to make longer loops around large buildings visible to JEV.
            if(memory.RecentPositions.Count==0||memory.RecentPositions[memory.RecentPositions.Count-1]!=unit.Cell)
                memory.RecentPositions.Add(unit.Cell);
            if(memory.RecentPositions.Count>32)memory.RecentPositions.RemoveRange(0,memory.RecentPositions.Count-32);
            if(updateMemory) unit.Memory=memory.Copy();o.Memory=memory;
            o.KnownMapAscii=KnownMap(memory,o.VisibleTiles,o.PublicTerrain,o.VisibleBuildings,unit.Cell);o.LegalActions=BuildLegalActions(o,true);
            o.Combat=CombatObservation(o);return o;
        }
        private List<EntitySnapshot> Remember(List<EntitySnapshot> old,IEnumerable<EntitySnapshot> visible,HashSet<Cell> visibleCells)
        {
            var records=old.Where(e=>!visibleCells.Contains(e.Cell)&&(!e.IsBuilding||!e.Footprint.Any(visibleCells.Contains))).ToDictionary(e=>e.Id,e=>e.Copy());
            foreach(var entity in visible) { var copy=entity.Copy();copy.LastSeenTime=TimeSeconds;records[copy.Id]=copy; }
            return records.Values.OrderByDescending(e=>e.LastSeenTime).ThenBy(e=>e.Id,StringComparer.Ordinal).Take(12).ToList();
        }
        private static GameRules CopyRules(GameRules source) => new GameRules
        {
            PerFactionUnitLimit=source.PerFactionUnitLimit,WoodPerGather=source.WoodPerGather,GoldPerGather=source.GoldPerGather,
            DecisionIntervalSeconds=source.DecisionIntervalSeconds,RotatingMovementPriority=source.RotatingMovementPriority,PublicLocalTerrain=source.PublicLocalTerrain,
            PublicTerrainRadius=source.PublicTerrainRadius,ExtendedMovementVocabulary=source.ExtendedMovementVocabulary,CircularUnitVision=source.CircularUnitVision,
            Units=source.Units.Select(u=>new UnitDefinition { Kind=u.Kind,MaxHp=u.MaxHp,Damage=u.Damage,Range=u.Range,Vision=u.Vision,Cost=u.Cost.Copy(),TrainingSeconds=u.TrainingSeconds }).ToList(),
            Buildings=source.Buildings.Select(b=>new BuildingDefinition { Kind=b.Kind,MaxHp=b.MaxHp,RequiredWork=b.RequiredWork,PopulationProvided=b.PopulationProvided,Damage=b.Damage,Range=b.Range,Vision=b.Vision,
                FootprintSizeX=b.FootprintSizeX,FootprintSizeY=b.FootprintSizeY,Cost=b.Cost.Copy(),TrainableUnits=(UnitKind[])b.TrainableUnits.Clone() }).ToList()
        };
        private static string KnownMap(UnitMemory memory,List<TileSnapshot> visible,List<TileSnapshot> publicTerrain,List<EntitySnapshot> visibleBuildings,Cell self)
        {
            var known=memory.Terrain.ToDictionary(t=>t.Cell);var present=visible.ToDictionary(t=>t.Cell);
            foreach(var tile in publicTerrain)known[tile.Cell]=tile;
            // Whole personally known footprints matter even when only one edge is currently visible.
            // No world entities or global occupancy are read by this presentation of the unit's knowledge.
            var currentBuildings=new HashSet<Cell>(visibleBuildings.Where(b=>b.Hp>0).SelectMany(b=>b.Footprint));
            var rememberedBuildings=new HashSet<Cell>(memory.Buildings.Where(b=>b.Hp>0).SelectMany(b=>b.Footprint));
            var extent=new HashSet<Cell>(known.Keys);extent.UnionWith(currentBuildings);extent.UnionWith(rememberedBuildings);extent.Add(self);
            int minX=extent.Min(c=>c.X),maxX=extent.Max(c=>c.X),minY=extent.Min(c=>c.Y),maxY=extent.Max(c=>c.Y);
            var text=new StringBuilder($"Columns x={minX}..{maxX}; rows y={minY}..{maxY}. @ self; # known static wall; B full known footprint of a currently observed building; b remembered building footprint (may be stale); o other currently observed occupant; . known observed/authored static terrain; ? unknown. Full footprints and authored terrain do NOT imply current visibility or empty dynamic occupancy.\n");
            for(int y=minY;y<=maxY;y++)
            {
                text.Append(y).Append(':');
                for(int x=minX;x<=maxX;x++)
                {
                    var p=new Cell(x,y);
                    char mark=p==self?'@':currentBuildings.Contains(p)?'B':present.TryGetValue(p,out var now)&&now.OccupiedBy!=null?'o':
                        rememberedBuildings.Contains(p)?'b':known.TryGetValue(p,out var terrain)?(terrain.Blocked?'#':'.'):'?';
                    text.Append(mark);
                }
                text.Append('\n');
            }
            return text.ToString();
        }
        public List<LegalAction> LegalActions(string unitId,bool includeSegments=true)
        { var unit=Unit(unitId);if(unit==null||!unit.Alive||MatchEnded) return new List<LegalAction>();var o=Observe(unitId,false);return includeSegments?o.LegalActions:BuildLegalActions(o,false); }
        private List<LegalAction> BuildLegalActions(UnitObservation o,bool includeSegments)
        {
            var self=o.Self;var result=new List<LegalAction> { new LegalAction { Id="wait",Kind=ActionKind.Wait,Cell=self.Cell,Description="Wait in place until an observable change, new order or scheduled review. Do not move or repeat work." } };
            var free=new HashSet<Cell>(o.VisibleTiles.Where(t=>!t.Blocked&&t.OccupiedBy==null).Select(t=>t.Cell));
            if(o.Rules.PublicLocalTerrain)
            {
                free.UnionWith(o.PublicTerrain.Where(t=>!t.Blocked).Select(t=>t.Cell));
                // Only facts available to this unit may restrict a proposed sequence. Unseen dynamic entities
                // are checked later by TryExecuteStep, never used here to leak their positions through options.
                free.ExceptWith(o.VisibleTiles.Where(t=>t.Blocked||t.OccupiedBy!=null).Select(t=>t.Cell));
                free.ExceptWith(o.VisibleBuildings.Where(b=>b.Hp>0).SelectMany(b=>b.Footprint));
                free.ExceptWith(o.Memory.Buildings.Where(b=>b.Hp>0).SelectMany(b=>b.Footprint));
                free.Remove(self.Cell);
            }
            // Fixed vocabulary, independent of goals, utility or enemy positions. No route search or ranking.
            bool extended=includeSegments&&o.Rules.ExtendedMovementVocabulary;
            for(int direction=0;direction<4;direction++)
            {
                var cells=new List<Cell>();var cursor=self.Cell;
                for(int length=1;length<=(extended?8:includeSegments?4:1);length++)
                {
                    cursor+=Directions[direction];if(!free.Contains(cursor))break;cells.Add(cursor);
                    AddMove(result,cells,length==1?"move_"+DirectionNames[direction]:"move_"+DirectionNames[direction]+"_"+length);
                }
            }
            if(includeSegments)
                for(int direction=0;direction<4;direction++) foreach(int turn in new[]{(direction+1)%4,(direction+3)%4})
                    foreach(int first in extended?ExtendedTurnLegLengths:ShortTurnLegLengths) foreach(int second in extended?ExtendedTurnLegLengths:ShortTurnLegLengths)
                    {
                        var cursor=self.Cell;var cells=new List<Cell>();
                        for(int n=0;n<first;n++) { cursor+=Directions[direction];cells.Add(cursor); }
                        for(int n=0;n<second;n++) { cursor+=Directions[turn];cells.Add(cursor); }
                        if(cells.All(free.Contains)) AddMove(result,cells,$"move_{DirectionNames[direction]}_{first}_{DirectionNames[turn]}_{second}");
                    }
            HashSet<Cell> attackBlockers=null;
            foreach(var enemy in o.VisibleUnits.Concat(o.VisibleBuildings).Where(e=>e.Faction!=self.Faction&&e.Hp>0))
            {
                var cells=enemy.IsBuilding?enemy.Footprint:new List<Cell>{enemy.Cell};
                bool clearAttack=false;
                foreach(var cell in cells)
                {
                    if(Cell.Chebyshev(self.Cell,cell)>self.Range||!o.VisibleTiles.Any(t=>t.Cell==cell))continue;
                    // Circular exploration is knowledge, not a shot through walls, trees or another structure.
                    if(attackBlockers==null)attackBlockers=SightBlockers();
                    if(HasLineOfSight(self.Cell,cell,attackBlockers)){clearAttack=true;break;}
                }
                if(clearAttack)
                    result.Add(new LegalAction { Id="attack_"+enemy.Id,Kind=ActionKind.Attack,TargetId=enemy.Id,Cell=enemy.Cell,Description=$"Attack visible enemy {enemy.KindLabel} {enemy.Id}, HP {enemy.Hp}, for {self.Damage} damage once." });
            }
            if(self.Kind!=UnitKind.Worker) return result;
            foreach(var resource in o.VisibleResources.Where(r=>Cell.Manhattan(self.Cell,r.Cell)<=1&&r.Remaining>0))
                result.Add(new LegalAction { Id="gather_"+resource.Id,Kind=ActionKind.Gather,TargetId=resource.Id,Cell=resource.Cell,Description=$"Gather up to {(resource.ResourceKind==ResourceKind.Wood?Definitions.WoodPerGather:Definitions.GoldPerGather)} {resource.KindLabel} from {resource.Id} once into your faction stock." });
            foreach(var building in o.VisibleBuildings.Where(b=>b.Faction==self.Faction&&!b.Complete&&b.Footprint.Any(c=>Cell.Manhattan(self.Cell,c)==1)))
                result.Add(new LegalAction { Id="build_"+building.Id,Kind=ActionKind.Build,TargetId=building.Id,Cell=building.Cell,BuildingKind=building.BuildingKind,Description=$"Add one work to {building.Id}; progress {building.Progress}/{building.RequiredWork}. Its price is already paid." });
            if(self.Order?.Kind==OrderKind.Build)
            {
                var spec=Definitions.Building(self.Order.BuildingKind);var footprint=Footprint(spec.Kind,self.Order.Cell);
                // Large authored footprints need not fit entirely inside a worker's sensor radius.
                // Unknown cells reveal no occupancy; authoritative execution still checks the whole site.
                var knownTerrain=o.Memory.Terrain.ToDictionary(t=>t.Cell);
                var visibleTiles=o.VisibleTiles.ToDictionary(t=>t.Cell);
                bool knownAvailable=footprint.All(c=>InBounds(c)&&(!knownTerrain.TryGetValue(c,out var known)||!known.Blocked)&&
                    (!visibleTiles.TryGetValue(c,out var visible)||(!visible.Blocked&&visible.OccupiedBy==null)));
                if(Affordable(o.Faction,spec.Cost)&&footprint.Any(c=>Cell.Manhattan(self.Cell,c)==1)&&knownAvailable)
                    result.Add(new LegalAction { Id="build_order",Kind=ActionKind.Build,Cell=self.Order.Cell,BuildingKind=spec.Kind,Description=$"Start ordered {UnitObservation.BuildingName(spec.Kind)} at {self.Order.Cell}; pay {spec.Cost.Wood} wood and {spec.Cost.Gold} gold once, add first of {spec.RequiredWork} work." });
            }
            return result;
        }
        private static void AddMove(List<LegalAction> actions,List<Cell> cells,string id)
        { actions.Add(new LegalAction { Id=id,Kind=ActionKind.Move,Cell=cells[cells.Count-1],Cells=new List<Cell>(cells),Description="Move only through these explicit adjacent cells in order: "+string.Join(" → ",cells)+". Stop if obstructed; no rerouting." }); }
    }
}
