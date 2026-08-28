# Optimization guidelines

Defaults for changing Buffer GI without making it slower. Each entry says what to do, why, and the
evidence where there is any. Measurements are from the dev machine (AMD Radeon iGPU, D3D12) unless
stated.

These are **guidelines, not laws** — they encode what has worked here and what has already been paid
for. Deviate when you have a measurement that says otherwise; that is how most of them got written.
The handful tagged **[hard]** are different: those are correctness or platform constraints, and
ignoring one produces a silent bug or a failed build rather than a slower frame.

**Numbering is `category.item`** — `2.5` is the fifth Compute guideline — so cite them in review and
commit messages as e.g. "violates 4.2". **Add new items at the END of their category**, never in the
middle: that way an addition changes no existing number, and references in other documents and in git
history stay valid. Categories themselves should not be renumbered.

Reference: [buffer-gi-architecture.md](buffer-gi-architecture.md). Verification:
[verifying-changes.md](verifying-changes.md).

## 1. Placement

**1.1. Move work leftward: bake → solve → fragment.**
Bake runs once. The solve runs ~50 frames and then idles (`_continuousGi` off = a static settled
scene costs zero GI compute). The fragment runs forever.
*Evidence: read-side visibility marching cost 4.85 → 26.5 ms at 1080p (+21.7 ms) every frame; the
same geometric work in the solve is ~65K voxels × 10 rays, amortised over ~50 frames, then gone.*

**1.2. Avoid per-pixel raymarching in the fragment.**
The fragment budget is one hardware tap plus a few ALU. Anything that marches belongs in the solve.

**1.3. Prefer an exact answer computed at bake and stored in spare bits over a cheap approximation
recomputed per frame.**
`_Surface` exists to be that store. The sun-ray origin moved from `CSInject` (every solve frame) to
`CSBuildSurface` (once) for exactly this reason.

**1.4. Check new per-frame work against the idle gate.**
If it has to run when the solve is idle, it is fragment-tier cost — budget it as such.

## 2. Compute

**2.1. Cost a branch by the fraction of WAVES that take it, not threads.**
A wave executes a divergent path in lockstep. Spatially clustered conditions (surface shells,
corners) have far higher wave coverage than thread coverage.
*Evidence: the sun-ray origin scan was taken by 6.1% of threads but 37% of working waves.*

**2.2. Order early-outs cheapest-first and most-clustered-first.**
Coherent conditions retire whole waves. `CSGather`'s far-air skip fires before anything else;
`CSInject` tests the occupancy bit before touching `_Material`.

**2.3. Test the occupancy bit before reading any 4-byte buffer.**
1 bit answers "solid?"; `_Material` and `_Surface` are cold data. Never read a word to learn a bit.

**2.4. Do not `[unroll]` memory-bound loops.**
Unroll when the body is ALU and the trip count is small and uniform (the 6-bucket loops). `CSBlur`'s
26-neighbour walk is deliberately rolled — 27 unrolled iterations bloat the kernel for a branch only
the solid minority takes.

**2.5. Keep `numthreads(64,1,1)` and linear `id.x` indexing.**
A wave then covers 64 consecutive X cells, so rays start spatially coherent even though their
directions diverge.

**2.6. Prefer own-index reads. Treat a neighbour stencil as a cost you are choosing to pay.**
A kernel that only touches `[_FieldOffset + id.x]` is trivially coalesced: consecutive lanes read
consecutive addresses and the wave consumes whole cache lines. That is the cheapest shape there is,
and it is worth reaching for first.
But it is a preference, not a constraint — several kernels here *must* read neighbours
(`CSBlur`'s 6- and 26-neighbour walks, `CSBuildAirDistance`'s min-relaxation, `CSBuildSurface`'s
gradient), and the DDA reads an entire ray of cells with every lane diverging. Neighbour access is
not the thing to avoid; paying for it unnecessarily is.

