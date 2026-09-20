using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Jev.Gameplay.Camera
{
    /// <summary>Moves an authored camera rig, independently of simulation and navigation.</summary>
    [DisallowMultipleComponent]
    public sealed class RtsCameraController : MonoBehaviour
    {
        [SerializeField] UnityEngine.Camera viewCamera;
        [SerializeField] Vector2 worldMinimum = new Vector2(-46, -38);
        [SerializeField] Vector2 worldMaximum = new Vector2(46, 38);
        [Tooltip("Optional map constraints. Leave both disabled for a free camera, including beyond the battlefield.")]
        [SerializeField] bool restrictLookAtToMap;
        [SerializeField] bool keepGroundViewInsideMap;
        [SerializeField, Min(1)] float moveSpeed = 15;
        [SerializeField, Min(0)] float edgePixels = 16;
        [SerializeField] bool edgePan = true;
        [SerializeField] float minimumDistance = 10, maximumDistance = 54, distance = 24;
        [SerializeField, Range(5, 85)] float pitch = 48;
        [SerializeField, Range(5, 85)] float minimumPitch = 22, maximumPitch = 78;
        [Tooltip("Degrees per pointer pixel while holding MMB or Command + RMB; X is yaw and Y is tilt.")]
        [SerializeField] Vector2 orbitSensitivity = new Vector2(.18f, .14f);
        [SerializeField] float yaw = 0, rotateSpeed = 65, zoomStep = 2.5f, smoothing = 11;
        Vector3 destination;
        float shownYaw, shownPitch, shownDistance;
        bool initialized, controlsEnabled = true;
        readonly CameraOrbitGesture orbit = new CameraOrbitGesture();

        public bool ControlsEnabled
        {
            get => controlsEnabled;
            set { controlsEnabled = value; if (!value) orbit.Reset(); }
        }
        public UnityEngine.Camera ViewCamera => viewCamera;
        public static bool CommandModifierHeld => Keyboard.current != null
            && (Keyboard.current.leftMetaKey.isPressed || Keyboard.current.rightMetaKey.isPressed);

        void Awake() { Initialize(); }

        void Initialize()
        {
            if (initialized) return;
            destination = transform.position;
            pitch = Mathf.Clamp(pitch, minimumPitch, maximumPitch);
            shownYaw = yaw;
            shownPitch = pitch;
            shownDistance = distance;
            initialized = true;
        }

        public void Focus(Vector3 position, bool instant = false)
        {
            Initialize();
            distance = FitDistance(distance, yaw, pitch);
            destination = Clamp(position);
            if (instant)
            {
                shownYaw = yaw; shownPitch = pitch; shownDistance = distance;
                transform.position = Clamp(destination, shownYaw, shownPitch, shownDistance);
                ApplyCamera();
            }
        }

        Vector3 Clamp(Vector3 value)
            => Clamp(value, yaw, pitch, distance);

        Vector3 Clamp(Vector3 value, float cameraYaw, float cameraPitch, float cameraDistance)
        {
            if (keepGroundViewInsideMap && viewCamera != null)
                return GroundViewBounds.ClampLookAt(value, worldMinimum, worldMaximum, viewCamera.fieldOfView, viewCamera.aspect, cameraYaw, cameraPitch, cameraDistance);
            if (!restrictLookAtToMap) return value;
            value.x = Mathf.Clamp(value.x, worldMinimum.x, worldMaximum.x);
            value.z = Mathf.Clamp(value.z, worldMinimum.y, worldMaximum.y);
            return value;
        }

        float FitDistance(float requested, float cameraYaw, float cameraPitch) => keepGroundViewInsideMap && viewCamera != null
            ? GroundViewBounds.FitDistance(requested, minimumDistance, maximumDistance, worldMinimum, worldMaximum, viewCamera.fieldOfView, viewCamera.aspect, cameraYaw, cameraPitch)
            : Mathf.Clamp(requested, minimumDistance, maximumDistance);

        void LateUpdate()
        {
            Initialize();
            float dt = Mathf.Min(Time.unscaledDeltaTime, .08f);
            if (ControlsEnabled && InputWindowHasFocus) ReadInput(dt);
            else orbit.Reset();
            distance = FitDistance(distance, yaw, pitch);
            destination = Clamp(destination);
            float blend = 1 - Mathf.Exp(-smoothing * dt);
            shownYaw = Mathf.LerpAngle(shownYaw, yaw, blend);
            shownPitch = Mathf.Lerp(shownPitch, pitch, blend);
            shownDistance = FitDistance(Mathf.Lerp(shownDistance, distance, blend), shownYaw, shownPitch);
            transform.position = Clamp(Vector3.Lerp(transform.position, destination, blend), shownYaw, shownPitch, shownDistance);
            ApplyCamera();
        }

        void ReadInput(float dt)
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            Vector2 move = Vector2.zero;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move.y++;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move.y--;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move.x++;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move.x--;
                if (keyboard.qKey.isPressed) yaw -= rotateSpeed * dt;
                if (keyboard.eKey.isPressed) yaw += rotateSpeed * dt;
            }
            if (mouse != null)
            {
                Vector2 p = mouse.position.ReadValue();
                bool inside = PointerInsideViewport(p, new Vector2(Screen.width, Screen.height), PointerOverInputWindow);
                bool commandOrbit = CommandModifierHeld && mouse.rightButton.isPressed;
                bool orbitHeld = mouse.middleButton.isPressed || commandOrbit;
                bool orbitPressed = mouse.middleButton.wasPressedThisFrame || (CommandModifierHeld && mouse.rightButton.wasPressedThisFrame);
                if (inside && edgePan && !overUi && !orbitHeld)
                {
                    if (p.x < edgePixels) move.x--;
                    if (p.x > Screen.width - edgePixels) move.x++;
                    if (p.y < edgePixels) move.y--;
                    if (p.y > Screen.height - edgePixels) move.y++;
                }
                if (!overUi && inside)
                {
                    float wheel = mouse.scroll.ReadValue().y;
                    if (Mathf.Abs(wheel) > .01f) distance = Mathf.Clamp(distance - Mathf.Sign(wheel) * zoomStep, minimumDistance, maximumDistance);
                }
                if (orbit.Update(orbitPressed, orbitHeld, inside, overUi))
                    ApplyOrbitDelta(mouse.delta.ReadValue());
            }
            else orbit.Reset();
            float boost = keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) ? 1.65f : 1;
            distance = FitDistance(distance, yaw, pitch);
            Vector3 travel = Quaternion.Euler(0, yaw, 0) * new Vector3(move.x, 0, move.y).normalized;
            destination = Clamp(destination + travel * (moveSpeed * Mathf.Sqrt(distance / 24) * boost * dt));
        }

        void ApplyOrbitDelta(Vector2 delta)
        {
            yaw = Mathf.Repeat(yaw + delta.x * orbitSensitivity.x, 360);
            pitch = Mathf.Clamp(pitch - delta.y * orbitSensitivity.y, minimumPitch, maximumPitch);
        }

        static bool InputWindowHasFocus
        {
            get
            {
                if (!Application.isFocused) return false;
#if UNITY_EDITOR
                // The Editor remains application-focused while its Inspector, Scene view or CLI is active.
                return IsGameView(UnityEditor.EditorWindow.focusedWindow);
#else
                return true;
#endif
            }
        }

        static bool PointerOverInputWindow
        {
            get
            {
#if UNITY_EDITOR
                var hovered = UnityEditor.EditorWindow.mouseOverWindow;
                // The Editor can leave hover unreported for a focused Game View (including injected
                // Input System devices). An explicitly hovered different panel still rejects input.
                return IsGameView(hovered != null ? hovered : UnityEditor.EditorWindow.focusedWindow);
#else
                return true;
#endif
            }
        }

