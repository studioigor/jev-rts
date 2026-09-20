using System.Collections.Generic;
using Jev.Gameplay.Authoring;
using Jev.Gameplay.Simulation;
using UnityEngine;

namespace Jev.Gameplay.Diagnostics
{
    [CreateAssetMenu(menuName="Jev RTS/Diagnostics/Navigation Scenario")]
    public sealed class NavigationScenarioDefinition : ScriptableObject
    {
        public BattlefieldDefinition Battlefield;
        public MatchSettings Settings;
        public FactionId Faction = FactionId.Human;
        public UnitKind UnitKind = UnitKind.Warrior;
        public List<Cell> Starts = new List<Cell>();
        public Cell Target;
        [Min(1)] public float RequiredHoldSeconds = 12;
        [Min(10)] public float TimeoutSeconds = 300;
        public int PassageMinX = 11, PassageMaxX = 13, PassageY = 9;
    }
}
