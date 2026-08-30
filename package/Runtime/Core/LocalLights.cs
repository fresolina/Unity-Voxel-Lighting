using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Publishes the scene's additional point/spot lights - as shader globals for fragment direct
    /// lighting, and onto the compute shader for the GI solve. Every light is authored on a
    /// <see cref="LocalLightsProvider"/>; this collects whatever is registered, packs it, and ships it.
    ///
    /// It is a static service and not a component ON PURPOSE. It used to be a LocalLightsPublisher that
    /// had to be placed by hand, and forgetting it failed in total silence: providers registered, their
    /// lights were on and bright, _PointLightCount stayed 0, and nothing said a word. That cost real
    /// debugging time and needed a runtime warning plus an editor validator to make it survivable.
    /// Nothing to place is nothing to forget, so all of that scaffolding is gone with it.
    ///
    /// The rebuild is LAZY and cached per frame, and that is what removed the ordering problem the
    /// component existed to solve. The publisher needed [DefaultExecutionOrder(-100)] so that it
    /// collected before the GI updaters consumed the data; here whoever asks first in a frame triggers
    /// the build and everyone else gets that same build, so nothing depends on script order. The ticks
    /// at the bottom only cover the frame in which NOTHING asks - GI switched off, where the fragment
    /// globals still have to be published.
    /// </summary>
    public static class LocalLights {
        // Keep in sync with MAX_POINT_LIGHTS / MAX_SPOT_LIGHTS in VoxelGiUpdate.compute and in the
        // fragment shaders.
        public const int MaxPointLights = 4;
        public const int MaxSpotLights = 4;

        static readonly int s_pointLightCount = Shader.PropertyToID("_PointLightCount");
        static readonly int s_pointLightPositionRange = Shader.PropertyToID("_PointLightPositionRange");
        static readonly int s_pointLightColor = Shader.PropertyToID("_PointLightColor");
        static readonly int s_spotLightCount = Shader.PropertyToID("_SpotLightCount");
        static readonly int s_spotLightPositionRange = Shader.PropertyToID("_SpotLightPositionRange");
        static readonly int s_spotLightDirectionAngleScale = Shader.PropertyToID("_SpotLightDirectionAngleScale");
        static readonly int s_spotLightColorAngleOffset = Shader.PropertyToID("_SpotLightColorAngleOffset");

        // The packed arrays the shaders read. Private: they used to be public fields on a separate
        // LocalLightData object, which let any caller write straight into what the GPU consumes.
        static readonly Vector4[] s_pointLightPositionRanges = new Vector4[MaxPointLights];
        static readonly Vector4[] s_pointLightColors = new Vector4[MaxPointLights];
        static readonly Vector4[] s_spotLightPositionRanges = new Vector4[MaxSpotLights];
        static readonly Vector4[] s_spotLightDirectionAngleScales = new Vector4[MaxSpotLights];
        static readonly Vector4[] s_spotLightColorAngleOffsets = new Vector4[MaxSpotLights];

        static readonly List<Light> s_gathered = new List<Light>();
        static readonly List<LocalLightsProvider> s_ordered = new List<LocalLightsProvider>();
        static int s_pointLightCountValue;
        static int s_spotLightCountValue;
        static int s_droppedLights;
        static int s_builtFrame = -1;
        static int s_warnedDroppedCount;

        /// <summary>Set this frame's lights as uniforms on a compute shader (the GI solve). Builds them
        /// first if nothing has yet this frame, so the solve can never run against a stale or
        /// unpublished set regardless of script execution order.</summary>
        public static void ApplyToCompute(ComputeShader cs) {
            if (cs == null) return;
            EnsureBuilt();
            cs.SetInt(s_pointLightCount, s_pointLightCountValue);
            cs.SetVectorArray(s_pointLightPositionRange, s_pointLightPositionRanges);
            cs.SetVectorArray(s_pointLightColor, s_pointLightColors);
            cs.SetInt(s_spotLightCount, s_spotLightCountValue);
            cs.SetVectorArray(s_spotLightPositionRange, s_spotLightPositionRanges);
            cs.SetVectorArray(s_spotLightDirectionAngleScale, s_spotLightDirectionAngleScales);
            cs.SetVectorArray(s_spotLightColorAngleOffset, s_spotLightColorAngleOffsets);
        }

        /// <summary>Every light that would currently be published, in collection order: each registered
        /// <see cref="LocalLightsProvider"/> by descending <see cref="LocalLightsProvider.Priority"/>.
        /// Fills <paramref name="into"/> (cleared first) so callers can reuse a scratch list. Nulls and
        /// duplicates are skipped; whether a light is SUPPORTED (type, range, enabled) is
        /// <see cref="IsSupportedPointLight"/>/<see cref="IsSupportedSpotLight"/>'s business, not
        /// this method's.</summary>
        public static void Gather(List<Light> into) {
            if (into == null) return;
            into.Clear();
            OrderProvidersByPriority(s_ordered);
            for (int p = 0; p < s_ordered.Count; p++) {
                IReadOnlyList<Light> lights = s_ordered[p].Lights;
                for (int i = 0; i < lights.Count; i++) {
                    // A light listed by two overlapping providers must only take one slot of the budget.
                    if (lights[i] != null && !into.Contains(lights[i])) into.Add(lights[i]);
                }
            }
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

        static void EnsureBuilt() {
            // In play mode one build per frame is right and cheap. In EDIT mode Time.frameCount is not a
            // reliable clock - the editor can repaint (and render) many times over without the player
            // loop advancing it - so a frame-keyed cache there would serve stale lights indefinitely.
            // Rebuilding on every call is fine: it walks a handful of providers.
            if (Application.isPlaying && s_builtFrame == Time.frameCount) return;
            s_builtFrame = Application.isPlaying ? Time.frameCount : -1;
            Gather(s_gathered);
            Collect(s_gathered);
            ApplyGlobals();
            WarnIfLightsDropped();
        }

        /// <summary>Fill the packed arrays from the supported point/spot lights in the list, in list
        /// order - so the caller controls priority by ordering (see <see cref="Gather"/>). Lights past
        /// the budget are counted rather than silently ignored.</summary>
        static void Collect(IReadOnlyList<Light> lights) {
            s_pointLightCountValue = 0;
            s_spotLightCountValue = 0;
            s_droppedLights = 0;

            if (lights == null) return;

            for (int i = 0; i < lights.Count; i++) {
                Light light = lights[i];
                if (IsSupportedPointLight(light)) {
                    // Deliberately no early break once both budgets fill: the rest of the list still has
                    // to be walked to count what's being dropped, and these lists hold a handful of items.
                    if (s_pointLightCountValue >= MaxPointLights) { s_droppedLights++; continue; }
                    Vector3 position = light.transform.position;
                    s_pointLightPositionRanges[s_pointLightCountValue] =
                        new Vector4(position.x, position.y, position.z, light.range);
                    // FinalColor, not color * intensity: see LightExtensions - the raw colour is sRGB and
                    // would light the scene in the wrong hue next to URP's own (already converted) lights.
                    s_pointLightColors[s_pointLightCountValue] = light.FinalColor();
                    s_pointLightCountValue++;
                } else if (IsSupportedSpotLight(light)) {
                    if (s_spotLightCountValue >= MaxSpotLights) { s_droppedLights++; continue; }
                    Vector3 position = light.transform.position;
                    Vector3 direction = light.transform.forward;
                    float outerCos = Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f);
                    float innerCos = Mathf.Cos(light.innerSpotAngle * Mathf.Deg2Rad * 0.5f);
                    float angleRange = Mathf.Max(innerCos - outerCos, 1e-4f);
                    float angleScale = 1f / angleRange;
                    float angleOffset = -outerCos * angleScale;

                    Vector4 color = light.FinalColor();
                    s_spotLightPositionRanges[s_spotLightCountValue] =
                        new Vector4(position.x, position.y, position.z, light.range);
                    s_spotLightDirectionAngleScales[s_spotLightCountValue] =
                        new Vector4(direction.x, direction.y, direction.z, angleScale);
                    s_spotLightColorAngleOffsets[s_spotLightCountValue] =
                        new Vector4(color.x, color.y, color.z, angleOffset);
                    s_spotLightCountValue++;
                }
            }
        }

        /// <summary>Publish the packed arrays as global shader uniforms (fragment direct lighting).</summary>
        static void ApplyGlobals() {
            Shader.SetGlobalInt(s_pointLightCount, s_pointLightCountValue);
            Shader.SetGlobalVectorArray(s_pointLightPositionRange, s_pointLightPositionRanges);
            Shader.SetGlobalVectorArray(s_pointLightColor, s_pointLightColors);
            Shader.SetGlobalInt(s_spotLightCount, s_spotLightCountValue);
            Shader.SetGlobalVectorArray(s_spotLightPositionRange, s_spotLightPositionRanges);
            Shader.SetGlobalVectorArray(s_spotLightDirectionAngleScale, s_spotLightDirectionAngleScales);
            Shader.SetGlobalVectorArray(s_spotLightColorAngleOffset, s_spotLightColorAngleOffsets);
        }

        // Highest priority first, registration order preserved within equal priorities. Insertion sort
        // rather than List.Sort because List.Sort is NOT stable and would scramble equal-priority
        // providers from frame to frame, which for a level sitting exactly on the budget edge means the
        // dropped light flickers between torches. N is the number of loaded levels, so the quadratic
        // term is noise.
        static void OrderProvidersByPriority(List<LocalLightsProvider> into) {
            into.Clear();
            IReadOnlyList<LocalLightsProvider> providers = LocalLightsProvider.All;
            for (int i = 0; i < providers.Count; i++) {
                LocalLightsProvider provider = providers[i];
                if (provider == null) continue;
                int at = into.Count;
                while (at > 0 && into[at - 1].Priority < provider.Priority) at--;
                into.Insert(at, provider);
            }
        }

        // The budget is 4 point + 4 spot; more supported lights than that means some simply don't light
        // the scene. Say so once per overflow - silently dropping is the kind of thing that gets debugged
        // as "the torch shader is broken" instead of "the level lists too many lights".
        static void WarnIfLightsDropped() {
            if (s_droppedLights == s_warnedDroppedCount) return;
            s_warnedDroppedCount = s_droppedLights;
            if (s_droppedLights == 0) return;
            Debug.LogWarning(
                $"Local lights: {s_droppedLights} light(s) exceeded the budget of {MaxPointLights} point " +
                $"+ {MaxSpotLights} spot and are not lighting anything. Providers are collected by " +
                "descending LocalLightsProvider.Priority (registration order breaks ties), so the dropped ones are " +
                "at the end of that order. Raise the Priority of the provider that must survive, shorten a list, " +
                "or switch lights off when they're out of reach.");
        }

        // The catch-all tick. Everything that consumes local lights already builds them on the way in;
        // this only covers the frame where nothing does (GI off, fragment globals still needed).
        // Idempotent thanks to the per-frame cache, so it costs one comparison when something already
        // asked. SubsystemRegistration so it re-installs with "Reload Domain" switched off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void InstallRuntimeTick() {
            Application.onBeforeRender -= EnsureBuilt;
            Application.onBeforeRender += EnsureBuilt;
        }

#if UNITY_EDITOR
        // Edit mode has no player loop to rely on, and both the GI solve and direct lighting run there,
        // so the same publish has to happen off the editor tick.
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallEditorTick() {
            UnityEditor.EditorApplication.update -= EditorTick;
            UnityEditor.EditorApplication.update += EditorTick;
        }

        static void EditorTick() {
            if (!Application.isPlaying) EnsureBuilt();
        }
#endif
    }
}
