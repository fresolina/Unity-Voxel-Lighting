using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// SDF ambient occlusion as a toggleable feature: while enabled it publishes the AO tuning
    /// (<see cref="SdfAoConfig"/>) and drives the SDF AO quality keyword group (bare default =
    /// off, SDF_AO_LQ, SDF_AO_HQ); disabling the component turns AO off (resets to bare default).
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [AddComponentMenu("Lotec/Voxel Lighting/SDF Ambient Occlusion")]
    public class SdfAmbientOcclusion : MonoBehaviour {
        [SerializeField] SdfAoConfig _config = new SdfAoConfig();

        public SdfAoConfig Config => _config;

        void Update() {
            _config.ApplyShaderGlobals();
        }

        void OnDisable() {
            LightingKeywords.SdfAo.Reset(); // bare default = AO off
        }
    }
}
