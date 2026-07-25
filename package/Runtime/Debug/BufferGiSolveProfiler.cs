using UnityEngine;
using UnityEngine.Profiling;

namespace Lotec.Lighting {
    /// <summary>
    /// Dev GPU timer for the buffer-GI solve: times the three solve dispatches (CSInject / CSGather /
    /// CSBlur) on the GPU and reports them against the frame time, so the compute solve's share of a
    /// Quest frame is a measured number rather than a guess.
    ///
    /// Inject and Gather are the MarchOccupancy-heavy kernels (direct-light shadow rays / gather rays +
    /// the air probe's sun ray); Blur marches nothing, so it reads as the march-free control - roughly
    /// the per-dispatch floor. (inject + gather) - blur is the budget an empty-space skip could attack.
    ///
    /// Scopes accumulate over BOTH fields in a frame (SolveField runs fine, then coarse), which is what
    /// you want: the total is the solve's whole per-frame GPU cost.
    ///
    /// GPU timings need a development build (or the editor) on a backend that reports GPU timestamps;
    /// the readout says so when the platform doesn't provide them - except on Vulkan, which reports no
    /// timings at all (neither per-dispatch nor frame timing), so there the logs are simply switched off
    /// rather than repeating that nothing can be measured. The solve is also gated (it idles
    /// once the ray budget is spent unless Continuous GI is on), so "idle" and "running but untimed"
    /// are separate verdicts: whether the solve DISPATCHED is tracked CPU-side (see s_dispatchedFrame),
    /// never inferred from the GPU block count - on Quest no blocks land at all, which used to report a
    /// running solve as idle.
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Debug/Buffer GI Solve Profiler")]
    public class BufferGiSolveProfiler : MonoBehaviour {
        public enum Stage { Inject = 0, Gather = 1, Blur = 2 }
        const int StageCount = 3;
        static readonly string[] s_stageNames = { "BGI.Inject", "BGI.Gather", "BGI.Blur" };

        [Tooltip("Seconds between Debug.Log lines (0 = don't log). Logging is how you read this on " +
                 "device - the overlay isn't visible in the headset. Ignored on Vulkan, which reports no " +
                 "GPU timings at all.")]
        [Min(0f)][SerializeField] float _logInterval = 2f;
        [Tooltip("Draw the readout as a screen overlay (bottom-left). Useful in the editor / desktop " +
                 "player; invisible in VR, so leave the log interval on for Quest runs.")]
        [SerializeField] bool _screenOverlay = true;
        [Tooltip("Exponential smoothing of the displayed numbers (1 = raw, no smoothing). GPU timings " +
                 "are noisy frame to frame; smoothing makes them readable without hiding a real shift.")]
        [Range(0.01f, 1f)][SerializeField] float _smoothing = 0.08f;

        // Static so the updater's dispatch hooks stay a no-op when no profiler is enabled.
        static BufferGiSolveProfiler s_active;
        static CustomSampler[] s_samplers;
        static Recorder[] s_recorders;
        // Frame the solve last dispatched, stamped CPU-side by Begin. This is the ONLY reliable answer to
        // "did the solve run this frame": a backend that reports no GPU sample blocks at all (Quest, off
        // a development build) leaves every block count at 0, which made a RUNNING solve read as idle.
        static int s_dispatchedFrame = -1;

        // Whole-frame GPU time from FrameTimingManager. Unity's PER-DISPATCH GPU recorder has no Vulkan
        // backend (see BuildReport), but this one works there, so on Quest the solve's cost is still
        // measurable - by differencing the GPU frame time with the solve dispatching vs idle.
        static readonly FrameTiming[] s_frameTimings = new FrameTiming[1];

        // Dispatched frames to allow before declaring per-stage GPU timing unavailable (results lag
        // submission by a few frames, and the first ones legitimately land empty).
        const int GpuTimingGraceFrames = 60;
        // Frames slower than this are hitches (scene load, compile, editor stall), not rendering cost, and
        // are excluded from the solving/idle frame-time averages.
        const float OutlierFrameMs = 200f;
        // Solving-vs-idle frame-time deltas below this fraction of the frame time are indistinguishable
        // from jitter on a vsync/compositor-paced platform (the app has headroom and both averages sit on
        // the pacing floor). 5% of a 72Hz frame is ~0.7ms.
        const float FramePacedDeltaFraction = 0.05f;

