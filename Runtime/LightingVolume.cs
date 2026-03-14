using UnityEngine;

namespace Lotec.Lighting {
    public class LightingVolume : MonoBehaviour {
        [Header("Bake Input")]
        [SerializeField] Transform _root;

        [Tooltip("Extra padding added to computed bounds (world units).")]
        [Min(0f)]
        [SerializeField] float _paddingWorld = 0.25f;

        [Tooltip("Maximum voxel resolution along the largest axis.")]
        [Min(4)]
        [SerializeField] int _maxResolution = 128;

        [Tooltip("Computed max voxel resolution")]
        public Vector3Int TrimmedMaxResolution;

        [Tooltip("Computed bounds used for baking.")]
        public Bounds Bounds = new Bounds(Vector3.zero, Vector3.one);

        [Header("Baked static fields")]
        public Texture3D sdfHiresTexture;
        public Texture3D sdfLowresTexture;
        public Texture3D occlusionBitmaskTexture;
        [Tooltip("Lower-resolution material property: albedo.rgb + emissionIntensity (a)")]
        public Texture3D materialAlbedoIntensityTexture;

        Vector3 _voxelSize;

        public Transform BakeRoot { get => _root; set => _root = value; }

        void OnValidate() {
            _maxResolution = Mathf.Max(4, _maxResolution);
            _paddingWorld = Mathf.Max(0f, _paddingWorld);
            if (BakeRoot == null) {
                BakeRoot = transform;
            }
            RecomputeBoundsAndResolution();
        }

        public void RecomputeBoundsAndResolution() {
            if (_root == null) return;

            ComputeBounds();
            ComputeMaxResolutionForBounds();
        }

        /// <summary>
        /// Expands Bounds to encapsulate all meshes under _root
        /// </summary>
        void ComputeBounds() {
            Bounds = new Bounds();
            MeshRenderer[] meshRenderers = _root.GetComponentsInChildren<MeshRenderer>(true);
            foreach (MeshRenderer mr in meshRenderers) {
                if (mr == null)
                    continue;
                if (!IsBakeEligible(mr))
                    continue;
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                    continue;

                if (Bounds.size == Vector3.zero) {
                    Bounds = mr.bounds;
                } else {
                    Bounds.Encapsulate(mr.bounds);
                }
            }

            if (_paddingWorld > 0f) {
                Bounds.Expand(_paddingWorld * 2f);
            }
        }

        static bool IsBakeEligible(Renderer renderer) {
            GameObject gameObject = renderer.gameObject;
            return gameObject.activeInHierarchy && gameObject.isStatic;
        }

        /// <summary>
        /// Compute TrimmedMaxResolution based on bounds and _maxResolution
        /// Example: 128x128x128 -> 128x67x24 for a long thin volume
        /// </summary>
        void ComputeMaxResolutionForBounds() {
            _maxResolution = Mathf.Max(4, _maxResolution);

            Vector3 size = Bounds.size;
            float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            maxAxis = Mathf.Max(0.0001f, maxAxis);

            float voxelSizeX = maxAxis / _maxResolution;
            voxelSizeX = Mathf.Max(0.0001f, voxelSizeX);

            int rx = Mathf.Clamp(Mathf.CeilToInt(size.x / voxelSizeX), 4, _maxResolution);
            int ry = Mathf.Clamp(Mathf.CeilToInt(size.y / voxelSizeX), 4, _maxResolution);
            int rz = Mathf.Clamp(Mathf.CeilToInt(size.z / voxelSizeX), 4, _maxResolution);

            TrimmedMaxResolution = new Vector3Int(rx, ry, rz);
            _voxelSize = Vector3.Scale(Bounds.size, new Vector3(1.0f / TrimmedMaxResolution.x, 1.0f / TrimmedMaxResolution.y, 1.0f / TrimmedMaxResolution.z));
        }
    }
}
