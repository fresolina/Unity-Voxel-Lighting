using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// SDF-baked lighting volume. Reads the tight mesh bounds off the <see cref="MeshBounds"/>
    /// on the same GameObject and derives its own grid from them: bake resolution, a voxel
    /// border and cubic-voxel alignment (stored in <see cref="Bounds"/>), plus the baked SDF
    /// textures. Owns the volume-derived shader globals (bounds, SDF texture) and how to publish
    /// them via <see cref="ApplyShaderGlobals"/> - but LightingManager decides *when* and for
    /// *which* volume to call it, so the singular globals reflect the active volume only.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshBounds))]
    [ExecuteAlways] // registration + globals must also work in edit mode (GI solves there too)
    [AddComponentMenu("Lotec/Voxel Lighting/Voxel Volume")]
    public class VoxelVolume : MonoBehaviour {
        static readonly int s_sdfHires = Shader.PropertyToID("_SdfHires");
        static readonly int s_boundsMin = Shader.PropertyToID("_VoxelVolumeBoundsMin");
        static readonly int s_boundsSize = Shader.PropertyToID("_VoxelVolumeBoundsSize");

        // Self-registry: every enabled volume adds itself here (no manager dependency, so
        // registration can't race the manager's initialization order).
        static readonly List<VoxelVolume> s_all = new List<VoxelVolume>();
        /// <summary>All enabled, registered volumes in the scene.</summary>
        public static IReadOnlyList<VoxelVolume> All => s_all;

        [Header("Bake Input")]
        [Tooltip("Maximum voxel resolution along the largest axis.")]
        [Min(4)]
        [SerializeField] int _maxResolution = 128;

        [SerializeField] bool _autoRegisterWithManager = true;

        [Header("Baked static fields")]
        public Texture3D sdfHiresTexture;
        public Texture3D sdfLowresTexture;

        [HideInInspector]
        [SerializeField] Vector3Int _trimmedMaxResolution;
        // The volume's OWN bounds: the MeshBounds' tight bounds grown by the voxel border and
        // aligned to whole cubic voxels. Serialized so builds don't recompute.
        [HideInInspector]
        [SerializeField] Bounds _bounds = new Bounds(Vector3.zero, Vector3.one);

        MeshBounds _meshBounds;

        // Fixed border around the geometry, so the SDF have valid distances at the boundary.
        // 2.0 (not 1.5): at concave corners a thinner border let GI rays leak exterior light.
        // NOTE: This can be reduced when GI light is tracked in all 6 axes.
        const float PaddingVoxels = 2f;

        MeshBounds SourceBounds {
            get {
                if (_meshBounds == null) _meshBounds = GetComponent<MeshBounds>();
                return _meshBounds;
            }
        }

        /// <summary>The mesh root, delegated to the MeshBounds on this GameObject.</summary>
        public Transform BakeRoot {
            get => SourceBounds != null ? SourceBounds.Root : null;
            set { if (SourceBounds != null) SourceBounds.Root = value; }
        }

        /// <summary>Padded, cubic-voxel-aligned bounds the SDF/occlusion fields are baked in.</summary>
        public Bounds Bounds => _bounds;

        public Vector3Int TrimmedMaxResolution => _trimmedMaxResolution;

        /// <summary>Cubic voxel edge length in world units, derived from Bounds and TrimmedMaxResolution.</summary>
        public float VoxelSize => Bounds.size.x / Mathf.Max(1, TrimmedMaxResolution.x);

        /// <summary>Per-axis inverse voxel size (TrimmedMaxResolution / Bounds.size) at the volume grid.</summary>
        public Vector3 VoxelSizeInverse => new Vector3(
            TrimmedMaxResolution.x / Mathf.Max(1e-9f, Bounds.size.x),
            TrimmedMaxResolution.y / Mathf.Max(1e-9f, Bounds.size.y),
            TrimmedMaxResolution.z / Mathf.Max(1e-9f, Bounds.size.z));

        /// <summary>Publish the universal volume-derived globals: world-space bounds and the
        /// hi-res SDF texture (needed by shadows + GI regardless of occlusion). Call from
        /// LightingManager for the active volume - these are singular globals, so a non-active
        /// volume must not publish them. Occlusion-grid globals are published by the occlusion
        /// binders (they read this volume's VoxelSizeInverse / TrimmedMaxResolution).</summary>
        public void ApplyShaderGlobals() {
            Shader.SetGlobalVector(s_boundsMin, Bounds.min);
            Shader.SetGlobalVector(s_boundsSize, Bounds.size);
            if (sdfHiresTexture != null)
                Shader.SetGlobalTexture(s_sdfHires, sdfHiresTexture);
        }

        void OnValidate() {
            _maxResolution = Mathf.Max(4, _maxResolution);
            RecomputeBoundsAndResolution();
        }

        void OnEnable() {
            if (_autoRegisterWithManager && !s_all.Contains(this))
                s_all.Add(this);
            if (Application.isPlaying) LogBoundsDiagnostics();
        }

        // TEMP diagnostic: at runtime, print the deserialized bounds (what the bundle/scene delivered)
        // next to what a fresh recompute from the live renderers WOULD produce. Tells us whether the
        // bundle shipped zero bounds and whether a runtime recompute could recover them.
        void LogBoundsDiagnostics() {
            MeshBounds mb = SourceBounds;
            Transform root = mb != null ? mb.Root : null;
            Bounds fresh = new Bounds();
            int eligible = 0, total = 0;
            if (root != null) {
                foreach (MeshRenderer mr in root.GetComponentsInChildren<MeshRenderer>()) {
                    if (mr == null) continue;
                    total++;
                    if (!MeshBounds.IsBakeEligible(mr)) continue;
                    MeshFilter mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (eligible == 0) fresh = mr.bounds; else fresh.Encapsulate(mr.bounds);
                    eligible++;
                }
            }
            Debug.Log(
                $"[VoxelVolume diag] '{name}' root={(root != null ? root.name : "<null>")}\n" +
                $"  VoxelVolume._bounds size={_bounds.size.ToString("F4")} center={_bounds.center.ToString("F4")}\n" +
                $"  MeshBounds._bounds  size={(mb != null ? mb.Bounds.size.ToString("F4") : "<no MeshBounds>")}\n" +
                $"  live recompute would: eligible={eligible}/{total} renderers, size={fresh.size.ToString("F4")} center={fresh.center.ToString("F4")}",
                this);
        }

        void OnDisable() {
            s_all.Remove(this);
        }

        /// <summary>Refresh the MeshBounds, then derive this volume's grid from the tight bounds:
        /// the voxel border and the cubic alignment. The SDF baker calls this before baking.</summary>
        public void RecomputeBoundsAndResolution() {
            MeshBounds source = SourceBounds;
            if (source == null || source.Root == null) return;

            source.Recompute();
            // Keep the serialized bounds if the source found no geometry (e.g. isStatic reads false
            // when this runs during a build) - don't overwrite good baked bounds with a degenerate box.
            if (source.Bounds.size == Vector3.zero) return;
            _bounds = source.Bounds;
            ComputeMaxResolutionForBounds();
        }

        /// <summary>
        /// Compute TrimmedMaxResolution based on bounds and _maxResolution
        /// Example: 128x128x128 -> 128x67x24 for a long thin volume
        /// </summary>
        void ComputeMaxResolutionForBounds() {
            _maxResolution = Mathf.Max(4, _maxResolution);

            Vector3 size = _bounds.size;
            float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            maxAxis = Mathf.Max(0.0001f, maxAxis);

            // Increase bounds with padding.
            float resolutionBudget = Mathf.Max(1f, _maxResolution - 2f * PaddingVoxels);
            float voxelSize = maxAxis / resolutionBudget;
            voxelSize = Mathf.Max(0.0001f, voxelSize);
            _bounds.Expand(2f * PaddingVoxels * voxelSize);
            size = _bounds.size;

            int rx = Mathf.Clamp(Mathf.CeilToInt(size.x / voxelSize), 4, _maxResolution);
            int ry = Mathf.Clamp(Mathf.CeilToInt(size.y / voxelSize), 4, _maxResolution);
            int rz = Mathf.Clamp(Mathf.CeilToInt(size.z / voxelSize), 4, _maxResolution);

            _trimmedMaxResolution = new Vector3Int(rx, ry, rz);

            // Enforce cubic voxels by expanding the bounds to an integer number of
            // cells of the chosen voxel size on every axis. Without this adjustment,
            // the ceil() above would make the effective voxel size drift per axis and
            // the runtime GI shaders would silently stop agreeing on shell widths,
            // offsets, and distance thresholds.
            Vector3 alignedSize = new Vector3(rx * voxelSize, ry * voxelSize, rz * voxelSize);
            _bounds = new Bounds(_bounds.center, alignedSize);
        }
    }
}
