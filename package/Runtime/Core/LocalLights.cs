using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lotec.Lighting {
    /// <summary>
    /// Finds and publishes the scene's realtime point/spot lights - as shader globals for fragment
    /// direct lighting, and onto the compute shader for the GI solve. There is nothing to place and
    /// nothing to list: put a point or spot light in the scene and it lights.
    ///
    /// THE RULE: every active point/spot light, minus the ones a <see cref="VoxelLights"/> list has
    /// already baked into a voxelization. That subtraction is what makes the old "don't list the same
    /// light in both places" mistake unrepresentable - a baked light cannot also arrive here, so it can
    /// never light the scene twice. It also needs no editor-only API, unlike asking a Light whether it
    /// is Baked, which is why the rule is phrased this way round.
    ///
    /// DISCOVERY IS SPLIT FROM PACKING, which is what makes scanning affordable. Discovery walks every
    /// Light in the scene and allocates, so it runs only on a scene load/unload, on an explicit
    /// <see cref="Refresh"/>, and on a slow throttle as a safety net for lights spawned at runtime.
    /// Packing - positions, colours, the budget - runs every frame off the discovered set, so lights
    /// still move perfectly smoothly; only the SET is rediscovered rarely.
    ///
    /// The per-frame rebuild is lazy and cached, and that is what removes any ordering dependency:
    /// whoever asks first in a frame triggers it and everyone else gets that same build, so nothing
    /// depends on script execution order. The ticks at the bottom only cover the frame in which nothing
    /// asks - GI switched off, where the fragment globals still have to be published.
    /// </summary>
    public static class LocalLights {
        // Keep in sync with MAX_POINT_LIGHTS / MAX_SPOT_LIGHTS in VoxelGiUpdate.compute and in the
        // fragment shaders.
        public const int MaxPointLights = 4;
        public const int MaxSpotLights = 4;

        // Safety net for lights that appear without a scene load (a spawned pickup, a dropped torch).
        // Deliberately slow: discovery allocates, and anything that needs to be instant can call Refresh.
        const float RediscoverInterval = 0.25f;

        static readonly int s_pointLightCount = Shader.PropertyToID("_PointLightCount");
        static readonly int s_pointLightPositionRange = Shader.PropertyToID("_PointLightPositionRange");
        static readonly int s_pointLightColor = Shader.PropertyToID("_PointLightColor");
        static readonly int s_spotLightCount = Shader.PropertyToID("_SpotLightCount");
        static readonly int s_spotLightPositionRange = Shader.PropertyToID("_SpotLightPositionRange");
        static readonly int s_spotLightDirectionAngleScale = Shader.PropertyToID("_SpotLightDirectionAngleScale");
        static readonly int s_spotLightColorAngleOffset = Shader.PropertyToID("_SpotLightColorAngleOffset");

        // The packed arrays the shaders read.
        static readonly Vector4[] s_pointLightPositionRanges = new Vector4[MaxPointLights];
        static readonly Vector4[] s_pointLightColors = new Vector4[MaxPointLights];
        static readonly Vector4[] s_spotLightPositionRanges = new Vector4[MaxSpotLights];
        static readonly Vector4[] s_spotLightDirectionAngleScales = new Vector4[MaxSpotLights];
        static readonly Vector4[] s_spotLightColorAngleOffsets = new Vector4[MaxSpotLights];

        // The discovered set, and the per-frame working lists built from it.
        static readonly List<Light> s_discovered = new List<Light>();
        static readonly List<VoxelLights> s_holders = new List<VoxelLights>();
        static readonly HashSet<Light> s_baked = new HashSet<Light>();
        static readonly List<Light> s_points = new List<Light>();
        static readonly List<Light> s_spots = new List<Light>();
        static int s_pointCount;
        static int s_spotCount;
        static int s_builtFrame = -1;
        static float s_nextDiscover;
        static bool s_hooked;
        // The camera the budget is ranked from, resolved on discovery and read for its position each
        // frame. A destroyed camera compares null and is re-resolved on the next discovery.
        static Transform s_viewer;

        /// <summary>Rediscover the scene's lights now instead of waiting for the throttle. Call it after
        /// spawning a light that has to be lit in the same frame; everything else is picked up on its
        /// own.</summary>
        public static void Refresh() {
            Discover();
            s_builtFrame = -1;
        }

        /// <summary>Set this frame's lights as uniforms on a compute shader (the GI solve). Builds them
        /// first if nothing has yet this frame, so the solve can never run against a stale or
        /// unpublished set regardless of script execution order.</summary>
        public static void ApplyToCompute(ComputeShader cs) {
            if (cs == null) return;
            EnsureBuilt();
            cs.SetInt(s_pointLightCount, s_pointCount);
            cs.SetVectorArray(s_pointLightPositionRange, s_pointLightPositionRanges);
            cs.SetVectorArray(s_pointLightColor, s_pointLightColors);
            cs.SetInt(s_spotLightCount, s_spotCount);
            cs.SetVectorArray(s_spotLightPositionRange, s_spotLightPositionRanges);
            cs.SetVectorArray(s_spotLightDirectionAngleScale, s_spotLightDirectionAngleScales);
            cs.SetVectorArray(s_spotLightColorAngleOffset, s_spotLightColorAngleOffsets);
        }

        /// <summary>The realtime lights currently being published, in the order they fill the budget
        /// (most to least relevant - see <see cref="Contribution"/>). Fills <paramref name="into"/>,
        /// cleared first, so callers can reuse a scratch list.</summary>
        public static void Gather(List<Light> into) {
            if (into == null) return;
            EnsureBuilt();
            into.Clear();
            for (int i = 0; i < s_pointCount; i++) into.Add(s_points[i]);
            for (int i = 0; i < s_spotCount; i++) into.Add(s_spots[i]);
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

        // Walk every Light in the loaded scenes and keep the realtime point/spot ones. Allocates, so it
        // is throttled by the callers below rather than run per frame.
        static void Discover() {
            s_nextDiscover = Time.realtimeSinceStartup + RediscoverInterval;
            s_viewer = ResolveViewer();

            // The baked set first: those lights are already emissive voxels, so publishing them here
            // would light the scene twice.
            s_baked.Clear();
            LightEmissionBake.CollectHolders(s_holders);
            for (int h = 0; h < s_holders.Count; h++) {
                IReadOnlyList<Light> baked = s_holders[h].Lights;
                for (int i = 0; i < baked.Count; i++)
                    if (baked[i] != null) s_baked.Add(baked[i]);
            }

            s_discovered.Clear();
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include)) {
                if (light == null || s_baked.Contains(light)) continue;
                // Type only - NOT isActiveAndEnabled: a switched-off torch has to stay in the discovered
                // set so switching it on lights up without waiting for the next rediscovery. The
                // per-frame pack applies the real support test.
                if (light.type != LightType.Point && light.type != LightType.Spot) continue;
                s_discovered.Add(light);
#if UNITY_EDITOR
                WarnIfBakedSpot(light);
#endif
            }
        }

        static void EnsureBuilt() {
            EnsureHooked();
            // In play mode one build per frame is right and cheap. In EDIT mode Time.frameCount is not a
            // reliable clock - the editor can repaint (and render) many times over without the player
            // loop advancing it - so a frame-keyed cache there would serve stale lights indefinitely.
            if (Application.isPlaying && s_builtFrame == Time.frameCount) return;
            s_builtFrame = Application.isPlaying ? Time.frameCount : -1;
            if (Time.realtimeSinceStartup >= s_nextDiscover) Discover();
            Select();
            Pack();
            ApplyGlobals();
        }

        // Fill the budget with the most RELEVANT lights rather than the first ones found: brightness
        // over distance squared from the viewer, which is what a surface at the camera would actually
        // receive. Walking toward a torch makes it light you and lets the one behind you yield its slot,
        // and it means a scene may hold any number of lights without an authored ranking.
        static void Select() {
            s_points.Clear();
            s_spots.Clear();
            // Position read per frame from the camera resolved during discovery, so the ranking follows
            // the viewer without Camera.allCameras allocating every frame.
            bool ranked = s_viewer != null;
            Vector3 eye = ranked ? s_viewer.position : Vector3.zero;

            for (int i = 0; i < s_discovered.Count; i++) {
                Light light = s_discovered[i];
                if (IsSupportedPointLight(light)) InsertRanked(s_points, light, eye, ranked, MaxPointLights);
                else if (IsSupportedSpotLight(light)) InsertRanked(s_spots, light, eye, ranked, MaxSpotLights);
            }
        }

        // Insertion into a list capped at the budget: O(budget) per light with budget = 4, and it never
        // sorts the whole scene. Without a camera to rank against (a headless or editor-only context)
        // the discovered order is kept, which is at least stable.
        static void InsertRanked(List<Light> into, Light light, Vector3 eye, bool ranked, int budget) {
            if (!ranked) {
                if (into.Count < budget) into.Add(light);
                return;
            }
            float score = Contribution(light, eye);
            int at = into.Count;
            while (at > 0 && Contribution(into[at - 1], eye) < score) at--;
            if (at >= budget) return;             // loses to a full budget of brighter lights
            into.Insert(at, light);
            if (into.Count > budget) into.RemoveAt(budget);
        }

        /// <summary>How much this light matters to a viewer at <paramref name="eye"/>: intensity over
        /// distance squared, the falloff a surface there would actually see. Distance is floored so a
        /// light at the camera cannot divide by zero.</summary>
        static float Contribution(Light light, Vector3 eye) {
            float d2 = Mathf.Max((light.transform.position - eye).sqrMagnitude, 1e-4f);
            return light.intensity / d2;
        }

        // Whose viewpoint the budget is ranked from. Camera.main first, but deliberately NOT only:
        // Camera.main needs the MainCamera TAG, and plenty of rigs never set it - a VR rig's eye
        // anchors and a flatscreen debug camera both commonly go untagged. Falling back to any enabled
        // camera keeps the ranking meaningful there instead of silently degrading to "whichever four
        // lights were found first", which is the kind of thing that looks like a lighting bug.
        static Transform ResolveViewer() {
            Camera main = Camera.main;
            if (main != null) return main.transform;
            Camera[] all = Camera.allCameras;    // enabled cameras only; allocates, hence discovery-time
            return all.Length > 0 ? all[0].transform : null;
        }

        static void Pack() {
            s_pointCount = Mathf.Min(s_points.Count, MaxPointLights);
            s_spotCount = Mathf.Min(s_spots.Count, MaxSpotLights);

            for (int i = 0; i < s_pointCount; i++) {
                Light light = s_points[i];
                Vector3 position = light.transform.position;
                s_pointLightPositionRanges[i] = new Vector4(position.x, position.y, position.z, light.range);
                // FinalColor, not color * intensity: see LightExtensions - the raw colour is sRGB and
                // would light the scene in the wrong hue next to URP's own (already converted) lights.
                s_pointLightColors[i] = light.FinalColor();
            }
            for (int i = 0; i < s_spotCount; i++) {
                Light light = s_spots[i];
                Vector3 position = light.transform.position;
                Vector3 direction = light.transform.forward;
                float outerCos = Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f);
                float innerCos = Mathf.Cos(light.innerSpotAngle * Mathf.Deg2Rad * 0.5f);
                float angleScale = 1f / Mathf.Max(innerCos - outerCos, 1e-4f);
                float angleOffset = -outerCos * angleScale;
                Vector4 color = light.FinalColor();
                s_spotLightPositionRanges[i] = new Vector4(position.x, position.y, position.z, light.range);
                s_spotLightDirectionAngleScales[i] =
                    new Vector4(direction.x, direction.y, direction.z, angleScale);
                s_spotLightColorAngleOffsets[i] = new Vector4(color.x, color.y, color.z, angleOffset);
            }
        }

        static void ApplyGlobals() {
            Shader.SetGlobalInt(s_pointLightCount, s_pointCount);
            Shader.SetGlobalVectorArray(s_pointLightPositionRange, s_pointLightPositionRanges);
            Shader.SetGlobalVectorArray(s_pointLightColor, s_pointLightColors);
            Shader.SetGlobalInt(s_spotLightCount, s_spotCount);
            Shader.SetGlobalVectorArray(s_spotLightPositionRange, s_spotLightPositionRanges);
            Shader.SetGlobalVectorArray(s_spotLightDirectionAngleScale, s_spotLightDirectionAngleScales);
            Shader.SetGlobalVectorArray(s_spotLightColorAngleOffset, s_spotLightColorAngleOffsets);
        }

        // A scene coming or going changes the light set wholesale, and waiting up to RediscoverInterval
        // for that would show a level lit by the previous level's lamps.
        static void EnsureHooked() {
            if (s_hooked) return;
            s_hooked = true;
            SceneManager.sceneLoaded += (scene, mode) => s_nextDiscover = 0f;
            SceneManager.sceneUnloaded += scene => s_nextDiscover = 0f;
        }