        readonly float[] _gpuMs = new float[StageCount];
        readonly bool[] _ran = new bool[StageCount];
        float _frameMs;
        float _frameMsSolving;   // frame time averaged over frames the solve DID dispatch
        float _frameMsIdle;      // frame time averaged over frames it didn't
        float _gpuFrameMs;
        float _nextLog;
        int _dispatchedFrames;
        bool _anyGpuTimingSeen;
        bool _solveDispatched;
        bool _gpuUnavailable;
        // Vulkan delivers neither per-dispatch GPU recorder timings nor FrameTimingManager gpuFrameTime, so
        // there is nothing to report there: skip the timing queries and stay quiet.
        bool _timingUnsupported;

        /// <summary>Smoothed GPU milliseconds for one solve stage (both fields summed).</summary>
        public float GpuMs(Stage stage) => _gpuMs[(int)stage];
        /// <summary>Smoothed GPU milliseconds for the whole solve (inject + gather + blur).</summary>
        public float SolveGpuMs => _gpuMs[0] + _gpuMs[1] + _gpuMs[2];
        /// <summary>Smoothed frame time in milliseconds, the denominator for <see cref="SolveShare"/>.</summary>
        public float FrameMs => _frameMs;
        /// <summary>Smoothed WHOLE-FRAME GPU milliseconds (FrameTimingManager). 0 if the platform reports
        /// none. Unlike the per-stage numbers this works on Vulkan, so it's the Quest measurement: read it
        /// with the solve dispatching, then idle, and the difference is the solve's GPU cost.</summary>
        public float GpuFrameMs => _gpuFrameMs;
        /// <summary>Solve GPU time as a fraction of frame time (0..1). 0 while the solve is idle.</summary>
        public float SolveShare => _frameMs > 0f ? SolveGpuMs / _frameMs : 0f;
        /// <summary>False when the platform/build reports no GPU timestamps (the ms values stay 0).</summary>
        public bool GpuTimingAvailable => !_gpuUnavailable;
        /// <summary>Screen overlay on/off, so the in-game debug panel can drive it without the inspector.</summary>
        public bool ScreenOverlay {
            get => _screenOverlay;
            set => _screenOverlay = value;
        }

        void OnEnable() {
            _timingUnsupported =
                SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan;
            EnsureSamplers();
            s_active = this;
            _nextLog = 0f;
        }

        void OnDisable() {
            if (s_active == this) s_active = null;
            _solveDispatched = false;
            _dispatchedFrames = 0;
            _anyGpuTimingSeen = false;
            _frameMsSolving = 0f;
            _frameMsIdle = 0f;
            for (int i = 0; i < StageCount; i++) {
                _gpuMs[i] = 0f;
                _ran[i] = false;
            }
        }

        static void EnsureSamplers() {
            if (s_samplers != null) return;
            s_samplers = new CustomSampler[StageCount];
            s_recorders = new Recorder[StageCount];
            for (int i = 0; i < StageCount; i++) {
                // collectGpuData: the whole point - without it the recorder only carries CPU time
                // (dispatch submission), which says nothing about the solve's actual cost.
                s_samplers[i] = CustomSampler.Create(s_stageNames[i], true);
                s_recorders[i] = s_samplers[i]?.GetRecorder() ?? null;
                if (s_recorders[i] != null) s_recorders[i].enabled = true;
            }
        }

        /// <summary>Open a GPU scope around a solve dispatch. No-op unless a profiler is enabled.</summary>
        public static void Begin(Stage stage) {
            if (s_active == null) return;
            // Stamp the frame even when the samplers are unavailable - the readout's "idle" verdict comes
            // from this, not from the GPU recorder.
            s_dispatchedFrame = Time.frameCount;
            if (s_samplers == null) return;
            s_samplers[(int)stage]?.Begin();
        }

