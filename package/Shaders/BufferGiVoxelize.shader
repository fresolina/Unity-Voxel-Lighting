Shader "Hidden/Lotec/BufferGiVoxelize" {
    // GPU triangle voxelizer for the buffer GI. Rasterizes scene meshes from three orthographic
    // directions (X/Y/Z); each generated fragment writes its voxel's albedo+emission straight into
    // the _Material StructuredBuffer via a fragment-stage UAV. Albedo = base color x base-map sample
    // (the grid-sized raster picks a ~voxel-footprint mip, i.e. the local average texture color);
    // mostly-transparent / alpha-clipped fragments leave their voxel EMPTY (windows don't occupy or
    // block GI rays). Three passes union their coverage so
    // triangles edge-on to one axis are still captured by another - no geometry shader (keeps it
    // Quest/WebGPU compatible), no distance rounding (unlike the SDF / nearest-triangle bakes).
    // Each fragment also thickens the solid one voxel INWARD (opposite the mesh normal) so walls are
    // 2-voxel solid-backed, not a hollow shell - otherwise the runtime occupancy-gradient normal
    // cancels to 0 on a thick mesh's surface voxel (air on both the room side and the hollow interior).
    Properties { }
    SubShader {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass {
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0 // we only write the UAV; the bound dummy RT is just to drive rasterization

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            // BGI_THICKEN: grow each fragment's solid one voxel INWARD - "how much of the grid does
            // this surface occupy", purely a leak control. The per-voxel mesh normal is no longer
            // switchable: it is always written, because CSBuildSurface needs it for the sub-voxel
            // cells where the occupancy gradient cancels and can supply nothing.
            #pragma multi_compile _ BGI_THICKEN
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../ShaderLibrary/BufferGiField.hlsl"

            // Bound via CommandBuffer.SetRandomWriteTarget(1, ...). u1 = first free UAV slot after
            // the one color render target (u0).
            RWStructuredBuffer<uint> _MaterialWrite : register(u1);
            RWStructuredBuffer<uint> _SurfaceWrite : register(u2); // per-voxel surface word (normal in low bits)

            TEXTURE2D(_VoxBaseMap); SAMPLER(sampler_VoxBaseMap); // material base map (white if none)

            float4 _VoxAlbedo;      // rgba base color of the submesh being drawn (a = transparency)
            float4 _VoxBaseMap_ST;  // base-map tiling (xy) + offset (zw)
            float  _VoxCutoff;      // 0 = opaque (alpha never clips); else combined alpha below it = EMPTY voxel
            float  _VoxEmission8;   // 8-bit log-encoded emission intensity
            int    _VoxAxis;        // projection axis: 0 = down X, 1 = down Y, 2 = down Z

            struct Attrib { float3 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Vary { float4 positionCS : SV_POSITION; float3 ws : TEXCOORD0; float3 wn : TEXCOORD1; float2 uv : TEXCOORD2; };

            Vary vert(Attrib i) {
                Vary o;
                float3 ws = TransformObjectToWorld(i.positionOS);
                o.ws = ws;
                o.wn = TransformObjectToWorldNormal(i.normalOS); // for inward thickening in frag
                o.uv = i.uv * _VoxBaseMap_ST.xy + _VoxBaseMap_ST.zw;
                // Project to NDC for the chosen axis (the third axis is recovered per-fragment from
                // the interpolated world position, so exact projection alignment is not required).
                float3 g = (ws - _BgiGridOrigin) / max(_BgiGridSize, 1e-6); // volume-normalized [0,1]
                float2 ndc = (_VoxAxis == 2) ? g.xy : ((_VoxAxis == 1) ? g.xz : g.yz);
                o.positionCS = float4(ndc * 2.0 - 1.0, 0.5, 1.0);
                return o;
            }

            float4 frag(Vary i) : SV_Target {
                // Base-map sample OUTSIDE the branch (keeps the uv derivatives well-defined). The
                // grid-sized raster target makes those derivatives span the texels one voxel covers,
                // so the mip chain hands back roughly the voxel's AVERAGE texture color for free.
                // Colour/alpha are fp16 (they end up as 8-bit channels anyway); the world position
                // and the grid coordinate derived from it stay fp32.
                half4 tex = (half4)SAMPLE_TEXTURE2D(_VoxBaseMap, sampler_VoxBaseMap, i.uv);
                int3 c = (int3)floor(BgiWorldToGrid(i.ws));
                if (all(c >= 0) && all(c < (int)BGI_GRID)) {
                    // Transparency: a mostly-transparent (or alpha-clipped) fragment leaves its voxel
                    // EMPTY - windows/cutouts neither occupy nor block GI rays. _VoxCutoff is 0 for
                    // opaque materials so an opaque base map's (often repurposed) alpha can't punch holes.
                    if ((half)_VoxAlbedo.a * tex.a < (half)_VoxCutoff) return 0;
                    // Floor AFTER the texture multiply so black texels stay occupied (rgb 0 = empty).
                    half3 albedo = max((half3)_VoxAlbedo.rgb * tex.rgb, 1.0h / 255.0h);
                    uint packed = BgiPackMaterial(albedo, _VoxEmission8);
                    _MaterialWrite[BgiSlot((uint3)c)] = packed;

                    // Bake the triangle normal per voxel. CSBuildSurface PREFERS the occupancy gradient
                    // and reads this only where the gradient cancels - a sub-voxel wall with air on
                    // both sides, where occupancy is mathematically silent about orientation and the
                    // triangle is the sole record of which side is out. Cheap enough (one UAV store)
                    // that writing it always beats deciding per bake whether those cells matter.
                    // Multiple triangles per voxel: last-write-wins (fine for flat surfaces).
                    // Two-sidedness is NOT detected here. Comparing against the normal already in the
                    // cell would need a read-modify-write, and the two faces' fragments race for the
                    // same voxel - a masked store can interleave and mix two normals into a third,
                    // garbage one. CSBuildSurface derives it instead, from occupancy (a solid voxel
                    // with air on BOTH sides along its normal), which is race-free, order-independent
                    // and tests the condition that actually matters rather than triangle bookkeeping.
                    if (dot(i.wn, i.wn) > 1e-6)
                        _SurfaceWrite[BgiSlot((uint3)c)] = BgiPackSurfaceNormal(normalize(i.wn));

                #if defined(BGI_THICKEN)
                    // Thicken one voxel INWARD (opposite the surface normal) so a wall is solid-backed
                    // instead of a 1-voxel hollow shell. LEAK BLOCKING is the reason: a sub-voxel
                    // occluder (curtain, banner, railing) occupies one cell with lit air on BOTH sides,
                    // and CSBlur's shell dilation then fills that cell's buckets from air on both sides
                    // at equal weight - so a surface on the dark side reads the lit side's light
                    // through it. Thickening makes the occluder opaque in the grid, and the dilation
                    // skips solid neighbours outright.
                    // Side effect worth knowing: a thickened wall is no longer THIN, so CSBuildSurface's
                    // two-sided detection stops firing on it and Cube's back-face radiance retires.
                    // The grown cell gets _MaterialWrite but no _SurfaceWrite, so its normal comes from
                    // the gradient (or, if that cancels, CSBuildSurface's thin-axis convention).
                    // Growing INTO the geometry (never outward) can't close openings or move the
                    // visible face. Caveat: a genuinely 1-voxel partition between two rooms grows 1
                    // voxel into each, and ALL thin geometry gains bulk - measured +73% solid cells in
                    // Sponza, which on foliage or railings can read as bloating.
                    if (dot(i.wn, i.wn) > 1e-6) {
                        int3 back = c - int3(round(normalize(i.wn)));
                        if (all(back >= 0) && all(back < (int)BGI_GRID))
                            _MaterialWrite[BgiSlot((uint3)back)] = packed;
                    }
                #endif
                }
                return 0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
