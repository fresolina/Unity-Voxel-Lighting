# Decoupling the GI field resolutions — implementation plan

**Status: plan, decisions taken 2026-08-23. Nothing implemented yet.** The evidence behind each
decision is in [Appendix A](#appendix-a--measured-evidence); approaches already tried and rejected are
in [Appendix B](#appendix-b--ruled-out).

**Goal.** `_giResolution` currently sets the resolution of four unrelated fields at once. Split them
so geometry can be fine while the solved lighting stays coarse — buying correct normals, sub-voxel
occluders and sharp baked shadows without paying 64x in rays or convergence time.

**Order of work: all decoupling first, then fixes.** Phases 1–3 are structural — a removal and two
nulls — and must land and be verified before any phase changes what the renderer produces.

Reference: [buffer-gi-architecture.md](buffer-gi-architecture.md). Cost model:
[optimization-guidelines.md](optimization-guidelines.md). Measurement:
[verifying-changes.md](verifying-changes.md).

- [Decisions taken](#decisions-taken)
- [Target architecture](#target-architecture)
- [How to work this plan](#how-to-work-this-plan)
- **Decoupling** — [P0 Baseline](#p0--baseline-and-harness) · [P1 Remove AO](#p1--remove-ambient-occlusion) · [P2 Split sun visibility](#p2--split-sun-visibility-into-its-own-texture) · [P3 Hi-res occupancy](#p3--hi-res-occupancy--configurable-resolutions)
- **Fixes** — [P4 Derive `_Surface`](#p4--derive-_surface-from-hi-res-occupancy) · [P5 Attribute the leak](#p5--attribute-the-leak-gate) · [P6 Fine shadow texture](#p6--fine-shadow-texture) · [P7 Hi-res DDA](#p7--hi-res-dda-in-the-solve) · [P8 Fine irradiance texture](#p8--fine-irradiance-texture-contingent) · [P9 Axis snap](#p9--contaminated-axis-snap-last-optional)
- [Hard constraints checklist](#hard-constraints-checklist)
- [Risk register](#risk-register)
- [Appendix A — Measured evidence](#appendix-a--measured-evidence)
- [Appendix B — Ruled out](#appendix-b--ruled-out)

## Decisions taken

| | decision |
|---|---|
| **Resolution** | **Configurable per platform**, not a constant: 64 / 128 / 256. Occupancy and shadow resolutions are independent settings. |
| **Modes** | **Single ships on mobile/Quest, Cube on PC.** Both are supported paths; memory budgets differ accordingly. |
| **Ambient occlusion** | **Removed**, to simplify. Lands first. |
| **Convergence** | **Long convergence is acceptable.** The hard requirement is that the solve actually reaches its 500-sample budget — **never cut `_maxSamples` to pay for a slower march.** |
| **Shadow path** | The fine shadow texture **replaces** the `Baked` path rather than adding a mode. |
| **Snap condition** | Binary and per-pixel: **if the trilinear kernel spans a one-voxel wall in the surface plane, snap.** Not a tuned threshold. |
| **Axis snap phase** | Optional. **Scheduled last.** |
| **Hi-res raster gating** | Deferred. Default is **always on** — a conditional bake creates a "did I bake?" failure mode. Revisit only if bake time actually hurts authoring. |

### Consequences worth noting

**Configurable resolution means one bake asset, not one per platform.** Bake occupancy at the highest
supported resolution and **OR-downsample at load** to whatever the platform runs. That is exactly the
operator established in [downsample operators](#downsample-operators), it keeps a single asset, and it
costs 2 MB per field on disk at 256^3. The alternative — baking per platform — multiplies build
variants for no benefit.

**Splitting the sun-visibility texture is free on the Single path and only risky on Cube.** Single
already stores one alpha per voxel, so there is nothing to lose; only Cube's per-slab alpha carries
face disambiguation. Since Single is the mobile path where the leak matters most, **the fine shadow
texture is most valuable exactly where it is least risky.** P6's risk is a PC/Cube risk.

**P9 and P8 attack the same problem and may be alternatives.** Both target the trilinear footprint —
P9 by not interpolating across it, P8 by making it smaller than a wall. P9 costs a mask texture; P8
costs up to 201 MB. Scheduling P9 last (as decided) means P8 may get built first; if it does, be
explicit that the cheaper option was deferred rather than rejected.

**The 4×4×4 block is fixed at 8 bytes regardless of ratio.** The tidy "one coarse cell = one 8-byte
load" only holds at 4:1 (128/32). At 2:1 a coarse cell is 8 fine cells = 1 byte; at 8:1 it is 512
cells = 64 bytes = **exactly one cache line**, which is the best of the three. All are closed-form;
only the load width changes.

## Target architecture

| field | today | target | why |
|---|---|---|---|
| `_Occupancy` | 32^3 | **configurable 64/128/256** (+ 32^3 traversal mip) | 0.125 B/voxel — geometry accuracy for zero rays |
| `_Surface` | 32^3 | **32^3, derived from hi-res** | consumers are 32^3 solve kernels; only the derivation moves |
| `_Material` | 32^3 | **32^3** | read once in `CSInject`; finer detail is averaged away before it is seen |
| `_Radiance` / `_Irradiance` / `_IrradianceBlur` | 32^3 | **32^3** | the only axis that costs rays *and* convergence time |
| sun visibility | alpha of the mirror texture | **own R16 texture, at occupancy resolution** | a scalar, re-evaluated not upsampled |
| neighbour-solidity mask | — | **own R8_UInt texture** (P9 only) | keeps the snap gate out of the fragment |
| irradiance mirror | 32^3 | 32^3 unless P5 says otherwise | 201 MB in Cube — gated on evidence |
| openness / AO | `_Surface` 16–23 | **removed** | |

**Memory at the three resolutions** (both fields, shadow R16 + mask R8):

| occupancy res | occupancy bits | shadow texture | mask | typical platform |
|---|---|---|---|---|
| 64^3 | 64 KB | 1.05 MB | 0.52 MB | Quest / WebGL |
| 128^3 | 512 KB | 8.4 MB | 4.2 MB | desktop default |
| 256^3 | 4 MB | 67 MB | 33.5 MB | high-end PC |

## How to work this plan

1. **Decoupling before fixes.** P1–P3 change no rendered output. Land and verify them first; a fix
   built on unverified plumbing cannot be attributed.
2. **Every phase states its acceptance criterion, and the decoupling ones are nulls.** A phase that
   should change nothing must be *shown* to change nothing — byte-identical captures.
3. **P5 is a gate.** P7–P8 are contingent on it. Do not start them in parallel.
4. **Keep the `[measured]` / `[proposed]` distinction** when adding to the appendices.
5. **Re-read the [hard constraints checklist](#hard-constraints-checklist) at the start of each
   phase.** Two of its items are bugs the 2026-08-22 PoC actually shipped into.

---

# Decoupling

## P0 — Baseline and harness

**Files:** none (measurement only).

**Do.** Re-take the baseline through the full reimport → re-bake → re-solve cycle at `giResolution`
32, Playground and Sponza, in **both Single and Cube** (both ship). Capture: solve ms via
`BufferGiSolveProfiler` (Inject/Gather/Blur split), the near-wall leak column, the lit-side
`mean |d2 lum/dx2|` blockiness number, and a fixed-pose reference image. Pin exposure first.

**Acceptance.** Two independent runs agree within the noise floor. If they do not, nothing later in
this plan can be trusted.

**Note.** A re-solve is not a re-bake. Reset `_collectedSamples` *and* force `_materialBaked = false`
+ `Voxelize()`, or the "baseline" measures the previous pass's contents.

## P1 — Remove ambient occlusion

**Depends on:** P0. **Files:** `BufferGiRead.hlsl`, `BufferGiBake.compute`, `BufferGiUpdater.cs`,
`BufferGiField.hlsl`.

**Do.** Delete the openness integral (`BgiComputeOpenness`), the face-plane AO read in
`BgiSampleFaceAoShadow`, `_BgiAoStrength`, and `DebugView.Ao`. Free `_Surface` bits 16–23 on solid
voxels (air keeps its air-distance there).

**Why first.** It removes work every later phase would otherwise have to carry: P4 would port the
openness integral to hi-res derivation only for this to delete it.

**The structural win.** Outside `BGI_DEBUG_VIEWS`, the AO face read is the *only* consumer of
`_Occupancy` and `_Surface` in `BufferGiRead.hlsl`. Removing it makes the fragment **purely
texture-based** — no SSBO taps, smaller WebGPU pipeline layout.

**Acceptance.** With `_BgiAoStrength` previously 0, output is byte-identical. With AO previously on,
the diff is confined to concave corners and contact regions — verify nothing else moves. Confirm no
`StructuredBuffer` remains declared in the fragment outside the debug keyword.

## P2 — Split sun visibility into its own texture

**Depends on:** P1. **Files:** `BufferGiSolve.compute` (`CSBlur`), `BufferGiRead.hlsl`,
`BufferGiUpdater.cs`.

**Do.** Move sun visibility out of the mirror texture's alpha into its own **R16** `Texture3D`, still
at 32^3, fine + coarse. **Leave the ComputeBuffers alone** — `_Radiance.w` and `_Irradiance.w` are
spare halves in an existing `uint2`; moving them out would add memory for nothing.

- The GI and shadow reads are already separate `SampleLevel` calls at different positions, so this
  costs **zero additional taps**.
- In Cube the alpha is currently replicated 6x across air cells ("a point in air has no sides").
- `_BgiBlurSunVis` and the ease asymmetry (RGB is confidence-eased, alpha deliberately is not) stop
  being branches inside `CSBlur`.

**Cube note.** Preserve the per-slab alpha for now — it resolves which face's visibility applies at a
sub-voxel wall. Collapsing it to a scalar is P6's bet, not this phase's.

**Acceptance. Pure null** — byte-identical in both Single and Cube. Any change means the tap position,
the Cube slab weighting or the "outside the field → 1 (lit)" guard was not reproduced faithfully.

## P3 — Hi-res occupancy + configurable resolutions

**Depends on:** P2. **Files:** `BufferGiVoxelize.shader`, `BufferGiBake.compute`,
`BufferGiUpdater.cs`, `BufferGiBakeAsset.cs`, `BufferGiField.hlsl`.

**Do.**

1. **Make the resolutions settings, not constants.** Occupancy and shadow resolutions become
   serialized, power-of-two, snapped to 64/128/256, independent of `_giResolution`. Publish
   `_BgiOccGrid` / `_BgiOccGridLog2` (and `_BgiShadowGrid` in P6). Provide a per-platform override.
2. Rasterize occupancy at the max supported resolution into a **bit-only** target — a dedicated
   bit-only raster pass, or a transient hi-res `_Material` buffer downsampled into the 32^3 one and
   released. See [downsample operators](#downsample-operators).
3. **Serialize only the finest level** into the bake asset (2 MB per field at 256^3), and
   **OR-downsample at load** to the platform's resolution. One asset, every platform.
4. Derive the 32^3 **traversal mip** by OR-downsampling, as one more dispatch in the derive chain.
   Keep it *separate* from today's 32^3 `_Occupancy`, which stays the storage field for blur gating,
   air-distance, surface build and the far-air skip.
5. **Store the fine level in 4×4×4 blocks** (8 bytes) — do it now, because changing the layout later
   invalidates every measurement taken on it.

**Acceptance.**
- **Null.** Nothing reads the new field yet: byte-identical to P2.
- **Containment.** `OR-downsample(hi-res) ⊇ 32^3 raster` with **low-res-only = 0**. Expect ~400
  hi-res-only cells at 128^3. A non-zero low-res-only count means the raster is not conservative and
  P7 would silently skip occluders.
- Round-trip: reload from disk reproduces the raster's bits, at every platform resolution.
- Report **bake time** at each resolution (see the deferred gating decision).

### Downsample operators

The operator is **per quantity**, and the wrong choice on occupancy deletes geometry silently.

| quantity | operator | why |
|---|---|---|
| occupancy → traversal mip, or → a lower platform resolution | **OR (Any)** | must be conservative: an empty coarse cell skips its children untested, so under-estimating is a silent missed-occluder bug |
| `_Material` albedo | **majority over the SOLID fine cells** | a colour has no "any"; you want the representative surface |
| `_Material` emission | **max, not majority** | a sub-voxel lamp in 3 of 64 cells must not be voted out of existence |

**Never majority-downsample occupancy.** The geometry is thin shells: in a 4×4×4 block an
axis-aligned wall one fine cell thick is **16 of 64 cells (25%)**, a diagonal ~35%, a two-cell wall
exactly 50%. A >50% rule deletes every wall in the scene. The measured solid fractions say the same —
5.1% / 5.9% hi-res against 17.3% / 21.3% low-res is the ~1/N scaling of a shell.

The real tradeoff is **binary vs fractional**, not OR vs majority: OR over-occludes, and that is the
binary representation's cost, not the operator's. Thickening is the safe error; deleting is not.

---

# Fixes

## P4 — Derive `_Surface` from hi-res occupancy

**Depends on:** P3. **Files:** `BufferGiBake.compute` (`CSBuildSurface`, `OccupancyNormal`).

**Storage stays 32^3 and 4 bytes.** Only the derivation moves.

| field | change |
|---|---|
| normal | gradient over the hi-res sub-cells instead of 32^3 neighbours — same 16 bits |
| `TWOSIDED` | real thickness on all three axes in hi-res cells, non-circularly — still one bit |
| air-distance | genuinely a 32^3 quantity — **leave it alone** |
| openness | gone (P1) |

**Acceptance.** Not a null; the prediction is what makes it verifiable:

1. Dump the 32^3 cells where the old and new gradients disagree.
2. That set predicts exactly which pixels may change.
3. **Pixels outside it must be byte-identical.** Anything moving outside is a bug, not an improvement.
4. `TWOSIDED` false positives stay 0; the 232 known misses go to 0.

**Risk.** Preserve the gradient sign convention — do **not** reintroduce flipping it to agree with the
triangle normal (see [Appendix B](#appendix-b--ruled-out)).

## P5 — Attribute the leak (GATE)

**Depends on:** P4. **Files:** none (measurement only).

The leak has two independent causes and P4 addresses only one:

- **Normal error** — the stored normal decides which side's air `CSBlur` dilates into the contested
  shell texel. Fixed by P4.
- **Footprint** — the trilinear kernel spans a one-voxel wall. Untouched so far.

**Do.** Re-measure the near-wall leak column at the fixed pose, in both Single and Cube. On walls
whose cell had a *correct* normal before P4, whatever leak remains is the footprint term.

**Output.** How much of the leak was footprint, and **how many pixels have a kernel that spans a
one-voxel wall in the surface plane** — that count is the population P9 would act on, and it sizes
both remaining options.

**Gate.** Footprint small → P7–P8 are a performance and shadow-quality project, not a leak fix; judge
them on that. Footprint dominant → the footprint must be attacked, by **P8 (smaller kernel) or P9
(don't interpolate across it)**. They are alternatives; P9 is far cheaper and is scheduled last only
because it is optional.

## P6 — Fine shadow texture

**Depends on:** P2, P3. **Files:** new sun pass, `BufferGiRead.hlsl`, `BufferGiUpdater.cs`.

**The cheapest real quality win here, and the only item that sharpens baked shadow edges on screen.**
**Replaces** the `Baked` path.

**Do.** Raise the sun-visibility texture to the occupancy resolution, **R16**, and **evaluate it
directly against the hi-res occupancy** — one shadow ray per fine texel, as its own pass triggered by
`HasSunChanged()`.

This is a **re-evaluation, not an upsample**: sun visibility depends only on occupancy and sun
direction, so a finer grid carries real new information.

- Use the `Supersampled` estimator so each texel holds a true area fraction — that is what
  `_BgiShadowSharpness` reconstructs an edge from.
- **R16, not R8:** sharpening amplifies quantisation with the signal, and `_BgiShadowSharpness` is
  clamped low but not high.
- **Never exceed the occupancy resolution** — detail beyond it is fabricated.
- Add `_BgiShadowGrid`; the tap offset must step one *shadow* cell.

**Deliberate deviation from guideline 5.3** (fuse the texture write into the computing pass): its
lifecycle is the sun, not the solve frame.

**Acceptance.** Shadow edges sharpen at the fixed pose; the reference image moves only where shadows
are; the GI-only capture is **byte-identical**.

**Risk — PC/Cube only.** Cube's alpha slabs resolve which face's visibility applies at a sub-voxel
wall (24.4% / 26.3% of shell cells contested). A scalar texture loses that; the bet is that resolution
substitutes for it. **On Single there is nothing to lose** — it already stores one alpha per voxel. So
verify on Cube at a fixed pose before collapsing the slabs, and if it fails, keep per-slab alpha on
the PC path or fall back to storing a signed distance to the shadow boundary, which is single-valued
inside a wall.

## P7 — Hi-res DDA in the solve

**Depends on:** P3. **Files:** `BufferGiSolve.compute`, `BufferGiField.hlsl`.

**Do, in this order — do not build the hierarchy first.**

1. **Flat hi-res march.** Point `MarchOccupancyFrom` at the fine field. `BGI_MAX_RAY_STEPS` must key
   off the **occupancy** grid. `BgiShadowOriginStep` becomes a step in occupancy cells or a float
   offset. Measure.
2. **If cache-bound**, confirm the 4×4×4 block layout from P3 is actually being used on this path.
3. **Two-level march.** Coarse bit in the always-hot traversal mip; on a hit, one load of that coarse
   cell's block (1 / 8 / 64 bytes depending on ratio) and march it in registers. Stay 2-level.

**Acceptance.**
- `(inject + gather) − blur` is the march cost — the number each step must move. Flat is the baseline
  the hierarchy must beat.
- Expected at 128^3: flat ~50–60 ms/solve, two-level ~22–30 ms, from the 14.7 ms baseline.
- Thin geometry occludes proportionally: a 0.22 m curtain stops casting a 0.68 m-thick shadow.
- **The solve still reaches all 500 samples.** Long convergence is acceptable; a reduced ray budget is
  not. Do not "fix" a slow march by cutting `_maxSamples`.

**Risk.** Per-dispatch time stays in the tens of ms against a 2-second watchdog, so the TDR does not
return — that crash was 2M *irradiance* voxels, and voxel count is unchanged here.

## P8 — Fine irradiance texture (contingent)

**Depends on:** P5 saying the footprint term dominates. **Consider P9 first — it is far cheaper.**

**Do.** Solve at 32^3, publish the mirror at the occupancy resolution, filling each fine texel from
the coarse cells **reachable** from it. No extra rays — a narrower kernel that respects the wall.

- **Single first** (33.5 MB at 128^3); Cube is 201 MB and is a PC-only proposition at best.
- **Build it once on convergence**, not per solve frame — this also means nothing ever needs to read
  back a write-only storage texture.
- The reachability classification is **static** — bake it.
- Add `_BgiReadGrid`; the tap offset must step one *read* cell.

**Acceptance.** The near-wall leak column flattens without the blockiness signature: the discontinuity
lives in the field, and the fragment still does one continuous trilinear tap.

**Do not build bricking.** See [Appendix B](#appendix-b--ruled-out).

## P9 — Contaminated-axis snap (last, optional)

**Depends on:** P6. **Scheduled last by decision.**

**Do.** Where the trilinear kernel spans a one-voxel wall in the surface plane, snap the sample
position to the voxel centre on **that in-plane axis only**, leaving the others continuous — the same
technique `BGI_TAP_AXIS_SNAPPED` already applies to the normal axis. **The condition is binary, not a
tuned threshold.**

Trilinear blends `c0 = floor(ga - 0.5)` and `c0 + 1` with weight `f`. Per in-plane axis: `c0 + 1`
solid → snap `f = 0`; `c0` solid → snap `f = 1`; both air → **do not snap**.

**The snap engages where it is a no-op**, which is what both prior attempts lacked: the moment
`c0 + 1` becomes the wall cell is the moment the tap crosses a cell boundary, where `f = 0`.

**The gate must be baked.** A per-texel **6-bit neighbour-solidity mask** (`±X, ±Y, ±Z`) in its own
**R8_UInt** texture, read with one `Load`. All six bits are needed because which two are consulted is
a runtime decision. It cannot ride the shadow texture's second channel — that texture is filtered, and
an interpolated bitmask is meaningless. Compute it at **bake**: pure geometry, must not be recomputed
on a sun change. `mask == 0` doubles as the early-out. Put the snap path behind a **keyword**.

**Acceptance.**
- **Byte-identical on pixels whose kernel does not span a wall.** Hard requirement.
- The near-wall column flattens toward the point-tap reference.
- Blockiness judged by eye against the **1.069** baseline and the **1.441** that was rejected.

**Why it may still fail.** A snap discards sub-texel coverage and `_BgiShadowSharpness` is pointwise,
so the value goes constant across the snapped cell on that axis. Measure with `Supersampled` on and
off.

---

## Hard constraints checklist

Re-read at the start of every phase. Items 1 and 2 are bugs the 2026-08-22 PoC actually shipped into.

- [ ] **Clear every new buffer and texture at allocation.** A fresh `ComputeBuffer` holds garbage, not
      zeros, and reads as uncorrelated noise that looks exactly like an index-mapping bug.
- [ ] **Hang new bake work off `Voxelize()`, not `VoxelizeScene()`.** `TryLoadBakeAssets()` returns
      first on the disk-bake path. Related: `_voxelizeMaterial` is created lazily inside the rasterize
      methods — an earlier pass must create it itself.
- [ ] **A traversal coarse level must be conservative** (OR-downsample, never an independent raster).
- [ ] **Nothing in `_Surface` may depend on the light.**
- [ ] **Anything that exists only at runtime raster time is dead in player builds.**
- [ ] **Never read back a storage texture** (write-only under WebGPU).
- [ ] **New fragment globals must be bound in every variant that declares them.**
- [ ] **Grid stays a power of two**, and `BGI_MAX_RAY_STEPS` keys off the occupancy grid once the two
      differ.
- [ ] **The solve must still reach `_maxSamples`.** Never trade samples for march speed.
- [ ] **Do not raise `giResolution` above 64 on the dev machine** — 128 TDRs the GPU. This plan raises
      *occupancy*, not `giResolution`.

## Risk register

| risk | phase | mitigation |
|---|---|---|
| Coarse level not conservative → silently skipped occluders | P3, P7 | containment check, low-res-only = 0 as acceptance |
| Cube shell-alpha disambiguation lost when sun-vis becomes scalar | P6 | PC/Cube-only risk; fixed-pose verification; fall back to per-slab alpha on PC or signed-distance encoding |
| One bake asset must serve 64/128/256 | P3 | bake at max, OR-downsample at load |
| Axis snap trades leak for blockiness (both prior attempts' failure) | P9 | keyword-gated; byte-identical null required off the contaminated set |
| P8 built when P9 would have sufficed | P8 | P5 reports the spanning-pixel count; state explicitly that P9 was deferred, not rejected |
| Flat hi-res march is cache-bound | P7 | block layout already landed in P3 |
| Bake time growth hurts authoring | P3 | measured and reported in P3; gating deferred, default always-on |
| Measurement drift across phases | all | P0 harness re-run at every phase boundary, in both modes |

## Appendix A — Measured evidence

**Baseline [measured 2026-08-23]** — Playground, Single, `samplesPerFrame` 10, coarse + fine,
`giResolution` 32, AMD Radeon iGPU / D3D12:

| | |
|---|---|
| solve, both fields | **14.73 ms** |
| buffers + textures | 2.52 MB |
| `Camera.main` @ 2560×1440 | 8.86 ms/frame |

**`giResolution` 128 never completed.** It invalidated the grid-32 disk bakes, fell into the runtime
`Voxelize()` path, and ~150 ms later D3D12 returned `887a0006` (DXGI_ERROR_DEVICE_HUNG). The log says
*"GfxDevice was not out of Local memory"* (585 MB of a 17 GB budget) — **a watchdog TDR from one
over-long submission, not an allocation failure.** Projected: ~0.9 s/frame, ~47 s for the 500-ray
budget; ~161 MB Single, ~673 MB Cube.

**Field economics [measured 2026-08-22].** Cube irradiance is 48 B/voxel against occupancy's 0.125 B —
**384:1**.

**Solid fractions [measured 2026-08-22].** Hi-res 5.1% / 5.9% versus low-res 17.3% / 21.3%.

**Coarse-level containment [measured 2026-08-22].** OR-downsample(128^3) vs the 32^3 raster:
**hi-res-only = 403 cells, low-res-only = 0.**

**Normals [measured 2026-08-16, Sponza, 5,693 thin voxels].** 3,242 have a zero 26-neighbour gradient;
**2,451 return a real but misleading direction** with nothing flagging them. `TWOSIDED` misses 232 of
5,693, no false positives.

**Contested shell cells [measured].** The shell alpha is the stored normal's face visibility and is
not dilated: **24.4% coarse / 26.3% fine** contested. Ungated 26-neighbour dilation measured ~10%
brighter than reference on Sponza's lower arcade.

**The leak is the footprint [measured 2026-08-22].** Floor strip toward a wall, GI-only luminance:

| x | trilinear | point tap |
|---|---|---|
| 500 | 14.3 | 13.3 |
| 600 | 62.6 | **19.5** |
| 660 | **93.8** | **19.5** |

Flat where trilinear ramps 5x. ~3.1% of shaded pixels off by >15%, in one-voxel bands along
wall/floor/roof junctions. **Cube does not fix this** — it separates along the *read* axis; the leak
is in-plane.

**Fragment-side DDA cost [measured 2026-08-22].** The read-side visibility PoC: **4.85 → 26.5 ms at
1080p (+21.7 ms)**, 8 corner taps × up to 12 steps, no early-out.

**AO scale.** `BGI_AO_RADIUS` 2 at `giResolution` 32 is ±1.36 m on Sponza's X axis — not contact
occlusion. Sub-voxel geometry gets `ao = 1` (no solid tap on the face plane).

## Appendix B — Ruled out

**Read-side binary visibility gate [measured 2026-08-23].** Lit-side blockiness 1.069 → **1.441
(+35%)**, with an identical `off` control at every occupancy resolution — resolution is not the cause,
the binary filter is. The softness sweep is **perfectly monotonic both ways**: smoothness bought 1:1
with leak, no window. occGrid 64 makes the leak *worse* than no fix; 256 is byte-identical to 128. The
renormalisation floor backfired (→ 2.082).

**Footprint-shrink `lerp(ga, floor(ga) + 0.5, solidWeight)`.** Removed the bleed, matched the point
reference exactly at strength 1 — and produced a hard-edged grid-aligned transition on the floor.
Rejected for blockiness. It snapped **all three axes** and drove the snap from a scalar mixing all
eight corners. P9 is a different mechanism, not a retune of this.

**Hard nearest-fetch-near-walls in the Single/Fast path.** Built, reverted. Nothing in the shipped
read path mitigates the in-plane leak today.

**Flipping the gradient normal to agree with the triangle normal.** The supporting measurement was
circular — it used the triangle normal as ground truth, and that value is last-write-wins, observed
flipping between bakes on one curtain cell.

**Brick atlas for the fine mirror [proposed].** Bricks needed ≈ 40% of a 32^3 grid ≈ 13,000. A 4^3
brick needs a 1-texel border for hardware trilinear → 6^3 = 216 texels for 64 useful. 13,000 × 216 =
**2.8M texels, more than dense 128^3 (2.1M)**. Border overhead eats the saving because a 32^3 grid's
shell is not sparse. **Go dense.**

**Three-level occupancy hierarchy.** At a 4:1 total ratio a middle level buys little skipping and
costs a third resident structure plus a third dependent load.

**Hi-res `_Material`.** Albedo only reaches the screen through the irradiance field, which averages it
back down. Watch two exceptions if revisited: emissive voxels, and cells straddling two materials
(Sponza's curtains).
