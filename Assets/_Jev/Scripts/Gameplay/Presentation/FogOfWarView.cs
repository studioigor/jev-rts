using System;
using System.Collections.Generic;
using Jev.Gameplay.Authoring;
using Jev.Gameplay.Runtime;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.UI;
using UnityEngine;

namespace Jev.Gameplay.Presentation
{
    /// <summary>Camera-local presentation of the match's existing visibility; never computes vision or changes AI knowledge.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(UnityEngine.Camera))]
    public sealed class FogOfWarView : MonoBehaviour
    {
        public Material FogMaterial;
        [ColorUsage(true)] public Color UnexploredColor = new Color(.008f, .012f, .025f, 1f);
        [ColorUsage(true)] public Color ExploredColor = new Color(.025f, .04f, .065f, .48f);
        [Range(.01f, 1f), Tooltip("Transition width within one map cell; only the fog edge is softened, never the rendered scene.")]
        public float EdgeSoftness = .85f;

        public Texture2D VisibilityTexture { get; private set; }
        public Vector4 WorldToMap { get; private set; }
        MatchController match;
        Color32[] pixels;

        public bool IsRenderingActive => Application.isPlaying && isActiveAndEnabled && FogMaterial && VisibilityTexture
            && match && match.World != null && match.Settings && match.Settings.EnableFogOfWar && match.Phase != MatchPhase.Setup;

        public void SetVisibility(MatchController owner, BattlefieldDefinition field, IEnumerable<Cell> visible, IEnumerable<Cell> explored)
        {
            match = owner;
            if (!field || field.Width < 1 || field.Height < 1 || field.CellSize <= 0) return;
            if (!VisibilityTexture || VisibilityTexture.width != field.Width || VisibilityTexture.height != field.Height)
            {
                ReleaseTexture();
                VisibilityTexture = new Texture2D(field.Width, field.Height, TextureFormat.RGBA32, false, true)
                {
                    name = "Jev RTS visibility: red=current green=explored",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave
                };
                pixels = new Color32[field.Width * field.Height];
            }
            WorldToMap = new Vector4(field.Origin.x, field.Origin.y, 1f / (field.Width * field.CellSize), 1f / (field.Height * field.CellSize));
            EncodeVisibility(field.Width, field.Height, visible, explored, pixels);
            VisibilityTexture.SetPixels32(pixels);
            VisibilityTexture.Apply(false, false);
        }

        /// <summary>Replaces the mask atomically, so lost sight becomes explored and a new match cannot inherit old exploration.</summary>
        public static void EncodeVisibility(int width, int height, IEnumerable<Cell> visible, IEnumerable<Cell> explored, Color32[] destination)
        {
            if (width < 1 || height < 1 || destination == null || destination.Length != width * height)
                throw new ArgumentException("Fog mask must match the battlefield dimensions.");
            Array.Clear(destination, 0, destination.Length);
            foreach (var cell in explored)
                if (Inside(cell, width, height)) destination[cell.Y * width + cell.X] = new Color32(0, 255, 0, 255);
            foreach (var cell in visible)
                if (Inside(cell, width, height)) destination[cell.Y * width + cell.X] = new Color32(255, 255, 0, 255);
        }

        static bool Inside(Cell cell, int width, int height) => cell.X >= 0 && cell.Y >= 0 && cell.X < width && cell.Y < height;
        void OnDestroy() => ReleaseTexture();
        void ReleaseTexture()
        {
            if (VisibilityTexture) Destroy(VisibilityTexture);
            VisibilityTexture = null;
            pixels = null;
        }
    }
}
