Shader "Hidden/Lotec/BufferGiCubeDebug" {
    // Procedural buffer-GI debug. One solid-color cube per voxel, built entirely in the vertex
    // shader from SV_InstanceID/SV_VertexID (no mesh, no CPU readback). The voxel's color is read
    // straight from the GI StructuredBuffers on the GPU. Single-value GI, so one color per cube
    // (unlike the directional 6-face VoxelCubeDebug this is based on). Mode selects what to show:
    // 0 = occupancy/albedo, 1 = irradiance, 2 = radiance, 3 = occupancy normals (xyz->rgb),
    // 4 = baked sun visibility (radiance.w: white = lit, black = sun-shadowed),
    // 5 = air distance (AIR voxels: distance-to-nearest-solid, dark = near .. white = far).
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
            StructuredBuffer<uint>  _DbgSurface;      // per-voxel surface word (normal in low bits)
            StructuredBuffer<uint>  _DbgOccupancy;    // 1-bit/voxel solidity bitfield (runtime source)

            // Solidity from the occupancy bitfield (mirrors BgiSolidBit in the compute/fragment) - the
            // actual source the runtime reads, rather than re-deriving it from the material albedo.
            bool DbgSolid(uint slot) {
                return (_DbgOccupancy[slot >> 5] >> (slot & 31u)) & 1u;
            }

            float3 _DbgGridDims;  // strided instance grid (gx,gy,gz); instanceCount = product
            float _DbgStride;
            float _DbgCubeFill;
            float _DbgIntensity;
            float _DbgMinLum;
            float _DbgMode;        // 0 occupancy, 1 irradiance, 2 radiance, 3 occupancy normals
            float _DbgFieldOffset; // base index of the field slice being shown (0 fine, BGI_COUNT coarse)

            // Wireframe mode: draw the field bounds as box edges (line topology) instead of cubes.
            float _DbgWire;        // 0 = voxel cubes, 1 = field-bounds wireframe
            float3 _WireOrigin0; float3 _WireSize0; // fine field box (world)
            float3 _WireOrigin1; float3 _WireSize1; // coarse field box (world)

            // Normal-line mode (Normals view): one line per solid voxel along its occupancy normal.
            float _DbgNormalLine;  // 1 = per-voxel normal lines (line topology, 2 verts/instance)
            float _DbgNormalLen;   // world-space length of each normal line

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

            // 12 box edges = 24 line-list vertices, corners in [0,1]^3.
            static const float3 kWire[24] = {
                float3(0,0,0), float3(1,0,0), float3(1,0,0), float3(1,0,1),
                float3(1,0,1), float3(0,0,1), float3(0,0,1), float3(0,0,0), // bottom
                float3(0,1,0), float3(1,1,0), float3(1,1,0), float3(1,1,1),
                float3(1,1,1), float3(0,1,1), float3(0,1,1), float3(0,1,0), // top
                float3(0,0,0), float3(0,1,0), float3(1,0,0), float3(1,1,0),
                float3(1,0,1), float3(1,1,1), float3(0,0,1), float3(0,1,1)  // verticals
            };

            // Surface normal for a solid voxel: the per-voxel baked value (mirrors SurfaceNormal in
            // BufferGi.compute). Always in _Surface now - filled at bake from mesh normals or the
            // occupancy gradient - so the viewer just reads it.
            float3 DbgSurfaceNormal(uint3 c) {
                return BgiSurfaceNormal(_DbgSurface[(uint)_DbgFieldOffset + BgiIndex(c)]);
            }

            struct v2f {
                float4 positionCS : SV_POSITION;
                float3 color : TEXCOORD0;
            };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
                v2f o;

                // Wireframe: instance 0 = fine box, instance 1 = coarse box.
                if (_DbgWire > 0.5) {
                    float3 origin = (iid == 0u) ? _WireOrigin0 : _WireOrigin1;
                    float3 size   = (iid == 0u) ? _WireSize0   : _WireSize1;
                    float3 world  = origin + kWire[vid] * size;
                    o.positionCS = TransformWorldToHClip(world);
                    o.color = (iid == 0u) ? float3(0.3, 1.0, 0.4) : float3(0.3, 0.8, 1.0);
                    return o;
                }

                // Per-voxel normal line: instance = voxel, vid 0 = center, vid 1 = center + normal*len.
                if (_DbgNormalLine > 0.5) {
                    uint3 gn = (uint3)_DbgGridDims;
                    uint sn = (uint)_DbgStride;
                    uint3 gin;
                    gin.x = iid % gn.x;
                    gin.y = (iid / gn.x) % gn.y;
                    gin.z = iid / (gn.x * gn.y);
                    uint3 voxn = gin * sn;
                    uint idxn = (uint)_DbgFieldOffset + BgiIndex(voxn);
                    float3 nrm = DbgSurfaceNormal(voxn);
                    bool shown = DbgSolid(idxn) && dot(nrm, nrm) > 1e-6;
                    float3 c0 = BgiVoxelCenter(voxn);
                    float3 world = (vid == 0u) ? c0 : c0 + nrm * _DbgNormalLen;
                    if (!shown) world = c0; // no normal (air / ambiguous) -> zero-length, invisible
                    o.positionCS = TransformWorldToHClip(world);
                    o.color = nrm * 0.5 + 0.5; // same xyz->rgb mapping as the cubes
                    return o;
                }

                uint3 g = (uint3)_DbgGridDims;
                uint stride = (uint)_DbgStride;
                uint3 gi;
                gi.x = iid % g.x;
                gi.y = (iid / g.x) % g.y;
                gi.z = iid / (g.x * g.y);
                uint3 vox = gi * stride;
                uint idx = (uint)_DbgFieldOffset + BgiIndex(vox);

                uint mode = (uint)_DbgMode;
                float3 col;
                bool show;
                if (mode == 0u) {
                    show = DbgSolid(idx);              // occupancy from the bitfield
                    col = BgiAlbedo(_DbgMaterial[idx]); // albedo still from the material buffer
                } else if (mode == 3u) {
                    // Normals: solid voxels only, coloured by the occupancy normal (xyz -> rgb).
                    // Grey (~0.5) = an AMBIGUOUS/zero normal (thin slab or enclosed interior).
                    show = DbgSolid(idx);
                    col = DbgSurfaceNormal(vox) * 0.5 + 0.5;
                } else if (mode == 4u) {
                    // Sun visibility: the baked voxel sun-shadow the solve stashes in the radiance's
                    // w channel (solid voxels only). White = lit, black = shadowed from the sun.
                    float w;
                    BgiUnpackRgb(_DbgRadiance[idx], col, w);
                    col = w.xxx;
                    show = DbgSolid(idx);
                } else if (mode == 5u) {
                    // Air distance: city-block distance-to-nearest-solid for AIR voxels (dark = near,
                    // white = far). Cap-saturated open space is hidden so only the shells around
                    // geometry show - air beyond the gather cutoff is the skipped region.
                    uint adist = BgiSurfaceAirDist(_DbgSurface[idx]);
                    show = !DbgSolid(idx) && adist < BGI_MAX_AIR_DIST;
                    col = ((float)adist / (float)BGI_MAX_AIR_DIST).xxx;
                } else {
                    float w;
                    BgiUnpackRgb(mode == 1u ? _DbgIrradiance[idx] : _DbgRadiance[idx], col, w);
                    float lum = dot(col, float3(0.2126, 0.7152, 0.0722)) * _DbgIntensity;
                    show = lum >= _DbgMinLum;
                }

                float3 center = BgiVoxelCenter(vox);
                float3 world = center + kCube[vid] * (_BgiVoxelSize * _DbgCubeFill);
                // Collapse hidden voxels to a point so they don't clutter the view.
                if (!show) world = center;

                // Subtle face shading so the cubes read as cubes, not flat blobs.
                float3 n = kFaceNormal[vid / 6u];
                float shade = 0.55 + 0.45 * saturate(dot(n, normalize(float3(0.4, 0.8, 0.3))));

                // Occupancy (albedo), Normals, Sun visibility and Air distance are 0..1 display values,
                // intensity-independent so they can't blank out; HDR irradiance/radiance scale by intensity.
                float gain = (mode == 0u || mode >= 3u) ? 1.0 : _DbgIntensity;

                o.positionCS = TransformWorldToHClip(world);
                o.color = col * gain * shade;
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                if (_DbgWire > 0.5) return float4(i.color, 1.0);   // wireframe: flat color, no tonemap
                uint m = (uint)_DbgMode;
                if (m >= 3u) return float4(i.color, 1.0); // normals / sun-vis / air-dist: raw, no tonemap
                float3 c = i.color / (1.0 + i.color); // Reinhard tonemap for display
                return float4(c, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
