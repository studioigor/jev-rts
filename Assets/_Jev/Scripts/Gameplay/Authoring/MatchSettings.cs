using UnityEngine;
using Jev.Gameplay.Simulation;

namespace Jev.Gameplay.Authoring
{
    [CreateAssetMenu(menuName = "Jev RTS/Match Settings", fileName = "MatchSettings")]
    public sealed class MatchSettings : ScriptableObject
    {
        [Range(1,60)] public int DefaultUnitCap = 20;
        public int StartingWood = 60, StartingGold = 40;
        [Min(.25f)] public float DecisionInterval = 1;
        [Min(0), Tooltip("Request the next genuine JEV choice near the end of an already committed step or work animation. Execution still waits for that presentation to finish.")]
        public float DecisionPrefetchSeconds = 1;
        [Min(1)] public float IdleHeartbeat = 10;
        [Min(.1f)] public float SensorInterval = .8f;
        [Header("Main-thread decision scheduling")]
        [Range(1,16), Tooltip("Spread simultaneous unit reviews over frames without changing their decision or sensor intervals.")]
        public int DecisionPreparationsPerFrame = 2;
        [Min(.1f)] public float DecisionPreparationBudgetMilliseconds = 2;
        [Range(1,32)] public int SensorReviewsPerFrame = 4;
        [Range(1,16)] public int DecisionCompletionsPerFrame = 4;
        [Header("Action timing")]
        [Min(.1f)] public float StepDuration = .48f;
        [Min(.1f)] public float WorkDuration = .75f;
        [Min(1)] public float CommanderInterval = 4;
        public bool EnableShortSegments = true;
        [Tooltip("Units know nearby static geography behind obstacles; enemy positions and buildings still require personal observation.")]
        public bool PublicTerrainNavigation = true;
        [Min(0), Tooltip("Radius of authored static geography, independent of entity vision and fog. Zero uses each unit's vision radius.")]
        public int PublicTerrainRadius = 12;
        [Tooltip("Offer fixed longer literal segments around large authored buildings. JEV chooses the exact cells; no pathfinding or ranking is added.")]
        public bool ExtendedMovementVocabulary = true;
        public bool EnableEnemyCommander = true;
        public bool EnableFogOfWar = true;

        [Header("Economy and combat")]
        [Tooltip("Editable costs, health, damage, vision, gather yields, construction work and training time. Match cap and decision interval use the fields above; footprints come from the prefab catalog.")]
        public GameRules Rules = new GameRules();

        /// <summary>Each match gets an independent rules snapshot; playing cannot mutate the authoring asset.</summary>
        public GameRules CreateRules(int unitCap)
        {
            var rules = JsonUtility.FromJson<GameRules>(JsonUtility.ToJson(Rules ?? new GameRules()));
            rules.PerFactionUnitLimit = Mathf.Clamp(unitCap, 1, 60);
            rules.DecisionIntervalSeconds = DecisionInterval;
            return rules;
        }
    }
}
