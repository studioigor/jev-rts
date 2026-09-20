using Jev.Gameplay.Presentation;
using Jev.Gameplay.Simulation;
using UnityEngine;

namespace Jev.Gameplay.Runtime
{
    public sealed partial class MatchController
    {
        public InteractionFeedback Feedback;
        public bool CanPresentInteraction(Cell cell) => World != null && playerVisible.Contains(cell);

        void PresentInteraction(WorldEvent item)
        {
            if (!Feedback || item.Amount <= 0 || !CanPresentInteraction(item.To)) return;
            InteractionImpact kind;
            if (item.Type == "gather") kind = item.ResourceKind == ResourceKind.Wood ? InteractionImpact.Wood : InteractionImpact.Mining;
            else if (item.Type == "build_progress") kind = InteractionImpact.Construction;
            else if (item.Type == "attack") kind = InteractionImpact.Combat;
            else return;
            var from = Battlefield.ToWorld(item.From);
            var target = Battlefield.ToWorld(item.To);
            Transform recoil = null;
            if (resourceVisuals.TryGetValue(item.TargetId ?? "", out var resource) && resource)
            {
                // The authored resource origin is the trunk/mine anchor, unlike the quantized grid centre.
                target = resource.transform.position;
                // Both kinds retain their authored visual through the final contact frame.
                recoil = resource.transform;
            }
            else if (unitViews.TryGetValue(item.TargetId ?? "", out var victim) && victim) target = victim.transform.position;
            else if (buildingViews.TryGetValue(item.TargetId ?? "", out var building) && building)
            {
                var surface = building.GetComponent<Collider>();
                if (surface) target = surface.ClosestPoint(from + Vector3.up * .85f) - Vector3.up * .85f;
            }
            Vector3 direction = target - from; direction.y = 0;
            if (direction.sqrMagnitude < .001f) direction = Vector3.forward;
            float delay = .1f;
            if (unitViews.TryGetValue(item.ActorId ?? "", out var actor) && actor)
            {
                actor.RestartInteractionAnimation(direction);
                delay = actor.ClipDuration("Attack") * Feedback.ContactPhase;
            }
            // For construction, place dust at the worker's accessible edge rather than inside a large building.
            var contact = kind == InteractionImpact.Construction ? from + direction.normalized * .85f + Vector3.up * .65f :
                target - direction.normalized * (kind == InteractionImpact.Combat ? .18f : .3f) + Vector3.up * .85f;
            bool depleted = item.Type == "gather" && World.Resource(item.TargetId)?.Remaining == 0;
            Feedback.Queue(kind, contact, -direction, item.To, recoil, delay, depleted);
        }
    }
}
