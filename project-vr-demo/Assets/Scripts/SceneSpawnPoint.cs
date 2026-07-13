using UnityEngine;

namespace Lotec.Demo {
    /// <summary>
    /// Marks where the persistent player should be placed when this level is loaded. Drop one in each
    /// level scene. The bootstrap-scene <see cref="PlayerSpawner"/> finds it in the freshly loaded scene
    /// and teleports the player to this transform's position/rotation - which is how a persistent player
    /// rig ends up in the right spot even though each level lives at its own world coordinates and can't
    /// be referenced from the bootstrap scene at edit time.
    /// </summary>
    [AddComponentMenu("Lotec/Demo/Scene Spawn Point")]
    public class SceneSpawnPoint : MonoBehaviour {
        void OnDrawGizmos() {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.35f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.2f);
            Gizmos.DrawLine(transform.position, transform.position + transform.up * 0.6f);
        }
    }
}
