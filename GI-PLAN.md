# Global Illumination (indirect lighting) plan

We are building a voxel-based global illumination system that simulates indirect lighting through light bounces within a voxel grid. The system consists of two main phases: prebaking and runtime updates. During runtime, we will use a compute shader to update the radiance field based on the hit material properties and incoming light.
We already have a SDF baker for the hires SDF used for shadows, and a Raymarching computeshader function (VoxelSdfShadows.hlsl) that can be reused for GI raymarching.

1. Prebaking: Create a compute shader baker that creates a static readonly lowres material field used for light bounces (complement to the SDF for describing surface color). Each voxel finds the actual color and roughness from the mesh material data, so it needs to map the voxel center to the closest point on the surface. It should be 1/4 of the hires SDF resolution (we should also ensure the hires SDF is a multiple of 4 in each dimension). This material field is stored as a Texture3D with 4 channels. rgb for color and a for roughness.

2. Runtime: Compute shader pass that runs before the fragment shader.
It will read and write in the same field, so we need to double buffer it, and Ping-Pong each frame so we read from one buffer and write to the other.
Create two read/write radiance field (lowres voxel field, same resolution as the material field) that stores accumulated incoming light and direction. We have two options settable via global shader keyword (LOTEC_GI_HDR) Split it into two Texture3D, one 32bit with HDR color (R11G11B10_Float), one with Control data like Direction of dominant light and one channel for Direct shadow. If not LOTEC_GI_HDR, each voxel will store rgb for accumulated light and a for direction (octahedral packed to 8bit). This field is updated every frame using a compute shader that raymarches from each voxel cell towards the light sources, accumulating incoming light based on the material properties from the material field.

Pseudo-code for the GI update:
foreach (voxel in the radiance field)
{
  // Ensure we always check the most dominant light direction first
  // rayDirection = GetDominantLightDirection(voxel.direction, iteration)
  for (_LOTEC_GI_RAYS_PER_FRAME)
  {
    // Generate a random direction based on the voxel's material roughness
    rayDirection = SampleSphere(voxelMaterial.roughness)
    // Check what the ray hits (we will need the hit position to sample material)
    lit = Raymarch(voxel.position, rayDirection, outHitPosition)
    // If we hit something.
    if (lit > 0.1 && lit < 1.0) {
      // Sample surface hit material properties
      surfaceMaterial = SampleMaterialField(outHitPosition)
      // Compute incoming light based on material.roughness. If roughness is low, bounce ray and blend with reflected hit.
      incomingLight = ComputeIncomingLight(lit, surfaceMaterial.roughness)
      // Accumulate light and direction
      voxel.accumulatedLight += incomingLight
      if (voxel.maxBrightness < incomingLight.brightness)
        voxel.direction = PackDirection(rayDirection)
    }
  }
}

## Data structures

+-----------------+------+--------------------+------------------------------------------+
| Data Source     | Type | Content               | Purpose |
+-----------------+------+--------------------+------------------------------------------+
| Mesh Textures   | 2D | Albedo + Normal         | High-res surface detail. |
| SDF Field1      | 3D | 10cm Distance           | Hires occlusion/shadows. |
| SDF Field2      | 3D | 40cm Distance           | Lowres occlusion. Use for optimizing raymarching |
| Material Field1 | 3D | 40cm Albedo/Rough       | Color for light bounces. |
| Material Field2 | 3D | 40cm Emission/Metallic  | Emissive color and metallic factor. (optional) |
| Radiance Field1 | 3D | 40cm 32bit HDR RGB      | The previous frame "Light Energy" at that point. read |
| Radiance Field2 | 3D | 40cm 32bit HDR RGB      | The current frame "Light Energy" at that point. write |
| Control Field   | 3D | 40cm Direction/Shadow/Stability | The "Direction" the energy is moving + shadow map + Stability How many rays have hit this voxel? (0 = New/Noisy, 1 = Static/Clean). |
+-----------------+------+--------------------+------------------------------------------+
