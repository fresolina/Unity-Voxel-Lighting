using UnityEngine;

namespace Lotec.Lighting {
    [System.Serializable]
    public class SdfAoConfig {
        static readonly int s_aoStep = Shader.PropertyToID("_SdfAoStep");
        static readonly int s_aoIntensity = Shader.PropertyToID("_SdfAoIntensity");

        public enum AoQuality { SDF_AO_OFF = 0, SDF_AO_LQ = 1, SDF_AO_HQ = 2 }

        [field: Min(0.000001f)]
        [field: Tooltip("How far each raymarching step is in world units.")]
        [field: SerializeField] public float Step { get; set; } = 0.08f;
        [field: Min(0f)]
        [field: Tooltip("AO darkening strength.")]
        [field: SerializeField] public float Intensity { get; set; } = 1.95f;
        [field: Tooltip("SDF AO mode: OFF, LQ (2 samples), HQ (4 samples).")]
        [field: SerializeField] public AoQuality SampleQuality { get; set; } = AoQuality.SDF_AO_OFF;

        public void ApplyShaderGlobals() {
            Shader.SetGlobalFloat(s_aoStep, Step);
            Shader.SetGlobalFloat(s_aoIntensity, Intensity);
            switch (SampleQuality) {
                case AoQuality.SDF_AO_LQ: LightingKeywords.SdfAo.Set(LightingKeywords.SdfAoLow); break;
                case AoQuality.SDF_AO_HQ: LightingKeywords.SdfAo.Set(LightingKeywords.SdfAoHigh); break;
                default: LightingKeywords.SdfAo.Reset(); break; // Off = bare default (no keyword)
            }
        }
    }
}
