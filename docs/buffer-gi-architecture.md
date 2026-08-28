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
 _Material         CSBuildNormalOccupancy   CSBlur     ─┘              BgiSampleSunShadow
 _Surface (seed)   CSBuildSurface                ↓
                   CSBuildAirDistance ×5     _BgiIrradianceTex + _BgiSunVisTex ─→ one tap each
                        ↓
                   _Occupancy, _Surface
```

Contracts:

- `BufferGiVoxelData.hlsl` declares exactly the three **static** fields (`_Material`, `_Occupancy`,
  `_Surface`). If a buffer is declared there, the bake owns its contents. `_Radiance`, `_Irradiance`
  and `_IrradianceBlur` are solve-owned and deliberately not there.
- The fragment reads NO buffer in a shipping variant. All lighting
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

### Two axes, and they are not the same axis

"Coarse" and "fine" now mean two unrelated things, so the words are worth pinning down:

| | **FIELD** (the cascade) | **GRID** (the subdivision) |
|---|---|---|
| what varies | the world **box** | the **resolution** inside one box |
| the two values | `CoarseField` 0 = the scene-covering box, `FineField` 1 = the active volume | `_BgiGrid` (lighting, 32³) and `_BgiOccGrid` (occupancy, 64–256³) |
| owned by | `LightingManager` / `BufferGiFields` | `BufferGiUpdater._giResolution` / `._occupancyResolution` |
| selected by | `BgiSelectField`, per shading point | never selected — both exist at once |

They multiply: **each of the two fields carries its own hi-res occupancy at the same `_BgiOccGrid`.**
`_OccFieldWordOffset` slices the hi-res buffer per field exactly as `_FieldOffset` slices the lighting
buffers, and `BgiWorldToOccGrid` reads whichever field's box is bound for the dispatch.

**The consequence to watch: `_BgiOccGrid` is a resolution, not a density.** Both fields subdivide
their own box into the same number of cells, and those boxes are not the same size, so the same number
buys different physical detail. Measured on Bootstrap at 128³:

| | box | hi-res voxel |
|---|---|---|
| fine field (`Room_dark`) | 6.37 × 6.13 × 10.32 m | 0.050 × 0.048 × 0.081 m |
| coarse field | 17.97 × 6.59 × 11.43 m | **0.140** × 0.052 × 0.089 m |

So the coarse field's X detail is 2.8× worse at the same setting — and
[P5](decoupling-field-resolutions.md#p5--attribute-the-leak-gate) measured that the coarse field is
where the in-plane leak actually lives (80.6% of its pixels take their GI almost entirely from shell
texels, against 7.3% in the fine field). **Whether the occupancy resolution should be per field is an
open question**; today it is one setting for both.

Where this doc says "the coarse level" of the occupancy hierarchy it means `_OccupancyTraversal` — the
lighting-grid mip of the hi-res field — never the coarse *field*.

### The second grid: occupancy

Solidity has its **own, finer** grid over the same world box: `_BgiOccGrid`, from
`BufferGiUpdater._occupancyResolution`, snapped to a power of two in `[64, 256]` and never below
`_BgiGrid`. It is the one axis worth raising — 0.125 bits per voxel against Cube irradiance's 48
**bytes**, a 384:1 ratio, and it costs neither rays nor convergence time. `_giResolution` still sizes
every lighting field; raising *that* is what TDRs the device.

| occupancy res | both fields | typical platform |
|---|---|---|
| 64³ | 64 KB | Quest / WebGL |
| 128³ | 512 KB | desktop default |
| 256³ | 4 MB | high-end PC |

`_occupancyResolutionMobile` overrides it on mobile/WebGL. That is a **load-time downsample of the
same bake asset**, not a second bake: the asset stores whatever level it was baked at plus its
`occGrid`, and `UploadOccupancyHiSlice` OR-downsamples to whatever the platform runs. One asset,
every platform. A *coarser* asset than the platform wants is rejected rather than upsampled.

**Storage is 4×4×4 blocks of two contiguous uints** (`BgiOccWord` / `BgiOccBitMask`). At a 4:1 ratio
one lighting cell is exactly one block, i.e. one 8-byte load that a two-level march can then walk in
registers; at 8:1 a lighting cell is 64 bytes = one cache line. The layout was fixed up front because
changing it invalidates every measurement taken on it.

**`_OccupancyTraversal`** is the OR-downsample of `_OccupancyHi` back onto `_BgiGrid`, in the plain
1-bit layout — the always-hot coarse level of the planned two-level march. Kept **separate** from
`_Occupancy`, which stays the lighting grid's own raster and feeds the blur gate, the air distance,
the surface build and the far-air skip. Never majority-downsampled and never rastered independently:
an empty coarse cell lets a march skip its children untested, so under-estimating is a silently
missed occluder. Measured on Playground: `OR-downsample(128³) ⊇ 32³ raster` with **hi-res-only = 403
(fine) / 246 (coarse) and low-res-only = 0**.

## Buffers

Sized for all fields (`TotalVoxels = FieldCount * Grid³`).

| buffer | element | slots/voxel | B/voxel | written by | read by |
|---|---|---|---|---|---|
| `_Material` | `uint` | 1 | 4 | raster; `CSInjectBakedLights` | **`CSInject` only** |
| `_Occupancy` | bitfield | 1 bit | 0.125 | `CSBuildOccupancy` | every solidity test; DDA; blur gate; the debug-only fragment view |
| `_OccupancyThick` | bitfield | 1 bit | 0.125 | `CSBuildNormalOccupancy` | `OccupancyNormal` (bake) only |
| `_Surface` | `uint` | 1 | 4 | raster → `CSBuildSurface` → `CSBuildAirDistance` | inject, gather, blur (compute only) |
| `_Radiance` | `uint2` | 1 or 2 | 8–16 | `CSInject` | `CSGather`, `CSBlur` (alpha) |
| `_Irradiance` | `uint2` | 1 or 6 | 8–48 | `CSGather` | `CSInject` (bounce), `CSBlur` |
| `_IrradianceBlur` | `uint2` | 1 or 6 | 8–48 | `CSBlur` | `CSBlur` (own history) |
| `_BgiIrradianceTex` | `ARGBHalf` Tex3D | 1 or 6 slabs | 8/texel | `CSBlur` (fused) | **the fragment** |
| `_BgiSunVisTex` | `RHalf` Tex3D at `ShadowGrid³` | 1 | 2/texel | `CSSunVisibility` (on a sun change) | **the fragment** |
| `_OccupancyHi` | bitfield, 4×4×4 blocks | 1 bit | 0.125 | hi-res raster; asset upload | `CSBuildTraversalMip`, `CSBuildSurface` |
| `_OccupancyTraversal` | bitfield | 1 bit | 0.125 | `CSBuildTraversalMip` | the two-level march (built, measured slower than flat, off by default) |
| `_BgiNeighbourMask` | `R8_UInt` Tex3D at `Grid` | 1 | 1/texel | `CSBuildNeighbourMask` (bake) | **the fragment**, under `BGI_TAP_SNAP_INPLANE` only |

`_OccupancyHi` is sized by the **occupancy** grid, not the field grid — see below.

`_BgiIrradianceTex` is `Grid × Grid × (Grid * IrradianceSlots)`, random-write, bilinear/clamp, one per
field, carrying the blurred irradiance in **RGB** — its alpha is dead, written as a constant 0.

`_BgiSunVisTex` is a plain `ShadowGrid³` cube, **not slabbed in either mode**, carrying the baked sun
visibility as one fp16 scalar. It was the irradiance mirror's alpha, at the lighting grid, with six Z
slabs in Cube — all three of those changed:

- [P2](decoupling-field-resolutions.md#p2--split-sun-visibility-into-its-own-texture) split it out of
  the alpha, for **no extra tap** (the GI read and the shadow read were already separate `SampleLevel`
  calls at different positions) and to get its asymmetries — deterministic, so never confidence-eased
  — out of the irradiance write.
- [P6](decoupling-field-resolutions.md#p6--fine-shadow-texture) moved it to its own grid and made
  `CSSunVisibility` **re-evaluate** it against the hi-res occupancy on a sun change. Not an upsample:
  sun visibility depends only on occupancy and sun direction, both known at full resolution.
- The **slabs went with it.** They existed so one lighting-grid texel could answer for both sides of a
  sub-voxel wall; at the shadow grid those sides are different texels.

`_BgiNeighbourMask` is 7 bits of neighbour solidity per LIGHTING cell (bit 0 self, 1/2 −X/+X, 3/4
−Y/+Y, 5/6 −Z/+Z), baked once by `CSBuildNeighbourMask` and **point-loaded, never sampled** — an
interpolated bitmask is meaningless, which is also why it cannot ride a spare channel of any of the
volumes above. It gates
[P9](decoupling-field-resolutions.md#p9--contaminated-axis-snap-last-optional)'s in-plane snap. Pure
geometry, so it is a BAKE product and must never be rebuilt on a sun change.

The `_Radiance` **w** channel still carries a sun-visibility value compute-side — `CSInject` needs it
for the direct term the voxel bounces, and it is a spare half in an existing `uint2`, so moving it out
would cost memory for nothing.

**`_Irradiance`'s w is dead, and so is the ray that filled it.** `CSGather` used to march an air-probe
sun ray into it for `CSBlur` to mirror into the sun-visibility texture; P6 replaced that mirror with
`CSSunVisibility` and `CSBlur` now writes a constant 0 there. So every one of those rays was marched,
packed, and overwritten — measured at **7.5 ms of a 10.2 ms solve frame** on Sponza. Removed in
[P7](decoupling-field-resolutions.md#p7--hi-res-dda-in-the-solve), byte-identical. Nothing reads
either w for display any more.

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
|  | origin k |TS|EM| free (solid)  | octahedral normal          |
                     -or- airdist  | (8 bits per axis)
```

