using System.Collections.Generic;
using Jev.Gameplay.Audio;
using Jev.Gameplay.Runtime;
using Jev.Gameplay.Simulation;
using UnityEngine;

namespace Jev.Gameplay.Presentation
{
    public enum InteractionImpact { Wood, Mining, Construction, Combat }

    /// <summary>Presentation of confirmed simulation events. All effects and markers are authored pools.</summary>
    [DisallowMultipleComponent]
    public sealed class InteractionFeedback : MonoBehaviour
    {
        public ParticleSystem[] Wood, Mining, Construction, Combat;
        public LineRenderer[] DestinationRings;
        [Range(.1f, .8f)] public float ContactPhase = .38f;
        [Range(0, 3)] public float TreeRecoilDegrees = 1.15f;
        [Min(.1f)] public float RecoilDuration = .42f;
        [Min(.1f)] public float TreeFallDuration = 1.1f;
        public Color HumanOrderColor = new Color(1, .77f, .28f, .9f);
        public Color UndeadOrderColor = new Color(.20f, .9f, .8f, .9f);
        readonly List<Pending> pending = new List<Pending>();
        readonly List<Recoil> recoils = new List<Recoil>();
        readonly Dictionary<ParticleSystem, Cell> activeBursts = new Dictionary<ParticleSystem, Cell>();
        readonly List<ParticleSystem> finishedBursts = new List<ParticleSystem>();
        readonly HashSet<Cell> markedCells = new HashSet<Cell>();
        readonly int[] cursor = new int[4];
        MatchController owner;
        string session;
        public int PlayedImpacts { get; private set; }
        public int VisibleDestinationCount { get; private set; }
        public int PendingCount => pending.Count;

        struct Pending
        {
            public InteractionImpact Kind;
            public Vector3 Position, Direction;
            public Transform Target;
            public Cell Cell;
            public float Due;
            public bool Depleted;
        }
        sealed class Recoil
        {
            public Transform Target;
            public Quaternion Rest;
            public Vector3 Axis;
            public float Start;
            public bool Falling;
        }

        public void Bind(MatchController controller)
        {
            if (owner == controller && session == controller.SessionId) return;
            Clear(); owner = controller; session = controller.SessionId;
        }

        public void Queue(InteractionImpact kind, Vector3 position, Vector3 direction, Cell cell, Transform target, float delay, bool depleted = false)
        {
            // A bounded visual queue never blocks or changes the authoritative simulation.
            if (pending.Count >= 128) pending.RemoveAt(0);
            pending.Add(new Pending { Kind = kind, Position = position, Direction = direction, Cell = cell,
                Target = target, Due = Time.time + Mathf.Max(0, delay), Depleted = depleted });
        }

        public bool RetainResource(Transform target)
        {
            if (!target) return false;
            foreach (var item in pending) if (item.Target == target) return true;
            foreach (var item in recoils) if (item.Target == target) return true;
            return false;
        }

        void Update()
        {
            if (owner && owner.World == null)
            {
                if (pending.Count > 0 || recoils.Count > 0 || activeBursts.Count > 0 || VisibleDestinationCount > 0) Clear();
                return;
            }
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var item = pending[i];
                if (Time.time < item.Due) continue;
                pending.RemoveAt(i);
                if (owner && !owner.CanPresentInteraction(item.Cell)) continue;
                Play(item);
            }
            finishedBursts.Clear();
            foreach (var burst in activeBursts)
            {
                if (owner && !owner.CanPresentInteraction(burst.Value)) burst.Key.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                if (!burst.Key.IsAlive(true)) finishedBursts.Add(burst.Key);
            }
            foreach (var burst in finishedBursts) activeBursts.Remove(burst);
            for (int i = recoils.Count - 1; i >= 0; i--)
            {
                var recoil = recoils[i];
                if (!recoil.Target) { recoils.RemoveAt(i); continue; }
                float phase = Mathf.Clamp01((Time.time - recoil.Start) / (recoil.Falling ? TreeFallDuration : RecoilDuration));
                recoil.Target.localRotation = recoil.Rest * Quaternion.AngleAxis(
                    recoil.Falling ? phase * phase * 82 : Mathf.Sin(phase * Mathf.PI * 3) * (1 - phase) * TreeRecoilDegrees, recoil.Axis);
                if (phase >= 1)
                {
                    // Hide before restoring the authored transform; a new match reuses the same scenery.
                    if (recoil.Falling) recoil.Target.gameObject.SetActive(false);
                    recoil.Target.localRotation = recoil.Rest; recoils.RemoveAt(i);
                }
            }
            UpdateMarkers();
        }

