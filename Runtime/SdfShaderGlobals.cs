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
        static readonly int sShadowMaxDistance = Shader.PropertyToID("_SdfShadowMaxDistance");
        static readonly int sShadowMaxSteps = Shader.PropertyToID("_SdfShadowMaxSteps");
        static readonly int sShadowEpsilon = Shader.PropertyToID("_SdfShadowEpsilon");
        static readonly int sShadowMinStep = Shader.PropertyToID("_SdfShadowMinStep");
        static readonly int sShadowStartOffset = Shader.PropertyToID("_SdfShadowStartOffset");
        static readonly int sVoxelDebugMode = Shader.PropertyToID("_VoxelDebugMode");
        static readonly int sFibonacciDirections = Shader.PropertyToID("_FibonacciDirections");

        [Header("Source")]
        public SdfVolume volume;

        [Header("Shadow Raymarch")]
        [Min(0f)] public float shadowMaxDistance = 10f;
        [Min(1)] public int shadowMaxSteps = 64;
        [Min(0.000001f)] public float shadowEpsilon = 0.02f;
        [Min(0.000001f)] public float shadowMinStep = 0.01f;
        [Min(0f)] public float shadowStartOffset = 0.02f;

        [Header("Update")]
        public bool autoUpdate = true;

        [Header("Lookup Textures")]
        public Texture2D fibonacciCheatIndices;

        [Header("Debug")]
        public bool debugColors = false;
        [Range(0, 5)] public int voxelDebugMode = 0;

        public enum ShadowMode { SDF = 0, BitmaskPoint = 1, BitmaskFiltered = 2 }
        [Header("Shadow Mode")]
        public ShadowMode shadowMode = ShadowMode.SDF;

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

            if (volume == null || volume.sdfTexture == null) return;

            Shader.SetGlobalTexture(sSdfTex, volume.sdfTexture);

            if (volume.occlusionBitmaskTexture != null) {
                Shader.SetGlobalTexture(sBitmaskTex, volume.occlusionBitmaskTexture);
                Shader.SetGlobalVector(sVoxelResolution,
                    new Vector3(volume.bakedResolution.x, volume.bakedResolution.y, volume.bakedResolution.z));
            }

            Shader.SetGlobalVector(sSdfBoundsMin, volume.bakedBounds.min);
            Shader.SetGlobalVector(sSdfBoundsSize, volume.bakedBounds.size);
            // Compute and set inverse voxel size (world units per voxel -> 1/voxelSize)
            Vector3 voxelSize = new Vector3(
                volume.bakedBounds.size.x / Mathf.Max(1, volume.bakedResolution.x),
                volume.bakedBounds.size.y / Mathf.Max(1, volume.bakedResolution.y),
                volume.bakedBounds.size.z / Mathf.Max(1, volume.bakedResolution.z));
            Vector3 invVoxelSize = new Vector3(
                1.0f / Mathf.Max(1e-9f, voxelSize.x),
                1.0f / Mathf.Max(1e-9f, voxelSize.y),
                1.0f / Mathf.Max(1e-9f, voxelSize.z));
            Shader.SetGlobalVector(sInverseVoxelSize, invVoxelSize);
            Shader.SetGlobalFloat(sShadowMaxDistance, shadowMaxDistance);
            Shader.SetGlobalInt(sShadowMaxSteps, shadowMaxSteps);
            Shader.SetGlobalFloat(sShadowEpsilon, shadowEpsilon);
            Shader.SetGlobalFloat(sShadowMinStep, shadowMinStep);
            Shader.SetGlobalFloat(sShadowStartOffset, shadowStartOffset);

            // Set cheat-sheet lookup textures if provided
            if (fibonacciCheatIndices != null)
                Shader.SetGlobalTexture(sFibIndexTexture, fibonacciCheatIndices);

            // Set shadow mode keyword
            switch (shadowMode) {
                case ShadowMode.SDF:
                    Shader.EnableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_FILTERED");
                    break;
                case ShadowMode.BitmaskPoint:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.EnableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_FILTERED");
                    break;
                case ShadowMode.BitmaskFiltered:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.EnableKeyword("BITMASK_FILTERED");
                    break;
            }

            // Debug colors toggle
            if (debugColors)
                Shader.EnableKeyword("VOXEL_OCCLUSION_DEBUG_COLORS");
            else
                Shader.DisableKeyword("VOXEL_OCCLUSION_DEBUG_COLORS");

            // Voxel debug visualization mode (0 = off)
            Shader.SetGlobalInt(sVoxelDebugMode, voxelDebugMode);
        }
    }
}
