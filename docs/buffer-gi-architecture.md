# Buffer GI architecture

Reference for the `GI_VOXEL_BUFFER` path: which buffers exist, what every bit means, which pass
writes it, which reads it.

Files: `BufferGiUpdater.cs` (driver), `BufferGiBake.compute` (static derive), `BufferGiSolve.compute`
(per-frame solve), `BufferGiRead.hlsl` (fragment), `BufferGiField.hlsl` (shared layout).

## Pipeline

```
 raster            bake (once)              solve (per frame)          read (per pixel)
 BufferGiVoxelize  CSInjectBakedLights      CSInject   ─┐              BgiSampleFieldTexture
   ↓               CSBuildOccupancy         CSGather    ├ per field    BgiSampleShadowTexture
 _Material         CSBuildNormalOccupancy   CSBlur     ─┘              BgiSampleFaceAoShadow
 _Surface (seed)   CSBuildSurface                ↓
                   CSBuildAirDistance ×5     _BgiIrradianceTex ──────→ one trilinear tap
                        ↓
                   _Occupancy, _Surface
```

Contracts:

- `BufferGiVoxelData.hlsl` declares exactly the three **static** fields (`_Material`, `_Occupancy`,
  `_Surface`). If a buffer is declared there, the bake owns its contents. `_Radiance`, `_Irradiance`
  and `_IrradianceBlur` are solve-owned and deliberately not there.
- The fragment reads only `_Occupancy` and `_Surface`, and only for the AO face plane. All lighting
  arrives via the mirror `Texture3D`.
- Every `ShaderLibrary/` header is engine-agnostic; the engine boundary is the `.shader` / `.compute`
  entry points. `BufferGiCommonCanary.compute` enforces it — do not add includes to the canary.

## Grid

One cubic, power-of-two grid per field. Power-of-two is load-bearing: index math is shifts/masks and
`Grid³` stays a multiple of 32, keeping the occupancy bitfield word-aligned per field.

```hlsl
BgiIndex(c) = c.x | (c.y << GRID_LOG2) | (c.z << (GRID_LOG2*2))   // X fastest
BgiSlot(c)  = _FieldOffset + BgiIndex(c)
```

- **Two fields, one allocation.** `FieldCount = 2`; coarse = slot 0, fine = slot 1, so future fine
  fields append at 1..N-1. Field `f` occupies `[f*BGI_COUNT, (f+1)*BGI_COUNT)`. `_FieldOffset` is set
  per dispatch — `SolveField` runs one field at a time.
- **Voxels are per-axis.** The grid stretches to fill non-cubic bounds, so `_BgiVoxelSize` is a
  `float3`. Step in *grid* units or the step is anisotropic.