**2.7. The cost of a stencil is the re-read multiplier, not the offset.**
A fixed `±1` offset is still a coalesced stream — just a shifted one (`±X` is adjacent, `±Y` strides
by `grid`, `±Z` by `grid²`). What actually costs is that a 26-neighbour stencil reads every voxel 27
times across the grid, so the question is whether the cache absorbs it. **Keep the stencil's working
set cache-resident** — that is a large part of why occupancy is a bitfield: 4 KB per field at grid 32
means the whole thing is L1-resident and the re-reads are nearly free. The same stencil over a 128³
field is 256 KB and behaves very differently (see 4.1-4.2).
*Groupshared tiling is the textbook fix, but it needs a 3D thread-group shape to have a halo worth
loading; at `numthreads(64,1,1)` a group is 2 rows of X, so only the `±X` neighbours are in-group.
That makes LDS tiling a bigger change than it looks.*

**2.8. [hard] If a kernel reads neighbours, write to a different buffer — or prove the operation is
order-independent.**
Otherwise a thread can observe a neighbour that has already been updated this dispatch, and the
result depends on scheduling. The three patterns in use, all deliberate:

| kernel | pattern | why it is safe |
|---|---|---|
| `CSBlur` | reads `_Irradiance`, writes `_IrradianceBlur` + texture | separate buffers — no thread can observe a partial write |
| `CSBuildAirDistance` | reads and writes `_Surface` **in place** | min-relaxation never underestimates: a neighbour's stored value is always ≥ its true distance |
| `CSBuildNormalOccupancy` | **scatter-writes** a neighbour cell's bit | `InterlockedOr` into a pre-zeroed buffer — order-independent because the ORs only ever set bits |

Scatter-writes need atomics and a pre-cleared target. Prefer a gather formulation if one exists.

## 3. Memory

**3.1. Decide which loop touches the data before choosing its width.**
Inner-loop data wants to be bits and contiguous. Per-hit data can afford 32 bits and random access.

**3.2. Avoid mixing hot and cold data in one buffer.**
`_Occupancy` was split out of `_Material` because the DDA paid 4 bytes for a 1-bit question.
`_Surface` was split the other way — a cold word touched once per ray-hit, never inside the DDA.

**3.3. Check `_Surface`'s free bits before adding a buffer.**
It already carries a normal, an AO/air-distance byte, two flags and a 5-bit sun-ray origin. Bit 31 is
the only one left, but a re-layout is still cheaper than a new allocation.

**3.4. [hard] Clear every new buffer at allocation.**
A fresh `ComputeBuffer` holds garbage, not zeros. An unwritten buffer reads as uncorrelated noise and
looks exactly like an index-mapping bug.

**3.5. Budget in bytes per voxel, times `FieldCount × Grid³`.**

| | Single | Cube |
|---|---|---|
| `_Material` / `_Surface` / occupancy×2 | 8.25 B | 8.25 B |
| `_Radiance` | 8 B | 16 B |
| `_Irradiance` + `_IrradianceBlur` | 16 B | 96 B |
| mirror texture | 8 B | 48 B |
| **total** | **~40 B** | **~168 B** |

*Grid 32 = 2.52 MB measured. Grid 128 would be ~161 MB Single, ~673 MB Cube.*

## 4. Layout and cache

**4.1. Work out cache-line coverage before raising any resolution.**
`BgiIndex` is linear with X fastest.

| grid | one 64 B line covers | one Z-slice | verdict |
|---|---|---|---|
| 32 | 512 cells = 16 full X-rows | 128 B | whole 4 KB field is L1-resident; layout irrelevant |
| 128 | 512 cells = 4 full X-rows | **2 KB** | X-marching is fast, Z-marching touches a new line every step |

**4.2. Block any field that exceeds L1.**
A 4×4×4 block is 64 cells = 2 uints = 8 bytes, so one cache line holds 8 neighbouring blocks and
locality stops depending on march direction. Index math stays shift/mask at power-of-two block sizes.

**4.3. Hierarchy is not what thrashes cache — unblocked layout is.**
The "hierarchies destroy cache" objection is about sparse octrees: log(N) dependent pointer chases
into scattered nodes. A two-level dense mip with arithmetic indexing has none of those properties and
strictly improves on the flat fine march it replaces.

