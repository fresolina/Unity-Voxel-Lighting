using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lotec.Demo
{
    /// <summary>
    /// Lives in the bootstrap scene next to the persistent player rig. When the scene loader finishes
    /// loading a level, this finds that level's <see cref="SceneSpawnPoint"/> and teleports the player
    /// there. This is the runtime substitute for a cross-scene reference: the bootstrap scene can't point
    /// at a level object at edit time, so the level advertises a spawn point and we resolve it after load.
    /// Levels without a spawn point are left alone (player keeps its current position).
    ///
    /// Depends only on <see cref="ISceneLoader"/>, so it works with any loader (the simple additive
    /// <see cref="SceneLoader"/> here, or an Addressables-backed one) and can be shared across projects.
    /// </summary>
    [AddComponentMenu("Lotec/Demo/Player Spawner")]
    public class PlayerSpawner : MonoBehaviour
    {
        [Tooltip("The persistent player root to reposition (usually the player/camera rig in this bootstrap scene).")]
        [SerializeField] Transform _player;
        [Tooltip("Component implementing ISceneLoader whose level loads drive spawning. Auto-found in this scene if left empty.")]
        [SerializeField] MonoBehaviour _sceneLoader;

        ISceneLoader _loader;

        void Awake()
        {
            _loader = _sceneLoader as ISceneLoader ?? FindSceneLoader();
        }

        void OnEnable()
        {
            if (_loader == null) return;
            _loader.SceneLoaded += OnLevelLoaded;
            // If a level was already loaded before we subscribed, spawn into it now.
            if (_loader.HasLoadedScene) OnLevelLoaded(_loader.LoadedScene);
        }

        void OnDisable()
        {
            if (_loader != null) _loader.SceneLoaded -= OnLevelLoaded;
        }

        void OnLevelLoaded(Scene scene)
        {
            if (_player == null) return;
            SceneSpawnPoint spawn = FindSpawnPoint(scene);
            if (spawn == null) return; // level defines no spawn - leave the player where it is
            Teleport(spawn.transform.position, spawn.transform.rotation);
        }

        // The first component in the scene that implements ISceneLoader (mirrors the old
        // FindAnyObjectByType<SceneLoader>, but resolves by interface so it stays loader-agnostic).
        static ISceneLoader FindSceneLoader()
        {
            MonoBehaviour[] behaviours =
                FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour is ISceneLoader loader) return loader;
            }
            return null;
        }

        static SceneSpawnPoint FindSpawnPoint(Scene scene)
        {
            if (!scene.IsValid()) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                SceneSpawnPoint spawn = root.GetComponentInChildren<SceneSpawnPoint>(true);
                if (spawn != null) return spawn;
            }
            return null;
        }

        // Move the player, cooperating with a CharacterController (which otherwise overrides direct
        // transform writes) and zeroing any Rigidbody velocity so it doesn't carry momentum across levels.
        void Teleport(Vector3 position, Quaternion rotation)
        {
            var cc = _player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            _player.SetPositionAndRotation(position, rotation);

            if (_player.TryGetComponent(out Rigidbody rb))
            {
                rb.position = position;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            if (cc != null) cc.enabled = true;
        }

#if UNITY_EDITOR
        void OnValidate() {
            // Catch a wrong drag early: the field accepts any MonoBehaviour, but only an ISceneLoader works.
            if (_sceneLoader != null && _sceneLoader is not ISceneLoader) {
                Debug.LogWarning(
                    $"{nameof(PlayerSpawner)}: '{_sceneLoader.GetType().Name}' does not implement ISceneLoader; " +
                    "it will be ignored (a loader will be auto-found instead).", this);
            }
        }
#endif
    }
}
