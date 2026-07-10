using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    /// <summary>
    /// Backend-agnostic display transform for the GI updaters: publishes the `_Exposure` global and
    /// the TONEMAP_OFF keyword, optionally auto-adapting exposure to the average scene-GI luminance.
    ///
    /// The luminance MEASUREMENT is backend-specific (texture vs buffer GI read different data), so
    /// the owner passes a small dispatch callback to <see cref="Apply"/>: this controller hands it a
    /// cleared 2-uint buffer to fill (encoded log-luminance sum in [0], sample count in [1], the same
    /// encoding both GI luminance kernels use), then does the readback + smooth adaptation itself.
    /// </summary>
    [System.Serializable]
    public class AutoExposure {
        [Tooltip("Apply the display transform (exposure + tonemap) in the lit shader. Off = output " +
                 "linear HDR for a post-processing stack to expose/tonemap once (also skips the " +
                 "auto-exposure luminance pass).")]
        [SerializeField] bool _tonemapInShader = true;
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

        const string TonemapOffKeyword = "TONEMAP_OFF";
        static readonly int s_exposure = Shader.PropertyToID("_Exposure");
        static readonly uint[] s_clear = { 0u, 0u };

        ComputeBuffer _luminanceBuffer;
        bool _readbackPending;
        float _autoExposureEV;

        /// <summary>Radius (m) around the camera the owner's luminance kernel should sample.</summary>
        public float MeasureRadius => _measureRadius;

        /// <summary>Apply the display transform (exposure + tonemap) in the lit shader. Off = output
        /// linear HDR for a post-processing stack to expose/tonemap once (also skips auto-exposure).</summary>
        public bool TonemapInShader { get => _tonemapInShader; set => _tonemapInShader = value; }

        /// <summary>
        /// Publish exposure + the tonemap keyword for this frame. When auto-exposure is on and a
        /// <paramref name="dispatchMeasure"/> callback is supplied, the controller clears its
        /// luminance buffer, lets the callback fill it (bind + dispatch the backend's luminance
        /// kernel), reads it back asynchronously, and adapts `_Exposure` toward the target.
        /// </summary>
        public void Apply(System.Action<ComputeBuffer> dispatchMeasure) {
            if (!_tonemapInShader) {
                Shader.EnableKeyword(TonemapOffKeyword); // linear HDR out; a post stack tonemaps
                return;
            }
            Shader.DisableKeyword(TonemapOffKeyword);

            if (_auto && dispatchMeasure != null) {
                MeasureLuminance(dispatchMeasure);
                AdaptExposure();
            } else {
                Shader.SetGlobalFloat(s_exposure, _exposure);
            }
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
            if (count == 0) return;

            // Decode: encoded = (log2(lum) + 16) * 1024, so avg log2 = (sum/count)/1024 - 16.
            float avgLog2Luminance = (encodedSum / (float)count) / 1024f - 16f;
            // Map the geometric-mean luminance to ~0.18 (mid-grey) after exposure: 2^avgLog2 * 2^EV
            // = 0.18 => EV = log2(0.18) - avgLog2. log2(0.18) ~ -2.474.
            float targetEV = -2.474f - avgLog2Luminance;
            _autoExposureEV = Mathf.Clamp(targetEV, _min, _max);
        }

        void AdaptExposure() {
            float targetExposure = _autoExposureEV + _exposure;
            float currentExposure = Shader.GetGlobalFloat(s_exposure);
            float speed = 1f - Mathf.Exp(-Time.deltaTime * _adaptationSpeed);
            Shader.SetGlobalFloat(s_exposure, Mathf.Lerp(currentExposure, targetExposure, speed));
        }

        /// <summary>Release the luminance buffer (call from the owner's OnDisable).</summary>
        public void Release() {
            _luminanceBuffer?.Release();
            _luminanceBuffer = null;
            _readbackPending = false;
        }

        /// <summary>Clear the TONEMAP_OFF keyword this controller may have set (owner's OnDisable, so
        /// a disabled GI updater leaves the direct-only path with the in-shader tonemap on).</summary>
        public void ResetKeyword() => Shader.DisableKeyword(TonemapOffKeyword);
    }
}
