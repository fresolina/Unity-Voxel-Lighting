using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Baked lighting volume: bake input, computed bounds/resolution, and the baked SDF
    /// textures. Owns the volume-derived shader globals (bounds, voxel grid, SDF texture) and
    /// how to publish them via <see cref="ApplyShaderGlobals"/> - but LightingManager decides
    /// *when* and for *which* volume to call it, so the singular globals reflect the active
    /// volume only. GiFieldUpdater publishes the GI-grid globals separately.
    /// </summary>
    public class VoxelVolume : MonoBehaviour {
        static readonly int s_sdfHires = Shader.PropertyToID("_SdfHires");
        static readonly int s_boundsMin = Shader.PropertyToID("_VoxelVolumeBoundsMin");
        static readonly int s_boundsSize = Shader.PropertyToID("_VoxelVolumeBoundsSize");

        [Header("Bake Input")]
        [SerializeField] Transform _root;

        [Tooltip("Maximum voxel resolution along the largest axis.")]
        [Min(4)]
        [SerializeField] int _maxResolution = 128;

        [SerializeField] bool _autoRegisterWithManager = true;

        [Header("Baked static fields")]
        public Texture3D sdfHiresTexture;
        public Texture3D sdfLowresTexture;

        [HideInInspector]
        [SerializeField] Vector3Int _trimmedMaxResolution;
        [HideInInspector]
        [SerializeField] Bounds _bounds = new Bounds(Vector3.zero, Vector3.one);
        Vector3 _voxelSize;

        // Fixed border around the geometry, so the SDF have valid distances at the boundary.
        // 2.0 (not 1.5): at concave corners a thinner border let GI rays leak exterior light.
        // NOTE: This can be reduced when GI light is tracked in all 6 axes.
        const float PaddingVoxels = 2f;

        /// <summary>Cubic voxel edge length in world units, derived from Bounds and TrimmedMaxResolution.</summary>
        public float VoxelSize => Bounds.size.x / Mathf.Max(1, TrimmedMaxResolution.x);

        /// <summary>Per-axis inverse voxel size (TrimmedMaxResolution / Bounds.size) at the volume grid.</summary>
        public Vector3 VoxelSizeInverse => new Vector3(
            TrimmedMaxResolution.x / Mathf.Max(1e-9f, Bounds.size.x),
            TrimmedMaxResolution.y / Mathf.Max(1e-9f, Bounds.size.y),
            TrimmedMaxResolution.z / Mathf.Max(1e-9f, Bounds.size.z));

        public Transform BakeRoot { get => _root; set => _root = value; }
        public Vector3Int TrimmedMaxResolution => _trimmedMaxResolution;
        public Bounds Bounds => _bounds;

        /// <summary>Publish the universal volume-derived globals: world-space bounds and the
        /// hi-res SDF texture (needed by shadows + GI regardless of occlusion). Call from
        /// LightingManager for the active volume - these are singular globals, so a non-active
        /// volume must not publish them. Occlusion-grid globals are published by the occlusion
        /// binders (they read this volume's VoxelSizeInverse / TrimmedMaxResolution).</summary>
        public void ApplyShaderGlobals() {
            Shader.SetGlobalVector(s_boundsMin, Bounds.min);
            Shader.SetGlobalVector(s_boundsSize, Bounds.size);
            if (sdfHiresTexture != null)
                Shader.SetGlobalTexture(s_sdfHires, sdfHiresTexture);
        }

        void OnValidate() {
            _maxResolution = Mathf.Max(4, _maxResolution);
            if (BakeRoot == null) {
                BakeRoot = transform;
            }
            RecomputeBoundsAndResolution();
        }

        void OnEnable() {
            if (_autoRegisterWithManager)
                LightingManager.Instance?.RegisterVolume(this);
        }

        void Start() {
            if (_autoRegisterWithManager)
                LightingManager.Instance?.RegisterVolume(this);
        }

        void OnDisable() {
            if (_autoRegisterWithManager)
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
            // Mutate the backing field directly. Encapsulate/Expand on the Bounds property
            // would mutate the getter's struct copy and be lost.
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

            Vector3 size = _bounds.size;
            float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            maxAxis = Mathf.Max(0.0001f, maxAxis);

            // Increase bounds with padding.
            float resolutionBudget = Mathf.Max(1f, _maxResolution - 2f * PaddingVoxels);
            float voxelSize = maxAxis / resolutionBudget;
            voxelSize = Mathf.Max(0.0001f, voxelSize);
            _bounds.Expand(2f * PaddingVoxels * voxelSize);
            size = _bounds.size;

            int rx = Mathf.Clamp(Mathf.CeilToInt(size.x / voxelSize), 4, _maxResolution);
            int ry = Mathf.Clamp(Mathf.CeilToInt(size.y / voxelSize), 4, _maxResolution);
            int rz = Mathf.Clamp(Mathf.CeilToInt(size.z / voxelSize), 4, _maxResolution);

            _trimmedMaxResolution = new Vector3Int(rx, ry, rz);

            // Enforce cubic voxels by expanding the bounds to an integer number of
            // cells of the chosen voxel size on every axis. Without this adjustment,
            // the ceil() above would make the effective voxel size drift per axis and
            // the runtime GI shaders would silently stop agreeing on shell widths,
            // offsets, and distance thresholds.
            Vector3 alignedSize = new Vector3(rx * voxelSize, ry * voxelSize, rz * voxelSize);
            _bounds = new Bounds(_bounds.center, alignedSize);
            _voxelSize = Vector3.one * voxelSize;
        }
    }
}
