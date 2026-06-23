using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Pure data container for a baked lighting volume: bake input, computed bounds/
    /// resolution, and the baked SDF/material textures. Does not publish shader globals -
    /// the LightingManager publishes the SDF core, GiFieldUpdater the GI volume globals,
    /// and the per-feature binders (VoxelOcclusionField / VoxelOcclusionBitmask) the rest.
    /// </summary>
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
        [Tooltip("Lower-resolution material property: albedo.rgb + emissionIntensity (a)")]
        public Texture3D materialAlbedoIntensityTexture;

        Vector3 _voxelSize;

        /// <summary>Cubic voxel edge length in world units, derived from Bounds and TrimmedMaxResolution.</summary>
        public float VoxelSize => Bounds.size.x / Mathf.Max(1, TrimmedMaxResolution.x);

        public Transform BakeRoot { get => _root; set => _root = value; }

        void OnValidate() {
            _maxResolution = Mathf.Max(4, _maxResolution);
            _paddingWorld = Mathf.Max(0f, _paddingWorld);
            if (BakeRoot == null) {
                BakeRoot = transform;
            }
            RecomputeBoundsAndResolution();
        }

        void OnEnable() {
            LightingManager.Instance?.RegisterVolume(this);
            if (materialAlbedoIntensityTexture == null) {
                Debug.LogWarning("LightingVolume: materialAlbedoIntensityTexture is null at runtime (scene reference may be unresolved).", this);
            } else {
                Debug.Log($"LightingVolume: materialAlbedoIntensityTexture loaded: {materialAlbedoIntensityTexture.name}", this);
            }
        }

        void Start() {
            LightingManager.Instance?.RegisterVolume(this);
        }

        void OnDisable() {
            LightingManager.Instance?.UnregisterVolume(this);
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
            MeshRenderer[] meshRenderers = _root.GetComponentsInChildren<MeshRenderer>();
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

            float voxelSize = maxAxis / _maxResolution;
            voxelSize = Mathf.Max(0.0001f, voxelSize);

            int rx = Mathf.Clamp(Mathf.CeilToInt(size.x / voxelSize), 4, _maxResolution);
            int ry = Mathf.Clamp(Mathf.CeilToInt(size.y / voxelSize), 4, _maxResolution);
            int rz = Mathf.Clamp(Mathf.CeilToInt(size.z / voxelSize), 4, _maxResolution);

            TrimmedMaxResolution = new Vector3Int(rx, ry, rz);

            // Enforce cubic voxels by expanding the bounds to an integer number of
            // cells of the chosen voxel size on every axis. Without this adjustment,
            // the ceil() above would make the effective voxel size drift per axis and
            // the runtime GI shaders would silently stop agreeing on shell widths,
            // offsets, and distance thresholds.
            Vector3 alignedSize = new Vector3(rx * voxelSize, ry * voxelSize, rz * voxelSize);
            Bounds = new Bounds(Bounds.center, alignedSize);
            _voxelSize = Vector3.one * voxelSize;
        }
    }
}
