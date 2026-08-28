# Decoupling the GI field resolutions — implementation plan

**Status: P0–P7 landed; P9 built and gated off. A P6 regression (normal-offset bias) was found by fs and fixed 2026-08-24. P0–P6 verified 2026-08-23 on Playground and re-verified on Sponza; P7 and P9 measured on Sponza 2026-08-24. P8 is the only phase not started.** The evidence behind each
decision is in [Appendix A](#appendix-a--measured-evidence); approaches already tried and rejected are
in [Appendix B](#appendix-b--ruled-out).

| phase | status |
|---|---|
| [P0 Baseline](#p0--baseline-and-harness) | **done** — and the harness got sharper than planned; see [Appendix A](#appendix-a--measured-evidence) |
| [P1 Remove AO](#p1--remove-ambient-occlusion) | **done** — byte-identical in both modes |
| [P2 Split sun visibility](#p2--split-sun-visibility-into-its-own-texture) | **done** — byte-identical in both modes |
| [P3 Hi-res occupancy](#p3--hi-res-occupancy--configurable-resolutions) | **done** — null; round-trip holds; containment is 0 low-res-only on Playground and [1 explained cell](#sponza-measured-2026-08-23) on Sponza |
| [P4 Derive `_Surface`](#p4--derive-_surface-from-hi-res-occupancy) | **partly done** — `TWOSIDED` reworked; 0 misses on Playground AND on Sponza, where the defect was found; the hi-res *gradient* was measured and rejected |
| [P5 Attribute the leak](#p5--attribute-the-leak-gate) | **done, then narrowed by [Sponza](#sponza-measured-2026-08-23)** — the leak is 2x larger there and Cube does not help; set `SingleTapFilter` to `AxisSnapped` and re-measure before judging P8/P9 |
| [P6 Fine shadow texture](#p6--fine-shadow-texture) | **done**, then **a regression it introduced was found and fixed 2026-08-24** — it cut the shadow normal-offset bias 4x by redefining its unit, which showed as shadow acne on sunlit walls |
| [P7 Hi-res DDA](#p7--hi-res-dda-in-the-solve) | **done** — flat hi-res is the default and byte-identical to the two-level walk, which measured slower at every ratio; a dead sun ray found in the solve paid for the resolution |
| [P8 Fine irradiance](#p8--fine-irradiance-texture-contingent) | not started - and now the only remaining candidate for the in-plane term |
| [P9 Axis snap](#p9--contaminated-axis-snap-last-optional) | **built, measured, defaulted OFF** - byte-exact outside the leak population and free, but visibly blocky on Sponza; the third snap attempt to fail the same way |

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
- [Sponza — measured 2026-08-23](#sponza-measured-2026-08-23) (P7 and P9 are measured in their own sections)
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

**"Coarse" means two different things in this document, and they are not 1:1.** The *coarse/fine
FIELD* split is `LightingManager`'s cascade — two different world boxes. The *coarse/hi-res GRID*
split this plan introduces is a resolution inside one box. They multiply: each field carries its own
hi-res occupancy at the same `_BgiOccGrid`. Since the two boxes are not the same size, **the same
setting buys different physical detail in each** — measured on Bootstrap at 128³, the coarse field's
hi-res voxel is 0.140 m in X against the fine field's 0.050 m, 2.8× worse, because its box is 2.8×
wider. Combined with [P5](#p5--attribute-the-leak-gate)'s finding that the coarse field is where the
in-plane leak actually lives, **whether the occupancy resolution should be per field is an open
question** this plan does not settle; today it is one setting for both. See the architecture doc's
["Two axes"](buffer-gi-architecture.md#two-axes-and-they-are-not-the-same-axis).

**The 4×4×4 block is fixed at 8 bytes regardless of ratio.** The tidy "one coarse cell = one 8-byte
load" only holds at 4:1 (128/32). At 2:1 a coarse cell is 8 fine cells = 1 byte; at 8:1 it is 512
cells = 64 bytes = **exactly one cache line**, which is the best of the three. All are closed-form;
only the load width changes.

## Target architecture

Rows marked **[landed]** are in the code as of 2026-08-23.

| field | today | target | why |
|---|---|---|---|
| `_Occupancy` | 32^3 | **[landed]** `_OccupancyHi` at a configurable 64/128/256, plus the 32^3 `_OccupancyTraversal` mip; `_Occupancy` itself stays 32^3 as the lighting grid's own raster | 0.125 B/voxel — geometry accuracy for zero rays |
| `_Surface` | 32^3 | **[partly landed]** 32^3; `TWOSIDED` now derived from hi-res thickness, the normal still from the coarse thick-gradient | consumers are 32^3 solve kernels; only the derivation moves |
| `_Material` | 32^3 | **32^3** | read once in `CSInject`; finer detail is averaged away before it is seen |
| `_Radiance` / `_Irradiance` / `_IrradianceBlur` | 32^3 | **32^3** | the only axis that costs rays *and* convergence time |
| sun visibility | alpha of the mirror texture | **[landed]** own R16 texture at its own `_BgiShadowGrid`, re-marched against the hi-res occupancy on a sun change | a scalar, re-evaluated not upsampled |
| neighbour-solidity mask | — | **own R8_UInt texture** (P9 only) | keeps the snap gate out of the fragment |
| irradiance mirror | 32^3 | 32^3 unless P5 says otherwise | 201 MB in Cube — gated on evidence |
| openness / AO | `_Surface` 16–23 | **[landed]** removed | |

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

### Done [2026-08-23] — and there is no noise floor

Two runs did *not* agree at first: ±1–3 LSB in Single, and **7% apart in Cube**. The cause was not the
raster. `_collectedSamples = 0` restarts the accumulation but does not clear `_Irradiance`, and
`CSGather`'s Cube branch makes a bucket that received no ray this frame HOLD its previous value — so a
bucket that never receives one keeps whatever the *previous run* left in it. Setting
`_resetAllFields = true` before each capture makes the whole cycle **byte-identical across three
consecutive runs in both modes**.

That turns every "null" in this plan from *within noise* into *bit-exact*, which is a much sharper
instrument, and it is why P1–P3 below could be verified as hard nulls. Procedure now in
[verifying-changes.md](verifying-changes.md#clear-the-dynamic-fields-or-the-capture-depends-on-the-previous-one).

Baseline, Playground, `samplesPerFrame` 10, `giResolution` 32, AMD Radeon iGPU / D3D12, fixed pose,
exposure pinned at `_ExposureLinear` 1.148698:

| | Single | Cube |
|---|---|---|
| solve, both fields | **15.03 ms** (15.044 / 15.022 over two runs) | **17.24 ms** |
| capture mean luminance | 106.113 | 114.739 |

**Sponza is covered separately.** Everything from here to the end of P6 was measured on Playground;
the numbers a scene can move were re-taken on Sponza in [its own section](#sponza-measured-2026-08-23),
which is where the leak and normal figures should be quoted from.

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

### Done [2026-08-23]

Byte-identical in both Single and Cube (`_aoStrength` was already 0). `BgiSampleFaceAoShadow` lost its
AO duty and is now `BgiSampleSunShadow`, returning the shadow scalar; `_Surface` is no longer declared
or bound in the fragment at all, and `_Occupancy` survives only under `BGI_DEBUG_VIEWS` for
`BgiTapSolidWeight`. `DebugView.Ao` is gone (value 3 is retired; the others keep their numbers).

**Proof the rebuilt fragment is what rendered**, since "byte-identical" is also what a stale shader
produces: forcing `_debugView = 3` now renders identically to view 0 (the AO case no longer exists),
where before it returned a raw greyscale. View 5 (`GiSolidWeight`) still reads real data, so the
debug-only `_Occupancy` declaration compiles and binds.

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

### Done [2026-08-23]

Byte-identical in both modes. `_BgiSunVisTex` / `_BgiSunVisTexCoarse` are `RHalf` volumes of exactly
the mirror's dimensions (so Cube keeps its per-slab values), written by `CSBlur` alongside the
irradiance; the mirror's alpha is now a constant `BGI_MIRROR_ALPHA_UNUSED`. `RHalf` and not `R8`
because it is bit-for-bit the precision the value had as the alpha, which is what makes the null
achievable at all. The `_Radiance` / `_Irradiance` **w** channels were left alone, as decided.

**Proof the shadow path is live at this pose** (a null over a dead path proves nothing): `DebugView.SunVisibility`
shows a real field (51.7% below 32/255, 24.3% above 223/255, not a constant), and sweeping
`_BgiShadowSharpness` 1 → 8 → 16 moves the shaded image (109.79 → 105.45 → 105.24 mean luminance).

Both new textures are cleared slice by slice at allocation. `CSBlur` does rewrite every texel, but a
field whose bake was rejected never dispatches a solve at all, and an uncleared `RenderTexture` reads
as noise there — hard-constraints item 1.

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

### Done [2026-08-23]

Everything above, as specified. `_occupancyResolution` (+ `_occupancyResolutionMobile`) are serialized
and snapped to 64/128/256, floored at `_giResolution`; `_BgiOccGrid` / `_BgiOccGridLog2` /
`_OccFieldWordOffset` are published; the fine level is a dedicated bit-only raster **pass 1** of
`BufferGiVoxelize.shader` at the occupancy grid, stored in 4×4×4 blocks; `CSBuildTraversalMip`
OR-downsamples it onto `_OccupancyTraversal`; `BufferGiBakeAsset` v5 stores the finest level plus its
`occGrid` and is OR-downsampled at load.

| check | result |
|---|---|
| **Null** | max channel delta **1**, zero pixels beyond 1 LSB, against both P0 captures (measured before the cold-clear fix; the residual was solve history, not this change) |
| **Containment**, fine | hi-res-only **403**, low-res-only **0** |
| **Containment**, coarse | hi-res-only **246**, low-res-only **0** |
| **Solid fractions** | hi-res **5.13% / 5.88%** vs low-res **17.29% / 21.30%** |
| **Round trip** @256 | 0 differing words of 524,288 |
| **Load downsample** | OR-downsample(256)→64 vs a *direct* 64 raster: both 30,264, downsample-only 1,022, **raster-only 0** |

**Containment is not universally 0 on the low-res-only side.** Playground gives 0; Sponza gives
**exactly 1**, and it is a low-res over-report at a wall lying on a cell boundary, not a missed
occluder — see [Sponza](#sponza-measured-2026-08-23) for the cell and for two false explanations that
were tested and discarded.

The containment and solid-fraction numbers reproduce
[Appendix A](#appendix-a--measured-evidence)'s 2026-08-22 figures exactly (403 / 0, and 5.1% / 5.9%
vs 17.3% / 21.3%), which is independent evidence that the block layout and the raster agree with what
was measured then.

**Bake time** (raster + full derive chain, both fields, GPU-synced). Two numbers because the first
bake after a resolution change also reallocates:

| occGrid | steady state | including realloc | memory, both fields |
|---|---|---|---|
| 64³ | — | 48.2 ms | 64 KB |
| 128³ | **21.1 ms** | 51.6 ms | 512 KB |
| 256³ | — | 68.6 ms | 4 MB |

**Gating stays deferred, default always-on** — the decision holds. Even at 256³ a full re-bake is
under 70 ms, which does not hurt authoring.

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

### Partly done [2026-08-23] — TWOSIDED reworked, the gradient rejected

**`TWOSIDED` — done, and it meets the acceptance.** The test now runs on **all three axes from
occupancy alone**, with `BgiHiSlabExtent` (the real per-axis solid thickness inside the coarse cell,
measured on the hi-res grid) breaking ties where more than one axis qualifies. Non-circular: the
stored normal is consulted nowhere.

| | before | after |
|---|---|---|
| thin on ≥1 axis, fine | 4,551 | 4,551 |
| flagged, fine | 4,514 | **4,551** |
| **misses** | 37 fine, 67 coarse | **0 / 0** |
| **false positives** | 0 | **0** |

Controlled A/B against the previous test, cold-clear harness: **Single is byte-identical, 0 pixels
changed** (the flag is gated on `_BgiRadianceDirs > 1` at every consumer); **Cube is +3.73% mean
luminance**, max 65/255, concentrated on the floor slab — the newly-flagged cells hand arriving rays a
real lit back face instead of the black ambient floor.

**The hi-res gradient — measured and NOT adopted.** See
[Appendix B](#appendix-b--ruled-out). Two findings killed it, and the second is the decisive one:
it disagrees in *sign* with the shipping normal on 89 solid-backed walls, because the hi-res raster is
a hollow shell for exactly the same reason the coarse one is. Making it work needs a hi-res
`_OccupancyThick`, which needs a per-hi-res-cell triangle normal the bit-only raster does not store —
a materially larger change than this phase budgeted. **Air-distance untouched**, as planned.

**Still open from this phase:** the normal for the 104 cells whose thin axis is not their gradient's
dominant axis. Those keep their gradient normal, so in Cube their back face is injected at `-normal`,
which may point into solid; `GetDirectLight`'s existing guard makes that read as shadowed rather than
as garbage, which is conservative but not right.

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

### Done [2026-08-23] — Playground, one pose

**First, correct the premise.** This phase assumed P4 had fixed the normal-error term. It has not:
P4 shipped the `TWOSIDED` rework and *rejected* the hi-res gradient, so the stored normals are
unchanged. The split below is therefore between "what the tap filter can already fix" and "what is
left", not between "normal error" and "footprint".

**Then, correct the pose.** The pose these measurements started from turned out to shade entirely
from the **coarse** field — `_autoSwitchToClosestVolume` is off in Bootstrap, so the active volume is
`Room_dark` while the camera sat in `Room_emissive`. Both sets are below; only the fine one is
representative of what the plan is about.

**Population** — `DebugView.GiSolidWeight`, i.e. how much of each pixel's GI trilinear footprint
landed on solid shell cells. This is the set a binary snap (P9) could act on.

| footprint on solid | **fine field**, Single | **fine field**, Cube | coarse field, Single |
|---|---|---|---|
| 0 — cannot leak | 3.6% | **74.8%** | 4.0% |
| 0.00–0.25 | 75.7% | 10.0% | 2.9% |
| 0.25–0.50 | 8.0% | 4.9% | 3.0% |
| 0.50–0.75 | 5.4% | 4.3% | 9.6% |
| 0.75–1.00 | 7.3% | 6.0% | **80.6%** |

Two things fall out of that table. **Cube's per-axis tap is already leak-free on three quarters of
pixels**, against Single's 3.6% — the per-bucket plane placement is doing most of the work a footprint
fix would otherwise have to do. And **the coarse field is where the leak really lives**: 80.6% of its
pixels take their GI almost entirely from shell texels, which is what 0.56 m voxels buy you. Any
footprint work should be judged on the coarse field too, not only on the detailed room.

**How much of it the existing tap filter already fixes.** `SingleTapFilter` is pure read state, so
Fast vs AxisSnapped isolates the normal-axis footprint term with the solved field held identical
(GI-only, Single, fine pose):

| | |
|---|---|
| mean GI luminance | 39.504 → 39.849 (**+0.88%**) |
| pixels changed | 38.05%, max 26/255 |
| **pixels off by >15% of their own value** | **1.55%** |

Against [Appendix A](#appendix-a--measured-evidence)'s ~3.1% of shaded pixels off by >15% for the
*total* leak, that suggests roughly half the badly-affected population is the normal-axis term —
already fixable for free by flipping the tap filter — and the rest is genuinely in-plane. Treat the
comparison as indicative only: Appendix A's figure is Sponza, this one is Playground.

**Gate verdict: judge P7–P8 as a performance and shadow-quality project, not as the leak fix.** The
residual in-plane term is real but small on the fine field (~1.5% of pixels at >15%, and the whole
0.25+ contamination band is 20.7% Single / 15.2% Cube), and P8 costs up to 201 MB to attack it. **P9
is the proportionate option** for the residual — and it should be evaluated on the COARSE field,
where the population is four times larger. P9 remains deferred, not rejected.

**Not measured:** the near-wall leak *column* (Appendix A's trilinear-vs-point-tap table). That needs
a point-tap build, which does not exist in the tree; the numbers above size the population instead of
re-deriving the magnitude. **Sponza has since been measured** — see
[Sponza](#sponza-measured-2026-08-23), which roughly doubles the contaminated population and makes the
free `AxisSnapped` tap filter the next step rather than P8.

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

### Done [2026-08-23]

`CSSunVisibility` in `BufferGiSolve.compute` re-marches the whole shadow volume against the hi-res
occupancy (new `MarchOccupancyHiFrom`, `BGI_MAX_OCC_RAY_STEPS` keyed off `_BgiOccGrid`), triggered by
`HasSunChanged()` or a re-bake. `_shadowResolution` (+ a mobile override) is its own setting, clamped
to `_BgiOccGrid`. The texture is a plain `RHalf` cube — **the six Cube slabs are gone**, and with them
the per-slab alpha machinery in `CSBlur`, `_BgiBlurSunVis`, and the Cube branch in
`BgiSampleShadowTexture`. `_BgiShadowNormalOffset` now steps SHADOW texels.

| check | result |
|---|---|
| **vs an independent reference** | at the fine pose the new Baked shadow and the `Sdf` per-pixel raymarch **both read 100% shadowed** — two unrelated implementations agreeing |
| **moves only where shadows are** | **12.18% of pixels** changed, in Single *and* Cube, confined to one crisply-bounded sunlight patch; the other 87.8% are byte-identical |
| edge quality | the blocky gradient on the far wall becomes a straight, sharp edge |
| mean luminance | Single −6.05%, Cube −7.37% (darker: the hi-res field seals walls the 32³ DDA leaked through) |
| texture distribution | fine mean 0.533 (46.4% shadowed / 53.0% lit), coarse mean 0.689 — same ordering as the solve's own independent estimate (0.368 / 0.466 over air voxels) |

**GI-only was not separately captured.** It is unchanged by construction — `BgiGatherIndirect` reads
only `_BgiIrradianceTex`, and `CSBlur`'s rgb write is untouched (only the sun-vis write was removed) —
and the 87.8% byte-identical pixels above carry GI, so it is unchanged there by measurement too.

**The Cube risk did not materialise.** Collapsing the slabs cost nothing measurable: Cube's changed-
pixel population is identical to Single's, to two decimal places. At the shadow resolution a sub-voxel
wall's two sides are different texels, so there is no ambiguity left for a slab index to resolve.

#### Two things this phase got wrong first, both worth keeping

**It TDR'd the device.** The pass was written as one dispatch over the whole volume. At 256³ that is
16.7M texels × 4 rays × up to 768 DDA steps × 2 fields, and the driver killed the editor. It is now
**chunked over Z slices** with a `SunVisTexelsPerDispatch` budget of 2^15, ~20 ms a chunk (including
the solve step in the same frame); a sun move sweeps in over 8 chunks at 64³ and 64 at 128³ instead of
hitching. Hard-constraints item "per-dispatch time in the tens of ms" — this phase is the one that
proves it is not theoretical.

**Then the chunking froze a moving sun.** Restarting the sweep whenever `HasSunChanged()` fired is
correct for a sun that moves once and wrong for one that moves every frame: a sun rotator re-armed the
cursor before it could advance, and the sweep **never got past slice 2 of 128** — measured over 120
frames of rotation, leaving 126 slices holding whatever the last completed sweep left. The shadow was
effectively frozen with a flickering sliver at the volume edge. Two changes fix it:

- An in-flight sweep is **never restarted**; a sun move during one sets `_sunVisRestartQueued` and the
  next sweep starts the moment this one lands. A moving sun then converges continuously (measured:
  cursor advances 2/frame, wraps, restarts) with at most one sweep of lag.
- The sun direction is **latched at sweep start** (`_sunVisDir`) rather than read per chunk. Marching
  the front and back of the volume against different sun directions would give a sheared shadow rather
  than a merely stale one.

**The sweep is visible.** A full re-march is ~64 frames at 128³, and it advances as a plane along Z,
so a sun move (or a re-bake) shows shadow arriving progressively across the scene over about a second.
That is the trade the chunking makes, and it is the right one against a device reset — but it is a
user-visible behaviour, not just an implementation detail. Options if it matters: double-buffer the
texture (no sweep, one sweep of latency, 2× shadow memory), drop the shadow resolution (64³ is 8
chunks), or make the rays cheaper — [P7](#p7--hi-res-dda-in-the-solve)'s two-level march over
`_OccupancyTraversal` would shorten this pass as well as the solve.

**A near-geometry gate made it slower, twice.** Only texels within a couple of cells of a surface can
ever be sampled, so skipping the rest looks like a large win. Per-cell (radius 2, 125 taps) took the
64³ sweep from 81 ms to 135 ms; at 4×4×4 block granularity it was still 110 ms. **In an interior
almost every texel is near a wall**, so the gate rejects little and costs everything it touches. Only
the sealed-block skip (two word loads) survives. The cost is inherent, and chunking is what makes it
safe.

#### The normal-offset bias was cut 4x by this phase [fixed 2026-08-24]

Found by fs, who pointed a camera at an upper Sponza wall that stands in open sun and saw it covered
in soft dark blobs. It is the most serious defect this plan introduced, it shipped through P6's own
acceptance, and it is what the "dapple" above actually was.

**`_BgiShadowNormalOffset` is stated in SHADOW TEXELS, and P6 made a shadow texel 4x smaller.** Before
P6 the sun-visibility texture was at the lighting grid, so the Bootstrap scene's serialized `0.5`
meant 0.5 x 0.68 m = **0.34 m** of bias. After P6 it means 0.5 x 0.169 m = **0.085 m**. Nobody
touched the value; the phase changed what it meant. The *code* default is 1.0 and was always fine —
only a scene that had serialized a tuned value was affected, which is why it survived every A/B in
this document (they all ran from that same scene, so both sides had it).

**Why half a texel is not enough, and never was.** The tooltip claimed 0.5 was "the geometrically
correct value: the shading point lies on a solid/air boundary, so half a texel lands at the first air
texel's centre". True for a POINT sample. The tap is **trilinear**, and its footprint still reaches a
full texel back into the solid layer — which does not hold a neutral value. `CSSunVisibility` marches
solid texels too, from jittered origins inside their own geometry, so they store an arbitrary partial
coverage: **0.25 on the wall this was found on**. Blending a varying fraction of 0.25 into a fully lit
surface is textbook shadow acne, and because the fraction depends on where the surface sits inside its
texel it appears as soft MOTTLING rather than a uniform darkening.

**Measured, sunlit wall region, fraction reading below 100/255:**

| `_shadowNormalOffset` | 0.25 | 0.5 | 0.6 | 0.7 | 0.8 | 0.9 | **1.0** | 1.5 | 2.0 |
|---|---|---|---|---|---|---|---|---|---|
| spuriously dark | 99.9% | **91.0%** | 79.6% | 36.6% | 13.0% | 3.8% | **3.2%** | 2.2% | 2.2% |

The knee is just under 1.0 and the curve is flat past it — exactly where "clear a whole texel" says it
should be. `_bakedShadowSharpness` 1.5 (also serialized in the scene) made it worse, 91.0% → 94.7%:
the sharpening re-centres on 0.5 and steepens, so it amplifies precisely the mid-grey the acne
produces. Sample count is irrelevant: 1 / 4 / 16 give 100.0 / 94.7 / 100.0%.

**The reference said so all along.** `ShadowMode.Sdf` renders that wall FULLY WHITE — flat, lit,
no structure. Two implementations, and this time they disagreed over an entire wall. P6's
"92.2% binary agreement" was measured at poses where the defect happened not to show.

**Fixes, both shipped:**

- **`Range` floored at 1.0** (was 0.25) and the published value clamped to it, so a stale sub-1 value
  migrates automatically rather than needing every scene re-tuned. Verified: with the scene still
  serializing `0.5`, the published global reads 1.0 and the wall goes from **94.7% → 3.6%** dark.
  Tooltip rewritten — 1.0 is the minimum that reconstructs correctly, not a taste setting.
- **The bias now clears a whole texel on the DOMINANT axis** (`normal / max|normal|`), the same
  construction the Fast GI tap already uses and for the same reason: `normal * shadowVox` moves less
  than one texel on *every* axis for any normal that is not axis aligned, and the shadow grid is
  anisotropic (0.169 x 0.117 x 0.267 m on Sponza, a 2.3x spread). **This is a latent bug, not this
  defect** — the wall in question is axis-aligned, so the fix is a measured no-op there (byte-identical
  across the whole sweep, confirmed against a deliberate canary that proved the rebuild path was
  live). It is in because a 45-degree wall would have needed 1.41x the setting to be safe.

**Effect on P6's own numbers**, Atrium wide: the dappling on the sun patch is gone, binary agreement
with the SDF is unchanged (92.2% → 92.4%), and `Sdf → Baked` goes from 1.18% to 2.24% of pixels off
by >15%. That last one is the honest cost: a bias always trades acne for shadows detaching slightly
from their casters, and the per-pixel SDF has no bias to trade.

#### The bug that made this look like a triumph

The first measurement said **+42% mean luminance and shadows gone**, which read as a spectacular
improvement if you wanted one. `_BgiShadowGrid` was bound to the compute shaders but never published
as a **fragment global**, so the read divided by zero, every `uvw` went out of range, and the tap
returned its "outside the field → lit" default. The whole baked shadow vanished with no error
anywhere. Hard-constraints item "new fragment globals must be bound in every variant that declares
them" — worth re-reading as *the failure is silent and flattering*.

**Risk — PC/Cube only.** Cube's alpha slabs resolve which face's visibility applies at a sub-voxel
wall (24.4% / 26.3% of shell cells contested). A scalar texture loses that; the bet is that resolution
substitutes for it. **On Single there is nothing to lose** — it already stores one alpha per voxel. So
verify on Cube at a fixed pose before collapsing the slabs, and if it fails, keep per-slab alpha on
the PC path or fall back to storing a signed distance to the shadow boundary, which is single-valued
inside a wall.

## Sponza [measured 2026-08-23]

Everything in P0–P6 above was measured on Playground. This section re-takes it on Sponza, the scene
the original normal and leak defects were found in. **It does not repeat the null checks** — those
are properties of the change, not of the scene — only the numbers a scene can move.

**Setup.** Bootstrap + Sponza only (`sceneCount == 2`). `grid` 32, `occGrid` 128, `shadowGrid` 128,
`thickenWalls` off, `samplesPerFrame` 10, `maxSamples` 500, exposure pinned at `_ExposureLinear`
1.148698, sun-visibility sweep drained before every capture. Poses from
[verifying-changes.md](verifying-changes.md#sponza-ab-captures).

**One field, so "coarse" is unambiguous here.** `HasCoarse` is false — Sponza has a single volume and
no coarse cascade. Every number below is the fine field, and "coarse" can only mean the 32³ lighting
grid. That removes the trap described in [Consequences](#consequences-worth-noting) and makes Sponza
the better scene to quote.

**The harness holds.** Two consecutive full re-bake + cold-cleared re-solve cycles are
**byte-identical** (mean luminance 127.669, Atrium wide). The bit-exact instrument from
[P0](#p0--baseline-and-harness) is not Playground-specific.

### P3 — containment fails by one cell, and that cell is not a missed occluder

| check | Playground | **Sponza** |
|---|---|---|
| low-res solid | — | 11,940 / 32,768 = **36.44%** |
| hi-res solid @128³ | 5.13% | **11.10%** |
| hi-res-only | 403 | **1,130** |
| **low-res-only** | **0** | **1** |
| traversal mip == OR-downsample | yes | yes (13,069 both) |

The failing cell is deterministic — the same cell on three consecutive bakes, and the same from the
runtime raster and the disk asset (which differ by exactly 1 hi-res bit in 232,701).

**It is a low-res over-report, not a hi-res miss.** Cell `(18,4,13)`, world `(1.688, -5.369, -2.668)`:

| | |
|---|---|
| hi-res cells solid *inside* it | **0 / 64** |
| hi-res shell one cell past its **+X** face | **16 / 16 — a complete solid plane** |
| all five other faces | 0 / 16 |

An axis-aligned wall lies exactly on the cell's `+X` boundary (`x = 2.0257`, which is also hi-res
column 76, the first column of coarse cell 19). The 32³ raster marks cell 18 *and* cell 19; the 128³
raster marks only 19. The wall is present in the hi-res field, one cell over, so
**[P7](#p7--hi-res-dda-in-the-solve) skips no occluder** — the acceptance criterion's stated failure
mode does not apply. Read `low-res-only` as "0, or every exception explained", not as a bare count.

**Two false leads, both worth keeping.**

- *"It closes at 256³, so it is a sampling-density artifact."* It does close at 256³ — but at 256³ the
  disk asset (`occGrid` 128) fails `BakeAssetValid`'s `a.occGrid >= OccGrid` gate and the whole field
  falls back to the **runtime raster**. The sweep changed the code path and the resolution at once.
  Held at `occGrid` 128 and compared disk against runtime, the gap is present in both.
- *"Pass 0 and pass 1 rasterize at different resolutions, so `SAMPLE_TEXTURE2D` picks different mips
  and the alpha cutoff disagrees."* Plausible — pass 0 renders into a `Grid`×`Grid` target and pass 1
  into `OccGrid`×`OccGrid`, and pass 0's comment says it *relies* on that for free albedo averaging.
  Tested by giving pass 1 a `SAMPLE_TEXTURE2D_BIAS` of `BGI_OCC_GRID_LOG2 - BGI_GRID_LOG2` so it
  selects pass 0's mip: **no change, still exactly one low-res-only cell**. Reverted. The mip
  difference is real, but it is not this.

### P4 — the scene the defect was found in now measures zero misses

[Appendix A](#appendix-a--measured-evidence)'s 2026-08-16 Sponza audit found **5,693 thin voxels**,
with `TWOSIDED` missing **232** of them. Re-audited against the same definition (solid cell with air
on both sides along some axis, low-res grid):

| | 2026-08-16 | **now** |
|---|---|---|
| solid cells | — | 11,940 |
| thin on ≥1 axis | 5,693 | **5,693** (X 3,128, Y 1,684, Z 899; >1 axis 18) |
| flagged `TWOSIDED` | 5,461 | **5,693** |
| **misses** | **232** | **0** |
| **false positives** | 0 | **0** |

The thin population reproduces to the cell, which is independent evidence the audit measures the same
thing it did then. The misses are gone.

### P5 — the leak is much larger here than on Playground, and Cube does not rescue it

`DebugView.GiSolidWeight`: the fraction of each pixel's GI trilinear footprint landing on solid cells.
Background is 0.0–2.2% of pixels at these poses, so it does not distort the bins.

| footprint on solid | Playground fine, Single | Playground fine, Cube | **Sponza Atrium**, Single | **Sponza Atrium**, Cube | **Sponza Upper**, Single | **Sponza Curtain**, Single |
|---|---|---|---|---|---|---|
| 0 — cannot leak | 3.6% | **74.8%** | 31.8% | 24.5% | 16.1% | 41.6% |
| 0.00–0.25 | 75.7% | 10.0% | 27.7% | 29.7% | 34.7% | 30.9% |
| 0.25–0.50 | 8.0% | 4.9% | 21.8% | 23.3% | 32.5% | 17.8% |
| 0.50–0.75 | 5.4% | 4.3% | 12.6% | 15.7% | 8.3% | 6.8% |
| 0.75–1.00 | 7.3% | 6.0% | 6.0% | 6.8% | 8.2% | 2.8% |
| **contaminated (≥0.25)** | 20.7% | 15.2% | **40.4%** | **45.8%** | **49.1%** | **27.5%** |

**Playground's Cube result does not generalise.** P5 read Cube's 74.8%-cannot-leak as "the per-axis
tap already does most of a footprint fix's work". On Sponza the same measurement is 24.5%, and Cube's
contaminated band is *larger* than Single's at two of the three poses. 74.8% was a property of a small
closed room, not of Cube.

**How much the existing tap filter already fixes** (`SingleTapFilter` Fast → AxisSnapped, GI-only,
Single — pure read state, so the solved field is held identical):

| | Playground fine | **Atrium wide** | **Curtain close** | **Upper gallery** |
|---|---|---|---|---|
| mean GI luminance | +0.88% | **−3.14%** | **−3.46%** | **−1.04%** |
| pixels changed | 38.05% | 89.58% | 96.85% | 76.86% |
| max channel delta | 26/255 | **177/255** | **183/255** | 138/255 |
| **pixels off by >15% of their own value** | 1.55% | **12.62%** | 3.39% | 9.00% |

Eight times the badly-affected population, and the sign is informative: AxisSnapped is *darker*
everywhere, i.e. the Fast tap is pulling light through walls. Not directly comparable to Appendix A's
"~3.1% of shaded pixels off by >15%" — that was a trilinear-vs-point-tap measurement on a floor strip;
this is a per-pixel relative delta on GI-only, which has no direct light diluting the denominator.

**This does not overturn [P5](#p5--attribute-the-leak-gate)'s gate, but it narrows it.** The residual
is "small" only on Playground. On Sponza the normal-axis term alone moves 12.6% of pixels by >15% —
and it is **already free**: flipping `SingleTapFilter` to `AxisSnapped` costs nothing but read state.
The order of work that falls out is *set the tap filter first, re-measure, then judge P8/P9* — not
"build P8".

### P6 — validated against an independent reference at scale

Playground could only offer "both read 100% shadowed at one pose". Sponza has a large, structured sun
patch, so the two implementations can actually disagree:

| | Atrium wide | Upper gallery |
|---|---|---|
| **Off → Baked** (proves the path is live) | −8.92% mean, 26.11% of pixels, 13.27% off by >15% | −19.72% mean, 28.18%, 17.74% |
| **Sdf → Baked** (independent reference) | **+0.44% mean, 12.95% of pixels, 1.18% off by >15%** | **−1.08%, 12.45%, 1.14%** |
| sun-visibility term, binary agreement | **92.2%** | **92.5%** |

The reference is `ShadowMode.Sdf`, a per-pixel raymarch of `_SdfHires` — a different data structure, a
different algorithm, and **81×56×128 over the 21.61×14.94×34.15 m box (≈0.27 m isotropic)**. The baked
shadow at 128³ is 0.169×0.117×0.267 m, so it is *finer than the reference on X and Y*. That is the
right way round for a validation, and it explains the one visible difference: Baked resolves dappled
shadow from the hanging ivy that the SDF, being a distance field, smooths away.

**~~The dapple is structure, not sampling noise.~~ WRONG — it was SHADOW ACNE. See
[the bias fix](#the-normal-offset-bias-was-cut-4x-by-this-phase-fixed-2026-08-24).** The test was
`_sunShadowSamples` 4 → 16: it moved 85.94% of pixels but by at most 52/255, and only **0.07%** by
more than 15%. That is a correct result and it does rule out *jitter noise* — but "not noise" is not
"real geometry", and the third possibility never got tested. The dapple was the trilinear footprint
reaching back into the solid layer, which is systematic, so of course more samples did not move it.
Straight out of [verifying-changes.md](verifying-changes.md): a green result confirms the invariant it
tested, not the one you wanted.

**Moving-sun chunking re-verified here**, where a full sweep is 64 chunks: the cursor advances 2
slices/frame under a sun rotating every frame, wraps at 128, restarts with a freshly latched
direction, and the latched direction holds constant for the whole sweep.

### Cost

Runtime raster (disk assets detached), both fields, GPU-synced, best of three:

| occGrid | shadowGrid | re-bake (raster + derive) | full sun-vis sweep |
|---|---|---|---|
| 64³ | 64 | ~113 ms | 21 ms / 8 chunks (2.6 ms) |
| 128³ | 128 | ~90–143 ms | 293 ms / 64 chunks (**4.6 ms**) |
| 256³ | 128 | ~134 ms | 578 ms / 64 chunks (**9.0 ms**) |

Re-bake is noisy enough (90–143 ms for the same configuration across runs) that only the order of
magnitude is meaningful: **a full Sponza re-bake is well under 200 ms at every resolution**, so the
deferred-gating decision holds here too. The sweep is the number that scales — per-chunk cost doubles
from `occGrid` 128 to 256 because the rays march the occupancy grid, which is
[P7](#p7--hi-res-dda-in-the-solve)'s case restated as a measurement.

Solve step (`samplesPerFrame` 10, one field): **12.50 ms**. `Camera.main` at 2560×1440: **25.05 ms**.

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

### Done [2026-08-24] — flat hi-res adopted, the hierarchy measured and rejected

`_BgiSolveMarchLevel` selects the level at runtime (`SolveMarchLevel` on the updater: `Coarse`,
`Flat`, `TwoLevel`), so a level-to-level A/B is a uniform write rather than a recompile. The branch is
uniform across the dispatch and sits outside the march loop; `Coarse` timings are unchanged, which is
the measurement that it costs nothing. **`Flat` is the default.** All of this is Sponza, `giResolution`
32, `occGrid` 128, AMD Radeon iGPU / D3D12.

**The origin cell stays exempt at LIGHTING resolution.** The coarse march steps off its origin cell so
a surface voxel does not occlude its own rays. Lifted naively to hi-res, that exemption shrinks to one
0.17 m cell and a gather ray leaving the centre of a solid 0.68 m wall voxel hits its own geometry on
step one — every surface goes black. `MarchOccupancyHiHit` therefore skips any hit whose parent
lighting cell is the origin cell.

**A hi-res hit can land in a lighting cell that has no lighting.** 1,130 of Sponza's 13,069 occupied
cells are hi-res-only, so `_Surface` and `_Radiance` hold nothing there. `GatherRayRadiance` returns
the ambient floor for those: the ray is genuinely blocked — that part is the new information — but the
occluder's outgoing radiance is not something this phase can produce.

#### The nulls

| check | result |
|---|---|
| `Coarse` vs the pre-P7 shader | **byte-identical** |
| `Flat` vs `TwoLevel` | **byte-identical** — the traversal level skips nothing it should not |

#### What it costs, and the thing that was hiding in the solve

The first cost measurement said `Flat` was 18.6 ms/frame at `samplesPerFrame` 1 against 10.2 ms
coarse. Decomposing it by `_sunShadowSamples` (4 → 1, a uniform, no recompile) put **11.4 of those
18.6 ms in the sun rays, not the gather** — and sun rays do not scale with `samplesPerFrame`, so they
were 74% of the *pre-P7* solve too.

**CSGather's air-probe sun ray had no consumer at all.** It existed so `CSBlur` could mirror the
scalar into the sun-visibility texture. [P6](#p6--fine-shadow-texture) replaced that with
`CSSunVisibility` and removed the blur's sun-vis write, so `CSBlur` now writes 0 into that `w` channel
unconditionally: every one of those rays was marched, packed, and overwritten. Nothing else read it —
`BgiGatheredIrradiance`'s and `BgiOmniIrradiance`'s `sunOut` are write-only at every call site. It is
gone, along with the Cube branch's six-load `BgiOmniIrradiance` call that existed only to fetch it.

**Required to be byte-identical, and is** — at `Centre` and at `Supersampled` 4, against the pre-P7
shader. That is the whole argument that it was dead.

| `samplesPerFrame` 1 | before | after removal |
|---|---|---|
| Coarse | 10.17 ms | **2.65 ms** |
| Flat | 18.57 ms | **10.40 ms** |

So hi-res GI now costs **10.4 ms/frame against the 10.2 ms the solve cost before this phase started** —
the dead rays paid for the resolution. At `samplesPerFrame` 10 it is 35.7 ms against 7.3 ms coarse.

`CSInject`'s sun ray was left supersampled. It is alive — it scales the direct term the voxel bounces —
and dropping it to a single centre ray measured **4.90% brighter** overall (96% of pixels, max 15/255).
A centre ray reads "lit" too often near a shadow boundary, so the area fraction is doing real work even
though its value is collapsed into one radiance number.

#### The two-level march is slower, at every ratio

Byte-identical to flat and consistently slower. The ratio sweep says why, and says it cleanly:

| occ/grid ratio | occGrid | coarse | flat | two-level | two/flat |
|---|---|---|---|---|---|
| 2 | 64 | 11.8 ms | 23.3 ms | 39.7 ms | **1.71x** |
| 4 | 128 | 16.4 ms | 50.4 ms | 67.3 ms | **1.33x** |
| 8 | 256 | 10.9 ms | 104.5 ms | 112.4 ms | **1.08x** |

The penalty shrinks monotonically as the ratio grows — more hi-res cells skipped per empty lighting
cell, exactly the mechanism the hierarchy is for — but it never reaches 1.0, and extrapolates to break
even somewhere around ratio 16 (`occGrid` 512), which is out of budget.

Two facts explain it. **The flat march is step-bound, not memory-bound**: it scales 23.3 → 50.4 →
104.5 for 64 → 128 → 256, which is linear in resolution, so there is no memory latency for a
hierarchy to hide. And **Sponza's traversal level is 39.9% solid**, so there is very little empty space
to skip; every occupied lighting cell instead costs a descent — a nested loop, in a wave whose rays all
go different directions.

Hoisting the descent's setup (the six divides, derived from the outer walk's `inv` instead) and adding
a fully-solid-block early hit changed nothing measurable. The cost is structural.

**Kept rather than deleted**, defaulted off: it is byte-identical, the branch is free, and this is one
AMD iGPU. A discrete GPU with different cache behaviour is a different measurement, and the enum is
how someone takes it.

#### That it is the right answer, not just a brighter one

GI-only at Atrium wide, coarse march against the flat march at three occupancy resolutions:

| occGrid | vs the coarse march | vs the previous resolution |
|---|---|---|
| 64 | +18.08% | — |
| 128 | +19.55% | **+1.25%** |
| 256 | +19.45% | **−0.08%** |

It **converges**. The 32³ march was wrong by ~19%, and 128³ is already at the resolution-independent
answer. A brightening that kept growing with resolution would be a bias; one that stops is the
sub-voxel occluder being resolved. The direction is predicted too: 0.68 m voxels over-occlude a 0.22 m
curtain, so removing that over-occlusion lets light through.

Per pose, GI-only, coarse → flat: Atrium wide +18.0%, Upper gallery +14.4% (49.8% of pixels off by
>15%), Curtain close +8.8%.

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

### Built and measured [2026-08-24] — the hard acceptance passes, the blockiness one does not

Implemented as specified and shipped **behind `BGI_TAP_SNAP_INPLANE`, off by default**
(`BufferGiUpdater.InPlaneSnap`). `CSBuildNeighbourMask` bakes the 7-bit mask into a per-field R8_UInt
`Texture3D` at the lighting grid; `BgiSnapContaminatedAxes` point-Loads it and snaps the two in-plane
coordinates. Its own keyword group rather than a third `SingleTapFilter` state, because it composes
with both filters and with Cube. `BgiTapSolidWeight` mirrors the snapped placement, so the analysis
view keeps describing the read the fragment actually performed. All figures Sponza, Atrium wide and
Curtain close, `giResolution` 32, `occGrid` 128, [P7](#p7--hi-res-dda-in-the-solve) `Flat`.

**Seven bits, not six**, because which two the tap consults is a runtime decision: trilinear on axis
`a` blends `c0 = floor(t - 0.5)` with `c0 + 1`, and below the cell centre that pair is
`(this - 1, this)` while above it is `(this, this + 1)`. The tap needs its own cell's solidity as well
as both neighbours'.

**The mask is exact.** Read back and compared against the occupancy bitfield cell by cell:
**0 mismatches in 32,768**. Worth stating plainly, because everything below is a rejection, and a
rejection is only worth anything if the thing rejected was built correctly.

#### The hard acceptance — passes

> Byte-identical on pixels whose kernel does not span a wall.

Population defined as `DebugView.GiSolidWeight > 0` measured with the snap OFF, then snap on/off
compared per pixel:

| | Fast, Atrium | Fast, Curtain | AxisSnapped, Atrium | AxisSnapped, Curtain |
|---|---|---|---|---|
| population (footprint > 0) | 68.2% | 58.4% | 72.6% | 85.2% |
| changed pixels | 38.4% | 29.6% | 59.9% | 68.1% |
| of those, inside the population | **99.99%** | **100.00%** | **100.00%** | **100.00%** |
| **violations** | 19 | 12 | 21 | 29 |

19–29 pixels of 921,600 (**0.002%**), every one of them **max delta 1/255**. That is the 8-bit
quantisation of the `GiSolidWeight` readout used to define the population — a footprint below 1/255
reads as 0 without being 0 — not the snap reaching outside it.

**And it is free.** `Camera.main` at 2560x1440: Fast 23.74 → 23.97 ms, AxisSnapped 23.79 → 23.74 ms.
Within noise either way, so cost is not the objection.

#### The blockiness acceptance — fails, the same way the previous two attempts did

Signed diff, GI-only, AxisSnapped + snap at Atrium wide: **33.66% of pixels brighter, 8.13% darker,
15.41% off by >15% of their own value**. The image shows what those numbers are: voxel-aligned
rectangles checkerboarding the atrium floor, blocky bands up the arch soffits, stair-stepped edges
across the columns. It is cell-scale structure, at the lighting grid's 0.68 x 0.47 x 1.07 m.

**The plan's "engages where it is a no-op" argument is true and does not save it.** The position IS
continuous across a cell centre, which is the crossing the argument is about: at `t = ci + 0.5` the
below-branch's snap target and the above-branch's are both `ci + 0.5`, whichever neighbour is solid.
What the argument does not cover is the boundary between two *different cells*: the gate is a per-cell
bitmask, so two adjacent cells with different neighbour patterns snap differently — one moves its
sample by up to half a cell, the next does not move at all — and that seam runs along every cell
boundary where the pattern changes. Continuity within a cell was never the problem; the discontinuity
is between cells, and no amount of care inside one cell removes it.

**Direction of the change is itself a finding.** The snap is overwhelmingly *brighter* (33.7% against
8.1%), where `AxisSnapped` on the normal axis measured uniformly *darker*. So the in-plane
contamination on Sponza is mostly the footprint picking up **dark** shell cells at wall/floor
junctions, not light bleeding through a wall. "The leak" is the wrong name for this half of it.

**A residual 0.175% of pixels go from lit to near-black** (1,611 of 921,600) — degenerate cases the
two fixes below do not cover.

#### Two bugs found on the way, both worth keeping

**Snapping onto a solid cell paints black rectangles.** The first version resolved "both cells of the
pair are solid" to `c0 + 0.5` — "prefer the near side" — which puts the entire trilinear weight on a
cell that is itself solid, and an undilated solid cell holds BLACK. Hard black rectangles appeared in
the atrium floor and around the column bases. **When neither cell is a clean read there is nothing to
snap to**; the tap must stay continuous. Only snap when *exactly one* of the pair is solid.

**A snap can push the tap out of the field, and out of the field means black, not clamped.** Both tap
paths treat an out-of-range `uvw` as no data — the Fast path returns black outright, the axis-snapped
one drops the tap and renormalises. High in the atrium `c0 + 1.5` reached past the last cell centre.
Clamped to `[0.5, GRID - 0.5]`, the same clamp the Cube path already applies to its slab-local Z.
(Measured after the fact: this one changed nothing on these poses — the first bug was producing all
the visible black — but it is a real out-of-range read either way.)

**`mask == 0` is not the early-out it looks like.** It was meant to make the gate nearly free in open
space. On Sponza **79.8% of cells have a non-zero mask**, so it fires on one cell in five. Same shape
as [P6](#p6--fine-shadow-texture)'s near-geometry gate: in an interior, almost everything is near a
wall. It costs nothing here only because the whole gate costs nothing.

#### Verdict

**Kept, keyword-gated, defaulted off.** The acceptance the plan wrote for it is "blockiness judged by
eye", and the toggle is how someone does that on their own scene — this is one scene on one GPU, and
the two implementation bugs above are fixed, so what remains to judge is the technique rather than a
half-built version of it. But on Sponza it reads worse than it fixes, and the honest summary is that
**the third attempt at snapping fails for the same reason as the first two**, now with the mechanism
identified: a per-cell binary gate cannot be continuous *between* cells.

**Not measured:** the near-wall leak column against a point-tap reference. That needs a point-tap
build, which still does not exist in the tree — the same gap [P5](#p5--attribute-the-leak-gate) hit.

**What this leaves for the in-plane term.** Nothing cheap. The residual is real (P5 measured it, and
the population here is 60–85% of pixels) but every technique that snaps or shrinks the footprint pays
for it with cell-scale structure. [P8](#p8--fine-irradiance-texture-contingent) is the remaining
candidate precisely because it moves the discontinuity into the *field*, where the fragment can still
take one continuous trilinear tap - at up to 201 MB.

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
**2,451 return a real but misleading direction** with nothing flagging them. `TWOSIDED` missed 232 of
5,693, no false positives — **fixed in [P4](#p4--derive-_surface-from-hi-res-occupancy)** (Playground:
37 fine + 67 coarse misses → 0, false positives still 0). **Re-audited on Sponza 2026-08-23: the same
5,693 thin voxels, 232 misses → 0** — see [Sponza](#sponza-measured-2026-08-23).


**Bake determinism [measured 2026-08-23].** The voxelize raster is deterministic on Playground: three
consecutive reimport + re-bake + re-solve captures are byte-identical in both Single and Cube **once
the dynamic fields are cleared first**. The run-to-run variation previously attributed to the raster
was the solve carrying stale `_Irradiance` buckets between runs — see
[P0](#p0--baseline-and-harness).

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
is in-plane, and the [2026-08-23 Sponza population](#sponza-measured-2026-08-23) confirms it: Cube is
not leak-free there the way it looked on Playground.

**Fragment-side DDA cost [measured 2026-08-22].** The read-side visibility PoC: **4.85 → 26.5 ms at
1080p (+21.7 ms)**, 8 corner taps × up to 12 steps, no early-out.

**AO scale.** `BGI_AO_RADIUS` 2 at `giResolution` 32 is ±1.36 m on Sponza's X axis — not contact
occlusion. Sub-voxel geometry gets `ao = 1` (no solid tap on the face plane).

## Appendix B — Ruled out

**Gradient normal over the hi-res sub-cells [measured 2026-08-23].** P4's first table row. Same
integral as the shipping gradient — `sum d/|d|^2` toward air over a ball three coarse cells wide —
sampled `ratio` times finer, on the fine field of Playground at occGrid 128 (6,980 solid cells).

It looks like a clear win right up to the last check:

| | coarse gradient (`_OccupancyThick`) | hi-res gradient |
|---|---|---|
| cancels (returns 0) | 678 cells, **9.7%** | **0** |
| mean angle between them | — | 31.7°, over 45° on 19.5% |
| **sign flips** (`dot < 0`) | — | **816 cells, 12.9%** |
| of those, on cells that are genuinely thin | — | 727 (sign is ambiguous there anyway) |
| of those, on **solid-backed** walls | — | **89 — the gradient points INTO the wall** |

Never cancelling is not the same as being right. The 89 non-thin flips are unambiguous failures: on a
solid-backed wall there is one correct outward direction and the hi-res gradient gets it backwards.
The cause is the one `_OccupancyThick` already exists for — **the hi-res raster is a hollow shell
too**. A wall's interior is not marked solid at *any* resolution, because the rasterizer marks
surface-crossed cells, so the finer gradient sees air outside and air inside and picks whichever
cavity is nearer. Fixing it needs a hi-res `_OccupancyThick`, i.e. a per-hi-res-cell triangle normal
to back the shell along — which the bit-only raster deliberately does not store.

Note also that 12.9% sits next to the 11.1% "inverted gradient" figure this appendix already records
as circular. The direction of the argument is opposite here — the reference is the well-defined
quantity and the candidate is what flips — but the coincidence is worth remembering.

**Re-aligning a two-sided cell's normal onto its thin axis [measured 2026-08-23].** The natural
companion to P4's `TWOSIDED` rework: the flag promises `CSInject` a second face at `-normal`, so snap
the normal onto the thin axis and the back face is guaranteed to point into air. **104 of 12,644 solid
Playground cells re-align** (67 coarse, 37 fine) — and the fixed-pose capture moves by **4.94% mean
luminance, up to 40/255**, over a broad smooth region of floor and wall.

Taking the sign from the triangle normal instead of from the gradient gives **4.94% as well**, so the
sign is not what does it: re-aligning at all is. A hundred cells swinging the whole scene 5% means
those cells carry a large share of the shell texels the fragment's tap reads — it is not evidence that
either image is more correct, and there is no reference here that can say which is. Left out; the flag
fix stands on its own measurement.

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
