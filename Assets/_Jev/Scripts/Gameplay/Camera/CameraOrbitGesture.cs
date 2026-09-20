namespace Jev.Gameplay.Camera
{
    /// <summary>Owns a complete orbit gesture, so crossing a HUD panel cannot interrupt it.</summary>
    public sealed class CameraOrbitGesture
    {
        public bool Active { get; private set; }

        public bool Update(bool pressedThisFrame, bool held, bool pointerInsideView, bool pointerOverUi)
        {
            if (!held) Active = false;
            else if (pressedThisFrame) Active = pointerInsideView && !pointerOverUi;
            return Active;
        }

        public void Reset() => Active = false;
    }
}
