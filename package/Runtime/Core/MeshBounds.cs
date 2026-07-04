using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Computes and stores the encapsulated world bounds of the static meshes under a root.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Lotec/Voxel Lighting/Mesh Bounds")]
    public class MeshBounds : MonoBehaviour {
        [Tooltip("Root whose static meshes are encapsulated (also the geometry bakes/voxelizers " +
                 "read). Defaults to this transform.")]
        [SerializeField] Transform _root;

        [HideInInspector]
        [SerializeField] Bounds _bounds = new Bounds(Vector3.zero, Vector3.one);

        public Transform Root { get => _root; set => _root = value; }
        /// <summary>Tight encapsulated bounds of the eligible meshes (see <see cref="Recompute"/>).</summary>
        public Bounds Bounds => _bounds;

        void OnValidate() {
            if (_root == null) _root = transform;
            Recompute();
        }

        /// <summary>Set Bounds to the encapsulated bounds of all bake-eligible meshes under the
        /// root. Mutates the backing field directly: Encapsulate on the Bounds property would
        /// mutate the getter's struct copy and be lost.</summary>
        public void Recompute() {
            if (_root == null) return;
            _bounds = new Bounds();
            MeshRenderer[] meshRenderers = _root.GetComponentsInChildren<MeshRenderer>();
            foreach (MeshRenderer mr in meshRenderers) {
                if (mr == null)
                    continue;
                if (!IsBakeEligible(mr))
                    continue;
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                    continue;

                if (_bounds.size == Vector3.zero) {
                    _bounds = mr.bounds;
                } else {
                    _bounds.Encapsulate(mr.bounds);
                }
            }
        }

        /// <summary>Whether a renderer participates in bounds and bakes/voxelization. One shared
        /// predicate so bounds and bake content always agree.</summary>
        public static bool IsBakeEligible(Renderer renderer) {
            GameObject gameObject = renderer.gameObject;
            return gameObject.activeInHierarchy && gameObject.isStatic;
        }
    }
}
