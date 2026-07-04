using UnityEngine;

namespace Lotec.Lighting {
    [System.Serializable]
    public class SdfShadowConfig {
        static readonly int s_sMaxSteps = Shader.PropertyToID("_RaymarchMaxSteps");
        static readonly int s_sSoftness = Shader.PropertyToID("_RaymarchSoftness");
        static readonly int s_sEpsilon = Shader.PropertyToID("_RaymarchEpsilon");
        static readonly int s_sMinStep = Shader.PropertyToID("_RaymarchMinStep");
        static readonly int s_sStartOffset = Shader.PropertyToID("_RaymarchStartOffset");

        // Epsilon: hard-hit threshold for the raymarcher (d <= epsilon → full shadow).
        // The hi-res SDF uses high softness for penumbra, so the explicit epsilon only
        // needs to catch near-zero crossings. 0.01 * voxelSize is sufficient.
        const float EpsilonScale = 0.01f;

        [field: Min(1)]
        [field: SerializeField] public int MaxSteps { get; set; } = 64;
        [field: Min(1f)]
        [field: SerializeField] public float Softness { get; set; } = 13.0f;
        [field: Min(0f)]
        [field: Tooltip("Distance, in voxels, to skip the ray forward along the light direction before sampling begins. This does the bulk of the self-shadow avoidance and is the single most important knob - too small (e.g. 0.1) leaves corners self-shadowed and black. ~0.5 works well. Too high: small/thin occluders near the surface are skipped (light leaks).")]
        [field: SerializeField] public float StartOffset { get; set; } = 0.5f;
        [field: Min(0f)]
        [field: Tooltip("Minimum march step, in voxels. Stops the marcher from stalling in the near-surface band and burning the step budget (which makes corners go black when MaxSteps runs out). Larger = fewer steps needed to escape corners, but risks skipping thin walls/occluders.")]
        [field: SerializeField] public float MinStep { get; set; } = 0.1f;

        public void ApplyShaderGlobals(float voxelSize) {
            Shader.SetGlobalInt(s_sMaxSteps, MaxSteps);
            Shader.SetGlobalFloat(s_sSoftness, Softness);
            Shader.SetGlobalFloat(s_sEpsilon, voxelSize * EpsilonScale);
            Shader.SetGlobalFloat(s_sMinStep, voxelSize * MinStep);
            Shader.SetGlobalFloat(s_sStartOffset, voxelSize * StartOffset);
        }
    }
}
