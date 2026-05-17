#ifndef LOTEC_VOXEL_OCCLUSION_FIELD_INCLUDED
#define LOTEC_VOXEL_OCCLUSION_FIELD_INCLUDED

// Per-direction occlusion field query.
//
// Expects the includer to provide:
//   float3 _SdfBoundsMin;
//   float3 _SdfBoundsSize;
//   float3 _InverseVoxelSize;
//
// Shader globals set by OcclusionFieldQuery.ApplyShaderGlobals():
//   float3 _OccFieldSunDir     - normalized sun direction
//   int    _OccFieldSunChannel  - which RGBA channel (index % 4)
//   Texture3D _OccFieldTex      - the active occlusion field texture for the current sun direction

// Sun direction query results (set from C# each frame)
float3 _OccFieldSunDir;
int _OccFieldSunChannel;

// Active occlusion field texture (bound per-frame to the texture matching the sun direction).
TEXTURE3D(_OccFieldTex);
SAMPLER(sampler_OccFieldTex);

// -----------------------------------------------------------------------------
// SHADOW QUERY FUNCTIONS
// -----------------------------------------------------------------------------

/// Sample the occlusion field shadow for a given world position using the
/// precomputed sun direction (single nearest direction, hardware trilinear).
/// Returns 0.0 (shadow) to 1.0 (lit).
float GetOccFieldShadow(float3 worldPos) {
    float3 uvw = (worldPos - _SdfBoundsMin) / max(_SdfBoundsSize, 1e-6);
    float4 raw = _OccFieldTex.SampleLevel(sampler_OccFieldTex, uvw, 0);
    if (_OccFieldSunChannel == 0) return raw.r;
    if (_OccFieldSunChannel == 1) return raw.g;
    if (_OccFieldSunChannel == 2) return raw.b;
    return raw.a;
}

/// Same as GetOccFieldShadow but with a normal-based offset to reduce self-occlusion.
float GetOccFieldShadow(float3 worldPos, float3 normal) {
    float3 voxelSize = 1.0 / max(_InverseVoxelSize, 1e-6);
    float3 offsetPos = worldPos + normal * voxelSize * 1.2;
    return GetOccFieldShadow(offsetPos);
}

#endif
