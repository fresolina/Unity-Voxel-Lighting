using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Collects the supported point/spot lights into the packed arrays consumed by both
    /// the GI compute solve and the fragment-shader direct lighting. Extracted so direct
    /// lighting can be published without the GI updater running.
    /// </summary>
    public class LocalLightData {
        // Keep in sync with MAX_POINT_LIGHTS / MAX_SPOT_LIGHTS in VoxelGiUpdate.compute and
        // in the fragment shaders.
        public const int MaxPointLights = 4;
        public const int MaxSpotLights = 4;

        static readonly int s_pointLightCount = Shader.PropertyToID("_PointLightCount");
        static readonly int s_pointLightPositionRange = Shader.PropertyToID("_PointLightPositionRange");
        static readonly int s_pointLightColor = Shader.PropertyToID("_PointLightColor");
        static readonly int s_spotLightCount = Shader.PropertyToID("_SpotLightCount");
        static readonly int s_spotLightPositionRange = Shader.PropertyToID("_SpotLightPositionRange");
        static readonly int s_spotLightDirectionAngleScale = Shader.PropertyToID("_SpotLightDirectionAngleScale");
        static readonly int s_spotLightColorAngleOffset = Shader.PropertyToID("_SpotLightColorAngleOffset");

        public int PointLightCount { get; private set; }
        public int SpotLightCount { get; private set; }
        /// <summary>Supported lights the last <see cref="Collect"/> had to leave out because the
        /// point/spot budget was already full. Non-zero means those lights light nothing at all, so the
        /// caller should report it rather than let it pass as a shading bug.</summary>
        public int DroppedLights { get; private set; }
        public readonly Vector4[] PointLightPositionRanges = new Vector4[MaxPointLights];
        public readonly Vector4[] PointLightColors = new Vector4[MaxPointLights];
        public readonly Vector4[] SpotLightPositionRanges = new Vector4[MaxSpotLights];
        public readonly Vector4[] SpotLightDirectionAngleScales = new Vector4[MaxSpotLights];
        public readonly Vector4[] SpotLightColorAngleOffsets = new Vector4[MaxSpotLights];

        /// <summary>Fill the arrays from the supported point/spot lights in the list, in list order -
        /// so the caller controls priority by ordering (see LocalLightsPublisher.GatherLights). Lights
        /// past the budget are counted in <see cref="DroppedLights"/> rather than silently ignored.</summary>
        public void Collect(IReadOnlyList<Light> lights) {
            PointLightCount = 0;
            SpotLightCount = 0;
            DroppedLights = 0;

            if (lights == null)
                return;

            for (int i = 0; i < lights.Count; i++) {
                Light light = lights[i];
                if (IsSupportedPointLight(light)) {
                    // Deliberately no early break once both budgets fill: the rest of the list still has
                    // to be walked to count what's being dropped, and these lists hold a handful of items.
                    if (PointLightCount >= MaxPointLights) { DroppedLights++; continue; }
                    Vector3 position = light.transform.position;
                    PointLightPositionRanges[PointLightCount] = new Vector4(position.x, position.y, position.z, light.range);
                    // FinalColor, not color * intensity: see LightExtensions - the raw colour is sRGB and
                    // would light the scene in the wrong hue next to URP's own (already converted) lights.
                    PointLightColors[PointLightCount] = light.FinalColor();
                    PointLightCount++;
                } else if (IsSupportedSpotLight(light)) {
                    if (SpotLightCount >= MaxSpotLights) { DroppedLights++; continue; }
                    Vector3 position = light.transform.position;
                    Vector3 direction = light.transform.forward;
                    float outerCos = Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f);
                    float innerCos = Mathf.Cos(light.innerSpotAngle * Mathf.Deg2Rad * 0.5f);
                    float angleRange = Mathf.Max(innerCos - outerCos, 1e-4f);
                    float angleScale = 1f / angleRange;
                    float angleOffset = -outerCos * angleScale;

                    Vector4 color = light.FinalColor();
                    SpotLightPositionRanges[SpotLightCount] = new Vector4(position.x, position.y, position.z, light.range);
                    SpotLightDirectionAngleScales[SpotLightCount] = new Vector4(direction.x, direction.y, direction.z, angleScale);
                    SpotLightColorAngleOffsets[SpotLightCount] = new Vector4(color.x, color.y, color.z, angleOffset);
                    SpotLightCount++;
                }
            }
        }

        /// <summary>Publish the collected arrays as global shader uniforms (fragment direct lighting).</summary>
        public void ApplyGlobals() {
            Shader.SetGlobalInt(s_pointLightCount, PointLightCount);
            Shader.SetGlobalVectorArray(s_pointLightPositionRange, PointLightPositionRanges);
            Shader.SetGlobalVectorArray(s_pointLightColor, PointLightColors);
            Shader.SetGlobalInt(s_spotLightCount, SpotLightCount);
            Shader.SetGlobalVectorArray(s_spotLightPositionRange, SpotLightPositionRanges);
            Shader.SetGlobalVectorArray(s_spotLightDirectionAngleScale, SpotLightDirectionAngleScales);
            Shader.SetGlobalVectorArray(s_spotLightColorAngleOffset, SpotLightColorAngleOffsets);
        }

        /// <summary>Set the collected arrays as uniforms on a compute shader (the GI solve).</summary>
        public void ApplyToCompute(ComputeShader cs) {
            if (cs == null) return;
            cs.SetInt(s_pointLightCount, PointLightCount);
            cs.SetVectorArray(s_pointLightPositionRange, PointLightPositionRanges);
            cs.SetVectorArray(s_pointLightColor, PointLightColors);
            cs.SetInt(s_spotLightCount, SpotLightCount);
            cs.SetVectorArray(s_spotLightPositionRange, SpotLightPositionRanges);
            cs.SetVectorArray(s_spotLightDirectionAngleScale, SpotLightDirectionAngleScales);
            cs.SetVectorArray(s_spotLightColorAngleOffset, SpotLightColorAngleOffsets);
        }

        public static bool IsSupportedPointLight(Light light) {
            return light != null &&
                   light.isActiveAndEnabled &&
                   light.type == LightType.Point &&
                   light.range > 0f &&
                   light.intensity > 0f;
        }

        public static bool IsSupportedSpotLight(Light light) {
            return light != null &&
                   light.isActiveAndEnabled &&
                   light.type == LightType.Spot &&
                   light.range > 0f &&
                   light.intensity > 0f &&
                   light.spotAngle > 0f;
        }
    }
}
