Shader "DebugVRS"
{
SubShader
{
    Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"}
    ZWrite Off Cull Off
    Pass
    {
        Name "ColorBlitPass"
        HLSLPROGRAM
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #pragma vertex Vert
        #pragma fragment Frag
 
        float4 Frag(Varyings input) : SV_Target0
        {
            // this is needed so we account XR platform differences in how they handle texture arrays
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            // sample the texture using the SAMPLE_TEXTURE2D_X_LOD
            float2 uv = input.texcoord.xy;
            uv *= 2;
            half4 color = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointRepeat, uv,
            0);

            if (input.texcoord.x < 0.5 || input.texcoord.y < 0.5){
                discard;
            }
            
            return color;
        }
        ENDHLSL
    }
    }
}