| bits | meaning |
|---|---|
| 0–15 | octahedral normal, 8 b/axis. `BgiPackSurfaceNormal` / `BgiSurfaceNormal` |
| 16–23 | **solid** → free (held openness / static AO until AO was removed); **air** → city-block distance to nearest solid, capped at `BGI_MAX_AIR_DIST` = 5 |
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

## Single mode: one value, no direction

`_BgiRadianceDirs = _BgiIrradianceDirs = 1`. Single is the mobile/Quest path and the default, and
almost every artifact in this file traces back to one trade: **a voxel holds one number, and a number
has no direction.** Three separate places pay for it, and they fail differently.

### 1. Air voxels: no incident direction

`BgiGatheredIrradiance` with `dirs == 1` returns the stored value and ignores `n` entirely; the
fragment's Single tap does the same. The reconstruction `sum(n_k^2 * bucket_k)` degenerates to a
constant, so **the only directional information left is where the tap samples, not what it finds
there** — direction is encoded purely as position, by the `normal / max|normal|` offset.

What that costs:

- A directional bounce is spherically averaged. A sunlit floor patch throwing light onto one wall
  stores, in the air between them, the mean over the whole sphere — the wall facing the patch and the
  wall facing away read the same number whenever their taps land in the same cell.
- Two coincident surfaces with opposite normals differ **only** by their offsets. Where both offsets
  land in one voxel (a corner, or any surface within a cell of another), they are identical.
