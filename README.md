# Voxel Lighting for Unity

A voxel-based lighting system playground for Unity. Goal is a performant lighting system with no dependency on Unity lightmaps or shadowmaps.

## Repository layout

* `package/` contains the Unity Package Manager package content (`package.json`, `Editor/`, `Runtime/`, `Samples~/`).
* `project-demo/` contains the Unity project used for local validation and WebGL builds.
* `project-vr-demo/` contains the Quest 3 (VR) demo project.
* The repository root contains docs, changelog, and CI/release configuration.

## Demo samples setup

If you want the demo project to use the in-repo package samples directly, or you want to modify the package samples in place from Windows, run `scripts\setup-samples-link.cmd` manually. The script creates this link:

* `project-demo/Assets/_Samples` -> `../../package/Samples~`

`mklink` requires Windows symlink permission. If the script fails, enable Windows Developer Mode or run the shell as Administrator and try again.

## Features

### Lights

Three kinds, each set up differently.

| | Set up with | Light types | Per-frame cost |
| --- | --- | --- | --- |
| Sun | nothing — URP's main light | Directional | one light |
| Baked | set the Light's Mode to Baked or Mixed | Point | ~free |
| Realtime | nothing — collected automatically | Point, Spot | 4 point + 4 spot max |

All three light the scene directly *and* bounce into the GI.

**Sun.** Nothing to set up: the package shades with URP's main light. URP uses the Lighting window's *Sun Source* when that light is a visible directional light, and otherwise falls back to the brightest directional light in the scene. If you swap suns (a day/night scenario switch), set `RenderSettings.sun` and make sure the new light is enabled — otherwise you can end up lit by a different light than the one you assigned. Note that `RenderSettings.sun` belongs to the **active scene**, so with additive scenes it changes with `SceneManager.SetActiveScene`.

**Baked lights** are stamped into the voxelization as emissive voxels, which is why they cost almost nothing per frame. Set a *point* light's Mode to **Baked** or **Mixed** and put it inside a GI volume — that is the whole setup.

The volume's `Voxel Lights` list is derived, not authored: while you are editing, the package keeps it in step with the scene and adds the component where it is missing, so a light you add, move, retype or delete is picked up on its own. (It has to be a serialized list because `Light.lightmapBakeType` does not exist in a player, so which lights are Baked must be decided while authoring.) The **Bake Voxelization To Disk** button writes exactly the same list — after this it is only needed to write the disk asset.

Spot lights are never baked: a cone cannot be expressed by a voxel that radiates equally in all directions. A spot marked Baked or Mixed is lit as a realtime light instead, and the package says so. A directional light is the sun.

**Baked does not mean permanent.** A baked light is still switchable at runtime:

* Disable the **Light** to switch that one off.
* Disable the **Voxel Lights component** to switch the whole list off at once — a group switch for a room's lamps, or a fireplace that burns out.

That works because only the *emission* is re-stamped from the lights that are currently on; the albedo always comes from every listed light, so the voxelization itself never moves. Switching costs one small dispatch rather than a re-voxelization. It is also why lights stay in the list while switched off instead of being removed from it.

**Emissive material or baked point light?** A baked point light is stamped into exactly **one voxel** — the one containing its position. Its radiance is scaled by that voxel's area (`pi * colour / area`), so it stays equally bright whatever the field resolution, but its *shape* is always a single cell. An emissive material is voxelized across every voxel its surface covers, so it is the right tool as soon as the emitter has real size. Both write the same emission channel and mix freely.

| Want | Use | Switchable at runtime |
| --- | --- | --- |
| A compact lamp you turn on and off | Point light, Mode Baked or Mixed | Yes |
| A glowing surface with real extent — a fire bed, a neon strip, a window | Material with `_EMISSION` enabled | No, it is baked into the voxelization |

The point light buys switchability, the material buys shape. Worth knowing:

* Do not cluster point lights to fake a bigger source. Lights landing in the same voxel are summed, which is identical to one brighter light in that voxel.
* Emission is stored log-encoded with a ceiling of 1024. A smaller voxel gets a proportionally brighter radiance, so a bright light in a fine field can clamp — with 0.17 x 0.12 x 0.24 m voxels that starts around intensity 14.
* A baked light ignores the Light's Range: an emissive voxel keeps falling off with distance forever, so it reaches a little further than the same light does as a realtime light.

**Realtime lights** need no setup at all. Put a point or spot light in the scene and it lights: the package collects every active point/spot light automatically, minus the ones a `Voxel Lights` list has already baked. That subtraction is why a light can never be counted twice — a baked light simply cannot arrive on the realtime path.

Up to 4 point and 4 spot lights are published at a time. When a scene holds more, the ones that matter most to the viewer win — ranked by `intensity / distance²` from the camera — so walking toward a torch lights you and the one behind you gives up its slot. There is no list to curate and no priority to author.

* Lights spawned at runtime are picked up within a quarter second. Call `LocalLights.Refresh()` if one has to light in the very frame it appears.
* A **spot** light set to Baked or Mixed is lit as a realtime light instead, and the package warns: only point lights can be baked.

### Realtime shadows

Static objects can cast shadows on dynamic objects. Reacts in realtime to lighting changes.
Dynamic objects can only *receive* shadows.

3 modes:

* SDF Shadows: (Accuracy) Ray marching from every pixel. Always used for local lights.
* Occlusion direction field (1bit): (Performance) 8-64 directions in one texture, hard blocky voxel shadows.
* Occlusion direction field (8bit): (Performance + Accuracy) 4 directions per texture, interpolated smoother voxel shadows. Supports 256 directions (64 textures).

### Global illumination / Indirect lighting

Global illumination (GI) for static meshes in a GI Volume. Dynamic objects can only *receive* GI.

* Path tracing: Ray trace from voxel towards light each frame. Requires longer temporal accumulation for stable results.

## Platform notes

* Runtime GI in this package depends on compute shaders and 3D textures.
* Web builds are expected to run with the WebGPU graphics backend. The demo project pins WebGPU as the only graphics API for BuildTarget.WebGL in its Project Settings (`project-demo/ProjectSettings/ProjectSettings.asset`).
* Voxelizing in runtime requires static batching to be disabled.

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
