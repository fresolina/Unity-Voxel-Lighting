using UnityEngine;

namespace Sponza {
    /// <summary>
    /// Drives a REALTIME point light so the walls around the brazier get light that moves with the
    /// flame. Deliberately not the baked light: BufferGiUpdater polls LightEmissionBake.StateHash
    /// every frame, and that hash includes intensity - changing a baked light's intensity resets
    /// the progressive GI solve (_collectedSamples = 0), so flickering one would restart the solve
    /// on every single frame and the field would never converge. The baked emissive voxels carry
    /// the steady warm glow; this carries the movement, through the fragment direct-light path
    /// (VoxelLit's GetPointLightDirect), which costs one uniform upload per frame and never touches
    /// the solve.
    ///
    /// The light is published by the LocalLightsProvider on the same GameObject, so the effect is
    /// self-contained in the prefab - no scene wiring, and no entry in the level's VoxelLights.
    /// </summary>
    [RequireComponent(typeof(Light))]
    [DisallowMultipleComponent]
    public class FlameFlicker : MonoBehaviour {
        [Tooltip("Intensity the flicker modulates around.")]
        [SerializeField] float _baseIntensity = 0.5f;

        [Tooltip("Peak deviation as a fraction of the base intensity.")]
        [Range(0f, 1f)]
        [SerializeField] float _flickerAmount = 0.4f;

        [Tooltip("Match FlameVolume.mat's Sway Speed so the light and the flame breathe together.")]
        [SerializeField] float _speed = 1.6f;

        Light _light;
        float _phase;

        void Awake() {
            _light = GetComponent<Light>();
            // Per-instance offset: two braziers running the identical waveform would pulse in
            // lockstep, which reads as a global dimmer rather than as two fires.
            _phase = Random.value * 100f;
        }

        void Update() {
            // Three incommensurate sines - the periods never line up, so the pattern does not
            // repeat audibly-obviously the way one sine does, and it costs nothing.
            float t = (Time.time + _phase) * _speed;
            float f = 0.55f * Mathf.Sin(t)
                    + 0.30f * Mathf.Sin(t * 2.37f + 1.7f)
                    + 0.15f * Mathf.Sin(t * 5.11f + 3.1f);
            _light.intensity = Mathf.Max(0f, _baseIntensity * (1f + _flickerAmount * f));
        }
    }
}
