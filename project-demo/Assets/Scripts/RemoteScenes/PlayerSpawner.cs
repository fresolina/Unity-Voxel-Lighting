using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lotec.Demo {
    /// <summary>
    /// Lives in the bootstrap scene next to the persistent player rig. When <see cref="SceneLoader"/>
    /// finishes loading a level, this finds that level's <see cref="SceneSpawnPoint"/> and teleports the
    /// player there. This is the runtime substitute for a cross-scene reference: the bootstrap scene
    /// can't point at a level object at edit time, so the level advertises a spawn point and we resolve
    /// it after load. Levels without a spawn point are left alone (player keeps its current position).
    /// </summary>
    [AddComponentMenu("Lotec/Demo/Player Spawner")]
    public class PlayerSpawner : MonoBehaviour {
        [Tooltip("The persistent player root to reposition (usually the player/camera rig in this bootstrap scene).")]
        [SerializeField] Transform _player;
        [Tooltip("The loader whose level loads drive spawning. Auto-found in this scene if left empty.")]
        [SerializeField] SceneLoader _loader;

        void Awake() {
            if (_loader == null) _loader = FindAnyObjectByType<SceneLoader>();
        }

        void OnEnable() {
            if (_loader == null) return;
            _loader.Loaded += OnLevelLoaded;
            // If a level was already loaded before we subscribed, spawn into it now.
            if (_loader.HasLoadedScene) OnLevelLoaded(_loader.LoadedScene, _loader.LoadedEntry);
        }

        void OnDisable() {
            if (_loader != null) _loader.Loaded -= OnLevelLoaded;
        }

        void OnLevelLoaded(Scene scene, RemoteSceneEntry entry) {
            if (_player == null) return;
            SceneSpawnPoint spawn = FindSpawnPoint(scene);
            if (spawn == null) return; // level defines no spawn - leave the player where it is
            Teleport(spawn.transform.position, spawn.transform.rotation);
        }

        static SceneSpawnPoint FindSpawnPoint(Scene scene) {
            if (!scene.IsValid()) return null;
            foreach (GameObject root in scene.GetRootGameObjects()) {
                SceneSpawnPoint spawn = root.GetComponentInChildren<SceneSpawnPoint>(true);
                if (spawn != null) return spawn;
            }
            return null;
        }

        // Move the player, cooperating with a CharacterController (which otherwise overrides direct
        // transform writes) and zeroing any Rigidbody velocity so it doesn't carry momentum across levels.
        void Teleport(Vector3 position, Quaternion rotation) {
            var cc = _player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            _player.SetPositionAndRotation(position, rotation);

            if (_player.TryGetComponent(out Rigidbody rb)) {
                rb.position = position;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            if (cc != null) cc.enabled = true;
        }
    }
}
