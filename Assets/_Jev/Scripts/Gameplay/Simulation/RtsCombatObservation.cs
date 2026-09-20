using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Simulation
{
    public sealed partial class RtsWorld
    {
        internal const double CombatDamageWindowSeconds = 6;

        /// <summary>Personal damage and current visible geometry only; no target selection or hidden enemy intelligence.</summary>
        internal JObject CombatObservation(UnitObservation observation)
        {
            var self = observation.Self;
            int damage = 0, hitCount = 0;
            var recentAttackers = new HashSet<string>();
            for (int i = Events.Count - 1; i >= 0; i--)
            {
                var hit = Events[i];
                if (hit.Type != "attack" || hit.TargetId != self.Id || hit.TimeSeconds < TimeSeconds - CombatDamageWindowSeconds) continue;
                damage += hit.Amount; hitCount++; recentAttackers.Add(hit.ActorId);
            }
            var offeredAttacks = new HashSet<string>();
            for (int i = 0; i < observation.LegalActions.Count; i++)
                if (observation.LegalActions[i].Kind == ActionKind.Attack) offeredAttacks.Add(observation.LegalActions[i].TargetId);
            var enemies = observation.VisibleUnits.Concat(observation.VisibleBuildings).Where(e => e.Faction != self.Faction && e.Hp > 0).ToArray();
            return new JObject
            {
                ["damageWindowSeconds"] = CombatDamageWindowSeconds,
                ["receivedDamage"] = damage,
                ["receivedHitCount"] = hitCount,
                ["underAttack"] = hitCount > 0,
                ["visibleThreats"] = new JArray(enemies.Select(enemy => new JObject
                {
                    ["id"] = enemy.Id,
                    ["distanceChebyshev"] = enemy.IsBuilding ? enemy.Footprint.Min(c => Cell.Chebyshev(self.Cell, c)) : Cell.Chebyshev(self.Cell, enemy.Cell),
                    ["hasRecentlyHitSelf"] = recentAttackers.Contains(enemy.Id),
                    ["attackOffered"] = offeredAttacks.Contains(enemy.Id)
                })),
                ["contract"] = "Received damage is this unit's own physical experience. Attacker identities appear only among currently visible enemies; no unseen source or position is revealed. WAIT does not block damage, heal, retaliate or withdraw. Every attack, retreat or approach still needs your physical action choice."
            };
        }
    }
}
