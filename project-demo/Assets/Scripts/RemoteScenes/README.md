# Remote Scenes (Addressables + Cloudflare)

Loads scenes that are too big to keep in git (e.g. a 180 MB scene) from Cloudflare at runtime.
The scene's built bundle lives in Cloudflare; the device downloads and caches it on first use.

## Files
- `SceneLoader.cs` — Addressables wrapper: query size → download w/ progress → additive load,
  keeps one remote scene loaded at a time. No UI.
- `RemoteSceneMenu.cs` — IMGUI menu with a **Remote Scenes** toggle button that shows the scene
  list, progress bar and Unload button.

## Scene / component setup
1. Add both scripts to one GameObject in the boot scene (RemoteSceneMenu auto-requires the loader).
2. On **SceneLoader**, add an entry per remote scene:
   - **displayName** — label shown in the menu.
   - **address** — the Addressables *address* of the scene (step 4 below).

## Addressables setup (one-time)
1. Install is already done (`com.unity.addressables` in `Packages/manifest.json`).
2. **Window ▸ Asset Management ▸ Addressables ▸ Groups** → *Create Addressables Settings*.
3. Mark the big scene asset **Addressable** (Inspector checkbox), and set its **address** to a stable
   name like `CloudRoom`. Put it in a group whose bundle mode is *Pack Separately* if you like.
4. Move that group to **remote**: in the group's *Content Packing & Loading* schema set
   Build Path = `RemoteBuildPath`, Load Path = `RemoteLoadPath`.

### Cloudflare profile
In **Addressables ▸ Profiles** (or the Groups window ▸ *Profile ▸ Manage Profiles*) set, on the
profile you build with:
- **RemoteBuildPath** = `ServerData/[BuildTarget]`  (default; this folder is git-ignored)
- **RemoteLoadPath**  = `https://<your-domain-or-r2-public-bucket>/[BuildTarget]`

Use one of:
- **Cloudflare R2** with a public bucket / custom domain, or
- any origin behind **Cloudflare CDN**.

Also enable **Build Remote Catalog** (Addressables *Settings* asset) so the catalog is fetched from
Cloudflare too — that lets you update the scene without shipping a new app build.

> Quest 3 / Android: the load URL **must be HTTPS** (cleartext HTTP is blocked by default).
> Cloudflare serves HTTPS out of the box.

## Build & upload
1. **Addressables ▸ Groups ▸ Build ▸ New Build ▸ Default Build Script.**
2. Upload the contents of `ServerData/<BuildTarget>/` (bundles + `catalog_*.json`/`.hash` +
   `settings.json`) to the matching path on Cloudflare so URLs line up with **RemoteLoadPath**.
3. Set the same **CDN cache** on `.bundle` files; bust cache on the catalog when you re-build.

`ServerData/` is git-ignored (see `project-demo/.gitignore`) — only the built output is excluded,
the `AddressableAssetsData/` config **is** committed.

## Runtime behaviour
First load downloads the bundle (progress bar); subsequent loads hit the on-device cache and skip
straight to loading. The loaded scene is set active (its skybox/lighting); Unload removes it.

---

# Bootstrap + self-contained levels (Plan A)

The scene loader is built to run from a small **persistent bootstrap scene** while **level scenes**
(Playground, Sponza, …) load and unload additively on top of it.

## Split of responsibilities
- **Bootstrap scene** (loaded first, never unloaded): the persistent, level-agnostic things —
  player/camera rig, input, the UI (`RemoteSceneMenu`), `SceneLoader`, `PlayerSpawner`, and
  exactly one `EventSystem` + one `AudioListener`. **No lighting components here.**
- **Level scene** (additive): geometry, lights, a `VoxelVolume`, and the **whole lighting stack**
  (`LightingManager`, `BufferGiUpdater`, `LocalLightsPublisher`) plus its bake assets — all wired
  *inside the level* (intra-scene references are fine). Add one `SceneSpawnPoint` where the player
  should appear. Prefab this "lighting rig" so it isn't hand-duplicated per level.

## Why no cross-scene references
Unity can't serialize a reference from one scene's object to another scene's object (only asset
references cross scenes). So nothing is wired from bootstrap → level at edit time; it's resolved at
runtime instead:
- The level's `VoxelVolume` self-registers into `VoxelVolume.All` on load; `LightingManager`
  (per-level, becomes `Instance`) picks it via its default/auto-closest logic, and `BufferGiUpdater`
  warm-switches to it. This already works — no new code.
- The persistent player is placed by `PlayerSpawner`, which listens to `SceneLoader.Loaded`
  and teleports the player to the level's `SceneSpawnPoint`.

## Components added for this
- `SceneLoader` — now also **auto-loads a startup level** on `Start` (`Load Startup Scene` +
  `Startup Scene Index`) and raises `Loaded(Scene, entry)` after each load.
- `SceneSpawnPoint` — marker dropped in each level for the player's spawn pose.
- `PlayerSpawner` — bootstrap-side; repositions the persistent player on `Loaded` (handles
  `CharacterController` / `Rigidbody`).

## Editor steps (one-time)
1. **Make every level Addressable**, including local ones. Put Playground in a group with the
   **Local** build/load path (loads instantly from the player, no Build Settings entry needed) and
   Sponza in the **Remote** group (Cloudflare). Give each a stable address (`Playground`, `Sponza`).
2. **Create `Bootstrap.unity`.** Move the player/camera rig, input, `SceneLoader`,
   `RemoteSceneMenu`, `PlayerSpawner`, `EventSystem`, `AudioListener` here. On `PlayerSpawner` assign
   the player Transform (loader auto-finds). On `SceneLoader` add your level entries and set
   `Startup Scene Index` to the level you want on boot.
3. **Turn each level self-contained.** In Playground/Sponza: remove the player rig, UI, loader and any
   extra `EventSystem`/`AudioListener`; keep/verify the lighting rig (`LightingManager` +
   `BufferGiUpdater` + `LocalLightsPublisher` + `VoxelVolume` + bake assets); add a `SceneSpawnPoint`.
4. **Build Settings ▸ Scenes:** list **only `Bootstrap`** (index 0). Levels load by Addressables
   address, not from this list — keeping Sponza out of it keeps it out of the player build.
5. Press Play on `Bootstrap`: it auto-loads the startup level, the player spawns at its
   `SceneSpawnPoint`, and the **Remote Scenes** menu swaps levels (each swap unloads the previous,
   sets the new one active, re-binds lighting, and respawns the player).

## Notes
- One level at a time: `LightingManager`/`BufferGiUpdater` are singletons, so don't additively stack
  two levels that each carry a lighting rig. The loader enforces this (unloads the previous first).
- Levels stay openable standalone (each has its own lighting) — handy for editing Sponza in isolation.
  Opened alone there's no bootstrap player; add a temporary camera or just open Bootstrap to play.
