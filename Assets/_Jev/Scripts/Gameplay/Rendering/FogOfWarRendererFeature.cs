using Jev.Gameplay.Presentation;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Jev.Gameplay.Rendering
{
    /// <summary>One depth-aware blend, restricted to an explicitly configured, active match camera.</summary>
    [DisallowMultipleRendererFeature]
    public sealed class FogOfWarRendererFeature : ScriptableRendererFeature
    {
        FogPass pass;
        public override void Create()
        {
            pass?.Dispose();
            pass = new FogPass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!Application.isPlaying || renderingData.cameraData.cameraType != CameraType.Game) return;
            var view = renderingData.cameraData.camera.GetComponent<FogOfWarView>();
            if (!view || !view.IsRenderingActive) return;
            pass.Setup(view);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing) { pass?.Dispose(); pass = null; }

        sealed class FogPass : ScriptableRenderPass
        {
            static readonly int DepthId = Shader.PropertyToID("_FogDepthTexture");
            static readonly int VisibilityId = Shader.PropertyToID("_FogVisibility");
            static readonly int MapId = Shader.PropertyToID("_FogWorldToMap");
            static readonly int UnexploredId = Shader.PropertyToID("_UnexploredColor");
            static readonly int ExploredId = Shader.PropertyToID("_ExploredColor");
            static readonly int SoftnessId = Shader.PropertyToID("_EdgeSoftness");
            readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
            FogOfWarView view;
            Texture2D maskTexture;
            RTHandle maskHandle;

            public FogPass() { profilingSampler = new ProfilingSampler("Jev RTS Fog of War"); }
            public void Setup(FogOfWarView target)
            {
                view = target;
                if (maskTexture == view.VisibilityTexture) return;
                maskHandle?.Release();
                maskTexture = view.VisibilityTexture;
                maskHandle = RTHandles.Alloc(maskTexture);
            }
            public void Dispose() { maskHandle?.Release(); maskHandle = null; maskTexture = null; view = null; }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!view || !view.IsRenderingActive || maskHandle == null) return;
                var resources = frameData.Get<UniversalResourceData>();
                if (!resources.cameraDepthTexture.IsValid()) return;
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Jev RTS Fog of War", out var data, profilingSampler))
                {
                    data.Material = view.FogMaterial;
                    data.Properties = properties;
                    data.Depth = resources.cameraDepthTexture;
                    data.Visibility = renderGraph.ImportTexture(maskHandle);
                    data.Map = view.WorldToMap;
                    data.Unexplored = view.UnexploredColor;
                    data.Explored = view.ExploredColor;
                    data.Softness = view.EdgeSoftness;
                    builder.UseTexture(data.Depth, AccessFlags.Read);
                    builder.UseTexture(data.Visibility, AccessFlags.Read);
                    // Hardware alpha blending reads the existing color attachment; no scene-color copy, blur, or extra target.
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderFunc(static (PassData d, RasterGraphContext context) =>
                    {
                        RTHandle depth = d.Depth, visibility = d.Visibility;
                        d.Properties.Clear();
                        d.Properties.SetTexture(DepthId, depth);
                        d.Properties.SetTexture(VisibilityId, visibility);
                        d.Properties.SetVector(MapId, d.Map);
                        d.Properties.SetColor(UnexploredId, d.Unexplored);
                        d.Properties.SetColor(ExploredId, d.Explored);
                        d.Properties.SetFloat(SoftnessId, d.Softness);
                        context.cmd.DrawProcedural(Matrix4x4.identity, d.Material, 0, MeshTopology.Triangles, 3, 1, d.Properties);
                    });
                }
            }

            sealed class PassData
            {
                public Material Material;
                public MaterialPropertyBlock Properties;
                public TextureHandle Depth, Visibility;
                public Vector4 Map;
                public Color Unexplored, Explored;
                public float Softness;
            }
        }
    }
}
