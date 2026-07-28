using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Collects the supported additional point/spot lights once per frame and publishes them as
    /// shader globals for fragment direct lighting; the GI updaters reuse the same packed data
    /// (<see cref="LocalLights"/>) for their compute solves. Disable the component to turn the
    /// additional lights off (it publishes zero counts on disable).
    ///
    /// Lights come from two places. Its OWN serialized list holds the ones that live in the same scene
    /// this component does - in practice the bootstrap scene's player-carried lights (flashlight, candle).
    /// Lights belonging to a LEVEL scene cannot be referenced from here across the scene boundary, so they
    /// are listed on a <see cref="LocalLightsProvider"/> in that level, which registers itself; this
    /// component appends every registered provider's list to its own each frame.
    ///
    /// The own list is collected FIRST, deliberately: the point/spot budget is small
    /// (<see cref="LocalLightData.MaxPointLights"/> + <see cref="LocalLightData.MaxSpotLights"/>), and a
    /// level full of torches must not be able to crowd out the light in the player's hand. Anything that
    /// doesn't fit is dropped, and that is reported once rather than passing silently.
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    // Collect before the GI updaters (default order 0) consume the packed data.
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("Lotec/Voxel Lighting/Local Lights Publisher")]
    public class LocalLightsPublisher : MonoBehaviour {
        public static LocalLightsPublisher Instance { get; private set; }

        [Tooltip("Extra runtime lights for direct lighting + GI, from THIS scene (the bootstrap's " +
                 "player-carried lights). Lights that belong to a level scene go on a " +
                 "LocalLightsProvider there instead - they are appended to this list every frame. The " +
                 "first 4 supported point lights and the first 4 supported spot lights are injected.")]
        [SerializeField] List<Light> _additionalLights = new List<Light>();

        readonly LocalLightData _localLights = new LocalLightData();
        // Own list + every registered provider's, rebuilt each frame. Reused so the merge allocates
        // nothing after the first grow.
        readonly List<Light> _gathered = new List<Light>();
        int _warnedDroppedCount; // report a new overflow once, not every frame

        /// <summary>The shared packed light data, collected once per frame for both fragment
        /// direct lighting (globals) and the GI compute solve.</summary>
        public LocalLightData LocalLights => _localLights;

        /// <summary>This component's OWN serialized lights (the ones in its scene). Does not include the
        /// per-level providers' - see <see cref="GatherLights"/> for the effective set.</summary>
        public IReadOnlyList<Light> AdditionalLights => _additionalLights;

        void OnEnable() {
            Instance = this;
        }

        void OnDisable() {
            if (Instance == this) Instance = null;
            // Publish zero lights so disabling the component actually turns the lights off.
            _localLights.Collect(null);
            _localLights.ApplyGlobals();
        }

        void Update() {
            GatherLights(_gathered);
            _localLights.Collect(_gathered);
            _localLights.ApplyGlobals();
            WarnIfLightsDropped();
        }

        /// <summary>Every light this publisher would currently publish, in collection order: its own
        /// serialized list first, then each registered <see cref="LocalLightsProvider"/>'s. Fills
        /// <paramref name="into"/> (cleared first) so callers can reuse a scratch list. Nulls and
        /// duplicates are skipped; whether a light is SUPPORTED (type, range, enabled) is
        /// <see cref="LocalLightData.Collect"/>'s business, not this method's.</summary>
        public void GatherLights(List<Light> into) {
            if (into == null) return;
            into.Clear();
            for (int i = 0; i < _additionalLights.Count; i++) {
                Light light = _additionalLights[i];
                if (light != null && !into.Contains(light)) into.Add(light);
            }
            IReadOnlyList<LocalLightsProvider> providers = LocalLightsProvider.All;
            for (int p = 0; p < providers.Count; p++) {
                LocalLightsProvider provider = providers[p];
                if (provider == null) continue;
                IReadOnlyList<Light> lights = provider.Lights;
                for (int i = 0; i < lights.Count; i++) {
                    // A light listed both here and in a level provider (or in two overlapping levels)
                    // must only take one slot of the budget.
                    if (lights[i] != null && !into.Contains(lights[i])) into.Add(lights[i]);
                }
            }
        }

        // The budget is 4 point + 4 spot; more supported lights than that means some simply don't light
        // the scene. Say so once per overflow - silently dropping is the kind of thing that gets debugged
        // as "the torch shader is broken" instead of "the level lists too many lights".
        void WarnIfLightsDropped() {
            int dropped = _localLights.DroppedLights;
            if (dropped == _warnedDroppedCount) return;
            _warnedDroppedCount = dropped;
            if (dropped == 0) return;
            Debug.LogWarning(
                $"Local lights: {dropped} light(s) exceeded the budget of {LocalLightData.MaxPointLights} point " +
                $"+ {LocalLightData.MaxSpotLights} spot and are not lighting anything. This component's own list " +
                "is collected first, then each LocalLightsProvider in registration order - so the dropped ones " +
                "are at the end of that order. Shorten a list, or switch lights off when they're out of reach.",
                this);
        }
    }
}
