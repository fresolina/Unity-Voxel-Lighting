# project-vr-demo

Thin VR development/test harness for the `com.lotecsoftware.voxel-lighting` package. It is a
*sibling* of `project-demo` (the desktop demo), not a fork: both consume the package via
`file:../../package` and read the same sample content from `package/Samples~`. Only VR-specific
configuration (XR Plug-in Management, an XR rig, VR scene wiring) lives here.

The XR dependency (`com.unity.xr.interaction.toolkit`) belongs to **this project only** — it must
never be added to `package/package.json`, or every consumer of the package would inherit it.
