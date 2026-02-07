using UnityEngine;

namespace Lotec.Lighting {
    [ExecuteAlways]
    public class RadianceFieldVisualizer : VoxelFieldVisualizerBase {
        public LightingManager source;
        public bool toneMap = true;
        [Min(0f)] public float exposure = 1.0f;
        [Min(0f)] public float minVisibleLuminance = 0.02f;
        public bool showZeroAsGray = true;

        SdfShaderGlobals _sdfShaderGlobals;
        int _lastStatusFrame;
        protected override string ConversionShaderName => "Hidden/Unpack3D";

        protected override Texture GetTexture() {
            if (source == null) {
                source = FindAnyObjectByType<LightingManager>();
                if (source == null) { LogStatus("GetTexture: no LightingManager found"); return null; }
            }

            RenderTexture rt = source.GiUpdater != null ? source.GiUpdater.RadianceRead : null;
            if (rt == null) { LogStatus("GetTexture: current radiance RT is null"); return null; }
            if (!rt.IsCreated()) rt.Create();
            if (rt.width == 0 || rt.height == 0 || rt.volumeDepth == 0) { LogStatus($"GetTexture: RT has invalid dims {rt.width}x{rt.height}x{rt.volumeDepth}"); return null; }

            if (Time.frameCount - _lastStatusFrame < 10) return null;
            _lastStatusFrame = Time.frameCount;

            return rt;
        }

        protected override bool TryGetBounds(out Bounds bounds) {
            LightingVolume volume = GetVolumeSafe();
            if (volume == null) {
                bounds = default;
                return false;
            }

            bounds = volume.Bounds;
            return true;
        }

        protected override Color ProcessColor(Color c) {
            c.a = 1f;

            // Assume c is linear HDR. Evaluate luminance in linear space first.
            float lumLinear = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

            if (lumLinear <= 0.0f && showZeroAsGray) {
                c = new Color(minVisibleLuminance, minVisibleLuminance, minVisibleLuminance, 1.0f);
            } else if (lumLinear > 0.0f && lumLinear < minVisibleLuminance) {
                float scale = minVisibleLuminance / lumLinear;
                c = new Color(
                    Mathf.Min(c.r * scale, 1f),
                    Mathf.Min(c.g * scale, 1f),
                    Mathf.Min(c.b * scale, 1f),
                    1f
                );
            }

            if (toneMap) c = ApplyToneMap(c, exposure);

            return c; // do not call .linear here unless input is sRGB
        }

        static Color ApplyToneMap(Color c, float exposure) {
            Color v = c * Mathf.Max(0.0f, exposure);
            return new Color(
                v.r / (1.0f + v.r),
                v.g / (1.0f + v.g),
                v.b / (1.0f + v.b),
                1.0f
            );
        }

        LightingVolume GetVolumeSafe() {
            if (_sdfShaderGlobals == null) _sdfShaderGlobals = FindAnyObjectByType<SdfShaderGlobals>();
            return _sdfShaderGlobals != null ? _sdfShaderGlobals.volume : null;
        }

        void LogStatus(string msg) {
            if (Time.frameCount - _lastStatusFrame < 30) return;
            _lastStatusFrame = Time.frameCount;
            Debug.Log($"RadianceFieldVisualizer: {msg}", this);
        }

        // DEBUG: Utility to log center pixel of radiance field for testing readback and visualization.
        [ContextMenu("Log RadianceRead Center Pixel")]
        void LogRadianceCenter() {
            var tex = source.GiUpdater.RadianceRead as Texture;
            Texture3DReadback.ReadbackRGBAAsync(tex, "Hidden/Unpack3D", (ok, pixels, w, h, d) => {
                if (!ok) { Debug.Log("readback failed"); return; }
                int cx = w / 2, cy = h / 2, cz = d / 2;
                Color c = pixels[cx + cy * w + cz * w * h];
                Debug.Log($"Radiance center = {c}");
            });
        }
    }
}