        /// <summary>Close the scope opened by <see cref="Begin"/>. Must stay balanced with it.</summary>
        public static void End(Stage stage) {
            if (s_active == null || s_samplers == null) return;
            s_samplers[(int)stage]?.End();
        }

        void LateUpdate() {
            // Frame time from unscaled delta: on Quest that's the app's real submit-to-submit interval,
            // which is the denominator we actually care about (not a timescaled gameplay delta).
            float frame = Time.unscaledDeltaTime * 1000f;
            _frameMs = _frameMs > 0f ? Mathf.Lerp(_frameMs, frame, _smoothing) : frame;

            // LateUpdate runs after every Update, so the updater's solve (if any) has already dispatched
            // and stamped the frame.
            _solveDispatched = s_dispatchedFrame == Time.frameCount;

            // Split frame time by whether the solve dispatched. On a platform with no GPU timers at all
            // (Quest - see BuildReport) this IS the measurement: the difference between the two averages
            // is the solve's cost. Both fill in on their own - the budget fills, then the solve idles.
            // Outliers are dropped: a scene load or hitch is orders of magnitude off and would otherwise
            // seed the average with a value that takes ~50 frames to decay, poisoning the delta.
            if (frame < OutlierFrameMs) {
                if (_solveDispatched) {
                    _dispatchedFrames++;
                    _frameMsSolving = _frameMsSolving > 0f ? Mathf.Lerp(_frameMsSolving, frame, _smoothing) : frame;
                } else {
                    _frameMsIdle = _frameMsIdle > 0f ? Mathf.Lerp(_frameMsIdle, frame, _smoothing) : frame;
                }
            }

            // Vulkan hands back nothing from either timer, so there is no point querying them - and no
            // point logging either, since every line would just repeat that it can't measure here. The
            // frame-time split above still runs; the overlay can show it if anyone wants it.
            if (_timingUnsupported) {
                _gpuUnavailable = true;
                return;
            }

            // Whole-frame GPU time (~4 frame delay). Documented as unavailable on XR platforms (both GLES
            // and Vulkan), so expect 0 on Quest; kept because it's free and real on desktop/editor.
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, s_frameTimings) > 0) {
                float gpuFrame = (float)s_frameTimings[0].gpuFrameTime;
                if (gpuFrame > 0f) {
                    _gpuFrameMs = _gpuFrameMs > 0f ? Mathf.Lerp(_gpuFrameMs, gpuFrame, _smoothing) : gpuFrame;
                }
            }

            bool anyBlocks = false;
            for (int i = 0; i < StageCount; i++) {
                Recorder rec = s_recorders != null ? s_recorders[i] : null;
                if (rec == null) continue;
                // GPU results resolve a few frames after submission; the recorder hands back the most
                // recent frame that has landed. Block count 0 = the dispatch didn't run this frame
                // (the solve is gated once the ray budget is spent), so hold the last value instead of
                // decaying an idle solve toward 0 and reading it as "the solve got cheaper".
                bool ran = rec.gpuSampleBlockCount > 0;
                _ran[i] = ran;
                if (!ran) continue;
                anyBlocks = true;
                float ms = rec.gpuElapsedNanoseconds * 1e-6f;
                _gpuMs[i] = _gpuMs[i] > 0f ? Mathf.Lerp(_gpuMs[i], ms, _smoothing) : ms;
            }
            if (anyBlocks && SolveGpuMs > 0f) _anyGpuTimingSeen = true;
            // Timings lag submission by a few frames, so only call it unavailable after the solve has
            // dispatched for a while with nothing landing. SystemInfo.supportsGpuRecorder can't be trusted
            // for this - Quest reports true and then never delivers a single timing.
            _gpuUnavailable = !_anyGpuTimingSeen && _dispatchedFrames > GpuTimingGraceFrames;

