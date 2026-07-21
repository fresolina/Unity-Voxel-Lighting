using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lotec.Demo {
    /// <summary>
    /// One level the loader can open, referenced directly by its scene asset (drag the .unity file into
    /// <see cref="sceneAsset"/>). The asset path is baked into <see cref="scenePath"/> in the editor and
    /// is the only field read at runtime, so each referenced scene must also be added to the Build
    /// Profile / Build Settings scene list (runtime scene loads only see build-listed scenes).
    /// </summary>
    [Serializable]
    public class SceneEntry {
#if UNITY_EDITOR
        [Tooltip("The scene to load. Its asset path is baked into 'Scene Path' for runtime use on save.")]
        public SceneAsset sceneAsset;
#endif
        [Tooltip("Name shown in the UI. Falls back to the scene file name when empty.")]
        public string displayName = "";

        // Baked from sceneAsset in the editor (see SceneLoader.OnValidate); read at runtime.
        [SerializeField, HideInInspector] internal string scenePath = "";

        public string SceneName =>
            string.IsNullOrEmpty(scenePath) ? "" : Path.GetFileNameWithoutExtension(scenePath);

        public string ResolvedDisplayName =>
            string.IsNullOrEmpty(displayName) ? SceneName : displayName;
    }

    /// <summary>
    /// Loads local level scenes additively, one at a time (unloading the previous first), from a plain
    /// serialized list - no Addressables. If the requested level is already open (e.g. left additively
    /// loaded in the editor) it is adopted instead of loaded twice, so there's never a duplicate copy.
    /// Pure logic + observable state; a UI or hotkey calls <see cref="Load(int)"/> / <see cref="Unload"/>
    /// and bootstrap systems (e.g. <see cref="PlayerSpawner"/>) subscribe to <see cref="SceneLoaded"/> to
    /// rebind to the new level. Referenced scenes must be in the Build Settings scene list.
    /// </summary>
    [AddComponentMenu("Lotec/Demo/Scene Loader")]
    public class SceneLoader : MonoBehaviour, ISceneLoader {
        [Tooltip("Levels available to load. Each entry references a scene asset that must also be in Build Settings.")]
        [SerializeField] List<SceneEntry> _scenes = new();

        [Header("Bootstrap")]
        [Tooltip("Auto-load one level on Start so the bootstrap scene is never left empty.")]
        [SerializeField] bool _loadStartupScene = true;
        [Tooltip("Index into the Scenes list to load on Start when 'Load Startup Scene' is on.")]
        [SerializeField] int _startupSceneIndex;

        public IReadOnlyList<SceneEntry> Scenes => _scenes;

        /// <summary>True while a level is loaded.</summary>
        public bool HasLoadedScene { get; private set; }
        /// <summary>The currently loaded Unity scene (only valid when <see cref="HasLoadedScene"/>).</summary>
        public Scene LoadedScene { get; private set; }
        /// <summary>The entry currently loaded (null if none).</summary>
        public SceneEntry LoadedEntry { get; private set; }
        /// <summary>True while a load/unload is in flight; further requests are ignored until it settles.</summary>
        public bool IsBusy { get; private set; }

        /// <summary>Raised after a level finished loading (or was adopted) and was made the active scene.
        /// Bootstrap-scene systems subscribe to re-bind to the freshly loaded level, since serialized
        /// cross-scene references aren't possible in Unity. The loaded entry is available via
        /// <see cref="LoadedEntry"/> for consumers that need its metadata.</summary>
        public event Action<Scene> SceneLoaded;

        void Start() {
            if (_loadStartupScene && _startupSceneIndex >= 0 && _startupSceneIndex < _scenes.Count)
                Load(_scenes[_startupSceneIndex]);
        }

        /// <summary>Load the level at <paramref name="index"/> in the Scenes list (no-op if out of range).</summary>
        public void Load(int index) {
            if (index < 0 || index >= _scenes.Count) return;
            Load(_scenes[index]);
        }

        /// <summary>Fire-and-forget load; swaps out any currently loaded level first.</summary>
        public async void Load(SceneEntry entry) {
            if (entry == null || IsBusy) return;
            if (HasLoadedScene && LoadedEntry == entry) return; // already the current level
            if (string.IsNullOrEmpty(entry.scenePath)) {
                Debug.LogError($"SceneLoader: '{entry.ResolvedDisplayName}' has no scene assigned.", this);
                return;
            }

            try {
                IsBusy = true;
                await LoadInternal(entry);
            } catch (Exception e) {
                Debug.LogException(e, this);
            } finally {
                IsBusy = false;
            }
        }

        async Task LoadInternal(SceneEntry entry) {
            await UnloadInternal();

            // Skip loading if this level is already open (left additively loaded in the editor, say) and
            // adopt it instead, so we don't create a duplicate copy of the scene.
            Scene existing = FindLoadedScene(entry);
            if (existing.IsValid() && existing.isLoaded) {
                Adopt(existing, entry);
                return;
            }

            AsyncOperation op = SceneManager.LoadSceneAsync(entry.scenePath, LoadSceneMode.Additive);
            if (op == null)
                throw new Exception($"Could not load '{entry.scenePath}'. Is the scene in Build Settings?");
            while (!op.isDone)
                await Task.Yield();

            LoadedScene = SceneManager.GetSceneByPath(entry.scenePath);
            HasLoadedScene = true;
            LoadedEntry = entry;
            SceneManager.SetActiveScene(LoadedScene); // level owns skybox / lighting settings
            SceneLoaded?.Invoke(LoadedScene);
        }

        // Take ownership of a scene that is already loaded (not by us) so callers see it as the current
        // level. Unloaded the same way as one we loaded, since a local additive scene has no handle.
        void Adopt(Scene scene, SceneEntry entry) {
            LoadedScene = scene;
            HasLoadedScene = true;
            LoadedEntry = entry;
            SceneManager.SetActiveScene(scene);
            SceneLoaded?.Invoke(scene);
        }

        /// <summary>Fire-and-forget unload of the currently loaded level.</summary>
        public async void Unload() {
            if (IsBusy) return;
            IsBusy = true;
            try {
                await UnloadInternal();
            } finally {
                IsBusy = false;
            }
        }

        async Task UnloadInternal() {
            if (!HasLoadedScene) return;
            // Never unload the last remaining scene (the persistent bootstrap scene must stay loaded).
            if (LoadedScene.IsValid() && LoadedScene.isLoaded && SceneManager.sceneCount > 1) {
                AsyncOperation op = SceneManager.UnloadSceneAsync(LoadedScene);
                while (op != null && !op.isDone)
                    await Task.Yield();
            }
            HasLoadedScene = false;
            LoadedScene = default;
            LoadedEntry = null;
        }

        // Is a scene matching this entry already loaded? Compared by scene asset path.
        static Scene FindLoadedScene(SceneEntry entry) {
            for (int i = 0; i < SceneManager.sceneCount; i++) {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && s.path == entry.scenePath) return s;
            }
            return default;
        }

#if UNITY_EDITOR
        // Bake each entry's SceneAsset reference into its runtime scenePath whenever the list is edited.
        void OnValidate() {
            foreach (SceneEntry entry in _scenes) {
                if (entry == null) continue;
                string path = entry.sceneAsset != null ? AssetDatabase.GetAssetPath(entry.sceneAsset) : "";
                if (entry.scenePath != path) entry.scenePath = path;
            }
        }
#endif
    }
}
