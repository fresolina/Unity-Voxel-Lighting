using UnityEngine;

namespace Lotec.Lighting {
    [System.Serializable]
    public class SdfShadowConfig {
        static readonly int sShadowMaxSteps = Shader.PropertyToID("_SdfShadowMaxSteps");
        static readonly int sShadowSoftness = Shader.PropertyToID("_SdfShadowSoftness");
        static readonly int sShadowEpsilon = Shader.PropertyToID("_SdfShadowEpsilon");
        static readonly int sShadowMinStep = Shader.PropertyToID("_SdfShadowMinStep");
        static readonly int sShadowStartOffset = Shader.PropertyToID("_SdfShadowStartOffset");

        [field: Min(1)]
        [field: SerializeField] public int MaxSteps { get; set; } = 64;
        [field: Min(0.000001f)]
        [field: SerializeField] public float Epsilon { get; set; } = 0.001f;
        [field: Min(1f)]
        [field: SerializeField] public float Softness { get; set; } = 13.0f;
        [field: Min(0.000001f)]
        [field: SerializeField] public float MinStep { get; set; } = 0.06f;
        [field: Min(0f)]
        [field: SerializeField] public float StartOffset { get; set; } = 0.06f;

        public void ApplyShaderGlobals() {
            Shader.SetGlobalInt(sShadowMaxSteps, MaxSteps);
            Shader.SetGlobalFloat(sShadowSoftness, Softness);
            Shader.SetGlobalFloat(sShadowEpsilon, Epsilon);
            Shader.SetGlobalFloat(sShadowMinStep, MinStep);
            Shader.SetGlobalFloat(sShadowStartOffset, StartOffset);
        }
    }
}
