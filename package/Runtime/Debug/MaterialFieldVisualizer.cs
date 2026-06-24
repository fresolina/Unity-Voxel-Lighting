using UnityEngine;

namespace Lotec.Lighting {
    [ExecuteAlways]
    public class MaterialFieldVisualizer : VoxelFieldVisualizerBase {
        LightingVolume Volume => LightingManager.Instance.Volume;

        protected override Texture GetTexture() {
            if (Volume == null) return null;
            return Volume.TryGetComponent(out VoxelMaterialField material) ? material.materialAlbedoIntensityTexture : null;
        }

        protected override bool TryGetBounds(out Bounds bounds) {
            if (Volume == null) {
                bounds = default;
                return false;
            }

            bounds = Volume.Bounds;
            return true;
        }

        protected override Color ProcessColor(Color c) {
            c.a = 1f; // show RGB only; alpha channel holds emission intensity
            return c;
        }
    }
}
