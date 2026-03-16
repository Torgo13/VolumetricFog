using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

sealed
public class ShadingRateFeature : ScriptableRendererFeature
{
    [SerializeField] private RenderPassEvent m_InjectionPoint = RenderPassEvent.AfterRenderingPrePasses;
    [SerializeField] private bool m_DebugVRS;
    private VRSGenerationPass m_ScriptablePass;
    private VRSDebugPass m_DebugPass;

    [SerializeField] Shader _debugVRS;
    [SerializeField] Shader _generateVRS;

    public override void Create()
    {
#if UNITY_EDITOR
        if (_debugVRS == null)
            _debugVRS = Shader.Find("DebugVRS");

        if (_generateVRS == null)
            _generateVRS = Shader.Find("Shader Graphs/GenerateVRS");
#endif // UNITY_EDITOR

        m_ScriptablePass = new VRSGenerationPass(_generateVRS);
        m_ScriptablePass.renderPassEvent = m_InjectionPoint;

        if(m_DebugVRS){
            m_DebugPass = new VRSDebugPass(_debugVRS);
            m_DebugPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        }
    }

    protected override void Dispose(bool disposing)
    {
        m_ScriptablePass?.Dispose();
        m_DebugPass?.Dispose();
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_ScriptablePass);
        if(m_DebugVRS){
            renderer.EnqueuePass(m_DebugPass);
        }
    }

    // Create the custom data class that contains the new texture
    sealed
    public class VRSData : ContextItem {
        public TextureHandle shadingRateTex;
        public TextureHandle sri;

        public override void Reset()
        {
            shadingRateTex = TextureHandle.nullHandle;
            sri = TextureHandle.nullHandle;
        }
    }

    sealed
    class VRSGenerationPass : ScriptableRenderPass
    {
        private TextureHandle m_SRIColorMask;
        private TextureHandle m_SRI;
        readonly
        private Material m_Material;

        // This class stores the data needed by the RenderGraph pass.
        // It is passed as a parameter to the delegate function that executes the RenderGraph pass.
        sealed
        private class PassData
        {
            public Material m_Mat;
        }

        public VRSGenerationPass(Shader generateVRS)
        {
            m_Material = new Material(generateVRS);

            VrsLut lut;

            // Get the render pipeline's conversion look-up table
            var vrsPipelineResources = GraphicsSettings.GetRenderPipelineSettings<VrsRenderPipelineRuntimeResources>();
            lut = vrsPipelineResources.conversionLookupTable;

#if UNITY_EDITOR
            vrsPipelineResources.visualizationLookupTable = lut;
#endif // UNITY_EDITOR

#if UNUSED
            if (m_Material == null)
            {
                m_Material = new Material(Resources.Load<Shader>("Shaders/GenerateVRS"));
            }
#endif // UNUSED

            m_Material.SetColor("_ShadingRateColor1x1", lut[ShadingRateFragmentSize.FragmentSize1x1]);
            m_Material.SetColor("_ShadingRateColor2x2", lut[ShadingRateFragmentSize.FragmentSize2x2]);
            m_Material.SetColor("_ShadingRateColor4x4", lut[ShadingRateFragmentSize.FragmentSize4x4]);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            const string passName = "VRS Generation";

            if (!ShadingRateInfo.supportsPerImageTile) {
#if DEBUG
                Debug.Log("VRS is not supported!");
#endif // DEBUG
                return;
            }

#if UNUSED
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
#endif // UNUSED
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            var vrsData = frameData.Create<VRSData>();
            var tileSize = ShadingRateImage.GetAllocTileSize(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData))
            {
                builder.AllowPassCulling(false);

                RenderTextureDescriptor textureProperties = new RenderTextureDescriptor(tileSize.x, tileSize.y, RenderTextureFormat.Default, 0);
                m_SRIColorMask = UniversalRenderer.CreateRenderGraphTexture(renderGraph, textureProperties, "_ShadingRateColor", false);

                builder.SetRenderAttachment(m_SRIColorMask, 0, AccessFlags.Write);
                vrsData.shadingRateTex = m_SRIColorMask;
                passData.m_Mat = m_Material;

                //Blit
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) => ExecutePass(data, context));

                //Create sri target
                RenderTextureDescriptor sriDesc = new RenderTextureDescriptor(tileSize.x, tileSize.y, ShadingRateInfo.graphicsFormat,
                    GraphicsFormat.None);
                sriDesc.enableRandomWrite = true;
                sriDesc.enableShadingRate = true;
                sriDesc.autoGenerateMips = false;

                m_SRI = UniversalRenderer.CreateRenderGraphTexture(renderGraph, sriDesc, "_SRI", false);
            }

            Vrs.ColorMaskTextureToShadingRateImage(renderGraph, m_SRI, m_SRIColorMask, TextureDimension.Tex2D, true);
            vrsData.sri = m_SRI;

        }

        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            RasterCommandBuffer cmd = context.cmd;
            Blitter.BlitTexture(cmd, new Vector4(1,1,0,0), data.m_Mat, 0);
        }

        public void Dispose()
        {
            CoreUtils.Destroy(m_Material);
        }
    }

    sealed
    class VRSDebugPass : ScriptableRenderPass
    {
        readonly
        private Material m_Material;
        private RenderPassEvent m_Event;
        private TextureHandle m_SRIColorMask;

        public VRSDebugPass(Shader debugVRS)
        {
            m_Material = new Material(debugVRS);
        }

        // This class stores the data needed by the RenderGraph pass.
        // It is passed as a parameter to the delegate function that executes the RenderGraph pass.
        sealed
        private class PassData
        {
            public Material m_Mat;
            public TextureHandle m_Tex;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            const string passName = "VRS Debugging";

            if (!ShadingRateInfo.supportsPerImageTile) return;

#if UNUSED
            if (m_Material == null)
                m_Material = new Material(Resources.Load<Shader>("Shaders/DebugVRS"));
#endif // UNUSED

            var vrsData = frameData.Get<VRSData>();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData))
            {
                builder.AllowPassCulling(false);
                passData.m_Tex = vrsData.shadingRateTex;

                builder.UseTexture(passData.m_Tex, AccessFlags.Read);
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0);

                passData.m_Mat = m_Material;

                //Blit
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) => ExecutePass(data, context));
            }
        }

        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            RasterCommandBuffer cmd = context.cmd;
            Blitter.BlitTexture(cmd, data.m_Tex, new Vector4(1, 1, 0, 0), data.m_Mat, 0);
        }

        public void Dispose()
        {
            CoreUtils.Destroy(m_Material);
        }
    }
}