using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Jev
{
    public enum JevBrainRole { Unit, Commander, Tower, Navigation, Gather }

    /// <summary>
    /// Prompt adaptations of rts-sim v8/v11 and event-wait-v1. The caller supplies personal
    /// observations and every physically legal option; this layer does not search, score or sort routes.
    /// </summary>
    public static class JevPromptBuilder
    {
        public const string DefaultModel = "jev-1.13.0";
        public const string PromptVersion = "jev-rts-unity-v14-active-commander";

        const string UnitInstructions = @"Choose ONE offered physical action to advance this unit's active order while surviving observed threats. All navigation and work are your decisions; no route, automatic work or fallback follows your choice. With no enemy threat, continue an unfinished task instead of WAIT. Unknown terrain does not establish enemy danger. A previous action is not proof of task completion. WAIT neither heals nor performs work.
MOVEMENT: Choose an exact offered cell sequence. Known building rectangles are summarized in navigationGeometry. To pass a large building from its north to its south, first leave its x interval on either side, continue beyond its y interval, then return toward the goal; similarly exchange x/y for east-west travel. You choose which side and every step. Continue around that side until you pass the obstruction, instead of repeatedly returning toward its blocked face. Moving north and south along the same column cannot get past a building occupying that column. Recent positions record up to 32 travel positions, not elapsed time spent standing. Walls may require moving away from the goal initially. Read the map, go along the wall past its end, then turn toward the goal. A blocked direct direction is a reason to choose a detour, not repeatedly WAIT. Do not oscillate between recent positions. When a wall blocks the goal and all immediate endpoints increase distance, explore a new detour along the obstacle. Repeatedly reversing to the cell just left makes no progress; recent visit counts describe actual history, not a path or a forbidden action. For a single-unit MOVE, WAIT at its exact destination. For sharedGroup MOVE, use the shared arrival radius and yield or park without blocking observed group traffic; no member has an allocated slot. Sequences stop on collisions or changed orders and never reroute. If publicTerrain is supplied, its local static geography is known even behind a corner. It contains no hidden units, resources or buildings. An offered sequence may round such a corner before the endpoint becomes currently visible; physical execution still stops if occupied.
PERSISTENT INTENTION: navigationIntent, when mode=waypoint, is YOUR earlier genuine JEV choice of an intermediate destination. Continue toward that point rather than returning toward the original goal's blocked face. Intermediate L1 in each movement choice measures progress to this selected intention; Final order L1 is separate and can temporarily increase during an obstacle bypass. You still choose every physical action and may respond to actual threats or perform the ordered work when in reach. No local pathfinder enforces the intention. At an intermediate point you will review your intention; only the original order's completion permits idle waiting.
Coordinates x/right, y/down map rows. @ is self, # wall, o occupied, . known terrain, ? unknown. Ordered goal is self.order.x/y, not @. Compact horizontal tile runs preserve all observations; tileEncoding and tileColumns explain the inclusive ranges. currentObjective repeats your active order before the sensor tables; public map landmarks are optional geography, not new orders. Goal Manhattan distance ignores obstacles and is not a route or utility score.
LOCAL COMBAT takes precedence over passive waiting, including after MOVE arrival or with no order. combat.receivedDamage and underAttack are actual recent hits, not hypothetical danger. If attacked, choose an offered counterattack or a movement that actually withdraws; WAIT leaves you exposed and is not retreat, cover, parry or healing. Nearby visible hostile units may be engaged to defend visible allies and your settlement even without an explicit ATTACK order. A healthy defender must not ignore an in-range hostile merely because it has no order. A retreat should open distance, not circle back unhealed. Preserve your assigned task for after the local threat. Survival: use actual HP, damage, ranges and numbers. Healthy fighters can fight weaker opponents; wounded units should withdraw from overwhelming enemies and not return unhealed. Workers may fight before resuming work; archers preserve range. Fear is independent and never automatically changes your action. You must select an offered ID exactly.";

        static string TaskInstructions(JObject observation)
        {
            var order=(observation["self"] as JObject)?["order"] as JObject;
            if(order==null)return "NO ORDER: provide local defense even without an explicit attack order. For a warrior or archer, fight visible enemy units threatening you, visible allies or your settlement. Choose an offered attack when effective; if the threat is outside range, choose a safe local approach into attack range. Under overwhelming force choose an actual retreat movement. Do not simply stand taking repeated hits. Stay local and do not chase an unseen enemy or wander without a threat. A worker may defend itself or withdraw. Only with no useful defensive response and no observed threat should you WAIT for a player or commander order.";
            if((string)order["kind"]=="move"&&order["sharedGroup"] is JObject shared)
                return $"GROUP MOVE: all listed members share ONE clicked center ({order["x"]},{order["y"]}) with arrival Chebyshev radius {shared["arrivalChebyshevRadius"]}. You have no allocated personal destination, lane, route or queue order. Choose your own legal movement toward the shared arrival area. Within that radius, choose WAIT or nearby parking/yielding steps using observed traffic so later members can enter; do not camp in a narrow entrance or repeatedly target an occupied exact center. Outside the area, keep making safe progress through necessary detours. Group membership is command information only, not hidden member positions. Every movement or WAIT remains your JEV choice; arrival does not auto-execute parking or a route.";
            switch ((string)order["kind"])
            {
                case "build": return "BUILD: finish a constructed building, not merely reach the site. For a new site compare BOTH stock balances with BOTH costs. If materials are missing, repeatedly gather the missing resource before returning to construct. Nearby resources can be behind you. An unfinished own building is ALREADY PAID: keep choosing build actions until complete. Buildings occupy LARGE rectangular footprints: stand on the OUTSIDE cardinal perimeter, never at the center or inside the planned rectangle. If already inside the unbuilt rectangle, move OUT to its interaction perimeter; WAIT inside cannot construct. Movement alone cannot gather or build. Read orderFacts for geometric and arithmetic facts about the order.";
                case "gather": return "GATHER: the explicit order gives the target resource ID and coordinates even when it is outside current sensors. Missing a visible or remembered resource row does NOT mean depletion: continue navigating toward its commanded coordinates to observe it. Move to a cardinal neighbor of that resource then repeatedly choose gather. Each gather credits the bank immediately. Do not enter its occupied cell. Continue until OBSERVED depletion or a new order; no delivery is needed. If the ordered resource cell is currently observed and the named resource is absent there, depletion is confirmed. The gather assignment persists: a separate JEV continuation choice can select another personally known nearby resource of the same kind. Never walk into or circle the depleted cell. If no replacement is offered, defend against threats or WAIT for changed knowledge or a new order; do not invent a resource. An unseen destination is normal, not a reason to oscillate or WAIT.";
                case "attack": return "ATTACK: approach the specified enemy or its last known position and choose offered attacks while survivable. A building can be attacked at its footprint edge. The center need not be in range.";
                case "hold": return "HOLD: return to and defend the ordered position; attack a reachable enemy without pursuing away from it. Otherwise WAIT at that position.";
                default: return "MOVE: reach the exact ordered cell. At it WAIT unless responding to a visible local threat; before arrival choose movement including necessary detours. Local combat and actual self-defense remain relevant after arrival. No movement runs automatically after an approved sequence.";
            }
        }

        const string FearInstructions = @"Is THIS unit afraid of currently observed or personally remembered enemies? Consider its own fearSusceptibility, known enemy intimidation, health and visible force. Lower susceptibility means less subjective fear. Fear is distinct from sensible tactical withdrawal. Unseen opponents and missing traits are unknown. Return the subjective probability as Noul; this independent measurement never overrides the action Choice.";

        const string NavigationInstructions = @"Choose a persistent intermediate navigation intention for this individual unit's current order. This choice does not move the unit or generate a route. A separate physical JEV decision still chooses every movement sequence, work action and response to danger from all legal options. direct_order means navigating toward the original order with no intermediate point. The other choices are factual outside corners of personally observed or remembered building rectangles, not computed paths. You choose whether a corner helps, which side to use, and how far around the obstacle to go.
When a large building separates you from the order, choose a corner on the destination side that can serve as a useful intermediate point outside the building; for example, with self north and goal south, a southwest or southeast outside corner can hold the intention to go around that side. Consider other known obstacles when choosing. The intention remains until arrival, a changed order or a bounded review; avoid picking a point that simply returns to the blocked face. Once you are beyond the obstruction on the original destination's side, relinquish the corner intention with direct_order and proceed to the actual task. Do not shuttle between corners of a building already behind you. A corner is useful only as part of reaching the ORIGINAL order, never a task of its own. direct_order is active navigation toward the original target, not WAIT. Reaching a corner is not completion of the original task. Distances ignore obstacles and do not rank the options. Remembered facts may be stale. You may always choose direct_order to relinquish an intermediate intention. Choose exactly one offered ID.";

        const string GatherInstructions = @"Continue this worker's existing resource assignment after personally confirmed depletion. Choose ONE offered next resource of the SAME kind from the supplied current observations or personal memory within the stated local work area. Prefer useful nearby continuation while accounting for known obstacles, occupancy and actual threats; memory may be stale and geometric distance is not a route. Choosing a target updates only the assignment: subsequent independent physical JEV choices perform every movement and gather. No target is chosen locally and no automatic work follows. keep_assignment defers target selection for a tactical response; it does not restore the depleted node or complete the resource assignment. Without an observed threat, select a useful offered replacement instead of idling beside an empty node. Never invent an unseen resource. Return an offered ID exactly.";

        const string CommanderInstructions = @"You are the opposing faction's strategic commander in an RTS. Choose ONE offered macro command to build a sustainable economy, recruit a balanced force, protect your settlement and defeat the enemy town hall. Your choices are genuine strategic decisions; no scripted build order or hidden policy will act for you. Read current faction stocks, population used/reserved/capacity, unit assignments, construction progress, recruitment queues, known resources, observed threats and available build sites. Facts marked last-seen can be stale; do not infer unseen enemy positions.
Grow from your town hall and initial worker. Keep workers usefully gathering both resources; recruit additional workers and defenders when affordable. Houses provide population capacity, barracks train melee units, archery ranges train archers, and towers defend an area. Use exact catalog costs and requirements. Complete useful unfinished construction; do not repeatedly replace a worker's unfinished useful order. Diversify spending when useful, preserve resources for prerequisites, and avoid issuing repeated identical orders or flooding one training queue. Population reservations already count toward capacity and the configured hard cap.
Choose when to expand, where to build from the offered authored sites, whom to assign to gather/build/scout/defend/attack, and when to gather an army for a coordinated attack. Use supplied force and threat facts; there are no guaranteed timing thresholds. Protect workers and the town hall from known attacks, preserve useful surviving units, and exploit an opportunity to destroy the enemy settlement.
ACTIVE REVIEW: decide what useful improvement to issue NOW. A previous WAIT is not an instruction to keep waiting. WAIT changes nothing: idle workers stay idle, an unassigned army receives no mission, and saved stocks do not recruit defenders. Use WAIT when no useful strategic change is available, including when existing work is genuinely progressing and further changes would only disrupt it. Do not repeatedly WAIT while a known income shortage, usable idle labor, an affordable reinforcement or a visible attack has an offered useful response. Compare the current facts again on every review; do not assume an earlier choice resolved them.
Assignments are intentions, not completed work. Read staffing and each worker's assignmentFacts: completed_build means that exact building is already finished, so its old Build label is not ongoing construction to preserve. without_order means no task was assigned. Actual creditedWood/Gold measure physical work; assignedWorkers does not prove progress. lastMacroExecution confirms command acceptance only.
At the worker-selection stage, choose who carries out the already selected task or cancel_task to reconsider it. Each assign option states the factual resource-assignment counts before and after replacing that worker's order. Consider whether reassignment would stop the only current source of a resource while other workers have no order or completed work. Nearness alone does not establish the best long-term assignment; compare current productivity, completed work, travel and the needs of the whole faction. Counts are intentions, not promised income, and do not select a worker for you.
At the strategic stage, consider opportunity costs: another worker costs resources and arrives without any assignment; recruitment alone cannot increase production. Compare existing unassigned/completed workers, actual income, queues and readyMilitaryProduction before spending. Housing's effectivePopulationGain already respects the hard cap and may be zero. Military production buildings unlock their listed unit types; an economy and empty production buildings cannot attack by themselves. Decide how to convert sustainable resources into a viable army while preserving useful work and reacting to threats. These are strategic tradeoffs, not a fixed build order or mandatory staffing ratio.
PRODUCTIVE LABOR: when a required resource has no recent credited income, a Gather label alone is not a reason to leave the shortage unresolved. Compare that worker's travel and current target evidence with other known resources and idle or completed workers. Choose a useful resource task and then a suitable worker; prefer making existing usable labor productive over purchasing another unassigned worker. Zero credits in a short window can mean travel, so preserve genuine progress while addressing sustained inactivity. Never infer that a selected task or recruited worker has already started gathering.
DEFENSE AND PRESSURE: visible enemies approaching or damaging the settlement require a timely strategic response. Coordinate existing military units against a viable observed threat and recruit useful affordable reinforcements where possible; waiting for a building to be destroyed does not improve defense. Do not keep purchasing unassigned workers while usable labor is idle and the army needs help. With production, resources and spare capacity, consider strengthening and giving the army a useful scouting or assault mission instead of leaving it permanently unassigned. Respect actual force balance, observed obstructions and stale enemy information; you choose the response and may decline a futile assault.
Each offered option is an exact command with described consequences and ownership/economic prerequisites. Option order is not priority. All unit navigation and subsequent physical work are chosen by each individual JEV brain. A macro order is not a hidden route, automatic work loop or permission to invent units/resources. Do not assume that issuing a construction order completed its building. Choose exactly one offered ID.";

        const string TowerInstructions = @"Control this defensive tower's next single action. Use its actual HP, damage, range, line of sight and current visible enemies. Select one offered attack when it usefully defends your faction; decide the target yourself from observed threats, their durability, range and danger to nearby friendly units or buildings. Only offered targets are physically attackable. There is no automatic target selector. WAIT if no useful attack exists. Each shot executes once and a later shot requires another JEV choice. Unknown enemies are unknown, option order is not priority, and you must return exactly one offered ID.";

        public static JObject BuildUnit(JObject observation, IDictionary<string, string> legalOptions)
            => Build(JevBrainRole.Unit, observation, legalOptions);

        public static JObject BuildCommander(JObject observation, IDictionary<string, string> legalOptions)
            => Build(JevBrainRole.Commander, observation, legalOptions);

        public static JObject BuildTower(JObject observation, IDictionary<string, string> legalOptions)
            => Build(JevBrainRole.Tower, observation, legalOptions);

        public static JObject BuildGather(JObject observation, IDictionary<string, string> legalOptions)
            => Build(JevBrainRole.Gather, observation, legalOptions);

        public static JObject BuildNavigation(JObject observation, IDictionary<string, string> legalOptions)
            => Build(JevBrainRole.Navigation, observation, legalOptions);

        public static JObject Build(JevBrainRole role, JObject observation, IDictionary<string, string> legalOptions)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (legalOptions == null || legalOptions.Count == 0 || legalOptions.Count > JevResponseValidator.MaximumChoices)
                throw new ArgumentException("Supply every legal option, between 1 and 255 choices.", nameof(legalOptions));
            var criteria = new JObject();
            foreach (var option in legalOptions)
            {
                if (string.IsNullOrWhiteSpace(option.Key) || string.IsNullOrWhiteSpace(option.Value))
                    throw new ArgumentException("Each legal action needs a nonempty ID and physical effect.", nameof(legalOptions));
                criteria.Add(option.Key, option.Value);
            }
            string instructions;
            switch (role)
            {
                case JevBrainRole.Unit: instructions = TaskInstructions(observation) + "\n" + UnitInstructions; JevObservationEncoding.AnnotateCriteria(observation, criteria); break;
                case JevBrainRole.Commander: instructions = CommanderInstructions; break;
                case JevBrainRole.Tower: instructions = TowerInstructions; break;
                case JevBrainRole.Navigation: instructions = NavigationInstructions; break;
                case JevBrainRole.Gather: instructions = GatherInstructions; break;
                default: throw new ArgumentOutOfRangeException(nameof(role));
            }
            var questions = new JObject
            {
                ["action"] = new JObject { ["type"] = "choice", ["instructions"] = instructions, ["criteria"] = criteria }
            };
            if (role == JevBrainRole.Unit)
                questions["fear"] = new JObject { ["type"] = "noul", ["instructions"] = FearInstructions };
            return new JObject
            {
                ["model"] = DefaultModel,
                ["state"] = role == JevBrainRole.Unit || role == JevBrainRole.Navigation || role == JevBrainRole.Gather ? JevObservationEncoding.Compact(observation) : observation.DeepClone(),
                ["questions"] = questions
            };
        }
    }
}
