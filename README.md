# Unity Shading Rate

Variable Rate Shading (VRS), also known as Fragment Shading Rate, is a technique which allows to decouple the rasterization and pixel shading rate. VRS can be used to minimize the pixel shading overhead, and improve performance while perserving image quality:

<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/vrs-demo.gif?raw=true" alt="vrs demo">
</p>

This project demonstrates the use of Unity's Shading Rate API, in order to optimize the performance of renderer features for the Universal Rendering Pipeline.

## Requirements
- Unity 6000.1.0b1 or above
- Universal Render Pipeline (URP) version 17.0.3 or above

## Platform Support
- Windows platforms with support for DirectX12 Variable Rate Shading 
- Android platforms with support for Vulkan Fragment Shading Rate
- Compatible consoles

## How to use

1. Open the "ShadingRateSample" scene (`\Assets\Scenes\ShadingRateSample.unity`)
2. Hit play
3. You can toggle the Shading Rate Debug View in the URP Renderer settings (`\Assets\Settings\Renderer.asset`). Navigate to "Shading Rate Feature", and click on "Debug VRS"

<p align="left">
  <img width="50%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/settings.png?raw=true" alt="vrs settings">
</p>

## Shading Rate Demo - Step by Step

A custom Renderer Feature is used to compute a "Shading Rate Image" (SRI), which encodes a 2D array of Shading Rates. The SRI can be generated on a per-frame basis, to balance between shading performance and image fidelity.

To begin, we create a new Renderer Feature (`\Asset\ShadingRate\ShadingRateFeature.cs`) and declare our render pass and resources. In the Render Graph record function, we name our render pass and check for VRS support on device:
```
class VRSGenerationPass : ScriptableRenderPass
{
    private TextureHandle m_ColorMask;
    private TextureHandle m_SRI;
    private Material m_Material;

    private class PassData
    {
        public Material m_Mat;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) 
    {
       const string passName = "VRS Generation";

       if (!ShadingRateInfo.supportsPerImageTile) {
           Debug.Log("VRS is not supported!");
           return;
       }

```

If VRS is supported, we continue by creating a VRS Look-Up Table (LUT). This table encodes a different basic color for each shading rate (`Red: 1x1`, `Green: 2x2`, `Blue: 4x4`).  We create a new Material using `GenerateVRS.shadergraph`, and set each color as a shader uniform:
```
  VrsLut lut = new VrsLut();
  lut = VrsLut.CreateDefault();

  // Get the render pipeline's conversion look-up table 
  var vrsPipelineResources = GraphicsSettings.GetRenderPipelineSettings<VrsRenderPipelineRuntimeResources>();
  lut = vrsPipelineResources.conversionLookupTable;
  vrsPipelineResources.visualizationLookupTable = lut;

  if (m_Material == null) {
      m_Material = new Material(Resources.Load<Shader>("Shaders/GenerateVRS"));
      m_Material.SetColor("_ShadingRateColor1x1", lut[ShadingRateFragmentSize.FragmentSize1x1]);
      m_Material.SetColor("_ShadingRateColor2x2", lut[ShadingRateFragmentSize.FragmentSize2x2]);
      m_Material.SetColor("_ShadingRateColor4x4", lut[ShadingRateFragmentSize.FragmentSize4x4]);
  }
```

In our Shader Graph, we simply read the property `_ShadingRateColor4x4` and pass it to the graph's color output:
<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/shader-graph-4x4.png?raw=true" alt="shadergraph-4x4">
</p>

Next, we need to create a Shading Rate color mask. We first need to query the native tile size, based on our render target's dimensions. Then create a new Render Graph Texture, and set it as the color target of our render pass. To render the shading rate color mask, we issue a full screen draw using our custom material:
```
  var tileSize = ShadingRateImage.GetAllocTileSize(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);

  RenderTextureDescriptor textureProperties = new RenderTextureDescriptor(tileSize.x, tileSize.y, RenderTextureFormat.Default, 0);

  m_ColorMask = UniversalRenderer.CreateRenderGraphTexture(renderGraph, textureProperties, "_ShadingRateColor", false);
  builder.SetRenderAttachment(m_ColorMask, 0, AccessFlags.Write);

  builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
  {
      RasterCommandBuffer cmd = context.cmd;
      Blitter.BlitTexture(cmd, new Vector4(1,1,0,0), data.m_Mat, 0);
  });

```
Using the Frame Debugger, we confirm that the output of the pass is now a blue texture:
<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/framedebugger-colormask.png?raw=true" alt="4x4-color">
</p>

