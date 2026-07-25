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
    /// the readout says so when the platform doesn't provide them. The solve is also gated (it idles
    /// once the ray budget is spent unless Continuous GI is on), so a 0 here reads as "idle", not
    /// "free" - the block count distinguishes the two.
    /// </summary>
    [AddComponentMenu("Lotec/Voxel Lighting/Debug/Buffer GI Solve Profiler")]
    public class BufferGiSolveProfiler : MonoBehaviour {
        public enum Stage { Inject = 0, Gather = 1, Blur = 2 }
        const int StageCount = 3;
        static readonly string[] s_stageNames = { "BGI.Inject", "BGI.Gather", "BGI.Blur" };

        [Tooltip("Seconds between Debug.Log lines (0 = don't log). Logging is how you read this on " +
                 "device - the overlay isn't visible in the headset.")]
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

        readonly float[] _gpuMs = new float[StageCount];
        readonly bool[] _ran = new bool[StageCount];
        float _frameMs;
        float _nextLog;
        bool _gpuUnavailable;

        /// <summary>Smoothed GPU milliseconds for one solve stage (both fields summed).</summary>
        public float GpuMs(Stage stage) => _gpuMs[(int)stage];
        /// <summary>Smoothed GPU milliseconds for the whole solve (inject + gather + blur).</summary>
        public float SolveGpuMs => _gpuMs[0] + _gpuMs[1] + _gpuMs[2];
        /// <summary>Smoothed frame time in milliseconds, the denominator for <see cref="SolveShare"/>.</summary>
        public float FrameMs => _frameMs;
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
            EnsureSamplers();
            s_active = this;
            _nextLog = 0f;
        }

        void OnDisable() {
            if (s_active == this) s_active = null;
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
            if (s_active == null || s_samplers == null) return;
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
            // Blocks recorded but every timing zero => the backend isn't reporting GPU timestamps.
            _gpuUnavailable = anyBlocks && SolveGpuMs <= 0f;

            if (_logInterval > 0f && Time.unscaledTime >= _nextLog) {
                _nextLog = Time.unscaledTime + _logInterval;
                Debug.Log(BuildReport(), this);
            }
        }

        string BuildReport() {
            if (!SolveRan())
                return "BufferGI solve: idle (ray budget spent - enable Continuous GI to keep it dispatching).";
            if (_gpuUnavailable)
                return "BufferGI solve: gpu timing unavailable (needs a development build / a backend " +
                       "that reports GPU timestamps).";

            float march = _gpuMs[0] + _gpuMs[1];
            return string.Format(
                "BufferGI solve {0:0.00}ms = {1:0.0}% of {2:0.00}ms frame | inject {3:0.00} gather {4:0.00} " +
                "blur {5:0.00} | march-heavy (inject+gather) {6:0.00}ms = {7:0.0}% of frame",
                SolveGpuMs, SolveShare * 100f, _frameMs,
                _gpuMs[0], _gpuMs[1], _gpuMs[2],
                march, _frameMs > 0f ? march / _frameMs * 100f : 0f);
        }

        bool SolveRan() => _ran[0] || _ran[1] || _ran[2];

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
