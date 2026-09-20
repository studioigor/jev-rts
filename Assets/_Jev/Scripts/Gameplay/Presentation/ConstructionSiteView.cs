using TMPro;
using UnityEngine;

namespace Jev.Gameplay.Presentation
{
    /// <summary>Displays simulation construction work on an authored world-space progress card.</summary>
    [DisallowMultipleComponent]
    public sealed class ConstructionSiteView : MonoBehaviour
    {
        [Header("Authored progress card")]
        [SerializeField] Transform scaleCompensator;
        [SerializeField] RectTransform progressCanvas;
        [SerializeField] Canvas worldCanvas;
        [SerializeField] UnityEngine.UI.Image progressFill, stateAccent;
        [SerializeField] TMP_Text percentageLabel;
        [Header("World presentation")]
        [SerializeField, Min(.1f)] float heightAboveGround = 1.15f;
        [SerializeField, Range(80, 180)] float targetPixelWidth = 136;
        [SerializeField, Min(.1f)] float minimumWorldWidth = 1.8f;
        [SerializeField, Min(.1f)] float maximumWorldWidth = 4.8f;
        [SerializeField] Color constructionColor = new Color(.88f, .73f, .44f);
        [SerializeField] Color plannedColor = new Color(.30f, .84f, .80f);
        [SerializeField] UnityEngine.Camera viewCamera;
        int previousWork = int.MinValue, previousRequired = int.MinValue;
        bool previousPlanned;

        public void SetProgress(int work, int required, bool planned)
        {
            if (work == previousWork && required == previousRequired && planned == previousPlanned) return;
            previousWork = work; previousRequired = required; previousPlanned = planned;
            float ratio = planned ? 0 : required > 0 ? Mathf.Clamp01((float)work / required) : 0;
            Color accent = planned ? plannedColor : constructionColor;
            if (progressFill)
            {
                progressFill.fillAmount = ratio;
                progressFill.rectTransform.anchorMax = new Vector2(ratio, 1);
                progressFill.color = accent;
            }
            if (stateAccent) stateAccent.color = accent;
            if (percentageLabel) percentageLabel.text = planned ? "ПЛАН · 0%" : "СТРОИТСЯ · " + Mathf.FloorToInt(ratio * 100) + "%";
        }

        void LateUpdate()
        {
            if (!scaleCompensator || !progressCanvas) return;
            if (!viewCamera) viewCamera = UnityEngine.Camera.main;
            if (!viewCamera) return;
            // Cancel the foundation's stretch on a separate, unrotated transform before billboarding.
            // Rotating a single reciprocal-scaled child under a nonuniform parent would shear the UI.
            var parentScale = scaleCompensator.parent ? scaleCompensator.parent.lossyScale : Vector3.one;
            scaleCompensator.localRotation = Quaternion.identity;
            scaleCompensator.localScale = new Vector3(Reciprocal(parentScale.x), Reciprocal(parentScale.y), Reciprocal(parentScale.z));
            scaleCompensator.position = transform.position + Vector3.up * heightAboveGround;
            progressCanvas.rotation = viewCamera.transform.rotation;
            float depth = Mathf.Max(.01f, Vector3.Dot(scaleCompensator.position - viewCamera.transform.position, viewCamera.transform.forward));
            float worldHeight = viewCamera.orthographic ? viewCamera.orthographicSize * 2
                : 2 * depth * Mathf.Tan(viewCamera.fieldOfView * .5f * Mathf.Deg2Rad);
            float desiredWidth = targetPixelWidth * worldHeight / Mathf.Max(1, viewCamera.pixelHeight);
            float worldWidth = Mathf.Clamp(desiredWidth, minimumWorldWidth, Mathf.Max(minimumWorldWidth, maximumWorldWidth));
            float scale = worldWidth / Mathf.Max(1, progressCanvas.rect.width);
            progressCanvas.localScale = Vector3.one * scale;
            if (worldCanvas && worldCanvas.worldCamera != viewCamera) worldCanvas.worldCamera = viewCamera;
        }

        static float Reciprocal(float scale) => Mathf.Abs(scale) > .0001f ? 1 / scale : 1;
    }
}
