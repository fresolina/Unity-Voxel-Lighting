using UnityEngine;

namespace Lotec.Lighting {
    public class LightingVolume : MonoBehaviour {
        static readonly int s_sSdfTex = Shader.PropertyToID("_SdfTex");
        static readonly int s_sBitmaskTex = Shader.PropertyToID("_BitmaskTex");
        static readonly int s_sSdfBoundsMin = Shader.PropertyToID("_SdfBoundsMin");
        static readonly int s_sSdfBoundsSize = Shader.PropertyToID("_SdfBoundsSize");
        static readonly int s_sInverseVoxelSize = Shader.PropertyToID("_InverseVoxelSize");
        static readonly int s_sVoxelResolution = Shader.PropertyToID("_VoxelResolution");
        static readonly int s_sVolumeSize = Shader.PropertyToID("_VolumeSize");
        static readonly int s_sVolumePosition = Shader.PropertyToID("_VolumePosition");
        static readonly int s_sBitmaskSunFibIndex = Shader.PropertyToID("_BitmaskSunFibIndex");
        static readonly int s_sBitmaskDirCount = Shader.PropertyToID("_BitmaskDirCount");

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
        [Tooltip("RGBA32 textures storing per-direction lit values. 4 directions per texture.")]
        public Texture3D[] occlusionFieldTextures;
        [HideInInspector]
        public Vector3[] occlusionBitmaskDirections;
        [HideInInspector]
        public Vector3[] occlusionFieldDirections;

        Vector3 _voxelSize;
        OcclusionFieldQuery _occlusionFieldQuery;

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

        public void ApplyShaderGlobals() {
            if (sdfHiresTexture == null) return;

            Shader.SetGlobalTexture(s_sSdfTex, sdfHiresTexture);

            if (occlusionBitmaskTexture != null) {
                Shader.SetGlobalTexture(s_sBitmaskTex, occlusionBitmaskTexture);
                Shader.SetGlobalVector(s_sVoxelResolution,
                    new Vector3(TrimmedMaxResolution.x, TrimmedMaxResolution.y, TrimmedMaxResolution.z));
            }

            Shader.SetGlobalVector(s_sVolumeSize, Bounds.size);
            Shader.SetGlobalVector(s_sVolumePosition, Bounds.min);
            Shader.SetGlobalVector(s_sSdfBoundsMin, Bounds.min);
            Shader.SetGlobalVector(s_sSdfBoundsSize, Bounds.size);

            Vector3 voxelSize = new Vector3(
                Bounds.size.x / Mathf.Max(1, TrimmedMaxResolution.x),
                Bounds.size.y / Mathf.Max(1, TrimmedMaxResolution.y),
                Bounds.size.z / Mathf.Max(1, TrimmedMaxResolution.z));
            Vector3 inverseVoxelSize = new Vector3(
                1.0f / Mathf.Max(1e-9f, voxelSize.x),
                1.0f / Mathf.Max(1e-9f, voxelSize.y),
                1.0f / Mathf.Max(1e-9f, voxelSize.z));
            Shader.SetGlobalVector(s_sInverseVoxelSize, inverseVoxelSize);

            ApplyBitmaskSunDirection();
            ApplyOcclusionFieldGlobals();
        }

        void ApplyBitmaskSunDirection() {
            if (occlusionBitmaskTexture == null) return;
            if (occlusionBitmaskDirections == null || occlusionBitmaskDirections.Length == 0) return;

            Vector3 sunDir = OcclusionFieldQuery.GetSunDirection();
            int bestIndex = OcclusionFieldQuery.FindNearestDirection(sunDir, occlusionBitmaskDirections, occlusionBitmaskDirections.Length);
            Shader.SetGlobalInt(s_sBitmaskSunFibIndex, bestIndex);
            Shader.SetGlobalInt(s_sBitmaskDirCount, occlusionBitmaskDirections.Length);
        }

        void ApplyOcclusionFieldGlobals() {
            if (occlusionFieldDirections == null || occlusionFieldDirections.Length == 0) return;
            if (occlusionFieldTextures == null || occlusionFieldTextures.Length == 0) return;

            if (_occlusionFieldQuery == null)
                _occlusionFieldQuery = new OcclusionFieldQuery();

            _occlusionFieldQuery.Initialize(occlusionFieldDirections, occlusionFieldTextures);
            _occlusionFieldQuery.ApplyShaderGlobals();
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