#if UNITY_EDITOR
        static bool IsGameView(UnityEditor.EditorWindow window)
            => window != null && window.GetType().FullName == "UnityEditor.GameView";
#endif

        static bool PointerInsideViewport(Vector2 pointer, Vector2 size, bool overInputWindow)
            // Input devices initially report (0, 0); window borders and off-window stale positions
            // must never start edge pan. A previously captured orbit can still continue outside.
            => overInputWindow && pointer.x > 0 && pointer.y > 0 && pointer.x < size.x && pointer.y < size.y;

        void OnApplicationFocus(bool focused) { if (!focused) orbit.Reset(); }
        void OnDisable() { orbit.Reset(); }

        void OnValidate()
        {
            minimumPitch = Mathf.Clamp(minimumPitch, 5, 85);
            maximumPitch = Mathf.Clamp(maximumPitch, minimumPitch, 85);
            pitch = Mathf.Clamp(pitch, minimumPitch, maximumPitch);
            minimumDistance = Mathf.Max(1, minimumDistance);
            maximumDistance = Mathf.Max(minimumDistance, maximumDistance);
            distance = Mathf.Clamp(distance, minimumDistance, maximumDistance);
            smoothing = Mathf.Max(.1f, smoothing);
        }

        void ApplyCamera()
        {
            if (viewCamera == null) return;
            transform.rotation = Quaternion.Euler(0, shownYaw, 0);
            viewCamera.transform.localRotation = Quaternion.Euler(shownPitch, 0, 0);
            viewCamera.transform.localPosition = Quaternion.Euler(shownPitch, 0, 0) * Vector3.back * shownDistance;
        }
    }
}