        void Play(Pending item)
        {
            var pool = item.Kind == InteractionImpact.Wood ? Wood : item.Kind == InteractionImpact.Mining ? Mining :
                item.Kind == InteractionImpact.Construction ? Construction : Combat;
            if (pool != null && pool.Length > 0)
            {
                var effect = pool[cursor[(int)item.Kind]++ % pool.Length];
                if (effect)
                {
                    effect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    effect.transform.position = item.Position;
                    var direction = item.Direction.sqrMagnitude > .001f ? item.Direction.normalized : Vector3.forward;
                    effect.transform.rotation = Quaternion.LookRotation((Vector3.up + direction * .55f).normalized);
                    effect.Play(true);
                    activeBursts[effect] = item.Cell;
                }
            }
            var sound = item.Kind == InteractionImpact.Wood ? GameSound.Chop : item.Kind == InteractionImpact.Mining ? GameSound.Mine :
                item.Kind == InteractionImpact.Construction ? GameSound.Build : GameSound.Hit;
            ProceduralAudio.Instance?.Play(sound, item.Position, true);
            PlayedImpacts++;
            if (item.Kind != InteractionImpact.Wood || !item.Target || !item.Target.gameObject.activeInHierarchy) return;
            var existing = recoils.Find(r => r.Target == item.Target);
            if (existing != null)
            {
                existing.Start = Time.time;
                if (item.Depleted && !existing.Falling) existing.Axis = -existing.Axis;
                existing.Falling |= item.Depleted; return;
            }
            Vector3 localDirection = item.Target.InverseTransformDirection(item.Direction);
            // A final strike tips the tree away from the worker; ordinary hits use a tiny spring recoil.
            Vector3 axis = Vector3.Cross(Vector3.up, item.Depleted ? -localDirection : localDirection).normalized;
            recoils.Add(new Recoil { Target = item.Target, Rest = item.Target.localRotation, Axis = axis, Start = Time.time, Falling = item.Depleted });
        }

        void UpdateMarkers()
        {
            VisibleDestinationCount = 0; markedCells.Clear();
            if (DestinationRings == null) return;
            if (owner && owner.World != null)
            foreach (var id in owner.SelectedIds)
            {
                var unit = owner.World.Unit(id);
                if (unit == null || !unit.Alive || unit.Faction != owner.PlayerFaction || unit.Order?.Kind != OrderKind.Move) continue;
                var order = unit.Order;
                if (Cell.Chebyshev(unit.Cell, order.Cell) <= order.GroupArrivalRadius) continue;
                if (!markedCells.Add(order.Cell) || VisibleDestinationCount >= DestinationRings.Length) continue;
                var ring = DestinationRings[VisibleDestinationCount++];
                ring.gameObject.SetActive(true);
                ring.transform.position = owner.Battlefield.ToWorld(order.Cell) + Vector3.up * .075f;
                float pulse = 1 + .05f * Mathf.Sin(Time.time * 4);
                ring.transform.localScale = new Vector3(pulse, pulse, pulse);
                ring.startColor = ring.endColor = owner.PlayerFaction == FactionId.Human ? HumanOrderColor : UndeadOrderColor;
            }
            for (int i = VisibleDestinationCount; i < DestinationRings.Length; i++) DestinationRings[i].gameObject.SetActive(false);
        }

        public void Clear()
        {
            pending.Clear();
            activeBursts.Clear();
            foreach (var recoil in recoils) if (recoil.Target) recoil.Target.localRotation = recoil.Rest;
            recoils.Clear();
            foreach (var pool in new[] { Wood, Mining, Construction, Combat })
                if (pool != null) foreach (var effect in pool) if (effect) effect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (DestinationRings != null) foreach (var ring in DestinationRings) if (ring) ring.gameObject.SetActive(false);
            VisibleDestinationCount = 0;
        }
        void OnDisable() => Clear();
    }
}
