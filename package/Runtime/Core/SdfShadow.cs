using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Publishes the SDF shadow-raymarch tuning (<see cref="SdfShadowConfig"/>) as shader globals,
    /// scaled by the active volume's voxel size. The SDF march is the shader's default shadow path
    /// (no keyword); this component only owns its tuning - disable it to freeze the current values.
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [AddComponentMenu("Lotec/Voxel Lighting/SDF Shadow")]
    public class SdfShadow : MonoBehaviour {
        [SerializeField] SdfShadowConfig _config = new SdfShadowConfig();

        public SdfShadowConfig Config => _config;

        void Update() {
            VoxelVolume volume = LightingManager.Instance != null ? LightingManager.Instance.Volume : null;
            if (volume == null) return;
            _config.ApplyShaderGlobals(volume.VoxelSize);
        }
    }
}
