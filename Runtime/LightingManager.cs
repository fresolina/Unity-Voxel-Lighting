using System;
using UnityEngine;

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

        void Awake() {
            _sdfShaderGlobals = GetComponent<SdfShaderGlobals>();
        }

        // Update is called once per frame
        void Update() {
            EnsureFieldsAssigned();
            _giUpdater.Update();
        }

        void OnDisable() {
            _giUpdater.ReleaseBuffers();
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
