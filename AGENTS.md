# Copilot instructions (Unity-Voxel-Lighting)

This repo is a **Unity Package Manager (UPM) package** (`com.lotecsoftware.voxel-lighting`) in the directory `package` and a demo project in the directory `project-demo`. Open project-demo in Unity to run the demo. The package code is meant to be imported via Package Manager.

- **Unity**: 6000.5.3f1 (Unity 6.5), URP.
- **Targets**: Quest 3 (VR) and a WebGL demo build — performance and shader-variant hygiene matter.

**To compile**: dotnet build project-demo/project-demo.slnx

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
- The shared lighting singletons are being consolidated into the **Bootstrap** scene as persistent objects, with each level carrying only its per-level data (`VoxelVolume` + occlusion binders, coarse `MeshBounds`, `BufferGiFields`, spawn point). See the memory notes for the current migration state.

## Runtime UI (samples)
- UI Toolkit, driven by **`PanelRenderer`** (Unity 6.5+, replaces `UIDocument`). There is no synchronous `rootVisualElement`: register `RegisterUIReloadCallback` and cache the root handed to the callback. `visualTreeAsset` / `panelSettings` are the same names; sort order is the inherited `Renderer.sortingOrder` (int).
- Panels data-bind with `[CreateProperty]` getters/setters + `INotifyBindablePropertyChanged`, plus a per-frame snapshot so inspector edits reflect back into the UI.

## Namespaces & style
- Use namespace `Lotec.Lighting` for runtime code.
- Never name classes with two leading uppercase letters (e.g., use `SdfVolume` not `SDFVolume`), except for enums and structs.
- Never add "private" to private methods or fields; it's implicit in C#.
- Private fields should start with an underscore (_).
- Static fields should start with an "s_" (e.g., `s_myStaticField`).
