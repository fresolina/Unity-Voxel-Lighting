using System.Collections.Generic;
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
    [RequireComponent(typeof(SdfShaderGlobals))]
    [RequireComponent(typeof(GiFieldUpdater))]
    public class LightingManager : MonoBehaviour {
        public static LightingManager Instance { get; private set; }

        // Keep in sync with MAX_POINT_LIGHTS and MAX_SPOT_LIGHTS in VoxelGiUpdate.compute.
        const int MaxPointLights = 4;
        const int MaxSpotLights = 4;

        [Header("Source")]
        [SerializeField] LightingVolume _volume;
        [SerializeField] GiFieldUpdater _giUpdater;

        [Header("Additional Lights")]
        [Tooltip("Extra runtime GI lights. The first 4 supported point lights and the first 4 supported spot lights are injected.")]
        [SerializeField] List<Light> _additionalLights = new List<Light>();

        SdfShaderGlobals _sdfShaderGlobals; // TODO: Merge SdfShaderGlobals into this class.
        Vector4[] _pointLightPositionRanges = new Vector4[MaxPointLights];
        Vector4[] _pointLightColors = new Vector4[MaxPointLights];
        Vector4[] _spotLightPositionRanges = new Vector4[MaxSpotLights];
        Vector4[] _spotLightDirectionAngleScales = new Vector4[MaxSpotLights];
        Vector4[] _spotLightColorAngleOffsets = new Vector4[MaxSpotLights];

        public LightingVolume Volume => _volume;
        public GiFieldUpdater GiUpdater => _giUpdater;

        void Awake() {
            Instance = this;
            Debug.Log($"LightingManager Awake: Instance set. Volume assigned? {_volume != null} ({_volume?.gameObject?.name ?? "null"})", this);
            EnsureFieldsAssigned();
        }

        void OnEnable() {
            EnsureFieldsAssigned();
        }

        // Update is called once per frame
        void Update() {
            EnsureFieldsAssigned();
        }

        void EnsureFieldsAssigned() {
            _sdfShaderGlobals = GetComponent<SdfShaderGlobals>();
            if (_sdfShaderGlobals != null) {
                _sdfShaderGlobals.Volume = _volume;
            }

            if (_giUpdater == null) {
                _giUpdater = GetComponent<GiFieldUpdater>();
                if (_giUpdater == null) return;
            }

            if (_giUpdater.Volume != Volume) {
                _giUpdater.Volume = Volume;
            }
#if UNITY_EDITOR
            if (!Application.isPlaying) {
                _giUpdater.Volume = Volume;
            }
#endif
        }

        internal int GetLocalLightStateHash() {
            unchecked {
                int hash = 17;
                int pointLightCount = 0;
                int spotLightCount = 0;

                for (int i = 0; i < _additionalLights.Count; i++) {
                    Light light = _additionalLights[i];
                    if (IsSupportedPointLight(light) && pointLightCount < MaxPointLights) {
                        Vector3 position = light.transform.position;
                        Color color = light.color;
                        hash = (hash * 31) + ((int)light.type);
                        hash = (hash * 31) + position.x.GetHashCode();
                        hash = (hash * 31) + position.y.GetHashCode();
                        hash = (hash * 31) + position.z.GetHashCode();
                        hash = (hash * 31) + color.r.GetHashCode();
                        hash = (hash * 31) + color.g.GetHashCode();
                        hash = (hash * 31) + color.b.GetHashCode();
                        hash = (hash * 31) + light.range.GetHashCode();
                        hash = (hash * 31) + light.intensity.GetHashCode();
                        pointLightCount++;
                    } else if (IsSupportedSpotLight(light) && spotLightCount < MaxSpotLights) {
                        Vector3 position = light.transform.position;
                        Vector3 direction = light.transform.forward;
                        Color color = light.color;
                        hash = (hash * 31) + ((int)light.type);
                        hash = (hash * 31) + position.x.GetHashCode();
                        hash = (hash * 31) + position.y.GetHashCode();
                        hash = (hash * 31) + position.z.GetHashCode();
                        hash = (hash * 31) + direction.x.GetHashCode();
                        hash = (hash * 31) + direction.y.GetHashCode();
                        hash = (hash * 31) + direction.z.GetHashCode();
                        hash = (hash * 31) + color.r.GetHashCode();
                        hash = (hash * 31) + color.g.GetHashCode();
                        hash = (hash * 31) + color.b.GetHashCode();
                        hash = (hash * 31) + light.range.GetHashCode();
                        hash = (hash * 31) + light.intensity.GetHashCode();
                        hash = (hash * 31) + light.spotAngle.GetHashCode();
                        hash = (hash * 31) + light.innerSpotAngle.GetHashCode();
                        spotLightCount++;
                    }

                    if (pointLightCount >= MaxPointLights && spotLightCount >= MaxSpotLights) {
                        break;
                    }
                }

                hash = (hash * 31) + pointLightCount;
                return (hash * 31) + spotLightCount;
            }
        }

        internal void SetPointLightShaderUniforms(ComputeShader computeShader, int countPropertyId, int positionRangePropertyId, int colorPropertyId) {
            if (computeShader == null) {
                return;
            }

            int pointLightCount = 0;
            for (int i = 0; i < _additionalLights.Count; i++) {
                Light light = _additionalLights[i];
                if (!IsSupportedPointLight(light)) {
                    continue;
                }

                Vector3 position = light.transform.position;
                _pointLightPositionRanges[pointLightCount] = new Vector4(position.x, position.y, position.z, light.range);
                _pointLightColors[pointLightCount] = (Vector4)light.color * light.intensity;
                pointLightCount++;

                if (pointLightCount >= MaxPointLights) {
                    break;
                }
            }

            computeShader.SetInt(countPropertyId, pointLightCount);
            computeShader.SetVectorArray(positionRangePropertyId, _pointLightPositionRanges);
            computeShader.SetVectorArray(colorPropertyId, _pointLightColors);
        }

        internal void SetSpotLightShaderUniforms(ComputeShader computeShader, int countPropertyId, int positionRangePropertyId, int directionAngleScalePropertyId, int colorAngleOffsetPropertyId) {
            if (computeShader == null) {
                return;
            }

            int spotLightCount = 0;
            for (int i = 0; i < _additionalLights.Count; i++) {
                Light light = _additionalLights[i];
                if (!IsSupportedSpotLight(light)) {
                    continue;
                }

                Vector3 position = light.transform.position;
                Vector3 direction = light.transform.forward;
                float outerCos = Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f);
                float innerCos = Mathf.Cos(light.innerSpotAngle * Mathf.Deg2Rad * 0.5f);
                float angleRange = Mathf.Max(innerCos - outerCos, 1e-4f);
                float angleScale = 1f / angleRange;
                float angleOffset = -outerCos * angleScale;

                _spotLightPositionRanges[spotLightCount] = new Vector4(position.x, position.y, position.z, light.range);
                _spotLightDirectionAngleScales[spotLightCount] = new Vector4(direction.x, direction.y, direction.z, angleScale);
                _spotLightColorAngleOffsets[spotLightCount] = new Vector4(light.color.r * light.intensity, light.color.g * light.intensity, light.color.b * light.intensity, angleOffset);
                spotLightCount++;

                if (spotLightCount >= MaxSpotLights) {
                    break;
                }
            }

            computeShader.SetInt(countPropertyId, spotLightCount);
            computeShader.SetVectorArray(positionRangePropertyId, _spotLightPositionRanges);
            computeShader.SetVectorArray(directionAngleScalePropertyId, _spotLightDirectionAngleScales);
            computeShader.SetVectorArray(colorAngleOffsetPropertyId, _spotLightColorAngleOffsets);
        }

        static bool IsSupportedPointLight(Light light) {
            return light != null &&
                   light.isActiveAndEnabled &&
                   light.type == LightType.Point &&
                   light.range > 0f &&
                   light.intensity > 0f;
        }

        static bool IsSupportedSpotLight(Light light) {
            return light != null &&
                   light.isActiveAndEnabled &&
                   light.type == LightType.Spot &&
                   light.range > 0f &&
                   light.intensity > 0f &&
                   light.spotAngle > 0f;
        }
    }
}
