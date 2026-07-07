using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace Lotec.Demo {
    /// <summary>
    /// One level the loader can open. <see cref="address"/> is the Addressables address of the scene
    /// (the string in the scene asset's "Addressable Name" field). A remote level (e.g. Sponza on
    /// Cloudflare) is referenced by address rather than by AssetReference, since the scene isn't in the
    /// project. Local levels can be Addressable too (a Local-path group) and load instantly.
    /// </summary>
    [Serializable]
    public class RemoteSceneEntry {
        public string displayName = "Scene";
        public string address = "";
        [Tooltip("Unity scene name, used only for the already-loaded check. Leave empty to derive it " +
                 "from the address (file name without path/extension).")]
        public string sceneName = "";
    }

    public enum RemoteSceneState { Idle, CheckingSize, Downloading, Loading, Loaded, Failed }

    /// <summary>
    /// Loads Addressables scenes additively - local or remote (Cloudflare) - one level at a time,
    /// unloading the previous first. If the requested level is already loaded (e.g. left additively
    /// open in the editor, or the current level), it is adopted instead of loaded again, so there's
    /// never a duplicate copy. Pure logic + observable state; the UI (<see cref="RemoteSceneMenu"/>)
    /// reads the public properties and calls <see cref="Load"/> / <see cref="Unload"/>.
    ///
    /// Flow per load: skip if already loaded -> query download size (0 if cached) -> download with
    /// progress -> load additively. First remote load pulls the bundle; later loads hit the cache.
    /// </summary>
    public class SceneLoader : MonoBehaviour {
        [Tooltip("Levels available to load. 'address' is the Addressables address of the scene. " +
                 "Local levels (e.g. Playground) can be Addressable too - just put them in a group " +
                 "with the Local build/load path so they load instantly from the player.")]
        [SerializeField] List<RemoteSceneEntry> _scenes = new();

        [Header("Bootstrap")]
        [Tooltip("Auto-load one level on Start so the bootstrap scene is never left empty.")]
        [SerializeField] bool _loadStartupScene = true;
        [Tooltip("Index into the Scenes list to load on Start when 'Load Startup Scene' is on.")]
        [SerializeField] int _startupSceneIndex;

        public IReadOnlyList<RemoteSceneEntry> Scenes => _scenes;

        /// <summary>Current step of the pending or last operation.</summary>
        public RemoteSceneState State { get; private set; } = RemoteSceneState.Idle;
        /// <summary>Download progress 0..1 while <see cref="State"/> is Downloading.</summary>
        public float Progress { get; private set; }
        /// <summary>Bytes that still need downloading for the pending op (0 when fully cached).</summary>
        public long DownloadBytes { get; private set; }
        /// <summary>Human-readable status for the UI.</summary>
        public string StatusMessage { get; private set; } = "";
        /// <summary>The entry currently loaded (null if none).</summary>
        public RemoteSceneEntry LoadedEntry { get; private set; }

        /// <summary>Raised after a level finished loading (or was adopted) and made the active scene.
        /// Bootstrap-scene systems (e.g. <see cref="PlayerSpawner"/>) subscribe to re-bind to the
        /// freshly loaded level, since serialized cross-scene references aren't possible in Unity.</summary>
        public event Action<Scene, RemoteSceneEntry> Loaded;

        /// <summary>True while a level is loaded.</summary>
        public bool HasLoadedScene => _hasLoadedScene;
        /// <summary>The currently loaded Unity scene (only valid when <see cref="HasLoadedScene"/>).</summary>
        public Scene LoadedScene => _currentScene;

        public bool IsBusy =>
            State == RemoteSceneState.CheckingSize ||
            State == RemoteSceneState.Downloading ||
            State == RemoteSceneState.Loading;

        SceneInstance _loadedSceneInstance; // valid only when _ownsHandle (we loaded it via Addressables)
        Scene _currentScene;
        bool _hasLoadedScene;
        bool _ownsHandle; // false when we adopted an already-open scene (unload via SceneManager, not Addressables)
        bool _quitting;   // set on play-stop / app quit so OnDestroy doesn't fight Addressables' own teardown

        void Start() {
            if (_loadStartupScene && _startupSceneIndex >= 0 && _startupSceneIndex < _scenes.Count)
                Load(_scenes[_startupSceneIndex]);
        }

        /// <summary>Fire-and-forget load driven by the UI; swaps out any currently loaded level.</summary>
        public async void Load(RemoteSceneEntry entry) {
            if (entry == null || IsBusy) return;
            if (_hasLoadedScene && LoadedEntry == entry) return; // already the current level
            if (string.IsNullOrWhiteSpace(entry.address)) {
                State = RemoteSceneState.Failed;
                StatusMessage = $"'{entry.displayName}' has no Addressables address set.";
                return;
            }
            try {
                await LoadInternal(entry);
            } catch (Exception e) {
                State = RemoteSceneState.Failed;
                StatusMessage = $"Failed: {e.Message}";
                Debug.LogException(e, this);
            }
        }

        async Task LoadInternal(RemoteSceneEntry entry) {
            await UnloadInternal();

            // Skip loading if this level is already open (left additively loaded in the editor, say).
            // Adopt it instead so we don't create a duplicate copy of the scene.
            Scene existing = FindLoadedScene(entry);
            if (existing.IsValid() && existing.isLoaded) {
                Adopt(existing, entry);
                return;
            }

            // 1. How much is there to download? Returns 0 if the bundle is already cached on device.
            State = RemoteSceneState.CheckingSize;
            Progress = 0f;
            StatusMessage = $"Checking '{entry.displayName}'...";
            AsyncOperationHandle<long> sizeHandle = Addressables.GetDownloadSizeAsync(entry.address);
            await sizeHandle.Task;
            if (sizeHandle.Status != AsyncOperationStatus.Succeeded) {
                Addressables.Release(sizeHandle);
                throw new Exception($"Unknown address '{entry.address}' (is the remote catalog loaded?).");
            }
            DownloadBytes = sizeHandle.Result;
            Addressables.Release(sizeHandle);

            // 2. Download dependencies with progress. Skipped instantly when already cached.
            if (DownloadBytes > 0) {
                State = RemoteSceneState.Downloading;
                AsyncOperationHandle dlHandle = Addressables.DownloadDependenciesAsync(entry.address);
                while (!dlHandle.IsDone) {
                    Progress = dlHandle.GetDownloadStatus().Percent;
                    StatusMessage = $"Downloading {FormatBytes(DownloadBytes)}  {Progress:P0}";
                    await Task.Yield();
                }
                bool ok = dlHandle.Status == AsyncOperationStatus.Succeeded;
                Addressables.Release(dlHandle);
                if (!ok) throw new Exception($"Download failed for '{entry.address}'.");
            }

            // 3. Load the scene additively and make it the active scene (skybox / lighting).
            State = RemoteSceneState.Loading;
            Progress = 1f;
            StatusMessage = $"Loading '{entry.displayName}'...";
            AsyncOperationHandle<SceneInstance> loadHandle =
                Addressables.LoadSceneAsync(entry.address, LoadSceneMode.Additive);
            await loadHandle.Task;
            if (loadHandle.Status != AsyncOperationStatus.Succeeded)
                throw new Exception($"Scene load failed for '{entry.address}'.");

            _loadedSceneInstance = loadHandle.Result;
            _currentScene = _loadedSceneInstance.Scene;
            _hasLoadedScene = true;
            _ownsHandle = true;
            LoadedEntry = entry;
            SceneManager.SetActiveScene(_currentScene);
            State = RemoteSceneState.Loaded;
            StatusMessage = $"Loaded '{entry.displayName}'.";
            Loaded?.Invoke(_currentScene, entry); // let bootstrap systems rebind to the new level
        }

        // Take ownership of a scene that is already loaded (not by us) so callers see it as the current
        // level. It's unloaded via SceneManager, not Addressables, since we hold no handle for it.
        void Adopt(Scene scene, RemoteSceneEntry entry) {
            _currentScene = scene;
            _hasLoadedScene = true;
            _ownsHandle = false;
            LoadedEntry = entry;
            SceneManager.SetActiveScene(scene);
            State = RemoteSceneState.Loaded;
            Progress = 1f;
            StatusMessage = $"Using already-loaded '{entry.displayName}'.";
            Loaded?.Invoke(scene, entry);
        }

        /// <summary>Fire-and-forget unload of the currently loaded level.</summary>
        public async void Unload() {
            if (IsBusy) return;
            await UnloadInternal();
            State = RemoteSceneState.Idle;
            Progress = 0f;
            StatusMessage = "";
        }

        async Task UnloadInternal() {
            if (!_hasLoadedScene) return;
            if (_ownsHandle) {
                AsyncOperationHandle<SceneInstance> h = Addressables.UnloadSceneAsync(_loadedSceneInstance);
                await h.Task;
            } else if (_currentScene.IsValid() && _currentScene.isLoaded) {
                // Adopted (editor-loaded) scene: unload through SceneManager since we hold no handle.
                AsyncOperation op = SceneManager.UnloadSceneAsync(_currentScene);
                while (op != null && !op.isDone) await Task.Yield();
            }
            _hasLoadedScene = false;
            _ownsHandle = false;
            _currentScene = default;
            LoadedEntry = null;
        }

        // Is a scene matching this entry already loaded? Compared by Unity scene name.
        static Scene FindLoadedScene(RemoteSceneEntry entry) {
            string name = SceneNameOf(entry);
            if (string.IsNullOrEmpty(name)) return default;
            for (int i = 0; i < SceneManager.sceneCount; i++) {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && s.name == name) return s;
            }
            return default;
        }

        // The Unity scene name to match against loaded scenes: the explicit sceneName if set, else the
        // address' file name (drops any path and the .unity extension).
        static string SceneNameOf(RemoteSceneEntry entry) {
            if (!string.IsNullOrEmpty(entry.sceneName)) return entry.sceneName;
            string a = entry.address ?? "";
            int slash = a.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) a = a.Substring(slash + 1);
            if (a.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) a = a.Substring(0, a.Length - ".unity".Length);
            return a;
        }

        // Play-stop / app quit is called before OnDestroy; flag it so OnDestroy skips the release.
        void OnApplicationQuit() => _quitting = true;

        void OnDestroy() {
            // On play-stop Addressables disposes itself and unloads its scenes, so releasing here would
            // race that teardown (double-release -> "invalid operation handle" / "cannot find handle").
            // Only release when the object is destroyed mid-play (not quitting). Adopted scenes (no
            // handle) are left alone - we didn't create them.
            if (_quitting || !_hasLoadedScene || !_ownsHandle) return;
            try { Addressables.UnloadSceneAsync(_loadedSceneInstance); }
            catch (Exception) { /* handle already gone (e.g. mid-teardown) - nothing to release */ }
        }

        static string FormatBytes(long bytes) {
            if (bytes >= 1L << 30) return $"{bytes / (float)(1L << 30):0.0} GB";
            if (bytes >= 1L << 20) return $"{bytes / (float)(1L << 20):0.0} MB";
            if (bytes >= 1L << 10) return $"{bytes / (float)(1L << 10):0.0} KB";
            return $"{bytes} B";
        }
    }
}
