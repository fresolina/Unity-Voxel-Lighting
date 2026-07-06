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
            // BGI_BAKED_NORMALS: bake a per-voxel oct normal instead of thickening the solid inward.
            #pragma multi_compile _ BGI_BAKED_NORMALS
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Includes/BufferGiField.hlsl"

            // Bound via CommandBuffer.SetRandomWriteTarget(1, ...). u1 = first free UAV slot after
            // the one color render target (u0).
            RWStructuredBuffer<uint> _MaterialWrite : register(u1);
            #if defined(BGI_BAKED_NORMALS)
            RWStructuredBuffer<uint> _SurfaceWrite : register(u2); // per-voxel surface word (normal in low bits)
            #endif

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
                float4 tex = SAMPLE_TEXTURE2D(_VoxBaseMap, sampler_VoxBaseMap, i.uv);
                int3 c = (int3)floor(BgiWorldToGrid(i.ws));
                if (all(c >= 0) && all(c < (int)BGI_GRID)) {
                    // Transparency: a mostly-transparent (or alpha-clipped) fragment leaves its voxel
                    // EMPTY - windows/cutouts neither occupy nor block GI rays. _VoxCutoff is 0 for
                    // opaque materials so an opaque base map's (often repurposed) alpha can't punch holes.
                    if (_VoxAlbedo.a * tex.a < _VoxCutoff) return 0;
                    // Floor AFTER the texture multiply so black texels stay occupied (rgb 0 = empty).
                    float3 albedo = max(_VoxAlbedo.rgb * tex.rgb, 1.0 / 255.0);
                    uint packed = BgiPackMaterial(albedo, _VoxEmission8);
                    _MaterialWrite[BgiSlot((uint3)c)] = packed;

                #if defined(BGI_BAKED_NORMALS)
                    // Bake the surface normal per voxel; the runtime reads it instead of the occupancy
                    // gradient, so walls need NOT be thickened (hollow 1-voxel shells keep correct
                    // normals). Multiple triangles per voxel: last-write-wins (fine for flat surfaces).
                    if (dot(i.wn, i.wn) > 1e-6)
                        _SurfaceWrite[BgiSlot((uint3)c)] = BgiPackSurfaceNormal(normalize(i.wn));
                #else
                    // Thicken one voxel INWARD (opposite the surface normal) so a wall is solid-backed
                    // instead of a 1-voxel hollow shell. Without this, a surface voxel of a thick mesh
                    // has air on BOTH the room side and the hollow interior, so the runtime occupancy-
                    // gradient normal cancels to 0 (gray) instead of pointing into the room. Growing
                    // INTO the geometry (never outward) can't close openings or move the visible face.
                    // Caveat: a genuinely 1-voxel partition between two rooms grows 1 voxel into each.
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
