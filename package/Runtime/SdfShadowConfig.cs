using UnityEngine;

namespace Lotec.Lighting {
    [System.Serializable]
    public class SdfShadowConfig {
        static readonly int sMaxSteps = Shader.PropertyToID("_RaymarchMaxSteps");
        static readonly int sSoftness = Shader.PropertyToID("_RaymarchSoftness");
        static readonly int sEpsilon = Shader.PropertyToID("_RaymarchEpsilon");
        static readonly int sMinStep = Shader.PropertyToID("_RaymarchMinStep");
        static readonly int sStartOffset = Shader.PropertyToID("_RaymarchStartOffset");

        // Epsilon: hard-hit threshold for the raymarcher (d <= epsilon → full shadow).
        // The hi-res SDF uses high softness for penumbra, so the explicit epsilon only
        // needs to catch near-zero crossings. 0.01 * voxelSize is sufficient.
        const float EpsilonScale = 0.01f;
        // MinStep: prevents the marcher from stalling near surfaces. Must be small
        // enough to not skip thin walls or corner geometry but large enough to make
        // meaningful progress (avoid burning the step budget near surfaces).
        const float MinStepScale = 0.1f;
        // StartOffset: skips the surface vicinity to avoid self-occlusion. Must clear
        // the epsilon shell without jumping past nearby walls in tight corners.
        const float StartOffsetScale = 0.1f;

        [field: Min(1)]
        [field: SerializeField] public int MaxSteps { get; set; } = 64;
        [field: Min(1f)]
        [field: SerializeField] public float Softness { get; set; } = 13.0f;

        public void ApplyShaderGlobals(float voxelSize) {
            Shader.SetGlobalInt(sMaxSteps, MaxSteps);
            Shader.SetGlobalFloat(sSoftness, Softness);
            Shader.SetGlobalFloat(sEpsilon, voxelSize * EpsilonScale);
            Shader.SetGlobalFloat(sMinStep, voxelSize * MinStepScale);
            Shader.SetGlobalFloat(sStartOffset, voxelSize * StartOffsetScale);
        }
    }
}
