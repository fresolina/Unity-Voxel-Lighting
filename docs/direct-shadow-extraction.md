# Extracting the direct sun shadow — implementation plan

**Status: S1 done and verified 2026-08-28. Written 2026-08-28 against `try-fix-light-bleed` (P0–P7 of
[decoupling-field-resolutions.md](decoupling-field-resolutions.md) landed).**

| phase | status |
|---|---|
| [S0 Baseline](#s0--baseline-and-harness) | **done** — folded into S1's verification (the before-capture is HEAD, re-taken through the identical cycle) |
| [S1 Shader seam](#s1--move-the-shader-seam) | **done 2026-08-28** — byte-identical render, and a positive control proves the moved code is what runs |
| [S2 Kernel](#s2--move-the-kernel) | not started |
| [S3 Driver](#s3--move-the-driver) | not started |
| [S4 Provider + Unity shadowmap](#s4--provider-interface--unity-shadowmap) | not started |
| [S5 Per-pixel raymarch](#s5--per-pixel-raymarch) | not started |

**Goal.** Make the main-light sun shadow a **self-contained subsystem with a swappable backend**, so
that a Unity shadowmap, the current baked voxel-visibility volume, and a future **per-pixel raymarch**
are three implementations of one seam rather than three special cases wired through the GI updater.

**Why now.** The seam already exists in the shader — `BgiSampleSunShadow` is the *sole* authority for
the buffer-GI main-light shadow and already dispatches five modes — but the C# that feeds it is spread
through a 2,660-line GI component, and the kernel that produces its data lives inside the solve's
compute file. Nothing about the sun shadow depends on the solve any more (P6 moved it out); the
coupling that remains is **file layout, not data flow**. That is the cheap moment to cut.

Reference: [buffer-gi-architecture.md](buffer-gi-architecture.md). Cost model:
[optimization-guidelines.md](optimization-guidelines.md). Measurement:
[verifying-changes.md](verifying-changes.md).

- [What is tangled today](#what-is-tangled-today)
- [What is already clean](#what-is-already-clean)
- [Target architecture](#target-architecture)
- [Decisions taken](#decisions-taken)
- [How to work this plan](#how-to-work-this-plan)
- **Extraction (nulls)** — [S0 Baseline](#s0--baseline-and-harness) · [S1 Shader seam](#s1--move-the-shader-seam) · [S2 Kernel](#s2--move-the-kernel) · [S3 Driver](#s3--move-the-driver)
- **New backends** — [S4 Provider interface + Unity shadowmap](#s4--provider-interface--unity-shadowmap) · [S5 Per-pixel raymarch](#s5--per-pixel-raymarch)
- [The resource problem, and why S5 needs its own phase](#the-resource-problem-and-why-s5-needs-its-own-phase)
- [Inline in the fragment, or a screen-space pass?](#inline-in-the-fragment-or-a-screen-space-pass)
- [What must NOT move](#what-must-not-move)
- [Hard constraints checklist](#hard-constraints-checklist)
- [Risk register](#risk-register)

---

## What is tangled today

Everything below is sun-shadow-only and currently lives inside the GI component, the GI solve, or the
GI read header. This is the extraction surface — nothing else moves.

| where | what |
|---|---|
| `BufferGiUpdater` state | `_sunVisTex`, `_sunVisTexCoarse`, `_sunVisKernel`, `_sunVisSliceBase`, `_sunVisRestartQueued`, `_sunVisDir`, `_sunVisDirty`, `_shadowGrid`/`_shadowGridLog2` |
| `BufferGiUpdater` settings | `_fineShadow`, `_coarseShadow`, `_sunShadowSamples`, `_bakedShadowSharpness`, `_shadowNormalOffset` + their tooltips and properties |
| `BufferGiUpdater` driver | `StartSunVisibilitySweep`, `DispatchSunVisibilityChunk`, `DispatchSunVisibilityField`, `CreateSunVisTexture`, `SunVisibilityPending`, `SunVisTexelsPerDispatch`, `HasSunChanged`/`StoreSunState` |
| `BufferGiUpdater` publish | `_BgiShadowGrid`, `_BgiShadowGridLog2`, `_BgiShadowMode{Fine,Coarse}`, `_BgiShadowSharpness`, `_BgiShadowNormalOffset`, `_BgiSunVisTex{,Coarse}`, `PublishOcclusionSources` |
| `BufferGiSolve.compute` | the `CSSunVisibility` kernel, `_BgiSunVisTexWrite`, `_BgiShadowSliceBase`, `_BgiShadowTexSamples`, and `MarchOccupancyHiFrom` |
| `BufferGiRead.hlsl` | `_BgiSunVisTex{,Coarse}` + samplers, the shadow uniforms, `BgiSampleShadowTexture`, `BgiSampleSunShadow` |
| `VoxelLit.shader` | the one call site, under `GI_VOXEL_BUFFER` |

Line anchors as of this writing: updater [472–502](../package/Runtime/Gi/BufferGiUpdater.cs),
[200–245](../package/Runtime/Gi/BufferGiUpdater.cs), [2392–2466](../package/Runtime/Gi/BufferGiUpdater.cs),
[1370–1455](../package/Runtime/Gi/BufferGiUpdater.cs); solve
[811](../package/Shaders/Compute/BufferGiSolve.compute) and
[274](../package/Shaders/Compute/BufferGiSolve.compute); read header
[86–131](../package/ShaderLibrary/BufferGiRead.hlsl) and
[190–272](../package/ShaderLibrary/BufferGiRead.hlsl);
[VoxelLit.shader:210](../package/Shaders/VoxelLit.shader).

## What is already clean

Three findings from reading the current code, all of which make the extraction cheaper than it looks.
**None of these are assumptions — they are what the code does today.**

**1. `CSSunVisibility` touches zero solve buffers.** Its complete input set is `_OccupancyHi` (plus
`_OccFieldWordOffset` and the field's origin/size/grid uniforms), `_DirectLightDir`,
`_BgiShadowTexSamples` and `_BgiShadowSliceBase`; its complete output is `_BgiSunVisTexWrite`. It
reads no `_Radiance`, no `_Irradiance`, no `_Material`, no `_Surface`. It shares `BufferGiSolve.compute`
with the solve **only** because `MarchOccupancyHiFrom` happens to be declared there. That one function
is the whole entanglement, and it belongs in a header regardless.

**2. The shader already has exactly one seam.** `BgiSampleSunShadow(worldPos, normal, lightDir)` →
`half`. Five modes resolve inside it with no fall-through, and `VoxelLit` calls it once. A sixth mode
is an `else if` and a C# enum entry. There is no second path to keep in sync.

**3. The solve's own sun term is already separate.** `_BgiInjectSunSamples` (CSInject — the direct term
a solid voxel *bounces*) was split from `_BgiShadowTexSamples` (the displayed shadow) on 2026-08-28,
because they run at different resolutions and answer different questions. So extracting the displayed
shadow **cannot** change the GI, and the bit-identical acceptance criteria below are reachable.

## Target architecture

```
                    ┌─────────────────────────────────────────┐
  geometry ────────►│ BufferGiUpdater                         │
  (bake/voxelize)   │   owns _OccupancyHi, the field boxes,   │
                    │   the solve, the irradiance mirrors     │
                    └───────────────┬─────────────────────────┘
                                    │ IVoxelOccupancySource
                                    │ (buffer + grid + per-field box)
                                    ▼
                    ┌─────────────────────────────────────────┐
                    │ VoxelSunShadow  [new component]         │
                    │   settings, sun-change detection,       │
                    │   publishes _BgiShadowMode* + the       │
                    │   backend's own globals                 │
                    └───────────────┬─────────────────────────┘
                                    │ ISunShadowProvider
        ┌───────────────────┬───────┴───────┬────────────────────┐
        ▼                   ▼               ▼                    ▼
  BakedVolume         UnityShadowmap    Raymarch            (Sdf / OccField /
  CSSunVisibility     URP cascades      per-pixel or         Bitmask — kept as
  → R16 Texture3D     → URP keywords    screen-space pass    legacy modes)
```

**The dependency arrow points one way.** The shadow component reads occupancy and field bounds from
the GI updater; the updater never calls back into it. That is what makes a "no GI, shadows only"
configuration possible later, and it is the direction to preserve at every phase.

## Decisions taken

| | decision |
|---|---|
| **Seam** | `BgiSampleSunShadow` stays the single shader entry point, keeps its signature, and keeps its name until S5 (renaming it is churn that hides real diffs in the null phases). |
| **Mode selection** | Stays an **`int` uniform**, not a keyword. `VoxelLit` already carries `GI_* × TONEMAP_* × snap`; a shadow keyword multiplies that set for a scalar branch. Take a keyword **only** where a mode's register footprint genuinely differs — the existing precedent is `BGI_TAP_AXIS_SNAPPED`, which is a whole second tap implementation. |
| **Per-field modes** | Kept. Fine and coarse pick independently, as today. A raymarch that only covers the fine box must remain composable with a baked coarse field. |
| **Unity shadowmap mode** | Resolved at the **`.shader` entry point**, never in `ShaderLibrary/`. The library is engine-agnostic and `BufferGiCommonCanary.compute` fails the build the moment it is not — that guard is deliberate and must not be "fixed". |
| **Component vs pass** | **Both, and in that order.** S0–S4 make it a component. S5 decides inline-vs-pass on measurement, not in advance — see [that section](#inline-in-the-fragment-or-a-screen-space-pass). |
| **Backwards compatibility** | Existing scenes must keep working with no manual step. The new component is auto-added / auto-resolved from `BufferGiUpdater`, and the serialized settings migrate. |
| **Occupancy ownership** | Stays with `BufferGiUpdater`. The shadow reads it; it does not bake its own. Two rasters of the same geometry is the failure mode this whole branch spent P3–P7 avoiding. |

## How to work this plan

1. **S1–S3 are nulls and land first.** They move code and change no rendered output. A backend built
   on unverified plumbing cannot be attributed.
2. **Every null phase's acceptance is a byte-identical capture**, per
   [verifying-changes.md](verifying-changes.md) — and for S1/S2 also a **bit-identical sun-visibility
   texture readback**, which is the tighter test and catches what a screenshot cannot.
3. **Read the [hard constraints checklist](#hard-constraints-checklist) at the start of each phase.**
   Several of its items are traps this codebase has already fallen into.
4. `AsyncGPUReadback.GetData<T>()` on a 3D texture returns **one layer**. Loop `req.layerCount`. This
   has produced a confident, wrong measurement twice on this branch.
5. **Discard the first timing after any keyword or kernel change** — first-run compilation has
   corrupted a measurement three times here, once by three orders of magnitude.

---

# Extraction (nulls)

## S0 — Baseline and harness

Capture the reference set before touching anything.

- Playground + Sponza, the A/B poses in [verifying-changes.md](verifying-changes.md).
- Both `RadianceDirections` modes; `ShadowMode` = `Baked` on both fields.
- **Also dump the raw sun-visibility volume** (both fields) to disk — `_sunVisTex` and
  `_sunVisTexCoarse`, all layers. This is the acceptance oracle for S1–S3, and it is far more
  sensitive than a framebuffer diff: a shadow change of a fraction of a texel is invisible on screen
  and obvious in the volume.

**Acceptance:** baseline stored, and the capture re-taken immediately reproduces it byte for byte
(the raster-nondeterminism check — thickened Sponza is *not* byte-stable, so run the baseline
unthickened, or accept a tolerance and record what it is).

## S1 — Move the shader seam

Create `ShaderLibrary/VoxelSunShadow.hlsl` and move into it, unchanged:

- `_BgiSunVisTex`, `_BgiSunVisTexCoarse` + samplers
- `_BgiShadowModeFine/Coarse`, `_BgiShadowSharpness`, `_BgiShadowNormalOffset`
- `BgiSampleShadowTexture`, `BgiSampleSunShadow`

`BufferGiRead.hlsl` includes it (guarded, exactly as it already includes `VoxelSdfShadows.hlsl` and
`VoxelOcclusion.hlsl`), so no call site changes and no global appears or disappears from any variant.

**Watch:** `BgiSampleSunShadow` calls `BgiSelectField`, which lives in `BufferGiRead.hlsl` and is
shared with the GI gather. Either move `BgiSelectField` down into `BufferGiField.hlsl` (it is pure
field geometry and arguably belongs there anyway) or have the new header include the read header.
**Prefer moving it down** — the shadow header including the GI read header points the dependency the
wrong way and would block a future GI-less configuration.

**Acceptance:** null. Byte-identical captures in both modes; the compiled variant list unchanged;
`BufferGiCommonCanary.compute` still compiles.

### Done [2026-08-28] — byte-identical, and the null is backed by a positive control

`ShaderLibrary/VoxelSunShadow.hlsl` now owns the two R16 sun-vis textures and their samplers, the four
shadow uniforms, `BgiSampleShadowTexture` and `BgiSampleSunShadow`, plus the two shadow-source includes
(`VoxelSdfShadows.hlsl`, `VoxelOcclusion.hlsl`). `BufferGiRead.hlsl` includes it from inside the same
`GI_VOXEL_BUFFER` guard the shadow globals were already declared under, so **no variant's declared
global set moved**. `BgiSelectField` went down into `BufferGiField.hlsl`, with the two coarse-field
uniforms it reads. Net: `BufferGiRead.hlsl` −139 lines, `BufferGiField.hlsl` +21, one new 173-line file.
**Nothing was edited on the way across** — the moved text is byte-for-byte what it was.

Verified on Bootstrap + Playground + Sponza loaded together, `ShadowMode.Baked` on both fields
(so the moved code is on the live path), sharpness 2, shadow grid 128, exposure pinned, the full
reimport → re-bake → cleared re-solve → 500/500 cycle per capture:

| capture | code | raw MD5 | mean luminance |
|---|---|---|---|
| before | `HEAD` | `CCED4C32…` | 163.816623 |
| after ×2 | S1 | `CCED4C32…` | 163.816623 |
| **control** | S1 + `return 0.5h` inside the moved `BgiSampleShadowTexture` | **`C40CBB2E…`** | **182.295493** |
| after (control removed) | S1 | `CCED4C32…` | 163.816623 |

The control row is the point. A byte-identical pair proves nothing on its own — it is equally the
signature of a reimport that never took, which is a failure this project has actually shipped into.
Breaking the function *in its new file* moved the image; restoring it brought the hash back. So the
render is executing the moved code, and executing it identically.

**Two things worth carrying forward.**

**The canary was covering `BufferGiRead.hlsl` in name only.** It includes that header, but every line
of it sits behind `#if defined(GI_VOXEL_BUFFER)`, which the canary does not define — so the include
contributed no code and the guarantee was empty for the whole GI read layer. `VoxelSunShadow.hlsl` is
now included *and called* there (`BgiSampleSunShadow` feeds the sink), so the engine-agnostic guarantee
genuinely covers it. **Whatever S2/S3 add, add a call, not just an include.** Extending the same
treatment to the rest of `BufferGiRead.hlsl` is out of scope here but is a real gap.

**`_BgiCoarseOrigin` / `_BgiCoarseVoxelSize` are now declared in the compute shaders too**, because
`BufferGiField.hlsl` is included by the solve, the bake, the voxelize raster and the canary, and they
had to follow `BgiSelectField` down. Nothing there reads them, so they are dead-stripped; all four
compile with zero messages and the render is unchanged. It is a widening of the declaration surface
by two loose `float3` uniforms — acceptable because the constraint that matters (and the one that
fails WebGPU pipeline creation) is about **resources**, not scalars. Had these been textures or
buffers, the right answer would have been a separate cascade header instead.

**Not covered by this phase's evidence:** `RadianceDirections.Cube` was not re-captured, because the
moved code has no mode dependence at all — `BgiSampleShadowTexture` reads no direction stride and the
header says so explicitly ("No Cube branch"). And the `Sdf` / `OcclusionField` / `Bitmask` modes were
not exercised at runtime; they moved verbatim and now compile inside the canary, which is what this
phase can honestly claim for them.

## S2 — Move the kernel

1. Move `MarchOccupancyHiFrom` (and `BGI_MAX_OCC_RAY_STEPS`) from `BufferGiSolve.compute` into
   `ShaderLibrary/BufferGiVoxelData.hlsl`, next to the block helpers it already uses. It is pure
   occupancy traversal with no solve dependency.
2. Create `Shaders/Compute/VoxelSunShadow.compute` with `CSSunVisibility` moved verbatim, plus its
   three uniforms (`_BgiShadowTexSamples`, `_BgiShadowSliceBase`, `_DirectLightDir`) and
   `_BgiSunVisTexWrite`.
3. Delete the kernel and its `#pragma kernel` from `BufferGiSolve.compute`. Every remaining
   `_BgiShadowTexSamples` reference in the solve should now be gone — if one is left, it was a bug.

**Acceptance:** null, and the strong form — **the dumped sun-visibility volumes are bit-identical to
S0**, both fields, at 1/4/16 samples. Same dispatch chunking, same jitter sequence, same slice base.

**Trap:** a compute buffer that is declared but not bound **silently drops the dispatch** and leaves
the previous texture contents in place, which looks byte-identical on screen. This exact failure cost
a day on P7. Verify by counting nonzero texels in the readback, not by looking at the render.

## S3 — Move the driver

New `VoxelSunShadow : MonoBehaviour` (`[ExecuteAlways]`, `[DisallowMultipleComponent]`, sibling of
`BufferGiUpdater`), owning:

- the two R16 volumes and their allocation/release
- `_shadowGrid` and its log2
- the sweep state machine (`_sunVisSliceBase`, `_sunVisRestartQueued`, `_sunVisDir`, `_sunVisDirty`)
  and `SunVisTexelsPerDispatch`
- the five serialized settings, their properties and their tooltips
- the `_BgiShadowMode*` / sharpness / offset publish, and `PublishOcclusionSources`

It reads geometry through a narrow interface on the updater — occupancy buffer, `_BgiOccGrid`, and
each field's origin/size/word-offset. **Do not** hand it the updater's whole surface.

**Sun-change detection stays shared.** A sun move restarts the *solve* as well as the shadow sweep, so
there are two consumers and there must stay exactly one detector. Keep `HasSunChanged`/`StoreSunState`
on the updater and have the shadow component be told, rather than polling `RenderSettings.sun` itself
— two detectors will drift, and the drift shows up as a shadow that is one frame stale only sometimes.

**Ordering.** The chunk dispatch currently runs inside `BufferGiUpdater.Update` *after* the field
uniforms are bound. Preserve that: either `[DefaultExecutionOrder]` the new component after the
updater, or have the updater call `_sunShadow.Tick()` explicitly. **Explicit is better** — execution
order attributes are invisible at the call site, and this ordering is load-bearing (the kernel's
bounds test reads grid constants that the updater publishes).

**Migration.** `Reset`/`OnValidate` on `BufferGiUpdater` auto-adds the component and copies the five
settings across once; the old fields then become hidden/`[Obsolete]` and are removed a release later.
A scene that is opened and saved must not lose its shadow settings.

**Also carry across:** the `OnValidate → _sunVisDirty = true` line. The inspector writes the backing
field directly and bypasses the property setters, so this is the *only* place that catches a settings
change the sun-visibility pass needs to see. It was a real, user-reported bug ("changing the sample
count does nothing"); it will regress the moment the settings move if it is not moved with them.

**Acceptance:** null, same as S2, plus: toggling every one of the five settings in the inspector still
re-marches the volume (the regression above), and a scene saved before S3 loads after it with
identical settings.

---

# New backends

## S4 — Provider interface + Unity shadowmap

Introduce `ISunShadowProvider` with the minimum the existing implementations actually need:

```csharp
bool Enabled { get; }   // can this provider answer right now
void Bind();            // publish this provider's globals for the frame
void Tick();            // amortized work, if any (the baked provider's sweep)
void Invalidate();      // sun moved / settings changed / geometry re-baked
```

Move today's baked path behind it as `BakedVolumeSunShadow`. Then add `UnityShadowmapSunShadow` as
mode 5:

- C# side: nothing but enabling URP main-light shadows and letting the pipeline do its work.
- Shader side: **in `VoxelLit.shader`, not in the library.**
  `#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE`,
  `TransformWorldToShadowCoord` + `MainLightRealtimeShadow`, resolved into a `half` and passed to
  `GetMainDirectLightingShadow` on the same line the voxel shadow uses today.

**This is the phase that proves the seam**, and it is deliberately the *easy* backend — URP does all
the work, so any friction found here is friction in the abstraction, not in the technique.

**The keyword cost is real and must be measured, not assumed.** `_MAIN_LIGHT_SHADOWS*` multiplies
`VoxelLit`'s variant set by 3. If that is unacceptable, the fallback is a separate SubShader/material
variant rather than a dynamic branch — URP's shadow sampling is not something to put behind a uniform
branch in a fragment kernel that is already register-pressured.

**Acceptance:** switching a field between `Baked` and `UnityShadowmap` changes only the shadow term;
`Baked` remains bit-identical to S3; the variant count is recorded before and after.

## S5 — Per-pixel raymarch

The target. A shadow ray per pixel against `_OccupancyHi`, replacing the volume tap for the fine field.

**The observation that motivates it:** at 128³ the occupancy grid holds ~2.1M cells against ~2.1M
pixels at 1920×1080. The baked volume spends a ray per *texel* and then reconstructs a sub-texel edge
with `_BgiShadowSharpness`; a per-pixel march spends a ray per *pixel* and needs no reconstruction at
all. Everything in the architecture doc's `_BgiShadowSharpness` note — the ±½-texel clamp, the lattice
faceting, the rounded convex corners — is an artifact of reconstructing from a lattice, and **none of
it applies to a per-pixel march.**

Prerequisites, in order:

1. **The resource problem** — see below. This is the whole difficulty, and why S5 is its own phase.
2. `MarchOccupancyHiFrom` must be callable from a fragment. Structurally it already is once S2 puts it
   in a header; it needs an occupancy accessor that is not a `StructuredBuffer`.
3. A per-pixel step budget. `BGI_MAX_OCC_RAY_STEPS = 3 × 128 = 384` worst case is a compute budget,
   not a fragment one. Needs a cap, and an honest answer for what a truncated ray returns — **lit**,
   matching every other "no information" case in this path, so a mistake shows as a missing shadow
   rather than as black blotches.
4. Soft shadows: a cone/multi-ray variant, or accept hard shadows. Decide before building, because it
   changes the cost by an integer factor.

**Acceptance criteria** (write them down *before* measuring — they are the point of the phase):

- **Sharper than `Baked` at any sharpness setting**, with no lattice faceting at the Sponza pose where
  faceting is visible today.
- **Frame cost stated at both poses**, against the ~0.4% the SDF prototype cost, and against the 0 ms
  the baked tap costs at draw time. That last comparison must be made explicit: the baked path moves
  cost off the frame and onto the sun move; the raymarch moves it back on.
- **No re-march on a sun move** — the whole amortization machinery becomes unnecessary for this mode,
  and that is a real simplification to claim.

## The resource problem, and why S5 needs its own phase

**The shipping fragment declares no `StructuredBuffer` at all.** That is not incidental — it is stated
and enforced in `BufferGiRead.hlsl`: WebGPU validates every declared global against the bound pipeline
layout and **fails pipeline creation** for a variant that declares a buffer while it is unbound.
`_Occupancy` reappears in the fragment only under `BGI_DEBUG_VIEWS`. A per-pixel march of
`_OccupancyHi` as a buffer would put a `StructuredBuffer` back into every shipped variant.

Options, cheapest first:

| option | cost at 128³/field | notes |
|---|---|---|
| **Mirror the bitfield into `Texture3D<uint>`**, x packed 32:1 (4×128×128 R32_UInt) | **256 KB** | One `Load` per 32 cells along X. Written once at bake, alongside the existing upload. **Recommended.** |
| `Texture3D<uint>` R8_UInt, one byte per cell | 2 MB | Simplest addressing, 8× the memory, and the DDA reads a byte per step instead of amortizing 32. |
| Keep the `StructuredBuffer`, gate the raymarch behind a keyword | 0 | A keyword-dependent global set is exactly what fails WebGPU pipeline creation. **Do not.** |
| Screen-space pass in compute (see below) | 0 extra | The buffer is legal in a compute pass; the constraint is a *fragment*-stage one. |

Note the last row: **routing the march through a compute pass sidesteps the resource problem
entirely.** That is an argument for the pass form beyond its performance argument, and it is worth
weighing before committing to the texture mirror.

Also note the **layout mismatch**. `_OccupancyHi` is stored in 4×4×4 blocks of two uints — good for the
"is there anything near me" whole-block query, awkward for a flat DDA that wants `[x/32, y, z]`. If the
mirror is built, **build it flat**, and keep the block layout for the block queries. Two layouts of the
same bits is acceptable; two *rasters* would not be.

## Inline in the fragment, or a screen-space pass?

"Its own component or pass" is the right framing, and the answer differs by phase. S0–S4 are
unambiguously a component. S5 has a genuine fork:

| | inline in `VoxelLit` | screen-space pass |
|---|---|---|
| cost scales with | **overdraw** — every fragment marches, including ones later covered | **pixels, exactly once** |
| needs | nothing new | depth prepass / depth texture, worldPos reconstruction, an R8 mask, a `ScriptableRendererFeature` |
| transparents | works | does not — they need the inline path as a fallback |
| denoising / temporal reuse | not possible | **natural** — the mask is a screen-space buffer with history |
| resource problem | must be solved (texture mirror) | **sidestepped** — compute can read the buffer |
| couples to | nothing | the render pipeline (a URP renderer feature; another for any other SRP) |

**Recommendation: build inline first, measure, then decide.** Inline is the smaller change, it is the
only form that works for transparents, and its cost is the number the pass form has to beat. Building
the pass first commits to a `ScriptableRendererFeature` and a depth prepass before there is evidence
either is needed. If the inline march measures within budget at the target resolution, the pass may
never be worth its plumbing.

If it *is* built, the pass is the natural home for a denoiser — and that, not raw throughput, is likely
to be the deciding argument, since a per-pixel hard shadow off a voxel grid will alias.

## What must NOT move

- **`_BgiInjectSunSamples` and `CSInject`'s sun term.** That is the direct light a solid voxel
  *bounces*; it belongs to the solve, runs at the lighting grid, and is deliberately a separate
  setting from the displayed shadow's. Moving it would re-merge the two things 2026-08-28 split.
- **`GetGeometricGate` and the local-light shadows.** `GetShadow(worldPos, lightDir, normal[, dist])`
  serves point/spot lights and the non-buffer-GI main light. Different subsystem, different lifetime;
  it stays in `VoxelDirectLighting.hlsl`.
- **The occupancy bake.** The shadow reads `_OccupancyHi`; it must never rasterize its own.
- **The engine boundary.** No URP include below `ShaderLibrary/`, ever.

## Hard constraints checklist

Re-read at the start of each phase.

- [ ] **No `StructuredBuffer` in a shipped fragment variant** (WebGPU pipeline-layout validation).
- [ ] **No keyword-dependent global set** — a global declared in one variant and not another fails
      WebGPU pipeline creation. Declare unconditionally, bind unconditionally.
- [ ] **`ShaderLibrary/` stays engine-agnostic.** `BufferGiCommonCanary.compute` enforces it; never
      "fix" a canary failure by adding an include to the canary.
- [ ] **An unbound buffer drops its dispatch silently** and leaves the previous texture contents.
      Verify compute output by reading it back and counting, never by looking at the render.
- [ ] **`GetData<T>()` on a 3D texture returns one layer.** Loop `layerCount`.
- [ ] **Discard the first timing after a keyword/kernel change.**
- [ ] **`OnValidate` must set the dirty flag** — the inspector bypasses property setters.
- [ ] **Never set the GI resolution above 64 on this machine** — 128 TDRs the device.

## Risk register

| risk | likelihood | mitigation |
|---|---|---|
| S3's settings migration silently drops a scene's shadow settings | medium | Migrate in `Reset`/`OnValidate` *and* verify by loading a pre-S3 scene in the acceptance step. Keep the old fields serialized (hidden) for one release. |
| The `VoxelLit` variant count becomes the real cost of S4 | **high** | Record it before and after. If URP's shadow keywords are unacceptable, fall back to a separate material variant rather than a dynamic branch. |
| The texture mirror in S5 becomes a second source of truth for geometry | medium | Build it in the same place and at the same time as the existing occupancy upload, from the same bits. Never from a second raster. |
| The per-pixel march aliases badly enough to need the pass form anyway | medium | Accept it — the inline measurement is not wasted; it is the baseline the pass has to beat, and it stays as the transparent path. |
| Sun-change detection ends up duplicated and drifts | low | Two consumers, one detector: keep `HasSunChanged` on the updater and have the shadow component be told. |
| The extraction lands and no new backend is ever built | low | S4 is deliberately the cheap backend and is scheduled immediately after the nulls, precisely so the seam is exercised by a second implementation before the plan is called finished. |