- Contact darkening has to come from the geometry of *where the tap lands*, never from the field
  itself. That is why tap placement in `BgiSampleFieldTexture` carries so much weight, and why so many
  of its failures read as lighting bugs rather than sampling bugs.

Cube's six cosine buckets are the direct fix, at 6x the irradiance storage and three taps.

### 2. Solid voxels: no outgoing face

Radiance is 1 slot, so `BgiRadianceSlot` collapses every face onto slot 0 and a solid voxel emits the
same radiance in all directions. For geometry two or more voxels thick this is invisible — the two
faces are different voxels. For anything **thinner than a voxel** — a curtain, a railing, a banner, a
foliage card, Sponza's 0.22 m curtains against a 0.68 m X-voxel — both faces land in one cell and the
lit face's bounce is what the shadowed face emits. `TWOSIDED` exists to describe exactly that cell,
and it is gated on `_BgiRadianceDirs > 1` everywhere: **in Single there is no second slot to serve, so
the flag is inert.** P4's `TWOSIDED` fix was byte-identical in Single and +3.7% in Cube for that
reason.

### 3. Solid shell voxels: one value, one side served

The fragment's tap is a raw trilinear read with no occupancy gate, so a solid cell holding black
bleeds darkness onto any surface whose footprint touches it. `CSBlur` prevents that by **dilating**
the first solid shell from its air neighbours. With one value per cell it can reconstruct only one
side, so it gates that neighbour average to the front hemisphere of the cell's stored normal.

Two facts make this the sharpest edge in the system:

- **The tap routinely lands on a solid cell, and that is the normal case.** Probed at a Playground
  corner pose, 3 of 6 pixels on *flat* ceilings and walls have their post-offset tap point inside a
  cell the lighting grid calls solid — occupancy is "any geometry in this cell", so a cell that is 75%
  air is still flagged. Those pixels look correct only because the dilation gave that cell the room's
  value. The dilation is not a corner case; it is the common path. Note the two resolutions disagree
  here on purpose: none of those six tap points is inside *hi-res* solid, which is what makes the
  hi-res test a usable classifier (below) where the lighting-grid test is not.
- **A dilated value is written for one reader.** Any other reader that catches the cell in its
  footprint gets someone else's answer, with nothing to tell it so.

### Corners: several air pockets, one slot

