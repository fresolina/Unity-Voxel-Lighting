using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Generic 3D field visualizer base class that handles readback and gizmo drawing.
    /// Derive and implement texture/bounds providers for specific fields.
    /// </summary>
    [ExecuteAlways]
    public abstract class VoxelFieldVisualizerBase : MonoBehaviour {
        [Min(1)] public int skip = 2;
        [Min(0f)] public float sphereRadius = 0.02f;
        [Min(1)] public int refreshInterval = 10;
        [SerializeField] bool _showInGameView;

        Texture _currentTex;
        Color[] _cachedPixels;
        int _rx, _ry, _rz;
        bool _readbackInProgress;
        int _lastReadbackFrame = -9999;

        static Material _glMaterial;

        protected virtual string ConversionShaderName => null;

        protected abstract Texture GetTexture();
        protected abstract bool TryGetBounds(out Bounds bounds);

        protected virtual Color ProcessColor(Color c) => c;

        void OnEnable() {
            _lastReadbackFrame = -9999;
            TryStartReadback();
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        void OnDisable() {
            _cachedPixels = null;
            _readbackInProgress = false;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        }

        void OnValidate() {
            _lastReadbackFrame = -9999;
            TryStartReadback();
        }

        void Update() {
            if (Time.frameCount - _lastReadbackFrame >= refreshInterval) {
                TryStartReadback();
            }
        }

        // Readback is throttled to once every refreshInterval frames.
        // _cachedPixels is never nulled between readbacks — the last good frame stays visible.
        void TryStartReadback() {
            Texture tex = GetTexture();
            if (tex == null) return;
            if (_readbackInProgress) return;

            _currentTex = tex;
            _lastReadbackFrame = Time.frameCount;
            _readbackInProgress = true;
            Texture3DReadback.ReadbackRGBAAsync(_currentTex, ConversionShaderName, (success, pixels, w, h, d) => {
                _readbackInProgress = false;
                if (!success) {
                    Debug.LogWarning($"{GetType().Name}: cannot read pixels from texture '{_currentTex?.name}'.");
                    return;
                }

                _cachedPixels = pixels;
                _rx = w;
                _ry = h;
                _rz = d;
            });
        }

        void OnDrawGizmos() {
            if (_showInGameView) return;
            if (!enabled) return;
            if (_cachedPixels == null) return;
            if (_rx <= 0 || _ry <= 0 || _rz <= 0) return;
            if (!TryGetBounds(out Bounds bounds)) return;

            for (int z = 0; z < _rz; z += skip) {
                for (int y = 0; y < _ry; y += skip) {
                    for (int x = 0; x < _rx; x += skip) {
                        int idx = x + y * _rx + z * _rx * _ry;
                        if (idx < 0 || idx >= _cachedPixels.Length) continue;

                        Color c = ProcessColor(_cachedPixels[idx]);
                        Vector3 localNorm = new Vector3((x + 0.5f) / _rx, (y + 0.5f) / _ry, (z + 0.5f) / _rz);
                        Vector3 pos = bounds.min + Vector3.Scale(localNorm, bounds.size);

                        Gizmos.color = c;
                        Gizmos.DrawSphere(pos, sphereRadius);
                    }
                }
            }
        }

        // Game-view rendering using the URP end-camera callback so draws land on top
        // of the final blitted image. GL.Lines are batched into a single draw call.
        void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam) {
            if (!_showInGameView) return;
            if (!enabled) return;
            if (cam.cameraType != CameraType.Game) return;
            if (_cachedPixels == null) return;
            if (_rx <= 0 || _ry <= 0 || _rz <= 0) return;
            if (!TryGetBounds(out Bounds bounds)) return;

            Material mat = GetOrCreateGLMaterial();
            if (mat == null) return;

            mat.SetPass(0);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;
            GL.Begin(GL.LINES);

            float r = sphereRadius;
            for (int z = 0; z < _rz; z += skip) {
                for (int y = 0; y < _ry; y += skip) {
                    for (int x = 0; x < _rx; x += skip) {
                        int idx = x + y * _rx + z * _rx * _ry;
                        if (idx < 0 || idx >= _cachedPixels.Length) continue;

                        Color c = ProcessColor(_cachedPixels[idx]);
                        Vector3 localNorm = new Vector3((x + 0.5f) / _rx, (y + 0.5f) / _ry, (z + 0.5f) / _rz);
                        Vector3 pos = bounds.min + Vector3.Scale(localNorm, bounds.size);

                        GL.Color(c);
                        GL.Vertex3(pos.x - r, pos.y, pos.z); GL.Vertex3(pos.x + r, pos.y, pos.z);
                        GL.Vertex3(pos.x, pos.y - r, pos.z); GL.Vertex3(pos.x, pos.y + r, pos.z);
                        GL.Vertex3(pos.x, pos.y, pos.z - r); GL.Vertex3(pos.x, pos.y, pos.z + r);
                    }
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        static Material GetOrCreateGLMaterial() {
            if (_glMaterial != null) return _glMaterial;

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;

            _glMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _glMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _glMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _glMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _glMaterial.SetInt("_ZWrite", 0);
            return _glMaterial;
        }
    }
}
