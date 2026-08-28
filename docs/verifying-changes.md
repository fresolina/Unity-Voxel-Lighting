# Verifying changes

How to drive the editor without touching it, and how to get a lighting comparison that
actually means something. Most of this exists because the obvious version of each procedure
produces a green result while measuring nothing.

- [Driving the editor from the command line](#driving-the-editor-from-the-command-line)
- [Verifying a BufferGI change](#verifying-a-buffergi-change)
- [Sponza A/B captures](#sponza-ab-captures)
- [Diffing and benchmarking](#diffing-and-benchmarking)
- [Troubleshooting](#troubleshooting)

## Driving the editor from the command line

The editor is controlled through the `com.unity.pipeline` UPM package (see
`project-demo/Packages/manifest.json`). It exposes an HTTP server, not a CLI binary — there is
nothing on `PATH`. Talk to it with `curl`.

- **Port and bearer token:** `project-demo/Library/Pipeline/.unity-pipeline-port`, fields `port`
  and `evalToken`. Both change on every editor launch — re-read the file, never cache them.
- **Endpoint:** `POST http://127.0.0.1:<port>/api/exec` with
  `{"command": ..., "parameters": {...}, "timeout": ...}`.
  The key is **`parameters`**, not `args`. With `args` the parameters are silently ignored and
  required ones are reported missing.
- `GET /api/commands` lists everything available; per-command docs live in the package's
  `Documentation~/commands/*.md`.
- Commonly used: `editor_play` / `editor_stop` / `editor_status`, `capture_game_view` (returns a
  base64 PNG), and `eval` (Roslyn C#, including `UnityEditor.*` and reflection).
- **`import_asset` cannot reach package files.** It is confined to the authoring root under
  `Assets/`, so it returns `success: false` for anything in `package/`. Reimport those from `eval`
  instead:

  ```csharp
  UnityEditor.AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
  ```

The server keeps the editor ticking while it is unfocused, so play mode and compilation proceed
without anyone at the keyboard.

## Verifying a BufferGI change

Three things must be forced, in order. Skipping any of them leaves the comparison silently
meaningless — the screen keeps showing the previous pass's contents and the diff looks clean.

1. **Reimport the changed shader through `AssetDatabase`** (see above — `import_asset` will not do
   it for package paths).
2. **Force a re-bake.** The derive passes only re-run on a bake input change, so a shader edit needs
   a forced re-voxelize: set `_materialBaked = false` on the `BufferGiUpdater`, then call
   `Voxelize()`.
3. **Force a re-solve, from a CLEARED field.** `_continuousGi` is `0`, so the solve stops once the
   ray budget is spent — the log reads `BufferGi solve: idle — ray budget spent` and nothing further
   changes on screen. Restart it by setting `_resetAllFields = true` **and** `_collectedSamples = 0`,
   then poll against `_maxSamples` (500) until converged. Takes roughly two seconds.

**A re-solve is not a re-bake.** Resetting `_collectedSamples` restarts the solve only. A "noise
floor" measured with re-solve alone is not a noise floor, and a baseline taken that way is not a
baseline: re-take it through the identical reimport + re-bake + re-solve cycle and require the two
readings to match before comparing anything against it.

### Clear the dynamic fields, or the capture depends on the previous one

`_collectedSamples = 0` restarts the *accumulation*; it does not clear `_Irradiance`. `CSGather`'s
Cube branch makes that observable: **a bucket that received no ray this frame HOLDS its previous
value** rather than folding in a zero (correct — otherwise it would decay to black), so a bucket that
never receives a ray keeps whatever the *previous run* left in it, forever. The result is a capture
that depends on the run before it.

Measured on Playground, two consecutive full reimport + re-bake + re-solve captures:

| | without `_resetAllFields` | with it |
|---|---|---|
| Cube | mean luminance **119.0 then 127.8** (7% apart) | byte-identical ×3 |
| Single | ±1–3 LSB, ~0.3% mean luminance | byte-identical ×3 |

So set `_resetAllFields = true` before every capture. With it the whole cycle is **exactly
reproducible in both modes** — a null phase can be required to be byte-identical rather than
"within noise", which is a far sharper instrument. Without it the Single drift looks like raster
nondeterminism and the Cube drift swamps any change worth measuring.

**Prove the changed code runs in the captured configuration.** A clean render diff over a path that
never executes verifies nothing — e.g. occlusion-texture changes are invisible in the default
`Baked` shadow mode, and light-attenuation changes are invisible with `_PointLightCount` and
`_SpotLightCount` both `0`. Enable the feature, or introduce a deliberate break that *should* change
the image, before trusting an A/B.

**A/B the two GI read paths** with `BufferGiUpdater.SsboRead` (public setter, takes effect the next
frame). The SSBO path is leak-free and is the reference the texture path is measured against.

**Drive the whole cycle synchronously from one `eval`.** `Update()` is private but is the same entry
the editor pump calls, so invoking it by reflection in a loop runs voxelize, the derive passes and
one solve step per call — no polling, no waiting on the editor to tick, and the capture happens in
the same call. Roughly: set `_materialBaked = false` and `_resetAllFields = true`, zero
`_collectedSamples`, call `Update()` `maxSamples/samplesPerFrame + 8` times, then render
`Camera.main` into a `RenderTexture` and `EncodeToPNG`. A whole A/B pair takes about ten seconds.

**A capture right after a `RadianceDirections` switch is a transient.** The switch reallocates and
re-seeds, and the first capture after it does not match the ones that follow (measured 106.1 against
a settled 107.5 in Single). Discard it, or compare only captures taken the same number of steps
after a switch.

## Sponza A/B captures

Set `Camera.main.transform` directly — nothing in the Sponza scene overrides it; the camera has no
controller on it or its parents.

**Scene geometry:** lower floor `y = -5.81`, upper floor `y = -2.10` (standing height ≈ `y = -4.11`
and `y = -0.40`). The atrium runs along Z (±12); the arcades sit at `x = ±2.1`. The curtains are
0.22 thick in X against a 0.68 X-voxel at `giResolution` 32 — genuinely sub-voxel, which is what
makes them the thin-wall test case.

| Name | Position | Euler | EV | Shows |
|---|---|---|---|---|
| Atrium wide | `(0, -4.11, 8)` | `(0, 180, 0)` | 0.2 | The classic corridor: curtains both sides, arch vaults, columns, carved relief at the far end. Best single overview shot. |
| Curtain close | `(0.2, -4.3, 6.55)` | `(0, 90, 0)` | 2.5 | One red curtain filling frame, lit by indirect only — the sub-voxel thin-wall case in isolation. Needs the raised EV or it reads near-black. |
| Upper gallery | `(0, -0.40, 8)` | `(0, 180, 0)` | 0.2 | Upper floor along the same axis; the `Fabric_Round` banners (0.85 thick vs 0.68 voxel) sit right at the sub-voxel boundary. |

Two more traps, both of which invalidate a pair of captures without any visible symptom:

1. **Load only Bootstrap + Sponza.** A third scene (Playground) loaded alongside them wrecks the
   captures: a large grey panel occludes the atrium and the lighting reads completely differently.
   Check `SceneManager.sceneCount` before trusting any capture.
2. **Pin exposure before the first capture, not between them.** Auto-exposure is on by default and
   is not clamped at these poses, so it adapts to the very change being measured. Set `_auto = false`
   and force both `_exposure` and `_currentEV` on the `ExposureControl`, then verify
   `_ExposureLinear` is identical in both captures. Captures from *different* poses can never be
   compared for brightness.

## Diffing and benchmarking

**Diffing captures.** There is no Python on the development machine; diff the PNGs inside Unity via
`eval` — `Texture2D.LoadImage` on both, per-pixel luminance delta, amplify ~8x, `EncodeToPNG`.
Report mean luminance, delta %, and percent-of-pixels-changed alongside the diff image; the numbers
are far easier to read than the images alone.

**Benchmarking a fragment-side change.** Editor `UnityStats.renderTime` is quantised to ~1 ms and
cannot resolve a GI tap change at 720p. What works: render `Camera.main` into a 2560x1440
`RenderTexture` in a loop (~120 frames) inside a single `eval`, bracketed by a 1-pixel `ReadPixels`
to force GPU sync, timed with a `Stopwatch`. That is stable and repeatable across rounds.

## Troubleshooting

**The debug voxel viewer looks empty.** `BufferGiDebug` draws only the *active* volume's field
(`LightingManager.Volume`), so the usual cause is the wrong room being active — the cubes are drawn
behind a wall, wireframe included (it is depth-tested: `ZTest LEqual`, queue Overlay). To tell that
apart from a real breakage without moving anything, switch the viewer's `field` to `Coarse`: the
coarse box spans both Bootstrap rooms, so if coarse cubes draw, the shader, buffers and
`Graphics.RenderPrimitives` path are all fine and only the placement is wrong. Note that
`_autoSwitchToClosestVolume` can differ between the saved scene and a dirty in-editor one; with it
off, `LightingManager.AdoptFallbackVolume()` locks onto `BufferGiFields.FallbackVolume` and never
re-evaluates.