            if (_logInterval > 0f && Time.unscaledTime >= _nextLog) {
                _nextLog = Time.unscaledTime + _logInterval;
                Debug.Log(BuildReport(), this);
            }
        }

        string BuildReport() {
            string state = SolveRan() ? "solving" : "idle";
            string gpuFrame = _gpuFrameMs > 0f ? string.Format(" | gpu frame {0:0.00}ms", _gpuFrameMs) : "";

            // No per-stage GPU numbers on this platform. Unity's GPU recorder has no Vulkan backend (nor
            // Metal/WebGL); on Android it only works on OpenGL with NVIDIA/Intel GPUs, so Adreno gets
            // nothing, and Graphics Jobs disables GPU profiling outright. FrameTimingManager's gpuFrameTime
            // is documented unavailable on XR too. So on Quest the measurement is the FRAME TIME DELTA
            // between solving and idle frames, which needs no GPU timer at all.
            if (_gpuUnavailable) {
                string platform = SystemInfo.graphicsDeviceType
                    + (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan
                        ? ", which Unity's GPU recorder doesn't support" : "");
                if (_frameMsSolving <= 0f || _frameMsIdle <= 0f) {
                    return string.Format(
                        "BufferGI solve: {0}, no per-stage GPU timer on this platform ({1}). frame-time delta " +
                        "needs both states - let the ray budget finish, or toggle Continuous GI.{2}",
                        state, platform, gpuFrame);
                }
                float delta = _frameMsSolving - _frameMsIdle;
                // A compositor-paced frame time (Quest) is QUANTIZED to the refresh interval: with headroom
                // to spare both averages sit on that floor and the difference is jitter, not the solve's
                // cost. Report that honestly instead of a number that looks like a measurement.
                if (delta < FramePacedDeltaFraction * _frameMsIdle) {
                    return string.Format(
                        "BufferGI solve: {0}, no per-stage GPU timer on this platform ({1}). frame paced at " +
                        "{2:0.00}ms (~{3:0}Hz) whether solving or idle, so the solve FITS IN HEADROOM and its " +
                        "cost cannot be read from frame time (delta {4:0.00}ms is jitter). To measure: raise " +
                        "Samples/Frame until frames start dropping, or read app GPU time from OVR Metrics " +
                        "Tool / Snapdragon Profiler.{5}",
                        state, platform, _frameMsIdle, 1000f / _frameMsIdle, delta, gpuFrame);
                }
                return string.Format(
                    "BufferGI solve: {0}, no per-stage GPU timer on this platform ({1}). frame: solving " +
                    "{2:0.00}ms vs idle {3:0.00}ms => solve ~{4:0.00}ms (frame-time delta; quantized by the " +
                    "compositor, so treat it as a floor){5}",
                    state, platform, _frameMsSolving, _frameMsIdle, delta, gpuFrame);
            }
            if (!SolveRan())
                return "BufferGI solve: idle - no dispatch this frame (ray budget spent; enable Continuous " +
                       "GI to keep it dispatching)." + gpuFrame;
            if (!_anyGpuTimingSeen)
                return string.Format(
                    "BufferGI solve: DISPATCHING, waiting for the first GPU timings ({0}/{1} frames; they lag " +
                    "submission){2}", _dispatchedFrames, GpuTimingGraceFrames, gpuFrame);

            float march = _gpuMs[0] + _gpuMs[1];
            return string.Format(
                "BufferGI solve {0:0.00}ms = {1:0.0}% of {2:0.00}ms frame | inject {3:0.00} gather {4:0.00} " +
                "blur {5:0.00} | march-heavy (inject+gather) {6:0.00}ms = {7:0.0}% of frame",
                SolveGpuMs, SolveShare * 100f, _frameMs,
                _gpuMs[0], _gpuMs[1], _gpuMs[2],
                march, _frameMs > 0f ? march / _frameMs * 100f : 0f) + gpuFrame;
        }

        // CPU-side truth, not the GPU block count: see s_dispatchedFrame.
        bool SolveRan() => _solveDispatched;

        void OnGUI() {
            if (!_screenOverlay) return;
            const float width = 460f, height = 120f, margin = 10f;
            // Bottom-left: keeps the top-left corner free for the other on-screen debug readouts.
            GUILayout.BeginArea(new Rect(margin, Screen.height - height - margin, width, height), GUI.skin.box);
            GUILayout.Label(BuildReport().Replace(" | ", "\n"));
            GUILayout.EndArea();
        }
    }
}
