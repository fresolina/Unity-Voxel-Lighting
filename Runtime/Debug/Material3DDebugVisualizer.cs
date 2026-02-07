using UnityEngine;

namespace Lotec.Lighting {
    [ExecuteAlways]
    public class Material3DDebugVisualizer : MonoBehaviour {
        public enum FieldType { AlbedoRoughness, EmissionMetallic }
        public FieldType field = FieldType.AlbedoRoughness;

        [Min(1)] public int skip = 2;
        [Min(0f)] public float sphereRadius = 0.02f;

        Texture3D _currentTex;
        Color[] _cachedPixels;
        int _rx, _ry, _rz;
        bool _readbackInProgress;

        public LightingVolume Volume => _sdfShaderGlobals != null ? _sdfShaderGlobals.volume : null;
        SdfShaderGlobals _sdfShaderGlobals;

        void OnEnable() {
            CacheIfNeeded();
        }

        void OnDisable() {
            _cachedPixels = null;
            _readbackInProgress = false;
        }

        void OnValidate() {
            CacheIfNeeded();
        }

        void Update() {
            if (!Application.isPlaying) CacheIfNeeded();
        }

        void CacheIfNeeded() {
            if (_sdfShaderGlobals == null)
                _sdfShaderGlobals = FindAnyObjectByType<SdfShaderGlobals>();

            if (Volume == null) return;

            Texture3D tex = (field == FieldType.EmissionMetallic) ? Volume.materialEmissionMetallicTexture : Volume.materialAlbedoRoughnessTexture;
            if (tex == _currentTex && _cachedPixels != null)
                return;

            _currentTex = tex;
            _cachedPixels = null;

            if (_currentTex == null) return;
            if (_readbackInProgress) return;

            _readbackInProgress = true;
            Texture3DReadback.ReadbackRGBAAsync(_currentTex, (success, pixels, w, h, d) => {
                _readbackInProgress = false;
                if (!success) {
                    Debug.LogWarning($"Material3DDebugVisualizer: cannot read pixels from texture '{_currentTex?.name}'.");
                    _cachedPixels = null;
                    return;
                }

                _cachedPixels = pixels;
                _rx = w;
                _ry = h;
                _rz = d;
            });
        }

        void OnDrawGizmos() {
            if (!enabled || _cachedPixels == null || Volume == null) return;
            if (_rx <= 0 || _ry <= 0 || _rz <= 0) return;

            Bounds bounds = Volume.Bounds;
            for (int z = 0; z < _rz; z += skip) {
                for (int y = 0; y < _ry; y += skip) {
                    for (int x = 0; x < _rx; x += skip) {
                        int idx = x + y * _rx + z * _rx * _ry;
                        if (idx < 0 || idx >= _cachedPixels.Length)
                            continue;

                        Color c = _cachedPixels[idx];
                        c.a = 1f; // show RGB only; alpha channel holds roughness

                        Vector3 localNorm = new Vector3((x + 0.5f) / _rx, (y + 0.5f) / _ry, (z + 0.5f) / _rz);
                        Vector3 pos = bounds.min + Vector3.Scale(localNorm, bounds.size);

                        Gizmos.color = c;
                        Gizmos.DrawSphere(pos, sphereRadius);
                    }
                }
            }
        }
    }
}
