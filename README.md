# Voxel Lighting for Unity

A voxel-based lighting system playground for Unity. Goal is a performant lighting system with no dependency on Unity lightmaps or shadowmaps.

## Repository layout

* `package/` contains the Unity Package Manager package content (`package.json`, `Editor/`, `Runtime/`, `Samples~/`).
* `project-demo/` contains the Unity project used for local validation and WebGL builds.
* The repository root contains docs, changelog, and CI/release configuration.

## Demo samples setup

If you want the demo project to use the in-repo package samples directly, or you want to modify the package samples in place from Windows, run `scripts\setup-samples-link.cmd` manually. The script creates this link:

* `project-demo/Assets/_Samples` -> `../../package/Samples~`

`mklink` requires Windows symlink permission. If the script fails, enable Windows Developer Mode or run the shell as Administrator and try again.

## Features

* Realtime shadows on static and dynamic objects.
  a) Accuracy mode: Ray marching on every pixel.
  b) Performance mode: Occlusion bitmask in the voxel field.

## Platform notes

* Runtime GI in this package depends on compute shaders and 3D textures.
* Web builds are expected to run with the WebGPU graphics backend. The sample web build script explicitly requests WebGPU for BuildTarget.WebGL.
* If the player starts on a non-WebGPU web backend, runtime GI now fails fast instead of falling back to a black GI volume.

## Web preview builds

Pushes to non-`main`, non-`release-please` branches publish a preview WebGL build automatically.

* Each branch keeps a stable preview URL under `previews/<branch-name>/`.
* The GitHub Pages index updates that link in place, and the link text shows the latest short SHA for the published branch build.
* Deleting a preview branch removes its entry from GitHub Pages automatically.

Use the `webgl-pages` workflow in GitHub Actions when you want a manual preview build from a feature branch or a specific commit.

1. Open the repository Actions tab and select the `webgl-pages` workflow.
2. Click `Run workflow`.
3. Optionally set `git_ref` to a branch name, tag, or commit SHA. If you leave it empty, the workflow builds the branch selected in the Actions UI.
4. Leave `publish_kind` as `preview` unless you are intentionally publishing a release build.
5. Optionally set `preview_name` to override the URL prefix. If you leave it empty, the workflow uses `git_ref` or the selected branch name.

Automatic branch previews and manual previews now publish under `previews/<preview-name>/`, so the URL always points at the latest build for that preview name.
If you need to remove a manual preview without deleting a branch, run the `webgl-preview-cleanup` workflow and pass the preview name.

Release Pages builds publish under `versions/<tag>/` when a `release-please` PR is merged into `main`. `release-please` also updates `package/package.json` to the same version recorded in `CHANGELOG.md` and the GitHub release tag. When the GitHub release is published, the release workflow updates the release notes with the Pages links.

## TODO

## TODO 2 PC

* Emission: Separate emission from albedo field. Add emissive materials field that supports light being a different color than albedo.
* Material roughness: Store material roughness, and dominant radiance direction in a second field, for bouncing light on a glossy surface.

## Refactoring TODOs

* Dra ner på antalet uniforms. Kanske gör om de flesta sdf-relaterade typ epsilon osv som konstanter.
* Använd Texture3DReadback.cs där det behövs.
* BakeFibonacciLookup.cs:UnpackOctahedral() ta bort och ersätt med Math.hsls. Även Fibonacci. Pack/UnpackOctahedral -> Pack/UnpackDirection
* RadianceField -> IrradianceField. (Light hitting the voxel, not leaving the voxel)
* Shadervars: _VolumeSize, VolumePosition ska vara world units._VoxelSize måste prefixas med vilket fält det hör till, eller om det räcker med lowres/hires.
  * Vi borde nog ha en global VolumePosition och VolumeSize. Sen egna Resolution och VoxelSize per fält.
* Compile-flagga för att ta bort bitmask-occlusion osv (även emission field).
* Döp om stuff till ...Field.
* Extrahera DirectShadows raymarch från SdfShaderGlobals till egen klass.
* Rename Sdf... stuff to Volume... if it is not SDF specific.
  * Kalla worldPos transformerat till sdf uvw, positionVS eller positionVolumeSpace. Definiera position utan postfix som worldspace.
