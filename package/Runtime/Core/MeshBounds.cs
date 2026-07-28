using UnityEngine;
using UnityEngine.Rendering;

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
            Bounds computed = new Bounds();
            bool found = false;
            MeshRenderer[] meshRenderers = _root.GetComponentsInChildren<MeshRenderer>();
            foreach (MeshRenderer mr in meshRenderers) {
                if (mr == null)
                    continue;
                if (!IsBakeEligible(mr))
                    continue;
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                    continue;

                if (!found) {
                    computed = mr.bounds;
                    found = true;
                } else {
                    computed.Encapsulate(mr.bounds);
                }
            }

            // Only overwrite the serialized bounds when we actually found eligible geometry. Otherwise
            // keep the previously baked value: IsBakeEligible checks GameObject.isStatic, an EDITOR-ONLY
            // flag that reads false at build/runtime, so a recompute triggered while packing a player /
            // Addressables bundle would otherwise zero the bounds that were correct in the editor.
            if (found)
                _bounds = computed;
        }

        /// <summary>Whether a renderer participates in bounds and bakes/voxelization. One shared
        /// predicate so bounds and bake content always agree.
        ///
        /// <b>Cast Shadows = Off is the opt-out.</b> Every field baked here IS a light-occlusion
        /// structure (the SDF and occlusion fields are shadows; a voxel occupied in the buffer GI stops
        /// GI rays), so a renderer that casts no shadow must not become solid in them either. That is
        /// what lets a VFX card - a fire, a glow, a decal quad - sit inside the volume without walling
        /// it off: it neither blocks light nor stretches the bounds. Anything meant to occlude keeps
        /// the default On.</summary>
        public static bool IsBakeEligible(Renderer renderer) {
            GameObject gameObject = renderer.gameObject;
            return gameObject.activeInHierarchy
                && gameObject.isStatic
                && renderer.shadowCastingMode != ShadowCastingMode.Off;
        }
    }
}
