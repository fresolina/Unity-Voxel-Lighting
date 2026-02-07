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
        static readonly int sShadowSoftness = Shader.PropertyToID("_SdfShadowSoftness");
        static readonly int sShadowEpsilon = Shader.PropertyToID("_SdfShadowEpsilon");
        static readonly int sShadowMinStep = Shader.PropertyToID("_SdfShadowMinStep");
        static readonly int sShadowStartOffset = Shader.PropertyToID("_SdfShadowStartOffset");
        static readonly int sFibonacciDirections = Shader.PropertyToID("_FibonacciDirections");
        static readonly int s_volumeSize = Shader.PropertyToID("_VolumeSize");
        static readonly int s_volumePosition = Shader.PropertyToID("_VolumePosition");


        [Header("Source")]
        public LightingVolume volume;

        [Header("Shadow Raymarch")]
        [Min(0f)] public float shadowMaxDistance = 10f;
        [Min(1)] public int shadowMaxSteps = 64;
        [Min(0.000001f)] public float shadowEpsilon = 0.02f;
        [Min(1f)] public float shadowSoftness = 16.0f;
        [Min(0.000001f)] public float shadowMinStep = 0.01f;
        [Min(0f)] public float shadowStartOffset = 0.02f;

        [Header("Update")]
        public bool autoUpdate = true;

        [Header("Lookup Textures")]
        public Texture2D fibonacciCheatIndices;


        public enum ShadowMode { SDF = 0, BitmaskPoint = 1, Bitmask4Tap = 2, BitmaskRay3 = 3, Bitmask8Tap = 4 }
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
            // Always set these globals every frame since they are used by other shaders and the volume may move.
            Shader.SetGlobalVector(s_volumeSize, volume.Bounds.size);
            Shader.SetGlobalVector(s_volumePosition, volume.Bounds.min);
        }

        public void ApplyGlobals() {
            // Always upload the Fibonacci direction list.
            Shader.SetGlobalVectorArray(sFibonacciDirections, OcclusionBitmaskBaker.GetOrCreateFibonacciDirectionsV4());

            if (volume == null || volume.sdfHiresTexture == null) return;

            Shader.SetGlobalTexture(sSdfTex, volume.sdfHiresTexture);

            if (volume.occlusionBitmaskTexture != null) {
                Shader.SetGlobalTexture(sBitmaskTex, volume.occlusionBitmaskTexture);
                Shader.SetGlobalVector(sVoxelResolution,
                    new Vector3(volume.TrimmedMaxResolution.x, volume.TrimmedMaxResolution.y, volume.TrimmedMaxResolution.z));
            }

            Shader.SetGlobalVector(sSdfBoundsMin, volume.Bounds.min);
            Shader.SetGlobalVector(sSdfBoundsSize, volume.Bounds.size);
            // Compute and set inverse voxel size (world units per voxel -> 1/voxelSize)
            Vector3 voxelSize = new Vector3(
                volume.Bounds.size.x / Mathf.Max(1, volume.TrimmedMaxResolution.x),
                volume.Bounds.size.y / Mathf.Max(1, volume.TrimmedMaxResolution.y),
                volume.Bounds.size.z / Mathf.Max(1, volume.TrimmedMaxResolution.z));
            Vector3 invVoxelSize = new Vector3(
                1.0f / Mathf.Max(1e-9f, voxelSize.x),
                1.0f / Mathf.Max(1e-9f, voxelSize.y),
                1.0f / Mathf.Max(1e-9f, voxelSize.z));
            Shader.SetGlobalVector(sInverseVoxelSize, invVoxelSize);
            Shader.SetGlobalFloat(sShadowMaxDistance, shadowMaxDistance);
            Shader.SetGlobalInt(sShadowMaxSteps, shadowMaxSteps);
            Shader.SetGlobalFloat(sShadowSoftness, shadowSoftness);
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
                    Shader.DisableKeyword("BITMASK_4TAP");
                    Shader.DisableKeyword("BITMASK_RAY3");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    break;
                case ShadowMode.BitmaskPoint:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.EnableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_4TAP");
                    Shader.DisableKeyword("BITMASK_RAY3");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    break;
                case ShadowMode.Bitmask4Tap:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.EnableKeyword("BITMASK_4TAP");
                    Shader.DisableKeyword("BITMASK_RAY3");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    break;
                case ShadowMode.BitmaskRay3:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_4TAP");
                    Shader.EnableKeyword("BITMASK_RAY3");
                    Shader.DisableKeyword("BITMASK_8TAP");
                    break;
                case ShadowMode.Bitmask8Tap:
                    Shader.DisableKeyword("SDF_ONLY");
                    Shader.DisableKeyword("BITMASK_POINT");
                    Shader.DisableKeyword("BITMASK_4TAP");
                    Shader.DisableKeyword("BITMASK_RAY3");
                    Shader.EnableKeyword("BITMASK_8TAP");
                    break;
            }
        }
    }
}
