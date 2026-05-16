using UnityEngine;

namespace Lotec.Lighting {
    [ExecuteAlways]
    public class SdfShaderGlobals : MonoBehaviour {
        static readonly int sSdfTex = Shader.PropertyToID("_SdfTex");
        static readonly int sBitmaskTex = Shader.PropertyToID("_BitmaskTex");
        static readonly int sFibIndexTexture = Shader.PropertyToID("_FibIndexTexture");
        static readonly int sSdfBoundsMin = Shader.PropertyToID("_SdfBoundsMin");
        static readonly int sSdfBoundsSize = Shader.PropertyToID("_SdfBoundsSize");
        static readonly int sInverseVoxelSize = Shader.PropertyToID("_InverseVoxelSize");
        static readonly int sVoxelResolution = Shader.PropertyToID("_VoxelResolution");
        static readonly int sFibonacciDirections = Shader.PropertyToID("_FibonacciDirections");
        static readonly int s_volumeSize = Shader.PropertyToID("_VolumeSize");
        static readonly int s_volumePosition = Shader.PropertyToID("_VolumePosition");

        public LightingVolume Volume { get; set; }

        [Header("Update")]
        public bool autoUpdate = true;

        [Header("Lookup Textures")]
        public Texture2D fibonacciCheatIndices;

        void OnEnable() {
            ApplyGlobals();
        }

        void OnDisable() {
            // Intentionally do not clear globals; user may have multiple managers.
        }

        void OnValidate() {
            if (autoUpdate)
                ApplyGlobals();
        }

        void Update() {
            if (autoUpdate)
                ApplyGlobals();
        }

        public void ApplyGlobals() {
            // Always upload the Fibonacci direction list.
            Shader.SetGlobalVectorArray(sFibonacciDirections, OcclusionBitmaskBaker.GetOrCreateFibonacciDirectionsV4());

            if (Volume == null || Volume.sdfHiresTexture == null) return;

            Shader.SetGlobalTexture(sSdfTex, Volume.sdfHiresTexture);

            if (Volume.occlusionBitmaskTexture != null) {
                Shader.SetGlobalTexture(sBitmaskTex, Volume.occlusionBitmaskTexture);
                Shader.SetGlobalVector(sVoxelResolution,
                    new Vector3(Volume.TrimmedMaxResolution.x, Volume.TrimmedMaxResolution.y, Volume.TrimmedMaxResolution.z));
            }

            Shader.SetGlobalVector(s_volumeSize, Volume.Bounds.size);
            Shader.SetGlobalVector(s_volumePosition, Volume.Bounds.min);
            Shader.SetGlobalVector(sSdfBoundsMin, Volume.Bounds.min);
            Shader.SetGlobalVector(sSdfBoundsSize, Volume.Bounds.size);
            // Compute and set inverse voxel size (world units per voxel -> 1/voxelSize)
            Vector3 voxelSize = new Vector3(
                Volume.Bounds.size.x / Mathf.Max(1, Volume.TrimmedMaxResolution.x),
                Volume.Bounds.size.y / Mathf.Max(1, Volume.TrimmedMaxResolution.y),
                Volume.Bounds.size.z / Mathf.Max(1, Volume.TrimmedMaxResolution.z));
            Vector3 invVoxelSize = new Vector3(
                1.0f / Mathf.Max(1e-9f, voxelSize.x),
                1.0f / Mathf.Max(1e-9f, voxelSize.y),
                1.0f / Mathf.Max(1e-9f, voxelSize.z));
            Shader.SetGlobalVector(sInverseVoxelSize, invVoxelSize);

            // Set cheat-sheet lookup textures if provided
            if (fibonacciCheatIndices != null)
                Shader.SetGlobalTexture(sFibIndexTexture, fibonacciCheatIndices);
        }
    }
}
