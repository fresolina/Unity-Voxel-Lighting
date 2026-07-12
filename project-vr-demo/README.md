# project-vr-demo

Thin VR development/test harness for the `com.lotecsoftware.voxel-lighting` package. It is a
*sibling* of `project-demo` (the desktop demo), not a fork: both consume the package via
`file:../../package` and read the same sample content from `package/Samples~`. Only VR-specific
configuration (XR Plug-in Management, an XR rig, VR scene wiring) lives here.

The XR dependency (`com.unity.xr.interaction.toolkit`) belongs to **this project only** — it must
never be added to `package/package.json`, or every consumer of the package would inherit it.

## First-time setup

These steps need the Unity editor / an elevated shell and can't be scripted headlessly:

1. **Import package samples** via **Package Manager → Voxel Lighting → Samples → Import**.

2. **Open in Unity 6.5** Let Package Manager resolve XRI and its transitive dependencies.

3. **Enable XR**: Project Settings → **XR Plug-in Management** → install and enable your runtime
   loader (OpenXR, or a vendor provider such as Meta). Installing the provider from this screen picks
   a version compatible with the editor, which is why it isn't pinned in `manifest.json`.

4. **Render pipeline (URP)**: this package targets URP. The URP assets + graphics/quality settings
   live in `project-demo` (`Assets/Settings`, `ProjectSettings/GraphicsSettings.asset`,
   `QualitySettings.asset`). Either copy those in, or point this project's Graphics settings at a URP
   asset, so the lighting renders correctly. Bootstrap glue used by the sample scenes
   (`project-demo/Assets/Scripts/RemoteScenes`, Addressables, `InputSystem_Actions`) is likewise not
   duplicated here — copy or symlink what a given sample scene needs.

5. **Add the XR rig + interactable UI**: add an XR Origin with ray interactors, an EventSystem with
   an `XRUIInputModule`, and an `XRUIToolkitManager` in the scene. On each world-space UI panel
   (`LightingController`, `BufferGiDebugUi`) keep the `PanelRenderer` **and** add a **disabled**
   `UIDocument` component plus a `Collider` — XRI 3.4 keys world-space ray interaction off a
   `UIDocument`, and a disabled one satisfies that while `PanelRenderer` does the rendering. Set the
   `Panel Input Configuration`'s **Panel Input Redirection** to **"No input redirection"** and leave
   `XRUIInputModule.bypassUIToolkitEvents` **off**.
