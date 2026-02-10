using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Generic 3D field visualizer base class that handles readback and gizmo drawing.
    /// Derive and implement texture/bounds providers for specific fields.
    /// </summary>
    [ExecuteAlways]
    public abstract class VoxelFieldVisualizerBase : MonoBehaviour {
        [Min(1)] public int skip = 2;
        [Min(0f)] public float sphereRadius = 0.02f;

        Texture _currentTex;
        Color[] _cachedPixels;
        int _rx, _ry, _rz;
        bool _readbackInProgress;

        protected virtual string ConversionShaderName => null;

        protected abstract Texture GetTexture();
        protected abstract bool TryGetBounds(out Bounds bounds);

        protected virtual Color ProcessColor(Color c) => c;

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
            Texture tex = GetTexture();
            if (tex == _currentTex && _cachedPixels != null)
                return;

            _currentTex = tex;
            _cachedPixels = null;

            if (_currentTex == null) return;
            if (_readbackInProgress) return;

            _readbackInProgress = true;
            Texture3DReadback.ReadbackRGBAAsync(_currentTex, ConversionShaderName, (success, pixels, w, h, d) => {
                _readbackInProgress = false;
                if (!success) {
                    Debug.LogWarning($"{GetType().Name}: cannot read pixels from texture '{_currentTex?.name}'.");
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
            if (!enabled) return;
            if (_cachedPixels == null) return;
            if (_rx <= 0 || _ry <= 0 || _rz <= 0) return;
            if (!TryGetBounds(out Bounds bounds)) return;

            for (int z = 0; z < _rz; z += skip) {
                for (int y = 0; y < _ry; y += skip) {
                    for (int x = 0; x < _rx; x += skip) {
                        int idx = x + y * _rx + z * _rx * _ry;
                        if (idx < 0 || idx >= _cachedPixels.Length)
                            continue;

                        Color c = ProcessColor(_cachedPixels[idx]);

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