**4.4. [hard] A coarse level used for traversal must be conservative.**
OR-downsample the fine field; do not reuse an independently rasterized coarse field.
*Evidence: OR-downsample(128³) vs the existing 32³ raster differs by 403 cells the 32³ raster misses.
A hierarchical DDA on that coarse level would skip real occluders, silently.*

**4.5. [hard] Never majority-downsample occupancy. Pick the operator per quantity.**
Voxelized geometry is thin shells, not filled volumes. In a 4×4×4 block an axis-aligned wall one fine
cell thick is **16 of 64 cells (25%)**, a diagonal ~35%, and a two-cell wall exactly 50% — so a >50%
rule deletes every wall in the scene and light passes straight through the result. Use **OR** for
occupancy (over-occluding is the safe error; deleting is not), **majority over the solid fine cells**
for albedo, and **max** for emission — a sub-voxel lamp in 3 of 64 cells must not be voted out of
existence.
*Evidence: solid fractions 5.1% / 5.9% hi-res against 17.3% / 21.3% low-res — the ~1/N scaling of a
shell.*
*If faithfulness is needed rather than a bound, store a coverage fraction instead of a bit; that is a
different data structure, not a different downsample rule.*

## 5. Textures

**5.1. Fragment reads go through `Texture3D`, not `StructuredBuffer`.**
Textures are hardware-swizzled (3D locality regardless of sample direction), use a separate texture
cache, and get filtering and clamping free in the sampler. On Adreno a sampler tap is nearly free
while SSBO taps dominate the fragment.
*The mirror texture replaced a 9-tap SSBO B-spline with one trilinear tap.*

**5.2. [hard] Keep `_Material`, `_Radiance` and `_Irradiance` undeclared in the fragment.**
WebGPU validates every declared global against the bound pipeline layout and fails pipeline creation
for unbound ones. D3D11/Vulkan tolerate it, so this only bites in a browser build.

**5.3. Fuse the texture write into the pass that computes the value.**
`CSBlur` writes the mirror in the same dispatch — no separate SSBO→texture copy.

**5.4. Stack directional slabs along Z in one texture, not N bindings.**
One binding, one sampler, bucket = a Z offset. Cost: every read must build the slab offset explicitly
and clamp `zLocal` to half a texel, or a raw `[0,1]` z sweeps across all slabs.

**5.5. [hard] Never read back a storage texture.**
It is write-only under WebGPU. `CSBlur`'s history lives in `_IrradianceBlur` for this reason.
Also: `AsyncGPUReadback` on a `Texture3D` returns slice 0 only — read the `ComputeBuffer` to verify.

## 6. Filtering

**6.1. Hardware trilinear is free ALU, not a free footprint.**
The kernel reaches ±1 texel per axis — at grid 32 that is ±0.68 m in Sponza, wider than a one-voxel
wall.
*Evidence: point sampling reads flat (13.3 → 19.5 → 19.5) where trilinear ramps 5× (14.3 → 62.6 →
93.8). The stored values are correct; the footprint is the artifact.*

**6.2. Put the discontinuity in the FIELD, at the resolution you want the steps to land on. Never in
the fragment's filter.**
A binary per-corner gate plus renormalisation *is* point sampling wherever few corners survive.
*Evidence: +35% lit-side blockiness (1.069 → 1.441); the softness sweep is perfectly monotonic — leak
buys smoothness 1:1, with no usable window; the renormalisation floor made it worse (→ 2.082).*

**6.3. Raising the resolution of the data a read-side gate consults does not make its steps finer.**
Steps land on the lattice where the *decision* was made.
*Evidence: occupancy grids 64 / 128 / 256 all showed elevated blockiness with no monotonic trend.*

**6.4. Snap one axis to a texel centre; keep the other two continuous.**
That axis degenerates to nearest while the rest stay smooth — bilinear in-plane, nearest along the
normal — at no extra cost. Note this defends the normal axis only; the in-plane leak is unaffected.

