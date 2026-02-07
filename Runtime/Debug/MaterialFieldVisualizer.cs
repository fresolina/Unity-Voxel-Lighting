using UnityEngine;

namespace Lotec.Lighting {
    [ExecuteAlways]
    public class MaterialFieldVisualizer : VoxelFieldVisualizerBase {
        public enum FieldType { AlbedoRoughness, EmissionMetallic }
        public FieldType field = FieldType.AlbedoRoughness;

        public LightingVolume Volume => _sdfShaderGlobals != null ? _sdfShaderGlobals.volume : null;
        SdfShaderGlobals _sdfShaderGlobals;

        protected override Texture GetTexture() {
            if (_sdfShaderGlobals == null)
                _sdfShaderGlobals = FindAnyObjectByType<SdfShaderGlobals>();

            if (Volume == null) return null;
            return (field == FieldType.EmissionMetallic)
                ? Volume.materialEmissionMetallicTexture
                : Volume.materialAlbedoRoughnessTexture;
        }

        protected override bool TryGetBounds(out Bounds bounds) {
            if (_sdfShaderGlobals == null)
                _sdfShaderGlobals = FindAnyObjectByType<SdfShaderGlobals>();

            if (Volume == null) {
                bounds = default;
                return false;
            }

            bounds = Volume.Bounds;
            return true;
        }

        protected override Color ProcessColor(Color c) {
            c.a = 1f; // show RGB only; alpha channel holds roughness
            return c;
        }
    }
}