#if UNITY_EDITOR
        static readonly HashSet<Light> s_warnedBakedSpot = new HashSet<Light>();

        // Only POINT lights can be baked (a cone cannot be expressed by a voxel that radiates equally in
        // all directions), so a spot marked Baked or Mixed would otherwise light nothing at all in this
        // renderer. It is adopted as a realtime light instead - which is the useful outcome - but that
        // silently overrides what the author asked for, so say it once. Editor-only because
        // lightmapBakeType is: in a player the light is simply realtime, which is the same result.
        static void WarnIfBakedSpot(Light light) {
            if (light.type != LightType.Spot || light.lightmapBakeType == LightmapBakeType.Realtime) return;
            if (!s_warnedBakedSpot.Add(light)) return;
            Debug.LogWarning(
                $"Local lights: spot light '{light.name}' is set to {light.lightmapBakeType}, but only " +
                "POINT lights can be baked into the voxelization. It is being lit as a REALTIME light " +
                "instead, which costs one of the spot budget slots. Set it to Realtime to say so " +
                "explicitly, or make it a point light if you wanted it baked.", light);
        }
#endif

        // The catch-all tick. Everything that consumes local lights already builds them on the way in;
        // this only covers the frame where nothing does (GI off, fragment globals still needed).
        // Idempotent thanks to the per-frame cache. SubsystemRegistration so it re-installs with
        // "Reload Domain" switched off.
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
