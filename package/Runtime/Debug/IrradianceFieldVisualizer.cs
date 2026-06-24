using UnityEngine;

namespace Lotec.Lighting
{
    [ExecuteAlways]
    public class IrradianceFieldVisualizer : VoxelFieldVisualizerBase
    {
        public LightingManager source;
        public bool toneMap = true;
        [Min(0f)] public float exposure = 1.0f;
        [Min(0f)] public float minVisibleLuminance = 0.02f;

        int _lastStatusFrame;
        protected override string ConversionShaderName => "Hidden/Unpack3D";
        LightingVolume Volume => LightingManager.Instance != null ? LightingManager.Instance.Volume : null;

        protected override Texture GetTexture()
        {
            if (source == null)
            {
                source = FindAnyObjectByType<LightingManager>();
                if (source == null) { LogStatus("GetTexture: no LightingManager found"); return null; }
            }

            GiFieldUpdater gi = GiFieldUpdater.Instance;
            RenderTexture rt = gi != null ? gi.IrradianceFinal : null;
            if (rt == null) { LogStatus("GetTexture: irradiance RT is null"); return null; }
            if (!rt.IsCreated()) rt.Create();
            if (rt.width == 0 || rt.height == 0 || rt.volumeDepth == 0) { LogStatus($"GetTexture: RT has invalid dims {rt.width}x{rt.height}x{rt.volumeDepth}"); return null; }

            return rt;
        }

        protected override bool TryGetBounds(out Bounds bounds)
        {
            if (Volume == null)
            {
                bounds = default;
                return false;
            }

            bounds = Volume.Bounds;
            return true;
        }

        protected override Color ProcessColor(Color c)
        {
            c.a = 1f;

            // Assume c is linear HDR. Use minVisibleLuminance as a hard visibility threshold.
            float lumLinear = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

            if (lumLinear < minVisibleLuminance)
            {
                c.a = 0f;
                return c;
            }

            if (toneMap) c = ApplyToneMap(c, exposure);

            return c; // do not call .linear here unless input is sRGB
        }

        static Color ApplyToneMap(Color c, float exposure)
        {
            Color v = c * Mathf.Max(0.0f, exposure);
            return new Color(
                v.r / (1.0f + v.r),
                v.g / (1.0f + v.g),
                v.b / (1.0f + v.b),
                c.a
            );
        }

        void LogStatus(string msg)
        {
            if (Time.frameCount - _lastStatusFrame < 30) return;
            _lastStatusFrame = Time.frameCount;
            Debug.Log($"IrradianceFieldVisualizer: {msg}", this);
        }

        // DEBUG: Utility to log center pixel of radiance field for testing readback and visualization.
        [ContextMenu("Log Irradiance Center Pixel")]
        void LogRadianceCenter()
        {
            var tex = GiFieldUpdater.Instance?.IrradianceFinal as Texture;
            Texture3DReadback.ReadbackRGBAAsync(tex, "Hidden/Unpack3D", (ok, pixels, w, h, d) =>
            {
                if (!ok) { Debug.Log("readback failed"); return; }
                int cx = w / 2, cy = h / 2, cz = d / 2;
                Color c = pixels[cx + cy * w + cz * w * h];
                Debug.Log($"Irradiance center = {c}");
            });
        }
    }
}