A concave corner is where a cell must serve more than one *disconnected* body of air. Measured on
Playground `Room_dark`, fine field, cell `(16,22,8)` at the wall/ceiling junction (`(23,22,8)` and
`(24,22,8)` are the same cell class one step over). Flood-filling its 4x4x4 hi-res sub-cells gives
**three disjoint air pockets**:

| pocket | sub-cells | air lighting-cell neighbours | what it is |
|---|---|---|---|
| 0 | 4 | 3 | outside, behind the wall |
| 1 | 28 | 9 | above the roof, outdoors |
| 2 | **8** | **3** | **the room** |

The stored normal is `(0, +1.000, -0.004)` — straight up through the roof. That is not a bug in the
gradient: `CSBuildSurface` takes the occupancy gradient over the *lighting* grid, and the open sky
subtends more of this neighbourhood than the room does, so the gradient correctly reports the biggest
air volume. It is simply not the quantity the dilation needs. Consequence: 12 of the cell's 15 air
neighbours are outdoors and pass the hemisphere gate, while the 3 room-side ones are diagonals at
`dot` -0.58..-0.71 and are all rejected. **The cell fills at 0.057 against the room's 0.267.**

On screen that one cell is the whole artifact. At the seam `GiSolidWeight` reads ~1.0 — the tap takes
essentially its entire footprint from solid cells — so there is no bright neighbour to dilute the bad
one, and the junction reads ~11% dark as a hard line. The same defect one cell away is invisible for a
purely mechanical reason:

| | up-left junction `(24,22,9)` | corner `(24,22,8)` |
|---|---|---|
| the cell's own value | 0.0123 | 0.0106 |
| **total dark weight in the tap footprint** | 0.07 - 0.14 | **0.71 - 0.88** |
| the footprint's dominant cell | `(23,22,9)` = 0.383, bright, w ~ 0.78 | `(23,22,8)` = 0.0097, also dark, w ~ 0.63 |
| tap result vs air-only reference | 0.356 vs 0.381 -> 7% | 0.115 vs 0.381 -> **70%** |

Equally dark cells, different neighbourhoods. At `y=22` the row `z=8` is dark for every `x` from 5 to
26 — the full junction line — while at `z=9` only `x` in {5,6,7,24,25,26} are, just the ends. A lone
dark cell is diluted by trilinear weighting; a *run* of them is not.

**Why no local rule fixes it.** Pockets 0 and 2 are geometric mirror images across a wall one hi-res
cell thick: same size class, same enclosure, same neighbour count, opposite sides. One is the room,
one is outdoors. Size, enclosure, neighbour count, hi-res air centroid and occupancy gradient all fail
to separate them. **Which pocket is "inside" is a global property of the scene, not a local one.**
Recorded so it is not rediscovered — every row below was built and measured, corner cell value,
target ~0.267:

| write-side attempt | result |
|---|---|
| shipped: hemisphere on the stored normal | 0.057 |
| remove the gate entirely | 0.086 — and the correctly-served ceiling shell collapses 0.269 -> 0.139, scene mean 79.0 -> 71.0 |
| hi-res *contact* test (is the shared sub-face open) | 0.086 — the roof is one hi-res cell thick, so the sub-layer shared with the sky is still air |
| contact **and** a hemisphere on the cell's own hi-res air centroid | 0.057 — that centroid is `(0, +0.075, +0.050)`, i.e. it points at the sky too |

| read-side attempt (pixel value, current 0.115, target ~0.38) | result |
|---|---|
| re-offset along the landed cell's stored normal | **0.0102** — 11x worse; that normal points into the roof |
| re-offset along the geometric normal again | 0.128 |
| re-offset along the hi-res air gradient, radius 1 | 0.076 — the gradient reads `(0, +0.71, +0.71)`, up and out |
| re-offset along the hi-res air gradient, radius 2 | 0.233 — best of the five, still 39% short |
| half-step of the radius-1 gradient | 0.160 |

Two by-products of that sweep are worth keeping. **"Is the tap point hi-res-solid" is a clean
per-pixel classifier** for "this pixel's tap is buried in geometry": it fired on 0 of 6 flat probes,
leaving them byte-identical, and 5 of 5 junction probes. That is the right place to hang an expensive
fallback — though at a corner-heavy pose it still fires on 22.5% of surface pixels, so it does not
make one free. And **the information is not missing, only mislabelled**: dropping just the mis-served
cells from the footprint and renormalising the remaining trilinear weights recovers
**0.369 / 0.375 / 0.359** at three seam pixels that currently read 0.115 / 0.156 / 0.067.

