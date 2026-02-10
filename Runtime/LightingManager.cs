using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lotec.Lighting {
    public static class TextureExtensions {
        public static Vector3 GetResolution(this Texture3D t) {
            if (t == null) return Vector3.zero;
            return new Vector3(t.width, t.height, t.depth);
        }
        public static Vector3Int GetResolutionInt(this Texture3D t) {
            if (t == null) return Vector3Int.zero;
            return new Vector3Int(t.width, t.height, t.depth);
        }
    }

    [ExecuteInEditMode]
    public class LightingManager : MonoBehaviour {
        [SerializeField] GiFieldUpdater _giUpdater;
        SdfShaderGlobals _sdfShaderGlobals; // TODO: Merge SdfShaderGlobals into this class.

        public LightingVolume Volume => _sdfShaderGlobals.volume;
        public GiFieldUpdater GiUpdater => _giUpdater;

#if UNITY_EDITOR
        void Reset() {
            _giUpdater = new GiFieldUpdater();
            // Editor fallback: search the project for a matching compute shader asset by name
            if (_giUpdater.GiComputeShader == null) {
                string[] guids = AssetDatabase.FindAssets("VoxelGiUpdate t:ComputeShader");
                if (guids.Length > 0) {
                    Debug.Log($"Auto-assigning VoxelGiUpdate compute shader to LightingManager from project search (found {guids.Length} candidates, using '{AssetDatabase.GUIDToAssetPath(guids[0])}').");
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    _giUpdater.GiComputeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                    EditorUtility.SetDirty(this);
                } else {
                    Debug.LogWarning("Could not find VoxelGiUpdate compute shader in project. Please assign it manually to the LightingManager.");
                }
            }
        }
#endif

        void Awake() {
            _sdfShaderGlobals = GetComponent<SdfShaderGlobals>();
            AssignComputeShader();
        }

        // Update is called once per frame
        void Update() {
            EnsureFieldsAssigned();
            _giUpdater.Update();
        }

        void OnDisable() {
            _giUpdater.ReleaseBuffers();
        }
        void AssignComputeShader() {
        }

        void EnsureFieldsAssigned() {
            if (_giUpdater == null) return;

            if (_sdfShaderGlobals == null) {
                _sdfShaderGlobals = GetComponent<SdfShaderGlobals>();
            }

            if (_giUpdater.Volume == null) {
                _giUpdater.Volume = Volume;
            }
            if (_giUpdater.MaterialFieldAlbedoRoughness == null) {
                _giUpdater.MaterialFieldAlbedoRoughness = Volume.materialAlbedoRoughnessTexture;
            }
            if (_giUpdater.MaterialFieldEmissionMetallic == null) {
                _giUpdater.MaterialFieldEmissionMetallic = Volume.materialEmissionMetallicTexture;
            }
            if (_giUpdater.SurfaceDistanceFieldHighRes == null) {
                _giUpdater.SurfaceDistanceFieldHighRes = Volume.sdfHiresTexture;
            }
            if (_giUpdater.SurfaceDistanceFieldLowRes == null) {
                _giUpdater.SurfaceDistanceFieldLowRes = Volume.sdfLowresTexture;
            }
        }
    }
}
