using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    [AddComponentMenu("Lotec/Radiance 3D Debug Visualizer")]
    [ExecuteAlways]
    public class Radiance3DDebugVisualizer : MonoBehaviour {
        public bool visualize = true;
        public VoxelGIUpdate source;

        [Min(1)] public int skip = 4;
        [Min(0f)] public float sphereRadius = 0.02f;
        public bool toneMap = true;
        [Min(0f)] public float exposure = 1.0f;
        [Min(0f)] public float minVisibleLuminance = 0.02f;
        public bool showZeroAsGray = true;

        RenderTexture _currentRt;
        Color[] _cachedPixels;
        int _rx, _ry, _rz;
        bool _readbackInProgress = false;
        Material _unpackMaterial;
        bool _pendingReadbackRequested = false;

        SdfShaderGlobals _sdfShaderGlobals;
        int _lastStatusFrame;

        void OnEnable() {
            CacheIfNeeded();
        }

        void OnDisable() {
            _cachedPixels = null;
            _readbackInProgress = false;
            _pendingReadbackRequested = false;
            if (_unpackMaterial != null) {
                DestroyImmediate(_unpackMaterial);
                _unpackMaterial = null;
            }
        }

        void OnValidate() {
            CacheIfNeeded();
        }

        void Update() {
            CacheIfNeeded();
        }

        void CacheIfNeeded() {
            if (source == null) {
                source = FindAnyObjectByType<VoxelGIUpdate>();
                if (source == null) { LogStatus("CacheIfNeeded: no VoxelGIUpdate found"); return; }
            }

            RenderTexture rt = source.GetCurrentRadianceTexture();
            if (rt == null) { LogStatus("CacheIfNeeded: current radiance RT is null"); return; }
            if (!rt.IsCreated()) rt.Create();
            if (rt.width == 0 || rt.height == 0 || rt.volumeDepth == 0) { LogStatus($"CacheIfNeeded: RT has invalid dims {rt.width}x{rt.height}x{rt.volumeDepth}"); return; }

            if (Time.frameCount - _lastStatusFrame < 10) return;
            _lastStatusFrame = Time.frameCount;

            _currentRt = rt;
            _cachedPixels = null;

            if (_currentRt == null) { LogStatus("CacheIfNeeded: _currentRt is null after assignment"); return; }

            try {
                // Debug.LogFormat("Radiance3DDebugVisualizer: CacheIfNeeded frame={0} reading RT='{1}' id={2} dims={3}x{4}x{5} format={6}",
                //     Time.frameCount, _currentRt.name, _currentRt.GetInstanceID(), _currentRt.width, _currentRt.height, _currentRt.volumeDepth, _currentRt.format);

                if (_readbackInProgress) {
                    Debug.Log("Radiance3DDebugVisualizer: readback already in progress, queuing pending request");
                    _pendingReadbackRequested = true;
                    return;
                }

                EnsureUnpackMaterial();
                var rtLocal = _currentRt;
                _pendingReadbackRequested = false;
                StartAsyncReadback(rtLocal, (success, pixels, w, h, d) => {
                    _readbackInProgress = false;
                    if (!success) {
                        Debug.LogWarning($"Radiance3DDebugVisualizer: async readback failed for RT '{rtLocal?.name}'");
                        _cachedPixels = null;
                    } else {
                        _cachedPixels = pixels;
                        _rx = w; _ry = h; _rz = d;

                        // quick luminance sanity check
                        double sum = 0.0;
                        int sampleStep = Mathf.Max(1, _cachedPixels.Length / 512);
                        for (int i = 0; i < _cachedPixels.Length; i += sampleStep) {
                            Color cc = _cachedPixels[i];
                            sum += 0.2126 * cc.r + 0.7152 * cc.g + 0.0722 * cc.b;
                        }
                        double avg = sum / (double)Mathf.Max(1, _cachedPixels.Length / 512);
                        // Debug.LogFormat("Radiance3DDebugVisualizer: Async readback avg luminance ~ {0}", avg);
                    }
                });
            } catch (System.Exception e) {
                Debug.LogWarning($"Radiance3DDebugVisualizer: cannot start async readback for RT '{_currentRt.name}': {e.Message}");
                _cachedPixels = null;
            }
        }

        void EnsureUnpackMaterial() {
            if (_unpackMaterial != null) return;
            Shader s = Shader.Find("Hidden/Unpack3DRadiance");
            if (s == null) {
                Debug.LogError("Radiance3DDebugVisualizer: shader Hidden/Unpack3DRadiance not found. Make sure Runtime/Shaders/Unpack3DRadiance.shader exists.");
                return;
            }
            _unpackMaterial = new Material(s);
        }

        void StartAsyncReadback(RenderTexture rt, System.Action<bool, Color[], int, int, int> onComplete) {
            if (rt == null) { onComplete(false, null, 0, 0, 0); return; }
            int w = rt.width, h = rt.height, d = rt.volumeDepth;
            Color[] pixels = new Color[w * h * d];
            int remaining = d;
            _readbackInProgress = true;

            for (int zi = 0; zi < d; ++zi) {
                int z = zi; // capture
                RenderTexture tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                tmp.wrapMode = TextureWrapMode.Clamp;
                tmp.filterMode = FilterMode.Bilinear;

                _unpackMaterial.SetTexture("_VolumeTex", rt);
                _unpackMaterial.SetFloat("_SliceZ", (z + 0.5f) / (float)d);
                Graphics.Blit((Texture)null, tmp, _unpackMaterial);

                AsyncGPUReadback.Request(tmp, 0, TextureFormat.RGBAFloat, req => {
                    if (req.hasError) {
                        Debug.LogWarning($"Radiance3DDebugVisualizer: Async readback error on slice {z}");
                    } else {
                        try {
                            var arr = req.GetData<Color>();
                            int baseIdx = z * w * h;
                            for (int i = 0; i < arr.Length && (baseIdx + i) < pixels.Length; ++i) {
                                pixels[baseIdx + i] = arr[i];
                            }
                        } catch (System.Exception e) {
                            Debug.LogWarning($"Radiance3DDebugVisualizer: exception reading slice {z}: {e.Message}");
                        }
                    }

                    RenderTexture.ReleaseTemporary(tmp);
                    remaining--;
                    if (remaining == 0) {
                        onComplete(true, pixels, w, h, d);
                    }
                });
            }
        }

        void OnDrawGizmos() {
            if (!enabled) return;
            if (!visualize) { LogStatus("OnDrawGizmos: visualize=false"); return; }
            if (_cachedPixels == null) { LogStatus("OnDrawGizmos: _cachedPixels is null"); return; }

            LightingVolume volume = GetVolumeSafe();
            if (volume == null) { LogStatus("OnDrawGizmos: no SdfVolume"); return; }
            if (_rx <= 0 || _ry <= 0 || _rz <= 0) { LogStatus($"OnDrawGizmos: invalid dims {_rx}x{_ry}x{_rz}"); return; }

            Bounds bounds = volume.Bounds;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(bounds.center, bounds.size);

            for (int z = 0; z < _rz; z += skip) {
                for (int y = 0; y < _ry; y += skip) {
                    for (int x = 0; x < _rx; x += skip) {
                        int idx = x + y * _rx + z * _rx * _ry;
                        if (idx < 0 || idx >= _cachedPixels.Length)
                            continue;

                        Color c = _cachedPixels[idx];
                        c.a = 1f;
                        if (toneMap) c = ApplyToneMap(c, exposure);

                        float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                        if (lum <= 0.0f && showZeroAsGray) {
                            c = new Color(minVisibleLuminance, minVisibleLuminance, minVisibleLuminance, 1.0f);
                        } else if (lum > 0.0f && lum < minVisibleLuminance) {
                            float scale = minVisibleLuminance / lum;
                            c = new Color(c.r * scale, c.g * scale, c.b * scale, 1.0f);
                        }

                        Vector3 localNorm = new Vector3((x + 0.5f) / _rx, (y + 0.5f) / _ry, (z + 0.5f) / _rz);
                        Vector3 pos = bounds.min + Vector3.Scale(localNorm, bounds.size);

                        Gizmos.color = c.linear;
                        Gizmos.DrawSphere(pos, sphereRadius);
                    }
                }
            }
        }

        static Color ApplyToneMap(Color c, float exposure) {
            Color v = c * Mathf.Max(0.0f, exposure);
            return new Color(
                v.r / (1.0f + v.r),
                v.g / (1.0f + v.g),
                v.b / (1.0f + v.b),
                1.0f
            );
        }

        LightingVolume GetVolumeSafe() {
            if (_sdfShaderGlobals == null) _sdfShaderGlobals = FindAnyObjectByType<SdfShaderGlobals>();
            return _sdfShaderGlobals != null ? _sdfShaderGlobals.volume : null;
        }

        void LogStatus(string msg) {
            if (Time.frameCount - _lastStatusFrame < 30) return;
            _lastStatusFrame = Time.frameCount;
            Debug.Log($"Radiance3DDebugVisualizer: {msg}", this);
        }

        [ContextMenu("Export Texture3D Slices (PNG)")]
        void ExportSlicesToPng() {
            CacheIfNeeded();
            if (_currentRt == null) {
                Debug.LogWarning("Radiance3DDebugVisualizer: no texture to export.");
                return;
            }
#if UNITY_EDITOR
            try {
                string dir = System.IO.Path.Combine("Assets", "Radiance3DDebug", _currentRt.name);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

                for (int z = 0; z < _rz; ++z) {
                    Color[] slice = new Color[_rx * _ry];
                    for (int y = 0; y < _ry; ++y) {
                        for (int x = 0; x < _rx; ++x) {
                            int idx = x + y * _rx + z * _rx * _ry;
                            if (idx >= 0 && idx < _cachedPixels.Length) slice[x + y * _rx] = _cachedPixels[idx];
                            else slice[x + y * _rx] = Color.black;
                        }
                    }

                    Texture2D t2 = new Texture2D(_rx, _ry, TextureFormat.RGBA32, false, true);
                    t2.SetPixels(slice);
                    t2.Apply();
                    byte[] png = t2.EncodeToPNG();
                    string path = System.IO.Path.Combine(dir, _currentRt.name + $"_slice_{z}.png");
                    System.IO.File.WriteAllBytes(path, png);
                    UnityEditor.AssetDatabase.ImportAsset(path.Replace("\\", "/"));
                    DestroyImmediate(t2);
                }
                UnityEditor.AssetDatabase.Refresh();
                Debug.Log($"Radiance3DDebugVisualizer: exported {_rz} slices to Assets/Radiance3DDebug/{_currentRt.name}");
            } catch (System.Exception e) {
                Debug.LogWarning("Radiance3DDebugVisualizer: export failed: " + e.Message);
            }
#else
            Debug.LogWarning("ExportSlicesToPng is editor-only.");
#endif
        }
    }
}
