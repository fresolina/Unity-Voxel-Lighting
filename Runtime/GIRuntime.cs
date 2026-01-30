using UnityEngine;

namespace Lotec.Lighting {
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class GIRuntime : MonoBehaviour {
        public SdfVolume targetVolume;
        public ComputeShader giCompute;
        public bool useHDR = true;
        [Min(1)] public int raysPerVoxel = 1;
        public float maxRayDistance = 10f;

        RenderTexture[] _radiance = new RenderTexture[2];
        RenderTexture _control;
        int _readIdx = 0;
        int _writeIdx = 1;

        int _kernel = -1;
        uint _frameIndex = 0;

        void OnEnable() {
            Initialize();
        }

        void OnDisable() {
            Release();
        }

        void OnValidate() {
            Initialize();
        }

        void Initialize() {
            if (giCompute == null || targetVolume == null) return;
            if (_kernel < 0) _kernel = giCompute.FindKernel("CSMain");

            Vector3Int res = ResolveResolution();
            EnsureTextures(res);

            // set static keywords
            if (useHDR) giCompute.EnableKeyword("LOTEC_GI_HDR"); else giCompute.DisableKeyword("LOTEC_GI_HDR");
        }

        Vector3Int ResolveResolution() {
            // Prefer material field resolution if available
            var mat = targetVolume.materialAlbedoRoughnessTexture;
            if (mat != null) return new Vector3Int(mat.width, mat.height, mat.depth);
            return targetVolume.bakedResolution;
        }

        void EnsureTextures(Vector3Int res) {
            // Create or recreate radiance textures if size/format changed
            RenderTextureFormat radFmt = useHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
            for (int i = 0; i < 2; ++i) {
                if (_radiance[i] == null || _radiance[i].width != res.x || _radiance[i].height != res.y || _radiance[i].volumeDepth != res.z) {
                    if (_radiance[i] != null) _radiance[i].Release();
                    // Use RenderTextureDescriptor to be explicit about format and 3D dimension
                    var desc = new RenderTextureDescriptor(res.x, res.y, RenderTextureFormat.Default, 0) {
                        dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                        volumeDepth = res.z,
                        enableRandomWrite = true,
                        msaaSamples = 1,
                        sRGB = (QualitySettings.activeColorSpace == ColorSpace.Linear) ? false : true
                    };
                    // pick explicit graphics format for UAV compatibility
                    desc.graphicsFormat = useHDR ? UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm;
                    var rt = new RenderTexture(desc) {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        name = $"GI_Radiance_{i}"
                    };
                    try {
                        rt.Create();
                    } catch (System.Exception e) {
                        Debug.LogError($"GIRuntime: Exception creating radiance RenderTexture[{i}] {res.x}x{res.y}x{res.z} format={desc.graphicsFormat}: {e.Message}");
                    }
                    if (!rt.IsCreated()) {
                        Debug.LogError($"GIRuntime: Failed to create radiance 3D RenderTexture[{i}] {res.x}x{res.y}x{res.z} format={desc.graphicsFormat}. Aborting creation.");
                        if (rt != null) { rt.Release(); rt = null; }
                        _radiance[i] = null;
                    } else {
                        _radiance[i] = rt;
                    }
                }
            }

            if (_control == null || _control.width != res.x || _control.height != res.y || _control.volumeDepth != res.z) {
                if (_control != null) _control.Release();
                var cdesc = new RenderTextureDescriptor(res.x, res.y, RenderTextureFormat.Default, 0) {
                    dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                    volumeDepth = res.z,
                    enableRandomWrite = true,
                    msaaSamples = 1,
                    sRGB = false
                };
                cdesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                var ctl = new RenderTexture(cdesc) {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point,
                    name = "GI_Control"
                };
                try {
                    ctl.Create();
                } catch (System.Exception e) {
                    Debug.LogError($"GIRuntime: Exception creating control RenderTexture {res.x}x{res.y}x{res.z}: {e.Message}");
                }
                if (!ctl.IsCreated()) {
                    Debug.LogError($"GIRuntime: Failed to create control 3D RenderTexture {res.x}x{res.y}x{res.z}. Aborting creation.");
                    if (ctl != null) { ctl.Release(); ctl = null; }
                    _control = null;
                } else {
                    _control = ctl;
                }
            }
        }

        void Release() {
            for (int i = 0; i < 2; ++i) {
                if (_radiance[i] != null) { _radiance[i].Release(); _radiance[i] = null; }
            }
            if (_control != null) { _control.Release(); _control = null; }
        }

        void Update() {
            if (!Application.isPlaying && !Application.isEditor) return;
            if (giCompute == null || targetVolume == null) return;

            Initialize();

            // ensure we have a valid kernel, a target volume, and allocated textures before proceeding
            if (_kernel < 0) return;
            Vector3Int res = ResolveResolution();
            if (res.x <= 0 || res.y <= 0 || res.z <= 0) return;

            // Ensure textures exist for the current resolution (recreate if necessary)
            EnsureTextures(res);

            // Make sure underlying RenderTextures are actually created (have image data)
            for (int i = 0; i < 2; ++i) {
                if (_radiance[i] != null && !_radiance[i].IsCreated()) {
                    try { _radiance[i].Create(); } catch { }
                }
            }
            if (_control != null && !_control.IsCreated()) {
                try { _control.Create(); } catch { }
            }

            // Validate RT readiness and required input textures
            if (_radiance[_readIdx] == null || !_radiance[_readIdx].IsCreated() || _radiance[_writeIdx] == null || !_radiance[_writeIdx].IsCreated() || _control == null || !_control.IsCreated()) {
                Debug.LogWarning("GIRuntime: RenderTextures not ready yet, skipping dispatch.");
                PublishGlobals();
                return;
            }
            if (targetVolume.sdfTexture == null || targetVolume.materialAlbedoRoughnessTexture == null) {
                Debug.LogWarning("GIRuntime: Required input textures missing on targetVolume, skipping dispatch.");
                PublishGlobals();
                return;
            }
            // bind resources
            giCompute.SetInts("_Resolution", res.x, res.y, res.z);
            giCompute.SetVector("_BoundsMin", targetVolume.bakedBounds.min);
            giCompute.SetVector("_BoundsSize", targetVolume.bakedBounds.size);
            giCompute.SetInt("_RaysPerVoxel", raysPerVoxel);
            giCompute.SetFloat("_MaxRayDistance", maxRayDistance);
            giCompute.SetInt("_FrameIndex", (int)_frameIndex);
            giCompute.SetInt("_RandomSeed", Random.Range(1, int.MaxValue));

            // Bind textures (all guaranteed non-null above)
            giCompute.SetTexture(_kernel, "_SdfField", targetVolume.sdfTexture);
            giCompute.SetTexture(_kernel, "_MaterialFieldA", targetVolume.materialAlbedoRoughnessTexture);
            if (targetVolume.materialEmissionMetallicTexture != null) giCompute.SetTexture(_kernel, "_MaterialFieldB", targetVolume.materialEmissionMetallicTexture);

            giCompute.SetTexture(_kernel, "_RadianceRead", _radiance[_readIdx]);
            giCompute.SetTexture(_kernel, "_RadianceWrite", _radiance[_writeIdx]);
            giCompute.SetTexture(_kernel, "_ControlField", _control);

            // dispatch
            giCompute.GetKernelThreadGroupSizes(_kernel, out uint tx, out uint ty, out uint tz);
            int gx = Mathf.CeilToInt(res.x / (float)tx);
            int gy = Mathf.CeilToInt(res.y / (float)ty);
            int gz = Mathf.CeilToInt(res.z / (float)tz);
            giCompute.Dispatch(_kernel, gx, gy, gz);

            // swap
            int tmp = _readIdx; _readIdx = _writeIdx; _writeIdx = tmp;
            _frameIndex++;

            // Publish the current read radiance and bounds for shaders to consume
            PublishGlobals();
        }

        [ContextMenu("Reset GI Fields")]
        public void ResetFields() {
            Release();
            Initialize();
        }

        void PublishGlobals() {
            if (_radiance[_readIdx] != null) {
                Shader.SetGlobalTexture("_GIRadiance", _radiance[_readIdx]);
                var bmin = targetVolume.bakedBounds.min;
                var bsize = targetVolume.bakedBounds.size;
                Shader.SetGlobalVector("_GIBoundsMin", new Vector4(bmin.x, bmin.y, bmin.z, 0f));
                Shader.SetGlobalVector("_GIBoundsSize", new Vector4(bsize.x, bsize.y, bsize.z, 0f));
            } else {
                Shader.SetGlobalTexture("_GIRadiance", null);
            }
        }
    }
}
