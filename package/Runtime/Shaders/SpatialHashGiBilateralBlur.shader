// Spatial Hash GI - Bilateral Blur (fragment shader path)
// GMEM-optimized single-pass (non-separable) bilateral blur.
// A single pass avoids the extra tile flush that a separable H+V pair causes on
// Adreno TBDRs. The 5x5 kernel is compact enough that ALU is cheaper than the
// DRAM bandwidth saved by staying in one render pass.
Shader "Hidden/Lotec/SpatialHashGiBilateralBlur"
{
    Properties
    {
        [HideInInspector] _MainTex ("", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SpatialHashGI_BilateralBlur"
            ZTest Always ZWrite Off Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize; // (1/w, 1/h, w, h)

            TEXTURE2D_X(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);

            float _BilateralDepthThreshold;
            int _BlurKernelRadius;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.uv = float2((input.vertexID << 1) & 2, input.vertexID & 2);
                output.positionCS = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                    output.uv.y = 1.0 - output.uv.y;
                #endif
                return output;
            }

            // Single-pass 2D bilateral blur.
            // Using a diamond / cross pattern instead of a full NxN kernel saves
            // ALU on mobile while covering enough samples for smooth GI.
            // The 13-tap pattern (center + 12 neighbors at radius 1 and 2) fits
            // inside two Adreno texture cache lines for half-res targets.
            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 texelSize = _MainTex_TexelSize.xy;

                half centerDepth = (half)SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, uv).r;
                half4 centerColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);

                // Gaussian-like weights for diamond pattern
                // Center weight: 0.25, ring-1 (4 taps): 0.125 each, ring-2 (8 taps): 0.03125 each
                static const half w0 = 0.25h;
                static const half w1 = 0.125h;
                static const half w2 = 0.03125h;

                half4 result = centerColor * w0;
                half totalWeight = w0;
                half threshold = (half)_BilateralDepthThreshold;

                // Ring 1: 4 axis-aligned neighbors at distance 1
                static const float2 ring1Offsets[4] = {
                    float2( 1, 0), float2(-1, 0),
                    float2( 0, 1), float2( 0,-1)
                };

                [unroll]
                for (int i = 0; i < 4; i++)
                {
                    float2 sampleUV = uv + ring1Offsets[i] * texelSize;
                    half sampleDepth = (half)SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, sampleUV).r;
                    half depthDiff = abs(centerDepth - sampleDepth);
                    half depthWeight = depthDiff < threshold ? 1.0h : 0.0h;
                    half w = w1 * depthWeight;
                    result += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, sampleUV) * w;
                    totalWeight += w;
                }

                // Ring 2: 8 diagonal + axis neighbors at distance 2
                // Only include if blur radius >= 2
                if (_BlurKernelRadius >= 2)
                {
                    static const float2 ring2Offsets[8] = {
                        float2( 2, 0), float2(-2, 0),
                        float2( 0, 2), float2( 0,-2),
                        float2( 1, 1), float2(-1, 1),
                        float2( 1,-1), float2(-1,-1)
                    };

                    [unroll]
                    for (int j = 0; j < 8; j++)
                    {
                        float2 sampleUV = uv + ring2Offsets[j] * texelSize;
                        half sampleDepth = (half)SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, sampleUV).r;
                        half depthDiff = abs(centerDepth - sampleDepth);
                        half depthWeight = depthDiff < threshold ? 1.0h : 0.0h;
                        half w = w2 * depthWeight;
                        result += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, sampleUV) * w;
                        totalWeight += w;
                    }
                }

                return result / max(totalWeight, 0.001h);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
