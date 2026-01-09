using UnityEngine;

namespace Lotec.Lighting {
    [ExecuteAlways]
    public class SdfShaderGlobals : MonoBehaviour {
        static readonly int sSdfTex = Shader.PropertyToID("_SdfTex");
        static readonly int sBitmaskTex = Shader.PropertyToID("_BitmaskTex");
        static readonly int sSdfBoundsMin = Shader.PropertyToID("_SdfBoundsMin");
        static readonly int sSdfBoundsSize = Shader.PropertyToID("_SdfBoundsSize");
        static readonly int sVoxelResolution = Shader.PropertyToID("_VoxelResolution");
        static readonly int sShadowMaxDistance = Shader.PropertyToID("_SdfShadowMaxDistance");
        static readonly int sShadowMaxSteps = Shader.PropertyToID("_SdfShadowMaxSteps");
        static readonly int sShadowEpsilon = Shader.PropertyToID("_SdfShadowEpsilon");
        static readonly int sShadowMinStep = Shader.PropertyToID("_SdfShadowMinStep");
        static readonly int sShadowStartOffset = Shader.PropertyToID("_SdfShadowStartOffset");

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
            if (volume == null || volume.sdfTexture == null) return;

            Shader.SetGlobalTexture(sSdfTex, volume.sdfTexture);

            if (volume.occlusionBitmaskTexture != null) {
                Shader.SetGlobalTexture(sBitmaskTex, volume.occlusionBitmaskTexture);
                Shader.SetGlobalVector(sVoxelResolution,
                    new Vector3(volume.bakedResolution.x, volume.bakedResolution.y, volume.bakedResolution.z));
            }

            Shader.SetGlobalVector(sSdfBoundsMin, volume.bakedBounds.min);
            Shader.SetGlobalVector(sSdfBoundsSize, volume.bakedBounds.size);
            Shader.SetGlobalFloat(sShadowMaxDistance, shadowMaxDistance);
            Shader.SetGlobalInt(sShadowMaxSteps, shadowMaxSteps);
            Shader.SetGlobalFloat(sShadowEpsilon, shadowEpsilon);
            Shader.SetGlobalFloat(sShadowMinStep, shadowMinStep);
            Shader.SetGlobalFloat(sShadowStartOffset, shadowStartOffset);
        }
    }
}