- **Resolution** is `BufferGiUpdater._giResolution`, snapped to a power of two in `[4, 256]`,
  independent of `VoxelVolume._maxResolution`. Changing it reallocates every buffer.
  **Do not exceed 64** — 128 TDRs the GPU
  ([details](decoupling-field-resolutions.md#appendix-a--measured-evidence)).

## Buffers

Sized for all fields (`TotalVoxels = FieldCount * Grid³`).

| buffer | element | slots/voxel | B/voxel | written by | read by |
|---|---|---|---|---|---|
| `_Material` | `uint` | 1 | 4 | raster; `CSInjectBakedLights` | **`CSInject` only** |
| `_Occupancy` | bitfield | 1 bit | 0.125 | `CSBuildOccupancy` | every solidity test; DDA; blur gate; fragment AO |
| `_OccupancyThick` | bitfield | 1 bit | 0.125 | `CSBuildNormalOccupancy` | `OccupancyNormal` (bake) only |
| `_Surface` | `uint` | 1 | 4 | raster → `CSBuildSurface` → `CSBuildAirDistance` | inject, gather, blur, fragment AO |
| `_Radiance` | `uint2` | 1 or 2 | 8–16 | `CSInject` | `CSGather`, `CSBlur` (alpha) |
| `_Irradiance` | `uint2` | 1 or 6 | 8–48 | `CSGather` | `CSInject` (bounce), `CSBlur` |
| `_IrradianceBlur` | `uint2` | 1 or 6 | 8–48 | `CSBlur` | `CSBlur` (own history) |
| `_BgiIrradianceTex` | `ARGBHalf` Tex3D | 1 or 6 slabs | 8/texel | `CSBlur` (fused) | **the fragment** |

Mirror texture: `Grid × Grid × (Grid * IrradianceSlots)`, random-write, bilinear/clamp.
**RGB = blurred irradiance, ALPHA = baked sun visibility.** One per field.

## Bit layouts

### `_Material`

```
 31          24 23          16 15           8 7            0
| emission8    | albedo B     | albedo G     | albedo R     |
```

- `BgiAlbedo(m)` → `half3` 0..1 (8-bit → fp16 is lossless).
- `BgiEmission8(m)` → raw 0..1; decode with `DecodeEmissionIntensityFrom8Bit` (log, max 1024, fp32).
- `BgiIsSolid(m) = (m & 0x00ffffff) != 0` — albedo doubles as the solidity flag. The bake forces every
  solid voxel to a nonzero dark-gray albedo so **empty is the only all-zero state**.

### `_Surface`

```
 31 30      26 25 24 23          16 15                          0
|  | origin k |TS|EM| openness     | octahedral normal          |
                     -or- airdist  | (8 bits per axis)
```

| bits | meaning |
|---|---|
| 0–15 | octahedral normal, 8 b/axis. `BgiPackSurfaceNormal` / `BgiSurfaceNormal` |
| 16–23 | **solid** → openness / static AO; **air** → city-block distance to nearest solid, capped at `BGI_MAX_AIR_DIST` = 5 |
| 24 | `BGI_SURFACE_FLAG_EMISSIVE` `0x01000000` |
| 25 | `BGI_SURFACE_FLAG_TWOSIDED` `0x02000000` |
| 26–30 | sun-ray origin, `0x7c000000`, `k = (dz+1)*9 + (dy+1)*3 + (dx+1)`, 0..26 |
| 31 | free |

- Bits 16–23 never collide — a voxel is either solid or air. **Pick by the occupancy bit**, never by
  the value.
- A written normal can never encode to zero (`-X` → `0x8000`), so `word & 0xffff == 0` is a reliable
  "the voxelizer never wrote one" test.
- `BGI_SURFACE_FLAGS_MASK` (`0xff000000`) covers the origin bits too, so `CSBuildSurface` carries
  flags *and* origin across when it rewrites the word.
- A zeroed word decodes the origin to `(-1,-1,-1)`, not self — callers keep their "landed in solid →
  use the cell" guard.
- The `BGI_SURFACE_FLAGS_MASK` comment saying "26-31 still reserved" is stale; the origin took 26–30.
- **Nothing here may depend on the light.** A sun change restarts the solve but does not re-run the
  derive passes, so a sun-scored value would freeze at bake-time sun.

### `_Radiance` / `_Irradiance`

```
 uint2.x = f32tof16(R) | f32tof16(G) << 16
 uint2.y = f32tof16(B) | f32tof16(W) << 16      // W = sun visibility, both buffers
```

Manual pack because `StructuredBuffer<half>` is patchy on WebGPU/GLES. `BgiUnpackRgbH` stays in fp16
registers; `BgiUnpackRgb` round-trips through fp32. Kernel accumulators are fp32 — a sample can sit
near the fp16 ceiling and summing several overflows.

## The two directional strides

Two independent direction counts, and they are not the same number.

| | `_BgiRadianceDirs` | `_BgiIrradianceDirs` |
|---|---|---|
| quantity | **outgoing** radiance | **incident** irradiance |
| Single / Cube | 1 / **2** | 1 / **6** |
| accessor | `BgiRadianceSlot(slot, faceN, n, surfaceWord)` | `BgiIrradianceBase(slot) + bucket` |

- **Radiance caps at 2** because outgoing radiance is a property of real geometry and the voxelizer
  stores one normal per cell. The only describable second face is its negation, under `TWOSIDED`. Six
  outgoing faces would need a per-face coverage mask the rasterizer does not produce.
- **Irradiance is 6** because a hemisphere is well defined for every voxel, air or solid. Buckets are
  cosine lobes about the six world axes; a surface reconstructs `sum(n_k² * bucket_k)` over the 3 axes
  in its hemisphere, weights summing to 1.

`BgiRadianceSlot` is the only place that knows the radiance mode. A slot is identified by the
**outward normal of the face it serves**:

| caller | `faceN` |
|---|---|
| inject, writing the face with outward normal `f` | `f` |
| gather, ray travelling `dir` (crossed the opposing face) | `-dir` |
| fragment, surface normal `N` | `N` |

A one-sided voxel always collapses to the front slot: its back slot holds the `CSClear` zero, which
decodes as black radiance *and* zero sun visibility — "no light, fully shadowed" for a face that
exists. Both strides clamp to `>= 1`; an unbound uniform reads 0, and a stride of 0 would collapse
every voxel onto slot 0.

## Bake

`BufferGiUpdater.RunDerivePasses`, once per voxelize or bake-asset load. **Order is load-bearing.**

| # | kernel | writes | notes |
|---|---|---|---|
| 0 | `CSInjectBakedLights` | `_MaterialWrite` | stamps `VoxelLights` as emissive voxels; C# resolves world→cell and merges. Batched at 16. |
| 1 | `CSBuildOccupancy` | `_Occupancy` | one thread per **word** (32 voxels); dispatch `VoxelCount/32/64` |
| 2 | `CSBuildNormalOccupancy` | `_OccupancyThick` | real solids + the cell behind each surfaced voxel along its triangle normal. After 1 (needs the finished bitfield), before 3 (which overwrites those normal bits). C# zeroes first; the kernel only ORs. |
| 3 | `CSBuildSurface` | `_Surface` | solid → normal, openness, flags, sun-ray origin; air → air-distance seeded at the cap |
| 4 | `CSBuildAirDistance` **×5** | `_Surface` 16–23 | min-relaxation, one voxel per pass. `AirDistancePasses` must equal `BGI_MAX_AIR_DIST`. Safe in place. |

`CSBuildSurface`'s normal:

1. Occupancy gradient over **`_OccupancyThick`**, not `_Occupancy` — a hollow one-voxel shell has air
   on the room side *and* inside, so plain occupancy cancels or resolves along the wall plane. Backing
   the wall recovers the true bisector at inside corners.
2. Voxelizer triangle normal only where even that cancels.
3. **The gradient's sign is kept.** An earlier revision flipped it to agree with the triangle normal;
   that measurement was circular (the triangle normal is last-write-wins and was seen flipping between
   bakes). Do not reintroduce.

Known gap: `TWOSIDED` is tested on the stored normal's dominant axis only, missing 232 of 5,693 thin
Sponza voxels. See [decoupling-field-resolutions.md](decoupling-field-resolutions.md).

## Solve

`Update` → `DispatchSolve` → `SolveField` per field (fine, then coarse). All kernels
`numthreads(64,1,1)`, dispatched over `ceil(VoxelCount/64)`.

**Gating:** runs while `_collectedSamples < _maxSamples` or `_continuousGi`; otherwise idles.
`HasSunChanged()` resets the counter (local lights deliberately excluded). `_SampleBase` is the
**global ray ordinal**, not the frame — which is what makes `samplesPerFrame` a pure rate knob: the
same 500 low-discrepancy points however the budget is sliced.

**`CSInject`** — solid voxels only (occupancy tested first, so air never touches the cold
`_Material`). `outgoing = emissive + min(albedo, 0.95) * (direct + bounce)`.
- `bounce` is the voxel's **own** `_Irradiance` (front hemisphere), not the adjacent air probe —
  reading the probe made the wall see its own outgoing radiance (F_ii loop).
- Albedo clamped below 1: albedo 1 is a lossless cavity that diverges to Inf/NaN.
- `direct` marches a shadow ray from `BgiShadowOriginStep`; sun visibility goes in the radiance `w`.
- Back face written only when `_BgiRadianceDirs > 1 && TWOSIDED`, with `-normal` and its own shadow ray.

**`CSGather`** — far-air skip first: air with `airDist > BGI_GATHER_MAX_AIR_DIST` (4) returns
immediately, since nothing reads it.
- Air samples omnidirectionally; surfaces cosine-sample the front hemisphere; two-sided in Cube
  samples the full sphere.
- A baked-light voxel is solid but **not** a surface — it gathers like air, or `CSBlur` punches a
  black hole where the lamp sits.
- `GatherRayRadiance`: hit front face / emissive / two-sided → the hit's `_Radiance` slot, reach-
  weighted then `ClampFirefly`; hit a real interior back face → `_AmbientFloor`; escape →
  `EvalEnvSH(dir)`.
- Sun visibility per `SunShadowSampling`: `Centre` (one ray = one **bit**, the source of Baked
  blockiness), `Supersampled` (n stratified interior origins → an area fraction, instant on sun move),
  `Temporal` (one ray/frame into the progressive mean).
- The bounce is a plain average — unbiased at any sample count, so `samplesPerFrame` never changes
  brightness.

**`CSBlur`** — three jobs, **no far-air skip** (runs over every voxel).
- *Air:* occupancy-gated 6-neighbour box blur, then eased from the previous `_IrradianceBlur` by
  `_Confidence` so a light change fades in and lands exactly at convergence.
- *Solid RGB:* dilated from **air** neighbours, 26-neighbourhood, `1/distance` weighted,
  front-hemisphere gated on the voxel's normal. Ungated averaging hands a thin wall's dark side the
  bright side's value — measured ~10% brighter than reference on Sponza's lower arcade. In Cube each
  bucket is filled from the air on its own side.
- *Solid alpha:* **not** dilated — it is the cell's own `_Radiance[...].w` for the stored normal's
  face. In Single one texel serves both sides of a sub-voxel wall; ~24–26% of shell cells are
  contested. Cube overrides per slab.
- *Mirror:* fused into the same pass. Alpha is not confidence-eased (deterministic, so it responds
  immediately to a sun move).

**Others:** `CSClear` (zero a field; a fresh `ComputeBuffer` holds garbage, and the clear is deferred
to `Update` so the grid constants are bound first), `CSInitFineFromCoarse` (seed a moved fine field
from the coarse approximation instead of black), `CSAverageLuminance` (air-voxel luminance in a
camera-centred radius for `AutoExposure`; field follows the camera).

## Fragment

All in `BufferGiRead.hlsl` under `#if defined(GI_VOXEL_BUFFER)` — WebGPU fails pipeline creation for
a variant declaring unbound globals, which D3D11/Vulkan tolerate.

**`BgiSelectField`** — bounds test on the fine box; returns origin, voxel size, buffer slice. Shared
by all three reads so they agree on the same voxels.

**`BgiSampleFieldTexture`** — the GI tap. Takes the **geometric** normal (it picks the axis; the grid
knows nothing about normal maps).
- *Single / Fast* (default): offset `g += normal / max|normal|` so the dominant axis clears one cell,
  then one trilinear tap. The offset is continuous, not snapped — a snap jumps a whole cell where the
  largest component changes, painting hard edges across carved detail.
- *Single / AxisSnapped* (`BGI_TAP_AXIS_SNAPPED`): up to three taps, each snapped to the cell centre
  one step along **its own** axis, weighted by `n²`. Taps below `BGI_TAP_MIN_WEIGHT` (0.01) are
  skipped and renormalised, so an axis-aligned face takes one tap.
- *Cube*: three taps, each offset along its own axis into its own Z slab. Where a normal component
  crosses zero both the offset and the slab flip, but `n²` is exactly 0 there, so it stays continuous.

**`BgiSampleShadowTexture`** — one trilinear tap of the mirror **alpha**, offset
`_BgiShadowNormalOffset` voxels along the normal (0.5 = first air voxel's centre; 1.0 over-reaches and
is why a floor beside a tall occluder reads shadowed). Returns 1 (lit) outside the field. In Cube it
reads the three hemisphere slabs weighted by `n²`. `_BgiShadowSharpness` re-centres on 0.5 and
steepens — this works only because the solve stores a coverage fraction.

**`BgiSampleFaceAoShadow`** — the sole authority for the main-light shadow, fully resolved with no
fall-through: `Off` = genuinely no sun shadow; `Baked` = the alpha tap; `Sdf` / `OcclusionField` /
`Bitmask` delegate to their baked sources, `saturate`d so a NaN from a mis-bound source reads as
shadowed rather than poisoning the fragment to black.
AO is a 4-tap bilinear read of `_Surface` openness on the face plane; non-solid and out-of-bounds taps
are skipped and renormalised, and AO stays 1.0 if none was solid. Entirely skipped when
`_BgiAoStrength <= 0`.

## Ambient occlusion

A static, baked, per-solid-voxel **openness** scalar that multiplies the indirect bounce only. It is
not a screen-space effect and it is not part of the solve — nothing about it changes per frame.

**Bake** — `BgiComputeOpenness`, called from `CSBuildSurface`: the cosine-weighted fraction of the
front hemisphere blocked by solid voxels within `BGI_AO_RADIUS` = 2 (a 5×5×5 neighbourhood).

```
w = dot(dir, n) / |dir|              // cosine of the neighbour direction to the surface normal
skip if w <= BGI_AO_MIN_COS (0.15)   // back hemisphere + near-coplanar self-neighbours
openness = 1 - blocked / total       // 1 = flat/convex (no AO), < 1 = concave or contact gap
```

The coplanar cutoff is what stops a voxelized diagonal or curved wall self-shadowing into a grid of
false AO. Result is 8 bits into `_Surface` 16–23 — **solid voxels only**; air voxels use those same
bits for the air-distance.

**Read** — `BgiSampleFaceAoShadow`: 4-tap bilinear on the face plane (dominant normal axis fixed, the
two in-plane axes interpolated). Non-solid and out-of-bounds taps are skipped and the weights
renormalised; `ao = lerp(1, opennessAcc / wsum, _BgiAoStrength)`. If no tap was solid, `ao` stays 1 —
no occlusion information is treated as unoccluded. The whole path, including all buffer traffic, is
skipped when `_BgiAoStrength <= 0`. Inspect with `DebugView.Ao`.

### Status: scheduled for removal

**Decided 2026-08-23: AO is being removed** — see P1 in the [implementation plan](decoupling-field-resolutions.md#p1--remove-ambient-occlusion).
A 32^3 field is too coarse to carry ambient occlusion, and it looks it. Recorded here so the
reasoning is not rediscovered:

1. **Wrong spatial scale.** Radius 2 at `giResolution` 32 is ±1.36 m on Sponza's X axis. That is not
   contact occlusion — it is a low-frequency darkening blob. The read then varies at 0.68 m
   granularity, so it is visibly blocky on anything with detail finer than a voxel.
2. **Sub-voxel geometry gets none.** `wsum == 0` (no solid tap on the face plane) returns `ao = 1`, so
   thin detail is unoccluded while coarser geometry beside it is heavily occluded — inconsistent in
   exactly the places AO is supposed to help.
3. **It double-counts.** The gather already integrates an omnidirectional probe that contains real
   occlusion; AO multiplies on top of it. The global's own description — restoring "the contact
   shadowing the omni gather reads weakly" — is an admission that it is a fudge for an under-resolved
   gather. Raising occupancy resolution attacks the same problem at the source; see
   [decoupling-field-resolutions.md](decoupling-field-resolutions.md).
4. **It is the fragment's only `StructuredBuffer` consumer.** Outside `BGI_DEBUG_VIEWS`, the AO face
   read is the *sole* reader of `_Occupancy` and `_Surface` in `BufferGiRead.hlsl`. Removing it makes
   the fragment purely texture-based: no SSBO taps (the Adreno cost the mirror texture exists to
   avoid) and a smaller WebGPU pipeline-layout surface.
5. **It frees 8 bits** of `_Surface` on solid voxels.

Caveats before acting: `_BgiAoStrength = 0` already disables it with zero buffer traffic, so this is a
question about deleting code and storage rather than about turning the effect off. And only the
openness bits are freed — the `_OccupancyThick` machinery and the gradient normal stay required for
everything else.

## Globals and keywords

| global | meaning |
|---|---|
| `_BgiGrid`, `_BgiGridLog2`, `_BgiCount` | resolution and derived index constants |
| `_BgiGridOrigin/Size/VoxelSize`, `_BgiCoarseOrigin/VoxelSize` | field bounds |
| `_FieldOffset` | current field's slice (per dispatch) |
| `_BgiRadianceDirs`, `_BgiIrradianceDirs` | the two strides |
| `_BgiIntensity` | sun `bounceIntensity`; scales the bounce only |
| `_BgiAoStrength`, `_BgiShadowModeFine/Coarse`, `_BgiShadowSharpness`, `_BgiShadowNormalOffset` | read-side tuning |
| `_BgiDebugView` | analysis view selector |
| `_Occupancy`, `_Surface`, `_BgiIrradianceTex`, `_BgiIrradianceTexCoarse` | the fragment's four bindings |

Keywords: `GI_VOXEL_BUFFER` (selects this path), `BGI_TAP_AXIS_SNAPPED` (Single tap filter),
`BGI_DEBUG_VIEWS` (analysis views + `BgiSolidWeightAt`).

`DebugView`: `Off 0`, `GiOnly 1`, `SunVisibility 2`, `Ao 3`, `DirectOnly 4`, `GiSolidWeight 5` — the
last predicts exactly which pixels any leak fix can move.

## Invariants

1. A fresh `ComputeBuffer` holds **garbage, not zeros**. Clear at allocation.
2. `AirDistancePasses == BGI_MAX_AIR_DIST`, and `BGI_GATHER_MAX_AIR_DIST` must stay below it.
3. Nothing in `_Surface` may depend on the light.
4. Grid stays a power of two, or the index math and word-aligned occupancy slices break.
5. Every `_Radiance` access goes through `BgiRadianceSlot`.
6. `TryLoadBakeAssets()` returns before `VoxelizeScene()` — hang per-load work off `Voxelize()`.
7. The fragment's normal must be geometric, not normal-mapped.
8. Runtime voxelization is dead in player builds; a rejected bake is a black field, not a fallback.

See [optimization-guidelines.md](optimization-guidelines.md) for the performance model and
[verifying-changes.md](verifying-changes.md) before measuring anything.
