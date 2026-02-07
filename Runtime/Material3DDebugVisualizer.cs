using UnityEngine;

namespace Lotec.Lighting {
    [AddComponentMenu("Lotec/Material 3D Debug Visualizer")]
    [ExecuteAlways]
    public class Material3DDebugVisualizer : MonoBehaviour {
        public bool visualize = true;

        public enum FieldType { AlbedoRoughness, EmissionMetallic }
        public FieldType field = FieldType.EmissionMetallic;

        [Min(1)] public int skip = 1;
        [Min(0f)] public float sphereRadius = 0.02f;

        Texture3D _currentTex;
        Color[] _cachedPixels;
        int _rx, _ry, _rz;

        public LightingVolume Volume => _sdfShaderGlobals.volume;
        SdfShaderGlobals _sdfShaderGlobals;

        void OnEnable() {
            CacheIfNeeded();
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

            Texture3D tex = (field == FieldType.EmissionMetallic) ? Volume.materialEmissionMetallicTexture : Volume.materialAlbedoRoughnessTexture;
            if (tex == _currentTex && _cachedPixels != null)
                return;

            _currentTex = tex;
            _cachedPixels = null;

            if (_currentTex == null)
                return;

            try {
                _cachedPixels = _currentTex.GetPixels();
                _rx = _currentTex.width;
                _ry = _currentTex.height;
                _rz = _currentTex.depth;
            } catch (System.Exception e) {
                Debug.LogWarning($"Material3DDebugVisualizer: cannot read pixels from texture '{_currentTex.name}': {e.Message}");
                _cachedPixels = null;
            }
        }

        void OnDrawGizmos() {
            if (!visualize || _cachedPixels == null || Volume == null) return;
            if (_rx <= 0 || _ry <= 0 || _rz <= 0) return;

            // Iterate voxels with a skip to reduce draw count
            Bounds bounds = Volume.Bounds;
            for (int z = 0; z < _rz; z += skip) {
                for (int y = 0; y < _ry; y += skip) {
                    for (int x = 0; x < _rx; x += skip) {
                        int idx = x + y * _rx + z * _rx * _ry;
                        if (idx < 0 || idx >= _cachedPixels.Length)
                            continue;

                        Color c = _cachedPixels[idx];
                        c.a = 1f; // show RGB only; alpha channel holds roughness
                        // Compute voxel center in world space using baked bounds
                        Vector3 localNorm = new Vector3((x + 0.5f) / _rx, (y + 0.5f) / _ry, (z + 0.5f) / _rz);
                        Vector3 pos = bounds.min + Vector3.Scale(localNorm, bounds.size);

                        Gizmos.color = c;
                        Gizmos.DrawSphere(pos, sphereRadius);
                    }
                }
            }
        }

        [ContextMenu("Export Texture3D Slices (PNG)")]
        void ExportSlicesToPng() {
            CacheIfNeeded();
            if (_currentTex == null) {
                Debug.LogWarning("Material3DDebugVisualizer: no texture to export.");
                return;
            }
#if UNITY_EDITOR
            try {
                string dir = System.IO.Path.Combine("Assets", "Material3DDebug", _currentTex.name);
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
                    string path = System.IO.Path.Combine(dir, _currentTex.name + $"_slice_{z}.png");
                    System.IO.File.WriteAllBytes(path, png);
                    UnityEditor.AssetDatabase.ImportAsset(path.Replace("\\", "/"));
                    DestroyImmediate(t2);
                }
                UnityEditor.AssetDatabase.Refresh();
                Debug.Log($"Material3DDebugVisualizer: exported {_rz} slices to Assets/Material3DDebug/{_currentTex.name}");
            } catch (System.Exception e) {
                Debug.LogWarning("Material3DDebugVisualizer: export failed: " + e.Message);
            }
#else
            Debug.LogWarning("ExportSlicesToPng is editor-only.");
#endif
        }
    }
}