* Rename SdfShaderGlobals to LightingManager.
* Se till att foldern Assets/VoxelLighting finns. Skapa om inte.
* Fibonachi cheat texture. Skapa den automatiskt om den inte finns. Skippa editor-menyn
* Gör shadern i ShaderGraph med custom lighting. Se sample tutorial, ska redan vara uppsatt.
* Så småningom: Stöd point light (attenuation från distance) och spot light (attenuation från distance och light direction och spridningskon).

## Global Illumination (indirect lighting) plan

1. Prebaking: Create a compute shader baker that creates a static readonly lowres material field used for light bounces (complement to the SDF for describing surface color). Each voxel finds the actual color and roughness from the mesh material data, so it needs to map the voxel center to the closest point on the surface. It should be 1/4 of the hires SDF resolution (we should also ensure the hires SDF is a multiple of 4 in each dimension). This material field is stored as a Texture3D with 4 channels. rgb for color and a for roughness. If possible later, we can try packing emission in the color (e.g. if color.r > 0.5 then it's emissive).
2. Runtime: Compute shader pass that runs before the fragment shader.
It will read and write in the same field, so we need to double buffer it, and Ping-Pong each frame so we read from one buffer and write to the other.
Create two read/write radiance field (lowres voxel field, same resolution as the material field) that stores accumulated incoming light and direction. We have two options settable via global shader keyword (LOTEC_GI_HDR) Split it into two Texture3D, one 32bit with HDR color (R11G11B10_Float), one with Control data like Direction of dominant light and one channel for Direct shadow. If not LOTEC_GI_HDR, each voxel will store rgb for accumulated light and a for direction (octahedral packed to 8bit). This field is updated every frame using a compute shader that raymarches from each voxel cell towards the light sources, accumulating incoming light based on the material properties from the material field.
Pseudo-code for the GI update:

foreach (voxel in the radiance field)
{
  // Ensure we always check the most dominant light direction first
  rayDirection = GetDominantLightDirection(voxel.direction, iteration)
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

Readonly static Hires SDF för fina skuggor. 128x eller 256x om platt.
Readonly static material field. beskrivning per SDF-voxel. (hires eller lowres?)
  Antingen bara ett material-ID (kanske i samma textur som SDF, men som samplas med point), eller en egen textur. (Voxelizern kan känna av om det räcker med 8-bit (256 olika ID:n), eller om vi ska infoga det direkt i gridet.)
material1: albedo.rgb, roughness.a
material2: emission.rgb, metallic.a
Kan börja med bara material1, som behövs för GI-beräkningen.
ReadWrite radiance field: lowres voxel field. Varje voxel fixed storlek eller SDF-storleken * 4 (konfigurerbart).
Sparar accumulated incoming light (rgb) och direction (a, octahedral packed to 8bit)

1. Uppdatera varje frame, genom att raymarcha från varje voxel cell mot ljuskällorna. Spara ljuset (inkl färg), samt direction?
Gör detta i en compute shader.
2. Uppdatera smartare bara de voxlar som ändrats. Dirty-flagga.
3. Uppdatera time-sliced över 4 frames.

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
// 1. High-res data from Mesh
float3 albedo = tex2D(_MainTex, uv).rgb;
float3 normal = UnpackNormal(tex2D(_NormalMap, uv)); // World Space
float roughness = tex2D(_SpecularMap, uv).a; // High-res!

// 2. Low-res data from Voxels
float3 radiance = _RadianceField.Sample(linear_sampler, worldUVW).rgb;
float3 lightDir = DecodeDirection(_ControlField.Sample(linear_sampler, worldUVW).rg);

// 3. Combine
// Use the Mesh Roughness to decide how sharp the "Voxel Light" looks on the wall
float specularGI = CalculateSpecular(normal, lightDir, viewDir, roughness);
float3 finalColor = (albedo *radiance) + (radiance* specularGI);