Population, fine field: **78 of 5,380** shell cells have the hemisphere gate selecting a side less
than half as bright as the ungated mean (worst case 0.057x); 35 of 5,652 in the coarse field. Small,
but they line junctions, which is where the eye goes.

### Possible solutions

None of these are implemented.

| | what it does | cost | status |
|---|---|---|---|
| **Accept** | ~11% dip confined to junction lines | none | current behaviour |
| **`giResolution` 32 -> 64** | halves the corner cell, so the seam narrows and the deficit shrinks | solve is ~resolution^3 | does not remove it. 64 is the hard ceiling on this GPU — 128 TDRs the device |
| **Global air-region ids, write side** | flood-fill the air at bake, label regions, and let `CSBlur` accept only neighbours in an *interior* region. "Touches the field boundary" is the criterion — topological, not a size heuristic | **no new buffers.** The gate only asks about lighting-cell *neighbours*, so 1 bit per air cell at 32^3 = 4 KB/field suffices, and `_Surface` bits 0-15 and 24-31 are dead on air voxels. Bake-time fill over 32,768 cells; recompute at load and there is no asset version bump | measured to separate cleanly on `Room_dark`: 11 regions, 19,361 cells touching the boundary against 4,140 enclosed. **Unverified where the interior is not voxel-sealed** — a doorway or window merges the room into the exterior region and the rule degrades to today's behaviour. Sponza's open atrium is the case to check first |
| **Region-matched taps, read side** | store the region each shell cell was dilated *for*; the fragment keeps only footprint cells matching its own region | hi-res ids at 128^3 x 8 bits = 256 KB/field, plus 8 point taps replacing one hardware trilinear tap. The earlier hi-res-tap prototype measured ~5x frame cost unoptimised | exact — it never has to *decide* which pocket is the room, only *record* which one was served. Would also close the in-plane leak between adjacent rooms |
| **Cube mode** | six buckets per cell, each filled from the air on its own side; fixes 1 and 3 structurally | 6x irradiance storage, three taps | already supported. Not the mobile path, and a corner cell's room-facing bucket can still have no air on its own side |
| **`_thickenWalls`** | grows walls inward so sub-voxel geometry stops sharing a cell | raster-time | addresses 2, not corners. Documented as a sub-voxel leak control; unthickened is the better default since the per-voxel normal rule landed |

