# Copilot instructions (Unity-Voxel-Lighting)

This repo is a **Unity Package Manager (UPM) package** (`com.lotecsoftware.voxel-lighting`) in the directory `package` and a demo project in the directory `project-demo`. Open project-demo in Unity to run the demo. The package code is meant to be imported via Package Manager.

- **Unity**: 6000.5.3f1 (Unity 6.5), URP.
- **Targets**: Quest 3 (VR) and a WebGL demo build — performance and shader-variant hygiene matter.

**To compile**: dotnet build project-demo/project-demo.slnx

**To run and verify**: see [docs/verifying-changes.md](docs/verifying-changes.md) — driving the editor headlessly, the reimport/re-bake/re-solve cycle a GI comparison requires, the Sponza A/B camera poses, and the diff/benchmark recipes.

**How Buffer GI works**: [docs/buffer-gi-architecture.md](docs/buffer-gi-architecture.md) — the reference for the buffers, every bit in `_Material` / `_Surface`, the two directional strides, the bake and solve pass order, and the fragment read paths.

**Optimization guidelines**: [docs/optimization-guidelines.md](docs/optimization-guidelines.md) — 44 guidelines with their evidence, numbered `category.item` so additions do not renumber the rest, plus the handful that are hard constraints: which tier work belongs in, waves vs threads, cache-line and layout math, Texture3D vs StructuredBuffer, what hardware trilinear really costs, and how to measure without fooling yourself.

**Implementation plan**: [docs/decoupling-field-resolutions.md](docs/decoupling-field-resolutions.md) — phased plan to decouple occupancy / surface / material / irradiance / shadow resolutions. Decoupling phases (P0-P3) first, fixes (P4-P9) after; per-phase acceptance criteria, a hard-constraints checklist, and the measured evidence and rejected approaches in appendices. Not started.

## Repository layout
- `package/Runtime/` — split by concern:
  - `Core/` — the always-on plumbing: `LightingManager` (tracks the active `VoxelVolume` and publishes its shader globals — nothing else), `VoxelVolume` (+ its `VoxelVolume.All` self-registry), `VoxelSdfField` (holds the baked hi-res SDF; the volume publishes it as `_SdfHires`), `MeshBounds`, the `GiMethodSelector` GI-method helper, the SDF shadow feature + its config, local-light publishing.
  - `Gi/` — the buffer GI: `BufferGiUpdater`, `BufferGiFields` (per-level provider), `AutoExposure`, bake-asset types.
  - `Occlusion/` — occlusion bitmask / field structures and queries (shadow sources).
  - `Baking/` — editor/runtime bakers; implementation classes are named `*Bake` / `*Baker` and write baked assets scene-adjacent.
  - `Debug/`, `Shaders/`, `Utils/`, `Assets/`.
- `package/Editor/` — custom inspectors + bake buttons (e.g. `BufferGiUpdaterEditor`).
- `package/Samples~/Usage samples/` — the demo content (scenes, UI, sample scripts).
- `project-demo/` — the Unity project that consumes the package.

**Samples symlink (important):** `project-demo/Assets/_Samples` is a symlink to `../../package/Samples~`. Edit sample scripts/UI/scenes through the **package** path (`package/Samples~/...`), not the demo copy.

## Big picture (voxel lighting architecture)
- For performance reasons, we use a voxel-space lookup structure for as much as possible.
- We are targeting Quest 3, so performance is important.
- **Components are features**: enabling a component turns its feature on. `LightingManager` stays minimal (which volume is active + publish its globals); every other capability (GI method, shadow source, AO, local lights) is its own component that reads the active volume via `LightingManager.Instance.Volume`.
- The shared lighting singletons are being consolidated into the **Bootstrap** scene as persistent objects, with each level carrying only its per-level data (`VoxelVolume` + occlusion binders, coarse `MeshBounds`, `BufferGiFields`, spawn point). This migration is in progress — check the scenes for how far it has got.

## Runtime UI (samples)
- UI Toolkit, driven by **`PanelRenderer`** (Unity 6.5+, replaces `UIDocument`). There is no synchronous `rootVisualElement`: register `RegisterUIReloadCallback` and cache the root handed to the callback. `visualTreeAsset` / `panelSettings` are the same names; sort order is the inherited `Renderer.sortingOrder` (int).
- Panels data-bind with `[CreateProperty]` getters/setters + `INotifyBindablePropertyChanged`, plus a per-frame snapshot so inspector edits reflect back into the UI.

## Namespaces & style
- Use namespace `Lotec.Lighting` for runtime code.
- Never name classes with two leading uppercase letters (e.g., use `SdfVolume` not `SDFVolume`), except for enums and structs.
- Never add "private" to private methods or fields; it's implicit in C#.
- Private fields should start with an underscore (_).
- Static fields should start with an "s_" (e.g., `s_myStaticField`).