Next, we need to create a native Shading Rate Image (SRI) using the format `ShadingRateInfo.graphicsFormat`.  Using a utility function, we then convert our color mask (RGB8) to a native SRI:
```
  RenderTextureDescriptor sriDesc = new RenderTextureDescriptor(tileSize.x, tileSize.y, ShadingRateInfo.graphicsFormat,
      GraphicsFormat.None);
  sriDesc.enableRandomWrite = true;
  sriDesc.enableShadingRate = true;
  sriDesc.autoGenerateMips = false;

  m_SRI = UniversalRenderer.CreateRenderGraphTexture(renderGraph, sriDesc, "_SRI", false);

  Vrs.ColorMaskTextureToShadingRateImage(renderGraph, m_SRI, m_ColorMask, TextureDimension.Tex2D, true);
```

We finally encoded our SRI, which can be used to set a uniform 4x4 shading rate for subsequent render passes. Using the Frame Debugger, we will now see an additional VRS conversion pass created for us. The pass reads our blue color mask and outputs an SRI:
<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/framedebugger-sriconversion.png?raw=true" alt="sri-conversion">
</p>

The next step is to apply the SRI on the relevant render pass we wish to optimize. We can use the `ContextItem` class to pass texture handles between render passes (see https://docs.unity3d.com/6000.0/Documentation/Manual/urp/render-graph-pass-textures-between-passes.html). Declare a new `VRSData` class inheriting `ContextItem`, with a public member for the SRI texture handles.  Instantiate `VRSData`, and assign the SRI texture handle after encoding:
```
  public class VRSData : ContextItem {
      public TextureHandle sri;

      public override void Reset()
      {
          sri = TextureHandle.nullHandle;
      }
  }  
```
```
var vrsData = frameData.Create<VRSData>(); 
Vrs.ColorMaskTextureToShadingRateImage(renderGraph, m_SRI, m_ColorMask, TextureDimension.Tex2D, true);
vrsData.sri = m_SRI;
```

The SRI can now be referenced through `VRSData`, and applied on relevant render passes. We can also combine multiple shading rate sources if needed (see https://docs.unity3d.com/6000.2/Documentation/ScriptReference/Rendering.CommandBuffer.SetShadingRateCombiner.html):
```
  if(ShadingRateInfo.supportsPerImageTile && frameData.Contains<ShadingRateFeature.VRSData>()) {
    var vrsData = frameData.Get<ShadingRateFeature.VRSData>();
    if (vrsData.sri.IsValid())
    {
        builder.SetShadingRateImageAttachment(vrsData.sri);
        builder.SetShadingRateCombiner(ShadingRateCombinerStage.Fragment,
        ShadingRateCombiner.Override);
    }
  } 
```

In our basic example, a uniform 4x4 shading rate is applied onto the computationally-intensive Volumetric Lighting pass:
<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/demo-4x4.gif?raw=true" alt="demo-4x4">
</p>

The reduction in quality is quite noticeable, especially when zooming up close:
<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/4x4-upclose.png?raw=true" alt="4x4-upclose">
</p>

In our example project, we are using a “Motion Blur” effect to emphasize the sense of speed while driving. This generates a motion-vectors texture, which we can access in our Shader Graph, and use to generate a velocity mask:
<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/shader-graph-mv.png?raw=true" alt="shadergraph-mv">
</p>

At the same time, we also sample a UI texture which correponds to our Speedometer and Minimap:

<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/shader-graph-ui.png?raw=true" alt="shadergraph-ui">
</p>

The shader combines these masks and sets a threshold per Shading Rate. The result is a dynamic shading rate, with lower rate for high-velocity pixels. We also reduce shading rate for screen areas occluded by transparent UI: 

<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/shader-graph-combine.png?raw=true" alt="shader-graph-combine">
</p>

You can find the full shader at `\Assets\Resources\Shaders\GenerateVRS.shadergraph`. By using motion vectors, we preserve fidelity for our car model, which is centered in the middle of the screen. While reducing the shading rate for high-velocity pixels, already affected by motion blur:

<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/vrs-demo.gif?raw=true" alt="vrs-demo">
</p>

We also maintain fidelity at lower speeds:
<p align="center">
  <img width="100%" src="https://github.com/Unity-Technologies/shading-rate-demo/blob/main/.github/images/1x1-upclose.png?raw=true" alt="4x4-upclose">
</p>


Measuring GPU performance an Nvidia RTX 3080 Ti (mobile):
| Shading Rate  | Volumetrics GPU Time | Total GPU Time       |
| ------------- |:--------------------:|:--------------------:| 
| Uniform 1x1   | ~6.3 ms              | ~11.3 ms             |
| Uniform 4x4   | ~0.5 ms (92% faster) | ~5.5 ms (51% faster) |
| Motion based  | ~2.7 ms (57% faster) | ~7.5 ms (33% faster) |
