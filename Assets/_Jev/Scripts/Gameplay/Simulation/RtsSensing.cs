using System;
using System.Collections.Generic;

namespace Jev.Gameplay.Simulation
{
    /// <summary>Reusable change detector. It contains no actions, routes or hidden contact identities.</summary>
    public sealed class UnitSensingSnapshot
    {
        public ulong Signature;
        public readonly List<string> VisibleEnemyIds = new List<string>(16);
        public int ReceivedDamage, ReceivedHitCount;
        public bool UnderAttack => ReceivedHitCount > 0;
    }

    public sealed partial class RtsWorld
    {
        /// <summary>
        /// Detect personal changes without constructing a decision request. In the circular RTS
        /// vision mode a reused snapshot allocates nothing after its contact list has warmed up.
        /// A real decision still calls Observe and receives the complete, unchanged observation.
        /// </summary>
        public UnitSensingSnapshot SenseUnit(string unitId, UnitSensingSnapshot reuse = null)
        {
            var self = Unit(unitId);
            if (self == null) throw new ArgumentException("Unknown unit " + unitId);
            var result = reuse ?? new UnitSensingSnapshot();
            result.VisibleEnemyIds.Clear(); result.ReceivedDamage = 0; result.ReceivedHitCount = 0;
            var hash = new SensingHash(); hash.Begin();
            hash.Add(self.Id); hash.Add((int)self.Faction); hash.Add((int)self.Kind); hash.Add(self.Cell);
            hash.Add(self.Hp); hash.Add(self.MaxHp); hash.Add(self.Damage); hash.Add(self.Range); hash.Add(self.Vision);
            hash.Add(self.OrderRevision);
            AddSensingOrder(ref hash, self.Order);
            var faction = Faction(self.Faction);
            hash.Add(faction.Traits.HealthMultiplier.GetHashCode()); hash.Add(faction.Traits.Intimidation.GetHashCode());
            hash.Add(faction.Traits.FearSusceptibility.GetHashCode());
            hash.Add(Width); hash.Add(Height); hash.Add(Definitions.CircularUnitVision);
            hash.Add(Definitions.PublicLocalTerrain); hash.Add(Definitions.PublicTerrainRadius);
            hash.Add(Definitions.ExtendedMovementVocabulary);

            var opaque = Definitions.CircularUnitVision ? null : SightBlockers();
            int publicRadius = Definitions.PublicLocalTerrain
                ? Math.Max(0, Definitions.PublicTerrainRadius > 0 ? Definitions.PublicTerrainRadius : self.Vision) : 0;
            int radius = Math.Max(self.Vision, publicRadius);
            // Terrain/visibility geometry determines the movement vocabulary, but constructing that
            // vocabulary, memory copies, map ASCII and JSON is unnecessary just to detect a change.
            for (int y = Math.Max(0, self.Cell.Y - radius); y <= Math.Min(Height - 1, self.Cell.Y + radius); y++)
            for (int x = Math.Max(0, self.Cell.X - radius); x <= Math.Min(Width - 1, self.Cell.X + radius); x++)
            {
                var cell = new Cell(x, y); int dx = x - self.Cell.X, dy = y - self.Cell.Y, distance = dx * dx + dy * dy;
                bool visible = distance <= self.Vision * self.Vision && (opaque == null || HasLineOfSight(self.Cell, cell, opaque));
                bool knownPublic = Definitions.PublicLocalTerrain && distance <= publicRadius * publicRadius;
                if (!visible && !knownPublic) continue;
                hash.Add(cell); hash.Add((visible ? 1 : 0) | (knownPublic ? 2 : 0) | (Blocked.Contains(cell) ? 4 : 0)
                    | (SightBlocked != null && SightBlocked.Contains(cell) ? 8 : 0));
            }
            hash.Add(-11);
            for (int i = 0; i < Units.Count; i++)
            {
                var other = Units[i];
                if (!other.Alive || other.Id == self.Id || !SensingCanSee(self, other.Cell, opaque)) continue;
                hash.Add(other.Id); hash.Add(other.Cell); hash.Add((int)other.Kind); hash.Add((int)other.Faction);
                hash.Add(other.Hp); hash.Add(other.MaxHp); hash.Add(other.Damage); hash.Add(other.Range);
                hash.Add(Faction(other.Faction).Traits.Intimidation.GetHashCode());
                if (other.Faction != self.Faction) result.VisibleEnemyIds.Add(other.Id);
            }
            hash.Add(-12);
            for (int i = 0; i < Resources.Count; i++)
            {
                var resource = Resources[i];
                if (resource.Remaining <= 0 || !SensingCanSee(self, resource.Cell, opaque)) continue;
                hash.Add(resource.Id); hash.Add(resource.Cell); hash.Add((int)resource.Kind); hash.Add(resource.Remaining);
            }
            hash.Add(-13);
            bool orderedConstructionPaid = false;
            for (int i = 0; i < Buildings.Count; i++)
            {
                var building = Buildings[i];
                if (!building.Alive || !SensingCanSeeBuilding(self, building.Cell, building.Footprint, opaque)) continue;
                hash.Add(building.Id); hash.Add(building.Cell); hash.Add((int)building.Kind); hash.Add((int)building.Faction);
                hash.Add(building.Hp); hash.Add(building.MaxHp); hash.Add(building.Progress); hash.Add(building.RequiredWork);
                hash.Add(building.Complete); hash.Add(building.Damage); hash.Add(building.Range);
                hash.Add(building.Cost.Wood); hash.Add(building.Cost.Gold);
                hash.Add(building.Footprint.Count);
                for (int n = 0; n < building.Footprint.Count; n++) hash.Add(building.Footprint[n]);
                if (building.Faction != self.Faction) result.VisibleEnemyIds.Add(building.Id);
                if (SensingMatchesConstruction(self, building.Faction, building.Kind, building.Cell)) orderedConstructionPaid = true;
            }
            // A personally remembered foundation is paid, even outside current vision. Conversely,
            // a now-visible empty site invalidates that stale memory, just as Observe/Remember does.
            if (self.Order?.Kind == OrderKind.Build && !orderedConstructionPaid)
                for (int i = 0; i < self.Memory.Buildings.Count; i++)
                {
                    var building = self.Memory.Buildings[i];
                    if (building.Hp > 0 && SensingMatchesConstruction(self, building.Faction, building.BuildingKind, building.Cell)
                        && !SensingCanSeeBuilding(self, building.Cell, building.Footprint, opaque))
                    { orderedConstructionPaid = true; break; }
                }
            bool waitingForConstructionFunds = self.Order?.Kind == OrderKind.Build && !orderedConstructionPaid;
            hash.Add(waitingForConstructionFunds);
            if (waitingForConstructionFunds)
            {
                hash.Add(faction.Wood); hash.Add(faction.Gold);
                hash.Add(faction.Population.Used); hash.Add(faction.Population.Capacity); hash.Add(faction.Population.Reserved);
            }
            hash.Add(-14);
            for (int i = Events.Count - 1; i >= 0; i--)
            {
                var hit = Events[i];
                if (hit.Type != "attack" || hit.TargetId != self.Id || hit.TimeSeconds < TimeSeconds - CombatDamageWindowSeconds) continue;
                result.ReceivedDamage += hit.Amount; result.ReceivedHitCount++;
                // Sequence notices a fresh equal-size hit even when another just expired. No unseen
                // attacker ID/position is exposed, and advancing the clock alone does not wake a unit.
                hash.Add(hit.Sequence); hash.Add(hit.Amount);
            }
            result.Signature = hash.Value;
            return result;
        }

