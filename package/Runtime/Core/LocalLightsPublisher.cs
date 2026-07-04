using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Collects the supported additional point/spot lights once per frame and publishes them as
    /// shader globals for fragment direct lighting; the GI updaters reuse the same packed data
    /// (<see cref="LocalLights"/>) for their compute solves. Disable the component to turn the
    /// additional lights off (it publishes zero counts on disable).
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    // Collect before the GI updaters (default order 0) consume the packed data.
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("Lotec/Voxel Lighting/Local Lights Publisher")]
    public class LocalLightsPublisher : MonoBehaviour {
        public static LocalLightsPublisher Instance { get; private set; }

        [Tooltip("Extra runtime lights for direct lighting + GI. The first 4 supported point lights " +
                 "and the first 4 supported spot lights are injected.")]
        [SerializeField] List<Light> _additionalLights = new List<Light>();

        readonly LocalLightData _localLights = new LocalLightData();

        /// <summary>The shared packed light data, collected once per frame for both fragment
        /// direct lighting (globals) and the GI compute solve.</summary>
        public LocalLightData LocalLights => _localLights;
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
            _localLights.Collect(_additionalLights);
            _localLights.ApplyGlobals();
        }
    }
}