Related: [P9](decoupling-field-resolutions.md#p9--contaminated-axis-snap-last-optional) is the
read-side snap that was built and rejected for blockiness, and
[Appendix B](decoupling-field-resolutions.md#appendix-b--ruled-out) holds the normal re-alignment that
was measured and rejected.

## Bake

`BufferGiUpdater.RunDerivePasses`, once per voxelize or bake-asset load. **Order is load-bearing.**

| # | kernel | writes | notes |
|---|---|---|---|
| 0 | `CSInjectBakedLights` | `_MaterialWrite` | stamps `VoxelLights` as emissive voxels; C# resolves world→cell and merges. Batched at 16. |
| 1 | `CSBuildOccupancy` | `_Occupancy` | one thread per **word** (32 voxels); dispatch `VoxelCount/32/64` |
| 1b | `CSBuildTraversalMip` | `_OccupancyTraversal` | OR-downsample of `_OccupancyHi` onto the lighting grid. Needs no other derive product, so it sits beside 1. C# zeroes first; the kernel only ORs. |
| 2 | `CSBuildNormalOccupancy` | `_OccupancyThick` | real solids + the cell behind each surfaced voxel along its triangle normal. After 1 (needs the finished bitfield), before 3 (which overwrites those normal bits). C# zeroes first; the kernel only ORs. |
| 3 | `CSBuildSurface` | `_Surface` | solid → normal, flags, sun-ray origin; air → air-distance seeded at the cap |
| 4 | `CSBuildAirDistance` **×5** | `_Surface` 16–23 | min-relaxation, one voxel per pass. `AirDistancePasses` must equal `BGI_MAX_AIR_DIST`. Safe in place. |

`CSBuildSurface`'s normal:

1. Occupancy gradient over **`_OccupancyThick`**, not `_Occupancy` — a hollow one-voxel shell has air
   on the room side *and* inside, so plain occupancy cancels or resolves along the wall plane. Backing
   the wall recovers the true bisector at inside corners.
2. Voxelizer triangle normal only where even that cancels.
3. **The gradient's sign is kept.** An earlier revision flipped it to agree with the triangle normal;
   that measurement was circular (the triangle normal is last-write-wins and was seen flipping between
   bakes). Do not reintroduce.

`CSBuildSurface`'s `TWOSIDED`, since [P4](decoupling-field-resolutions.md#p4--derive-_surface-from-hi-res-occupancy):

- **All three axes, from occupancy alone.** A cell is two-sided if *some* axis has air on both coarse
  sides. It used to test only the stored normal's dominant axis, which both missed cells (232 of
  5,693 in Sponza, 37 of 4,551 in Playground) and was circular — the flag decides which face
  `CSInject` serves, so deriving it from the normal let a wrong normal justify itself.
- **The hi-res slab extent breaks ties.** Where more than one axis has air on both sides (a railing, a
  free-standing post) the coarse grid cannot say which way the slab faces; `BgiHiSlabExtent` measures
  the real thickness per axis on the occupancy grid and the thinnest wins.
- Measured after: misses **0**, false positives **0**, both fields. Byte-identical in Single (the flag
  is gated on `_BgiRadianceDirs > 1` everywhere); **+3.7% mean luminance in Cube**, concentrated on
  the floor slab — the newly-flagged cells now serve a real lit back face instead of handing arriving
  rays the black ambient floor.

The **normal** is deliberately not re-aligned onto that thin axis. See
[Appendix B](decoupling-field-resolutions.md#appendix-b--ruled-out) — it was built and measured, and
104 of 12,644 cells moved the whole image by 4.9%.

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
- `direct` marches a shadow ray from `BgiShadowOriginStep`, at whatever level `_BgiSolveMarchLevel`
  selects; sun visibility goes in the radiance `w`. Still supersampled (`_BgiSunShadowSamples`) —
  unlike the air-probe ray below, this one is alive, and a single centre ray measured 4.90% brighter.
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
- At a hi-res march level the hit can land in a lighting cell the 32³ raster never marked solid
  (1,130 of Sponza's 13,069). Those carry no `_Surface` and no `_Radiance`, so they read
  `_AmbientFloor`: the ray IS blocked — that is the new information — but the occluder's outgoing
  radiance is not something this grid holds.
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
- *Solid sun-vis:* gone from this pass entirely. `CSBlur` used to publish each solid cell's own
  `_Radiance[...].w`, per slab in Cube, because one lighting-grid texel had to serve both sides of a
  sub-voxel wall (~24-26% of shell cells were contested). `CSSunVisibility` owns it now.
- *Mirror:* the irradiance rgb, fused into the same pass. Sun visibility is written elsewhere now
  (`CSSunVisibility`), on the sun's schedule rather than the solve's.

**`CSSunVisibility`** — NOT per frame. One supersampled shadow ray per texel of the `ShadowGrid³`
sun-visibility volume, marched against the hi-res occupancy (`MarchOccupancyHiFrom`), dispatched when
`HasSunChanged()` fires or the geometry is re-baked. A re-evaluation, not an upsample: sun visibility
depends only on occupancy and sun direction, both known at full resolution.

**Chunked over Z slices**, `SunVisTexelsPerDispatch` texels at a time. The whole volume in one
dispatch TDR'd the device at 256³ (16.7M texels x 4 rays x up to 768 steps x 2 fields); a sun move now
sweeps in over several frames instead — ~64 frames at 128³, visibly, as a plane travelling along Z.

Two rules make the chunking safe for a sun that keeps moving:

- **An in-flight sweep is never restarted.** `HasSunChanged()` fires every frame under a sun rotator,
  and re-arming the cursor each time stalled the sweep at slice 2 of 128 — the shadow froze. A sun
  move during a sweep sets `_sunVisRestartQueued` and the next sweep begins when this one lands.
- **The sun direction is latched at sweep start** (`_sunVisDir`), not read per chunk, so the whole
  volume is marched against one direction. Otherwise the front and back of the sweep disagree and the
  shadow shears.

A wider "skip texels far from geometry" gate was tried and measured SLOWER twice — in an interior
almost every texel is near a wall — so only a sealed-block skip survives. Deliberate deviation from
optimization-guideline 5.3: its lifecycle is the sun, not the solve frame.

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
- *In-plane snap* (`BGI_TAP_SNAP_INPLANE`, **default off**, composes with all three above): one point
  `Load` of `_BgiNeighbourMask`, then each **in-plane** coordinate whose trilinear pair straddles a
  one-voxel wall is snapped to a cell centre. The tap's own axis is exempt — snapping it would undo
  the normal-axis guarantee the other filters buy. Snap only when **exactly one** cell of the pair is
  solid (both solid has nowhere clean to go, and landing on a solid cell reads black), and clamp the
  result to `[0.5, GRID-0.5]` (out of range is not clamped downstream, it is *no data*). It is
  byte-exact outside the contaminated population and costs nothing measurable, but it reads BLOCKY on
  Sponza — see
  [P9](decoupling-field-resolutions.md#p9--contaminated-axis-snap-last-optional) for why a per-cell
  binary gate cannot be continuous *between* cells.

**`BgiSampleShadowTexture`** — one trilinear tap of `_BgiSunVisTex`, offset `_BgiShadowNormalOffset`
**shadow texels** along the normal, scaled so the offset clears a whole texel on the DOMINANT axis
(`normal / max|normal|`, as the Fast GI tap does). **1.0 is the floor of the range, not a taste
setting**: the tap is trilinear, so at 0.5 its footprint still reaches into the solid layer, and solid
texels hold an arbitrary partial coverage rather than a neutral value - that is shadow acne, and it
reads as mottling. See
[the bias fix](decoupling-field-resolutions.md#the-normal-offset-bias-was-cut-4x-by-this-phase-fixed-2026-08-24). Returns 1 (lit) outside the
field. Same single tap in both modes — the Cube slab branch went with the slabs. `_BgiShadowSharpness`
re-centres on 0.5 and steepens, which works because `CSSunVisibility` always supersamples and so
stores a real coverage fraction.

The over-reach that made a floor beside a tall occluder read shadowed scales with the texel, so at the
shadow resolution the same offset is centimetres rather than most of a metre.

### What `_BgiShadowSharpness` is actually approximating [measured 2026-08-28]

**Coverage IS a signed distance to the shadow boundary — clamped to ±½ texel.** Near a locally planar
boundary `c - 0.5` is exactly where the edge falls inside the texel, which is why supersampling helps
at all and why the sharpen works. But it saturates the moment the boundary leaves the texel: outside
that half-texel the field is 0 or 1 and carries no direction. **That clamp is what pins the
reconstructed edge to the texel lattice** — trilinear over a saturated field can only ramp between
adjacent centres, so the edge is smooth but its position snaps to the grid.

`_BgiShadowSharpness` is a hand-tuned constant standing in for `fwidth(d)`, the screen-space rate of
change the GPU can compute exactly. That is why one value cannot be right everywhere: a constant
slope over-sharpens close up and under-sharpens far away. Measured against the per-pixel `Sdf`
reference at a Sponza pose, pixels off by >15/255:

| | sharpness 1.0 | sharpness 1.5 |
|---|---|---|
| 1 sample | 7.15% | 7.88% |
| 4 samples | **5.76%** | 6.42% |

So the shipped 1.5 measurably moves *away* from the reference. It buys a crisper-looking edge at the
cost of accuracy - a legitimate aesthetic choice, but a trade rather than free.

**What supersampling buys, separately.** It only ever touches texels ON a boundary. At the 128³ shadow
grid on Sponza, the fraction of texels holding a value strictly between 0 and 1: **1 sample 0.000%,
4 samples 1.91%, 16 samples 2.92%** - and the field mean is unchanged to four decimals (0.1881 /
0.1884 / 0.1883). Cost is linear and paid only when the sun moves: 330 / 621 / 1497 ms to re-march
the volume. At 1 sample the field is strictly binary, so `_BgiShadowSharpness` has nothing to sharpen
and degenerates to a threshold on a staircase.

**Un-clamping it was built, measured and removed (2026-08-28).** A narrow-band distance relaxation
(seed the boundary from coverage, everything else at the cap, then N passes of a 6-neighbour min -
the same shape as `CSBuildAirDistance`) extends the band past ±½ texel, after which `fwidth` has a
linear gradient to measure. It worked and it was cheap: **~11 ms** on top of a ~1078 ms march, and
**+0.4%** fragment cost (+0.07 ms at 2560x1440, two paired rounds agreeing to 4 µs). Cost was never
the objection. It was removed because it showed **no demonstrated visual benefit**, and because
sharpening is what EXPOSES reconstruction error - trilinear over texel centres is piecewise-linear,
so an edge sharpened to one pixel shows the lattice as facets, and a distance field rounds convex
corners on top of that.

Two traps worth carrying if it is ever revisited:

- **Seed only the boundary band.** Seeding every texel with `c - 0.5` is inert: a min-relaxation can
  only pull values down, so if the whole volume already holds |d| <= 0.5 the band never grows and the
  field comes back as the raw seed. Near-boundary texels get the true value, everything else the cap.
- **`fwidth` under a non-uniform branch is undefined.** `BgiSampleSunShadow` picks the mode per pixel
  via `insideFine`, so at the fine-box boundary a quad can straddle two paths. The tap has to be
  hoisted out of the branch.

None of this applies to a per-pixel raymarch, which has no lattice to reconstruct against - see
`ShadowMode.Sdf`, already a per-pixel path, and the sixth-mode seam in `BgiSampleSunShadow`.

**`BgiSampleSunShadow`** — the sole authority for the main-light shadow, fully resolved with no
fall-through: `Off` = genuinely no sun shadow; `Baked` = the sun-vis tap; `Sdf` / `OcclusionField` /
`Bitmask` delegate to their baked sources, `saturate`d so a NaN from a mis-bound source reads as
shadowed rather than poisoning the fragment to black.

**No buffer taps.** Outside `BGI_DEBUG_VIEWS` the fragment declares no `StructuredBuffer` at all —
everything it reads arrives through the mirror `Texture3D`. That is what removing AO bought: the
face-plane openness read was the sole SSBO consumer here (the Adreno cost the mirror texture exists to
avoid), and its removal also shrinks the WebGPU pipeline-layout surface. `_Occupancy` is still
declared, and still bound, under the debug keyword alone, for `BgiTapSolidWeight`.

## Ambient occlusion: removed

There is none. A static per-solid-voxel **openness** scalar used to live in `_Surface` bits 16–23 and
multiply the indirect bounce; it was deleted on 2026-08-23 (P1 of the
[decoupling plan](decoupling-field-resolutions.md#p1--remove-ambient-occlusion)). Recorded here so the
reasoning is not rediscovered:

1. **Wrong spatial scale.** `BGI_AO_RADIUS` 2 at `giResolution` 32 is ±1.36 m on Sponza's X axis. That
   is not contact occlusion — it is a low-frequency darkening blob, varying at 0.68 m granularity, so
   it read as blocky on anything with detail finer than a voxel.
2. **Sub-voxel geometry got none.** No solid tap on the face plane returned `ao = 1`, so thin detail
   was unoccluded while coarser geometry beside it was heavily occluded — inconsistent in exactly the
   places AO is supposed to help.
3. **It double-counted.** The gather already integrates an omnidirectional probe containing real
   occlusion; AO multiplied on top of it. Raising occupancy resolution attacks the same problem at the
   source.
4. **It was the fragment's only `StructuredBuffer` consumer** (see above).
5. **It freed 8 bits** of `_Surface` on solid voxels.

Removal was verified as a null: with `_BgiAoStrength` already 0, the fixed-pose captures are
byte-identical in both Single and Cube.

## Globals and keywords

| global | meaning |
|---|---|
| `_BgiGrid`, `_BgiGridLog2`, `_BgiCount` | lighting-grid resolution and derived index constants |
| `_BgiOccGrid(Log2)`, `_OccFieldWordOffset` | occupancy-grid resolution and its per-field slice |
| `_BgiShadowGrid(Log2)` | shadow-texture resolution. **Must be published as a fragment global** — unbound it reads 0, the tap offset divides by it, and every pixel silently reads LIT |
| `_BgiGridOrigin/Size/VoxelSize`, `_BgiCoarseOrigin/VoxelSize` | field bounds |
| `_FieldOffset` | current field's slice (per dispatch) |
| `_BgiRadianceDirs`, `_BgiIrradianceDirs` | the two strides |
| `_BgiIntensity` | sun `bounceIntensity`; scales the bounce only |
| `_BgiShadowModeFine/Coarse`, `_BgiShadowSharpness`, `_BgiShadowNormalOffset` | read-side tuning |
| `_BgiDebugView` | analysis view selector |
| `_BgiSolveMarchLevel` | which occupancy level the SOLVE rays march: 0 coarse, 1 flat hi-res (default), 2 two-level. Compute-side only |
| `_BgiIrradianceTex(Coarse)`, `_BgiSunVisTex(Coarse)`, `_BgiNeighbourMask(Coarse)` | the fragment’s six texture bindings (plus `_Occupancy`, declared only under `BGI_DEBUG_VIEWS`). All six are bound whenever BufferGI is active, keyword or not — a declared-but-unbound global fails WebGPU pipeline creation |

Keywords: `GI_VOXEL_BUFFER` (selects this path), `BGI_TAP_AXIS_SNAPPED` (Single tap filter),
`BGI_TAP_SNAP_INPLANE` (P9 in-plane snap, composes with the filter and with Cube, default OFF),
`BGI_DEBUG_VIEWS` (analysis views + `BgiSolidWeightAt`).

`DebugView`: `Off 0`, `GiOnly 1`, `SunVisibility 2`, `DirectOnly 4`, `GiSolidWeight 5` — the
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