**6.5. Skip near-zero-weight taps and renormalise.**
`BGI_TAP_MIN_WEIGHT` = 0.01 keeps an axis-aligned face on one tap. The error is bounded by the cutoff
itself, not by the field's dynamic range.

**6.6. Store a coverage fraction, not a bit, if anything downstream will sharpen it.**
`_BgiShadowSharpness` re-centres on 0.5 and steepens — one MAD, reconstructs an edge finer than the
voxel. Against a 0/1 field it only hardens the staircase.

## 7. Precision

**7.1. Store fp16, accumulate fp32.**
A single sample can sit near the ~60000 fp16 ceiling; summing several overflows. The mean lands back
in range.

**7.2. [hard] Pack 4 halves into a `uint2`; do not use `StructuredBuffer<half>`.**
That type is patchy on WebGPU/GLES. Use `BgiUnpackRgbH` to stay in fp16 registers;
`BgiUnpackRgb` round-trips through fp32.

**7.3. Leave uniforms as `float` and narrow at the point of use.**
A uniform load costs the same either way; the fp16 win is in the ALU.

## 8. Variants

**8.1. Use a keyword when register pressure differs between paths; a uniform when every path loads the
value anyway.**
A fragment kernel's register allocation covers every path it contains, so a never-taken expensive
branch still taxes occupancy. `BGI_TAP_AXIS_SNAPPED` is a keyword; the two directional strides are
uniforms.

**8.2. Guard against redundant `EnableKeyword` / `DisableKeyword`.**
A keyword change is not free on the CPU. Targets are Quest 3 and WebGL, so variant count is a
shipping constraint.

## 9. Measuring

**9.1. Use `BufferGiSolveProfiler`, and treat `CSBlur` as the march-free control.**
`(inject + gather) − blur` is the traversal budget — the number any DDA change moves. Vulkan reports
no GPU timings at all; D3D12 does.

**9.2. Do not use `UnityStats.renderTime` for fragment changes.**
Quantised to ~1 ms — it cannot resolve a GI tap change at 720p. Render into a 2560×1440
`RenderTexture` in a ~120-frame loop, bracketed by a 1-pixel `ReadPixels` to force GPU sync.

**9.3. [hard] Extrapolate resolution cost from 32 or 64. Never "just try" 128.**
Every per-voxel kernel is grid³, the DDA bound is `BGI_GRID * 3`, and `CSBlur` has no far-air skip.
*Evidence: 128 TDR'd the GPU (`DXGI_ERROR_DEVICE_HUNG`) with 585 MB used of a 17 GB budget — a
watchdog timeout, not an allocation failure.*

**9.4. When A/B-ing inside one frame, set `Shader.SetGlobalFloat` / `EnableKeyword` directly.**
`BufferGiUpdater` properties publish in `SetGlobals` during `Update`, so setting one and rendering in
the same `eval` silently uses the old state for every shot.

**9.5. [hard] Hang new bake work off `Voxelize()`, not `VoxelizeScene()`.**
`TryLoadBakeAssets()` returns first on the disk-bake path, so a pass added to `VoxelizeScene()` never
runs on a normal load.
*Related: `_voxelizeMaterial` is created lazily inside the two rasterize methods, so a pass that runs
earlier than those must create it itself.*

**9.6. [hard] Anything that only exists at runtime raster time is dead in player builds.**
A rejected bake means a black field, not a fallback.

## Known cost hotspots

- **`CSBlur` has no far-air skip.** It runs the full neighbourhood walk and mirror write for every
  voxel, making it the term that scales worst with grid resolution. Look there first at higher grids.
- **`BGI_MAX_RAY_STEPS` is `BGI_GRID * 3`** — a compile-time bound that must key off the *occupancy*
  grid if occupancy is ever decoupled from the GI grid.
- **The far-air gather skip** (`airDist > 4`) is what keeps `CSGather` proportional to surface area
  rather than volume. Preserve it.
