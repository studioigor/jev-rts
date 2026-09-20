using Jev.Gameplay.Audio;
using Jev.Gameplay.Simulation;
using UnityEngine;

namespace Jev.Gameplay.Presentation
{
    [DisallowMultipleComponent]
    public sealed class UnitView : MonoBehaviour
    {
        [SerializeField] Animator animator;
        [SerializeField] GameObject selectionRing;
        [SerializeField, Range(.05f, .4f)] float transitionDuration = .16f;
        [SerializeField] float turnSpeed = 720;
        [SerializeField, Min(4)] float corpseLifetime = 20;
        [SerializeField] Color humanSelection = new Color(.88f, .72f, .38f), undeadSelection = new Color(.20f, .83f, .76f);
        string action = "";
        bool dead;
        bool applyPoseOnEnable;
        float deathTime;
        Quaternion targetRotation;
        public string Id { get; private set; }
        public UnitKind Kind { get; private set; }
        public bool CorpseExpired => dead && Time.time - deathTime >= corpseLifetime;

        void Awake() { if (animator != null) animator.keepAnimatorStateOnDisable = true; }

        void OnEnable()
        {
            if (animator == null || string.IsNullOrEmpty(action) || (!applyPoseOnEnable && !dead)) return;
            float phase = dead ? Mathf.Clamp01((Time.time - deathTime) / Mathf.Max(.01f, ClipDuration("Death"))) : 0;
            animator.Play(action, 0, phase);
            animator.Update(0);
            applyPoseOnEnable = false;
        }

        public void Initialize(string id, UnitKind kind, FactionId faction = FactionId.Human)
        {
            Id = id; Kind = kind; dead = false; action = ""; applyPoseOnEnable = false;
            targetRotation = transform.rotation;
            if (selectionRing != null)
            {
                var ring = selectionRing.GetComponent<LineRenderer>();
                if (ring != null) ring.startColor = ring.endColor = faction == FactionId.Human ? humanSelection : undeadSelection;
            }
            SetSelected(false);
            if (animator != null) { animator.applyRootMotion = false; animator.keepAnimatorStateOnDisable = true; animator.Play("Idle", 0, Random.value); }
            action = "Idle";
        }

        public void SetPose(Vector3 position, Vector3 facing, string requestedAction)
        {
            transform.position = position;
            facing.y = 0;
            if (!dead && facing.sqrMagnitude > .001f) targetRotation = Quaternion.LookRotation(facing);
            if (!dead) SetAction(requestedAction);
        }

        public void SetAction(string requestedAction)
        {
            string next = requestedAction == "Walk" || requestedAction == "Run" || requestedAction == "Attack" || requestedAction == "Death" ? requestedAction : "Idle";
            if (next == action || animator == null) return;
            if (!animator.isActiveAndEnabled) { action = next; applyPoseOnEnable = true; return; }
            float phase = 0;
            if ((next == "Walk" || next == "Run") && (action == "Walk" || action == "Run"))
                phase = animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1;
            animator.CrossFadeInFixedTime(next, transitionDuration, 0, phase * ClipDuration(next));
            action = next;
        }

        public float ClipDuration(string clip)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return 1;
            foreach (var animation in animator.runtimeAnimatorController.animationClips) if (animation.name == clip) return animation.length;
            return 1;
        }

        public void RestartInteractionAnimation(Vector3 facing)
        {
            if (dead) return;
            facing.y = 0;
            if (facing.sqrMagnitude > .001f) targetRotation = Quaternion.LookRotation(facing);
            if (animator && animator.isActiveAndEnabled) animator.CrossFadeInFixedTime("Attack", transitionDuration, 0, 0);
            action = "Attack";
        }

        void Update()
        {
            if (!dead) transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeed * Time.deltaTime);
        }

        public void SetSelected(bool selected) { if (selectionRing != null) selectionRing.SetActive(selected && !dead); }
        public void TakeHit(bool audible = true) { if (audible) ProceduralAudio.Instance?.Play(GameSound.Hit, transform.position, true); }
        public void Die(bool audible = true)
        {
            if (dead) return;
            SetAction("Death"); dead = true; deathTime = Time.time; SetSelected(false);
            if (audible) ProceduralAudio.Instance?.Play(GameSound.Death, transform.position, true);
        }
    }
}
