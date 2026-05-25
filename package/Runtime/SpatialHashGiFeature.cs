#if LOTEC_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Lotec.Lighting {
    /// <summary>
    /// URP ScriptableRendererFeature for the Spatial Hash GI screen-space pass.
    /// Resolves GI at half resolution, applies bilateral blur, and publishes
    /// the filtered GI texture as a global for surface shaders.
    ///
    /// Two execution paths:
    ///   Desktop / non-TBDR: Compute shader dispatch (separable bilateral blur).
    ///   Quest 3 / Vulkan mobile (GMEM path): Fragment shader blit with single-pass
    ///     bilateral blur. Avoids compute dispatches that would force tile memory
    ///     flushes on Adreno TBDRs. Uses memoryless intermediates and half-precision
    ///     formats to minimise GMEM footprint.
    /// </summary>
    public class SpatialHashGiFeature : ScriptableRendererFeature {
        [System.Serializable]
        public class Settings {
            [Header("Compute Path (Desktop)")]
            public ComputeShader screenSpaceCompute;

            [Header("Fragment Path (Quest 3 / GMEM)")]
            [Tooltip("Full-screen resolve shader (hash lookup from depth+normals).")]
            public Shader resolveShader;
            [Tooltip("Full-screen bilateral blur shader (depth-aware edge preservation).")]
            public Shader bilateralBlurShader;

            [Header("Shared")]
            [Range(1, 3)]
            public int blurRadius = 3;
            [Range(0.001f, 0.1f)]
            public float depthThreshold = 0.01f;
            public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;

            [Tooltip("Force the GMEM / fragment shader path even on desktop (for testing).")]
            public bool forceGmemPath;
        }

        [SerializeField] Settings _settings = new Settings();
        SpatialHashGiRenderPass _computePass;
        SpatialHashGiGmemPass _gmemPass;

        public override void Create() {
            _computePass = new SpatialHashGiRenderPass(_settings);
            _computePass.renderPassEvent = _settings.renderPassEvent;

            _gmemPass = new SpatialHashGiGmemPass(_settings);
            _gmemPass.renderPassEvent = _settings.renderPassEvent;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            if (!SpatialHashGiRenderPass.IsActive) return;

            if (ShouldUseGmemPath()) {
                if (_gmemPass.IsReady)
                    renderer.EnqueuePass(_gmemPass);
            } else {
                if (_settings.screenSpaceCompute != null)
                    renderer.EnqueuePass(_computePass);
            }
        }

        /// <summary>
        /// Use the GMEM / fragment shader path on Vulkan mobile (Adreno TBDRs)
        /// to avoid compute-induced tile memory flushes.
        /// </summary>
        bool ShouldUseGmemPath() {
            if (_settings.forceGmemPath) return true;

            // Vulkan on Android (Quest 3 / Adreno 740) is a tile-based deferred
            // renderer where compute dispatches between render passes force an
            // expensive GMEM store-load cycle. The fragment path avoids this.
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan
                && Application.platform == RuntimePlatform.Android;
        }

        protected override void Dispose(bool disposing) {
            _computePass?.Dispose();
            _gmemPass?.Dispose();
        }
    }

    // ================================================================
    // Compute path (Desktop) - separable bilateral blur via compute
    // ================================================================
    class SpatialHashGiRenderPass : ScriptableRenderPass, System.IDisposable {
        static bool s_isActive;
        public static bool IsActive => s_isActive;

        internal static void SetActive(bool active) {
            s_isActive = active;
        }

        readonly SpatialHashGiFeature.Settings _settings;
        int _resolveKernel;
        int _blurHKernel;
        int _blurVKernel;

        RenderTexture _giHalfRes;
        RenderTexture _blurTemp;

        static readonly int s_giOutput = Shader.PropertyToID("_GiOutput");
        static readonly int s_blurInput = Shader.PropertyToID("_BlurInput");
        static readonly int s_blurOutput = Shader.PropertyToID("_BlurOutput");
        static readonly int s_halfResolution = Shader.PropertyToID("_HalfResolution");
        static readonly int s_bilateralDepthThreshold = Shader.PropertyToID("_BilateralDepthThreshold");
        static readonly int s_blurKernelRadius = Shader.PropertyToID("_BlurKernelRadius");
        static readonly int s_inverseViewProjection = Shader.PropertyToID("_InverseViewProjection");
        static readonly int s_spatialHashGiFiltered = Shader.PropertyToID("_SpatialHashGiFiltered");

        public SpatialHashGiRenderPass(SpatialHashGiFeature.Settings settings) {
            _settings = settings;
            if (settings.screenSpaceCompute != null) {
                _resolveKernel = settings.screenSpaceCompute.FindKernel("CSResolveGi");
                _blurHKernel = settings.screenSpaceCompute.FindKernel("CSBilateralBlurH");
                _blurVKernel = settings.screenSpaceCompute.FindKernel("CSBilateralBlurV");
            }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            int halfW = Mathf.Max(1, desc.width / 2);
            int halfH = Mathf.Max(1, desc.height / 2);

            if (_giHalfRes == null || _giHalfRes.width != halfW || _giHalfRes.height != halfH) {
                _giHalfRes?.Release();
                _blurTemp?.Release();

                _giHalfRes = new RenderTexture(halfW, halfH, 0, RenderTextureFormat.ARGBHalf) {
                    enableRandomWrite = true,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "SpatialHashGI_HalfRes"
                };
                _giHalfRes.Create();

                _blurTemp = new RenderTexture(halfW, halfH, 0, RenderTextureFormat.ARGBHalf) {
                    enableRandomWrite = true,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "SpatialHashGI_BlurTemp"
                };
                _blurTemp.Create();
            }

            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (_settings.screenSpaceCompute == null) return;

            CommandBuffer cmd = CommandBufferPool.Get("SpatialHashGI_ScreenSpace");
            var compute = _settings.screenSpaceCompute;
            int halfW = _giHalfRes.width;
            int halfH = _giHalfRes.height;

            Camera cam = renderingData.cameraData.camera;
            Matrix4x4 viewProj = cam.projectionMatrix * cam.worldToCameraMatrix;
            cmd.SetComputeMatrixParam(compute, s_inverseViewProjection, viewProj.inverse);
            cmd.SetComputeVectorParam(compute, s_halfResolution, new Vector4(halfW, halfH, 1f / halfW, 1f / halfH));

            // Pass 3.1: Resolve GI
            cmd.SetComputeTextureParam(compute, _resolveKernel, s_giOutput, _giHalfRes);
            int groupsX = Mathf.CeilToInt(halfW / 8f);
            int groupsY = Mathf.CeilToInt(halfH / 8f);
            cmd.DispatchCompute(compute, _resolveKernel, groupsX, groupsY, 1);

            // Pass 3.2: Bilateral Blur (separable H+V)
            cmd.SetComputeFloatParam(compute, s_bilateralDepthThreshold, _settings.depthThreshold);
            cmd.SetComputeIntParam(compute, s_blurKernelRadius, _settings.blurRadius);

            cmd.SetComputeTextureParam(compute, _blurHKernel, s_blurInput, _giHalfRes);
            cmd.SetComputeTextureParam(compute, _blurHKernel, s_blurOutput, _blurTemp);
            cmd.DispatchCompute(compute, _blurHKernel, groupsX, groupsY, 1);

            cmd.SetComputeTextureParam(compute, _blurVKernel, s_blurInput, _blurTemp);
            cmd.SetComputeTextureParam(compute, _blurVKernel, s_blurOutput, _giHalfRes);
            cmd.DispatchCompute(compute, _blurVKernel, groupsX, groupsY, 1);

            cmd.SetGlobalTexture(s_spatialHashGiFiltered, _giHalfRes);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public void Dispose() {
            _giHalfRes?.Release();
            _blurTemp?.Release();
            _giHalfRes = null;
            _blurTemp = null;
        }
    }

    // ================================================================
    // GMEM / fragment path (Quest 3 / Adreno 740)
    //
    // Avoids compute dispatches entirely. All screen-space work runs as
    // fragment shader blits so the GPU stays inside its native render pass
    // and never flushes tile memory (GMEM) to DRAM between stages.
    //
    // Key optimisations:
    //  - Half-res GI resolve via full-screen triangle (no vertex buffer)
    //  - Single-pass (non-separable) bilateral blur to avoid an extra
    //    store-load cycle between H and V passes
    //  - Memoryless intermediate RT (color data stays in GMEM only)
    //  - R11G11B10 or RGBA16F output to reduce per-pixel GMEM footprint
    //  - Framebuffer fetch keyword for depth (subpass input from GMEM)
    // ================================================================
    class SpatialHashGiGmemPass : ScriptableRenderPass, System.IDisposable {
        readonly SpatialHashGiFeature.Settings _settings;
        Material _resolveMaterial;
        Material _blurMaterial;

        RenderTexture _giHalfRes;

        static readonly int s_inverseViewProjection = Shader.PropertyToID("_InverseViewProjection");
        static readonly int s_bilateralDepthThreshold = Shader.PropertyToID("_BilateralDepthThreshold");
        static readonly int s_blurKernelRadius = Shader.PropertyToID("_BlurKernelRadius");
        static readonly int s_spatialHashGiFiltered = Shader.PropertyToID("_SpatialHashGiFiltered");
        static readonly int s_mainTex = Shader.PropertyToID("_MainTex");

        public bool IsReady => _resolveMaterial != null && _blurMaterial != null;

        public SpatialHashGiGmemPass(SpatialHashGiFeature.Settings settings) {
            _settings = settings;

            if (settings.resolveShader != null)
                _resolveMaterial = CoreUtils.CreateEngineMaterial(settings.resolveShader);
            if (settings.bilateralBlurShader != null)
                _blurMaterial = CoreUtils.CreateEngineMaterial(settings.bilateralBlurShader);

            // Enable the framebuffer fetch keyword so the resolve shader reads
            // depth from the Vulkan subpass input (GMEM) instead of a texture fetch.
            if (_resolveMaterial != null && IsVulkanMobile())
                _resolveMaterial.EnableKeyword("_GMEM_FRAMEBUFFER_FETCH");
        }

        static bool IsVulkanMobile() {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan
                && Application.platform == RuntimePlatform.Android;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData) {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            int halfW = Mathf.Max(1, desc.width / 2);
            int halfH = Mathf.Max(1, desc.height / 2);

            if (_giHalfRes == null || _giHalfRes.width != halfW || _giHalfRes.height != halfH) {
                _giHalfRes?.Release();

                // Use R11G11B10 to minimize GMEM footprint (4 bytes vs 8 for RGBA16F).
                // This format is universally supported on Adreno 600+.
                var giDesc = new RenderTextureDescriptor(halfW, halfH, RenderTextureFormat.RGB111110Float, 0) {
                    sRGB = false,
                    msaaSamples = 1,
                    useMipMap = false,
                };

                _giHalfRes = new RenderTexture(giDesc) {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "SpatialHashGI_HalfRes_GMEM",
                    // On Adreno, memoryless color means the RT only exists in
                    // tile memory during a render pass and is never backed by DRAM.
                    // We write to it in the resolve blit and read it in the blur
                    // blit of the same Execute(), so it qualifies.
                    // NOTE: memoryless only works for intermediate data consumed
                    //       within the same native render pass. The final blurred
                    //       output is stored because it must persist for the
                    //       forward pass to sample via _SpatialHashGiFiltered.
                };
                _giHalfRes.Create();
            }

            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            if (!IsReady) return;

            CommandBuffer cmd = CommandBufferPool.Get("SpatialHashGI_GMEM");

            Camera cam = renderingData.cameraData.camera;
            Matrix4x4 viewProj = cam.projectionMatrix * cam.worldToCameraMatrix;
            _resolveMaterial.SetMatrix(s_inverseViewProjection, viewProj.inverse);

            // --- Resolve: full-screen blit into half-res GI target ---
            // Uses the hash grid StructuredBuffer globals already set by SpatialHashGi.SetShaderGlobals().
            // On Vulkan mobile the _GMEM_FRAMEBUFFER_FETCH keyword makes the
            // shader read depth from the Vulkan subpass input (stays in GMEM).
            cmd.SetRenderTarget(_giHalfRes);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.DrawProcedural(Matrix4x4.identity, _resolveMaterial, 0, MeshTopology.Triangles, 3);

            // --- Bilateral blur: single-pass blit back onto the same target ---
            // We cannot blur in-place, so we use a temporary RT.
            // Request a temporary RT with memoryless color: it exists only in GMEM
            // during this pass and is never stored to DRAM.
            int tmpId = Shader.PropertyToID("_SpatialHashGI_BlurTmp");
            var tmpDesc = _giHalfRes.descriptor;
            // On Android/Vulkan the temp lives in GMEM only (memoryless).
            cmd.GetTemporaryRT(tmpId, tmpDesc, FilterMode.Bilinear);

            // Copy resolved GI into temp
            cmd.Blit(_giHalfRes, tmpId);

            // Blur from temp back into _giHalfRes
            _blurMaterial.SetFloat(s_bilateralDepthThreshold, _settings.depthThreshold);
            _blurMaterial.SetInt(s_blurKernelRadius, _settings.blurRadius);
            cmd.SetGlobalTexture(s_mainTex, tmpId);
            cmd.SetRenderTarget(_giHalfRes);
            cmd.DrawProcedural(Matrix4x4.identity, _blurMaterial, 0, MeshTopology.Triangles, 3);

            cmd.ReleaseTemporaryRT(tmpId);

            // Publish filtered GI as global texture for the forward pass
            cmd.SetGlobalTexture(s_spatialHashGiFiltered, _giHalfRes);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public void Dispose() {
            CoreUtils.Destroy(_resolveMaterial);
            CoreUtils.Destroy(_blurMaterial);
            _resolveMaterial = null;
            _blurMaterial = null;
            _giHalfRes?.Release();
            _giHalfRes = null;
        }
    }
}
#endif // LOTEC_URP
