using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Backend-agnostic display transform for the GI updaters: publishes the `_ExposureLinear` global
    /// and selects the tonemap operator's shader KEYWORD (Off / Reinhard / AgX / ACES), optionally
    /// auto-adapting exposure to the average scene-GI luminance.
    ///
    /// The operator is a keyword, not a uniform the shader branches on: see
    /// <see cref="LightingKeywords.Tonemap"/> for why (register allocation / occupancy). Exposure is
    /// published pre-exponentiated so the fragment doesn't run exp2 per pixel on a uniform.
    ///
    /// The luminance MEASUREMENT is backend-specific (texture vs buffer GI read different data), so
    /// the owner passes a small dispatch callback to <see cref="Apply"/>: this controller hands it a
    /// cleared 2-uint buffer to fill (encoded log-luminance sum in [0], sample count in [1], the same
    /// encoding both GI luminance kernels use), then does the readback + smooth adaptation itself.
    /// </summary>
    [System.Serializable]
    public class AutoExposure {
        /// <summary>In-shader tonemap operator. Off outputs linear HDR for a post-processing stack.</summary>
        public enum TonemapMode { Off, Reinhard, AgX, ACES }

        [Tooltip("Tonemap operator applied in the lit shader (with exposure). Reinhard: cheap, " +
                 "classic rolloff. AgX: film-like, desaturated highlights. ACES: contrasty filmic " +
                 "rolloff. Off: output linear HDR for a post-processing stack to expose/tonemap " +
                 "once (also skips the auto-exposure luminance pass).")]
        [SerializeField] TonemapMode _tonemap = TonemapMode.Reinhard;
        [Tooltip("Manual exposure offset in EV stops (added on top of auto-exposure).")]
        [SerializeField] float _exposure;
        [Tooltip("Automatically adapt exposure to the average scene-GI luminance near the camera.")]
        [SerializeField] bool _auto;
        [Tooltip("Minimum auto-exposure in EV stops.")]
        [SerializeField] float _min = -2f;
        [Tooltip("Maximum auto-exposure in EV stops.")]
        [SerializeField] float _max = 4f;
        [Tooltip("How quickly exposure adapts (higher = faster). Roughly seconds to adapt.")]
        [Range(0.5f, 10f)]
        [SerializeField] float _adaptationSpeed = 2f;
        [Tooltip("Radius around the camera (in meters) to sample for auto-exposure.")]
        [SerializeField] float _measureRadius = 3f;

        static readonly int s_exposureLinear = Shader.PropertyToID("_ExposureLinear");
        static readonly uint[] s_clear = { 0u, 0u };
        // Scratch for the open-sky fallback's ambient-probe sampling (6 axis directions).
        static readonly Vector3[] s_ambientDirs =
            { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
        static readonly Color[] s_ambientCols = new Color[6];

        ComputeBuffer _luminanceBuffer;
        bool _readbackPending;
        float _autoExposureEV;
        // The EV actually published (pre-exp2). Kept here rather than read back from the shader global,
        // which now carries the LINEAR multiplier and so can't be smoothed in EV space.
        float _currentEV;

        /// <summary>Radius (m) around the camera the owner's luminance kernel should sample.</summary>
        public float MeasureRadius => _measureRadius;

        /// <summary>In-shader tonemap operator (with exposure). Off outputs linear HDR for a
        /// post-processing stack to expose/tonemap once (also skips auto-exposure).</summary>
        public TonemapMode Tonemap { get => _tonemap; set => _tonemap = value; }

        /// <summary>Adapt exposure to the measured scene-GI luminance. Off holds the manual exposure
        /// and - the reason this is switchable at runtime - skips the per-measurement luminance
        /// dispatch + readback, which is the part of "tonemapping on" that actually costs frame time
        /// (the in-shader operator itself is a handful of ALU).</summary>
        public bool Auto { get => _auto; set => _auto = value; }

        /// <summary>
        /// Publish exposure + the tonemap operator for this frame. When auto-exposure is on and a
        /// <paramref name="dispatchMeasure"/> callback is supplied, the controller clears its
        /// luminance buffer, lets the callback fill it (bind + dispatch the backend's luminance
        /// kernel), reads it back asynchronously, and adapts the published exposure toward the target.
        /// </summary>
        public void Apply(System.Action<ComputeBuffer> dispatchMeasure) {
            // Selects the lit shader's variant: only the chosen operator is compiled into the kernel.
            LightingKeywords.Tonemap.Set(KeywordFor(_tonemap));
            if (_tonemap == TonemapMode.Off) {
                return; // linear HDR out; a post stack tonemaps + exposes (auto-exposure skipped)
            }

            if (_auto && dispatchMeasure != null) {
                MeasureLuminance(dispatchMeasure);
                AdaptExposure();
            } else {
                PublishExposure(_exposure);
            }
        }

        static string KeywordFor(TonemapMode mode) {
            switch (mode) {
                case TonemapMode.Off: return LightingKeywords.TonemapOff;
                case TonemapMode.AgX: return LightingKeywords.TonemapAgx;
                case TonemapMode.ACES: return LightingKeywords.TonemapAces;
                default: return LightingKeywords.TonemapReinhard;
            }
        }

        // exp2 on the CPU rather than per pixel: it's a uniform, so the fragment was recomputing the
        // same transcendental for every pixel in the frame.
        void PublishExposure(float ev) {
            _currentEV = ev;
            Shader.SetGlobalFloat(s_exposureLinear, Mathf.Pow(2f, ev));
        }

        void MeasureLuminance(System.Action<ComputeBuffer> dispatchMeasure) {
            if (_readbackPending) return; // one measurement in flight at a time
            if (_luminanceBuffer == null) _luminanceBuffer = new ComputeBuffer(2, sizeof(uint));
            _luminanceBuffer.SetData(s_clear);
            dispatchMeasure(_luminanceBuffer);
            _readbackPending = true;
            AsyncGPUReadback.Request(_luminanceBuffer, OnLuminanceReadback);
        }

        void OnLuminanceReadback(AsyncGPUReadbackRequest request) {
            _readbackPending = false;
            if (request.hasError) return;

            NativeArray<uint> data = request.GetData<uint>();
            uint encodedSum = data[0];
            uint count = data[1];

            float avgLog2Luminance;
            if (count > 0) {
                // Decode: encoded = (log2(lum) + 16) * 1024, so avg log2 = (sum/count)/1024 - 16.
                avgLog2Luminance = (encodedSum / (float)count) / 1024f - 16f;
            } else {
                // No GI voxels sampled - the camera is outside every field. Assume it's out in the
                // open (sunlight), not in darkness, so leaving the volumes doesn't strand the
                // exposure at its last value (which would then be adapted from stale data).
                avgLog2Luminance = Mathf.Log(EstimateOpenSkyLuminance(), 2f);
            }
            // Map the geometric-mean luminance to ~0.18 (mid-grey) after exposure: 2^avgLog2 * 2^EV
            // = 0.18 => EV = log2(0.18) - avgLog2. log2(0.18) ~ -2.474.
            float targetEV = -2.474f - avgLog2Luminance;
            _autoExposureEV = Mathf.Clamp(targetEV, _min, _max);
        }

        // Assumed scene luminance when the camera is outside every GI field: the sun + sky, so the
        // exposure settles at an outdoor-daylight level instead of holding a stale interior value.
        // The measured field is INDIRECT (bounce) luminance, so the sun's direct illuminance is
        // weighted down to a rough indirect-equivalent rather than used at full strength.
        static float EstimateOpenSkyLuminance() {
            Light sun = RenderSettings.sun;
            float sunLum = sun != null ? Luminance(sun.color) * sun.intensity : 0f;
            // Mean environment radiance from Unity's ambient probe (reflects the Lighting window's
            // Environment source: Skybox / Gradient / Color), sampled over the 6 axes.
            RenderSettings.ambientProbe.Evaluate(s_ambientDirs, s_ambientCols);
            float skyLum = 0f;
            for (int i = 0; i < s_ambientCols.Length; i++) skyLum += Luminance(s_ambientCols[i]);
            skyLum /= s_ambientCols.Length;
            const float sunIndirectFraction = 0.25f;
            return Mathf.Max(skyLum + sunIndirectFraction * sunLum, 1e-4f);
        }

        static float Luminance(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;

        void AdaptExposure() {
            float targetExposure = _autoExposureEV + _exposure;
            float speed = 1f - Mathf.Exp(-Time.deltaTime * _adaptationSpeed);
            // Smoothed in EV (log) space, which is why the EV is tracked here: the published global is
            // the linear multiplier and lerping that would adapt unevenly across stops.
            PublishExposure(Mathf.Lerp(_currentEV, targetExposure, speed));
        }

        /// <summary>Release the luminance buffer (call from the owner's OnDisable).</summary>
        public void Release() {
            _luminanceBuffer?.Release();
            _luminanceBuffer = null;
            _readbackPending = false;
        }

        /// <summary>Restore the default (Reinhard) in-shader tonemap (owner's OnDisable, so a
        /// disabled GI updater leaves the direct-only path with the in-shader tonemap on).</summary>
        public void ResetToDefault() => LightingKeywords.Tonemap.Set(LightingKeywords.TonemapReinhard);
    }
}