        bool SensingCanSee(UnitState self, Cell cell, HashSet<Cell> opaque)
        {
            if (!InBounds(cell)) return false;
            int dx = cell.X - self.Cell.X, dy = cell.Y - self.Cell.Y;
            return dx * dx + dy * dy <= self.Vision * self.Vision && (opaque == null || HasLineOfSight(self.Cell, cell, opaque));
        }

        bool SensingCanSeeBuilding(UnitState self, Cell center, List<Cell> footprint, HashSet<Cell> opaque)
        {
            if (footprint.Count == 0) return SensingCanSee(self, center, opaque);
            for (int i = 0; i < footprint.Count; i++) if (SensingCanSee(self, footprint[i], opaque)) return true;
            return false;
        }

        static bool SensingMatchesConstruction(UnitState self, FactionId faction, BuildingKind kind, Cell center) =>
            self.Order?.Kind == OrderKind.Build && self.Faction == faction && self.Order.BuildingKind == kind && self.Order.Cell == center;

        static void AddSensingOrder(ref SensingHash hash, Order order)
        {
            hash.Add(order != null); if (order == null) return;
            hash.Add((int)order.Kind); hash.Add(order.Cell); hash.Add(order.TargetId);
            hash.Add(order.GatherResourceKind.HasValue ? (int)order.GatherResourceKind.Value : -1);
            hash.Add((int)order.BuildingKind); hash.Add(order.GroupId); hash.Add(order.GroupArrivalRadius);
            hash.Add(order.GroupMembers.Count);
            for (int i = 0; i < order.GroupMembers.Count; i++) hash.Add(order.GroupMembers[i]);
            hash.Add(order.FormationAnchor); hash.Add(order.Formation.Count);
            for (int i = 0; i < order.Formation.Count; i++) { hash.Add(order.Formation[i].UnitId); hash.Add(order.Formation[i].Cell); }
        }

        struct SensingHash
        {
            public ulong Value;
            public void Begin() => Value = 14695981039346656037UL;
            public void Add(long value) { unchecked { Value ^= (ulong)value; Value *= 1099511628211UL; } }
            public void Add(bool value) => Add(value ? 1 : 0);
            public void Add(Cell value) { Add(value.X); Add(value.Y); }
            public void Add(string value)
            {
                Add(value?.Length ?? -1);
                if (value != null) for (int i = 0; i < value.Length; i++) Add(value[i]);
            }
        }
    }
}
