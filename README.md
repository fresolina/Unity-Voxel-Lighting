# Voxel Lighting for Unity

A voxel-based lighting system playground for Unity. Goal is a performant lighting system with no dependency on Unity lightmaps or shadowmaps.

## Repository layout

* `package/` contains the Unity Package Manager package content (`package.json`, `Editor/`, `Runtime/`, `Samples~/`).
* `project-demo/` contains the Unity project used for local validation and WebGL builds.
* The repository root contains docs, changelog, and CI/release configuration.

## Demo samples setup

If you want the demo project to use the in-repo package samples directly, or you want to modify the package samples in place from Windows, run `scripts\setup-samples-link.cmd` manually. The script creates this link:

* `project-demo/Assets/_Samples` -> `../../package/Samples~`

`mklink` requires Windows symlink permission. If the script fails, enable Windows Developer Mode or run the shell as Administrator and try again.

## Features

### Realtime shadows

Static objects can cast shadows on dynamic objects. Reacts in realtime to lighting changes.
Dynamic objects can only receive shadows.

3 modes:
* SDF Shadows: (Accuracy) Ray marching from every pixel. Always used for additional lights.
* Occlusion direction field (1bit): (Performance) 8-64 directions in one texture, hard blocky voxel shadows.
* Occlusion direction field (8bit): (Performance + Accuracy) 4 directions per texture, interpolated smoother voxel shadows. Supports 256 directions (64 textures).

### Global illumination

Global illumination (GI) from static and dynamic objects, 2 modes.
* Path tracing: Ray trace from voxel towards light each frame. Requires longer temporal accumulation for stable results.
* Light propagation volume: Simpler more performant approximation where light propagates through a voxel grid.

## Platform notes

* Runtime GI in this package depends on compute shaders and 3D textures.
* Web builds are expected to run with the WebGPU graphics backend. The sample web build script explicitly requests WebGPU for BuildTarget.WebGL.

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
