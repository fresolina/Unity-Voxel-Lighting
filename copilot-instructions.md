

# Copilot instructions (Unity-Voxel-Lighting)

This repo is a **Unity Package Manager (UPM) package** (`com.lotecsoftware.lighting`), not a full Unity project. Code is meant to be imported via Package Manager.

## Big picture (voxel lighting architecture)
- For performance reasons, we use a voxel-space lookup structure for as much as possible.
- We are targeting Quest 3, so performance is important.

## Namespaces & style
- Use namespace `Lotec.Lighting` for runtime code.
- Never name classes with two leading uppercase letters (e.g., use `SdfVolume` not `SDFVolume`), except for enums and structs.
- Never add "private" to private methods or fields; it's implicit in C#.
- Private fields should start with an underscore (_).
- Static fields should start with an "s_" (e.g., `s_myStaticField`).
