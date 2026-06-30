Shader "Hidden/Lotec/BufferGiCubeDebug" {
    // Procedural buffer-GI debug. One solid-color cube per voxel, built entirely in the vertex
    // shader from SV_InstanceID/SV_VertexID (no mesh, no CPU readback). The voxel's color is read
    // straight from the GI StructuredBuffers on the GPU. Single-value GI, so one color per cube
    // (unlike the directional 6-face VoxelCubeDebug this is based on). Mode selects which buffer:
    // 0 = occupancy/albedo, 1 = irradiance, 2 = radiance.
    Properties { }
    SubShader {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay" }
        Pass {
            // Faces wind CCW about their outward normal; Cull Back keeps the outward faces and
            // hides the inward ones.
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../Shaders/Includes/BufferGiField.hlsl"

            StructuredBuffer<uint>  _DbgMaterial;
            StructuredBuffer<uint2> _DbgRadiance;
            StructuredBuffer<uint2> _DbgIrradiance;

            float3 _DbgGridDims;  // strided instance grid (gx,gy,gz); instanceCount = product
            float _DbgStride;
            float _DbgCubeFill;
            float _DbgExposure;
            float _DbgMinLum;
            float _DbgMode;       // 0 occupancy, 1 irradiance, 2 radiance

            // 36 verts = 6 faces * 2 tris. Face order: 0=+X 1=-X 2=+Y 3=-Y 4=+Z 5=-Z.
            static const float3 kCube[36] = {
                // All faces wound CCW about their OUTWARD normal (so the geometric normal points
                // out and back-face culling hides the inward faces). Face order: +X -X +Y -Y +Z -Z.
                float3( 0.5,-0.5,-0.5), float3( 0.5, 0.5,-0.5), float3( 0.5, 0.5, 0.5),
                float3( 0.5,-0.5,-0.5), float3( 0.5, 0.5, 0.5), float3( 0.5,-0.5, 0.5),
                float3(-0.5,-0.5,-0.5), float3(-0.5, 0.5, 0.5), float3(-0.5, 0.5,-0.5),
                float3(-0.5,-0.5,-0.5), float3(-0.5,-0.5, 0.5), float3(-0.5, 0.5, 0.5),
                float3(-0.5, 0.5,-0.5), float3( 0.5, 0.5, 0.5), float3( 0.5, 0.5,-0.5),
                float3(-0.5, 0.5,-0.5), float3(-0.5, 0.5, 0.5), float3( 0.5, 0.5, 0.5),
                float3(-0.5,-0.5,-0.5), float3( 0.5,-0.5,-0.5), float3( 0.5,-0.5, 0.5),
                float3(-0.5,-0.5,-0.5), float3( 0.5,-0.5, 0.5), float3(-0.5,-0.5, 0.5),
                float3(-0.5,-0.5, 0.5), float3( 0.5,-0.5, 0.5), float3( 0.5, 0.5, 0.5),
                float3(-0.5,-0.5, 0.5), float3( 0.5, 0.5, 0.5), float3(-0.5, 0.5, 0.5),
                float3(-0.5,-0.5,-0.5), float3( 0.5, 0.5,-0.5), float3( 0.5,-0.5,-0.5),
                float3(-0.5,-0.5,-0.5), float3(-0.5, 0.5,-0.5), float3( 0.5, 0.5,-0.5)
            };
            static const float3 kFaceNormal[6] = {
                float3(1,0,0), float3(-1,0,0), float3(0,1,0), float3(0,-1,0), float3(0,0,1), float3(0,0,-1)
            };

            struct v2f {
                float4 positionCS : SV_POSITION;
                float3 color : TEXCOORD0;
            };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
                v2f o;
                uint3 g = (uint3)_DbgGridDims;
                uint stride = (uint)_DbgStride;
                uint3 gi;
                gi.x = iid % g.x;
                gi.y = (iid / g.x) % g.y;
                gi.z = iid / (g.x * g.y);
                uint3 vox = gi * stride;
                uint idx = BgiIndex(vox);

                uint mode = (uint)_DbgMode;
                float3 col;
                bool show;
                if (mode == 0u) {
                    uint m = _DbgMaterial[idx];
                    show = BgiIsSolid(m);
                    col = BgiAlbedo(m);
                } else {
                    float w;
                    BgiUnpackRgb(mode == 1u ? _DbgIrradiance[idx] : _DbgRadiance[idx], col, w);
                    float lum = dot(col, float3(0.2126, 0.7152, 0.0722)) * _DbgExposure;
                    show = lum >= _DbgMinLum;
                }

                float3 center = BgiVoxelCenter(vox);
                float3 world = center + kCube[vid] * (_BgiVoxelSize * _DbgCubeFill);
                // Collapse hidden voxels to a point so they don't clutter the view.
                if (!show) world = center;

                // Subtle face shading so the cubes read as cubes, not flat blobs.
                float3 n = kFaceNormal[vid / 6u];
                float shade = 0.55 + 0.45 * saturate(dot(n, normalize(float3(0.4, 0.8, 0.3))));

                // Occupancy shows raw albedo (exposure-independent so it can't blank out);
                // the HDR irradiance/radiance modes scale by exposure.
                float gain = (mode == 0u) ? 1.0 : _DbgExposure;

                o.positionCS = TransformWorldToHClip(world);
                o.color = col * gain * shade;
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                float3 c = i.color / (1.0 + i.color); // Reinhard tonemap for display
                return float4(c, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
