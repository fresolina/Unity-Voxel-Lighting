using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Disk-baked buffer-GI voxelization for ONE field (the coarse field or a single fine/detailed
    /// field). Stores that field's raster products - the _Material (albedo/emission occupancy) and
    /// _Surface (voxelizer-written normals) slice - plus the world grid it was rasterized against.
    /// At load BufferGiUpdater uploads the coarse asset + the asset matching the active fine volume
    /// instead of re-rasterizing scene meshes; only the RASTER products are stored, so the derive
    /// passes (occupancy bitfield, gradient normals/openness, air distance) still run on GPU and the
    /// asset stays valid when those kernels change. Created from the BufferGiUpdater inspector's
    /// "Bake Voxelization To Disk" button, one asset per field, each named for its field.
    /// </summary>
    [PreferBinarySerialization]
    public class BufferGiBakeAsset : ScriptableObject {
        /// <summary>Bump when the stored layout changes; loaders reject other versions.</summary>
        // 6: grown occupancy added (`occupancyGrown`). It is a RASTER product - the cell behind each
        // FRAGMENT along its triangle normal - and that is per-fragment information nothing else
        // retains: the voxelizer's per-voxel normal is last-write-wins, so a v5 asset cannot
        // reconstruct it (measured: deriving it per voxel via _OccupancyThick moves the defect it
        // exists to fix by 0.0025). A v5 asset must be re-baked, not adopted.
        // 5: hi-res occupancy added (`occupancyHi` + `occGrid`). A v4 asset carries no solidity at the
        // occupancy grid at all, and there is nothing to reconstruct it from without the mesh - the
        // low-res material is a DIFFERENT, coarser raster, so synthesising the fine field from it
        // would fabricate exactly the sub-voxel detail the field exists to record. Re-bake, don't adopt.
        // 4: bakedNormals dropped - the voxelizer now always writes the triangle normal, and
        // CSBuildSurface decides per voxel whether to use it. A v3 asset stores `surface` from a bake
        // that may have had that write DISABLED (bakedNormals == false), so its sub-voxel cells carry
        // no orientation at all and would silently fall back to the thin-axis convention. Nothing in
        // the asset distinguishes that from a normal-bearing bake, so v3 must be re-baked, not adopted.
        // 3: thickening split out of bakedNormals into its own flag. A v2 asset cannot be reinterpreted,
        // because in v2 "bakedNormals == false" silently ALSO meant thickened - the material array it
        // stores already has the grown solids baked in, and nothing records that independently.
        public const int Version = 6;

        public int version = Version;
        public int grid;
        /// <summary>Resolution the hi-res occupancy in <see cref="occupancyHi"/> was rasterized at.
        /// Stored rather than assumed: the asset is baked once at the highest resolution the project
        /// supports and OR-downsampled at load to whatever the running platform uses, so the loader
        /// has to know which level it is holding. Never upsampled - a coarser asset than the platform
        /// wants is rejected, because inventing sub-voxel detail is exactly the failure the hi-res
        /// field exists to avoid.</summary>
        public int occGrid;
        public bool isCoarse;      // true = the coarse field slice, false = a fine/detailed field slice
        // Whether the raster grew each solid one voxel inward. A required part of the identity:
        // thickening changes `material` itself, so an asset baked with it must never be loaded by an
        // updater running without it (that would restore the very leak the setting exists to close,
        // with no visible sign that the bake disagrees).
        public bool thickened;
        public Vector3 origin;     // grid mapping this field was rasterized against (world min)
        public Vector3 size;       // grid mapping this field was rasterized against (world size)
        // NOTE: baked LIGHTS are not stored here. They are re-stamped from the scene's VoxelLights lists
        // every time this asset is uploaded, which is what lets one be switched off at runtime (and
        // retuned without a re-bake); freezing them into `material` would rule both out.
        [HideInInspector] public uint[] material; // one field slice, VoxelCount words
        [HideInInspector] public uint[] surface;  // one field slice, VoxelCount words
        /// <summary>One field slice of the hi-res occupancy bitfield at <see cref="occGrid"/>, in
        /// 4x4x4 blocks of two contiguous uints (see BGI_OCC_BLOCK_LOG2 in BufferGiField.hlsl).
        /// Length = (occGrid/4)^3 * 2. 0.125 bits per voxel, so 2 MB at 256^3.</summary>
        [HideInInspector] public uint[] occupancyHi;
        /// <summary>One field slice of the GROWN occupancy bitfield on the LIGHTING grid: every
        /// rasterized voxel plus the one behind each fragment along its triangle normal, 1 bit per
        /// voxel. Length = VoxelCount/32 (4 KB at 32^3). Read by CSBlur's shell dilation alone -
        /// nothing that traces a ray sees it, which is what separates it from `thickened`.</summary>
        [HideInInspector] public uint[] occupancyGrown;

        /// <summary>Words <see cref="occupancyHi"/> must hold for a given occupancy resolution.</summary>
        public static int OccWordsFor(int occGrid) {
            int blocks = occGrid >> 2;
            return blocks * blocks * blocks * 2;
        }

        /// <summary>True when this bake was rasterized against exactly this grid mapping. Bounds must
        /// match within a millimetre - the stored voxels are only valid for the mapping they were baked
        /// against, so a moved or rescaled volume invalidates them. This is the ONE definition of that
        /// test; BufferGiUpdater's load/diagnostics and BufferGiFields' fallback pick both call it,
        /// rather than each carrying a private epsilon that can drift apart.</summary>
        public bool MatchesBounds(Vector3 gridOrigin, Vector3 gridSize) =>
            (origin - gridOrigin).sqrMagnitude < 1e-6f && (size - gridSize).sqrMagnitude < 1e-6f;

        /// <summary>FNV-1a over the raster products, for telling two BAKES apart in a log.
        ///
        /// Exists because "the file on disk is v4 but the player reports v2" has two very different
        /// causes and the version number alone cannot separate them: the player may hold genuinely
        /// OLDER data (a stale imported artifact), or the right data with one field read wrong. The
        /// content hash answers that - it is computed from the arrays, not from any field that
        /// changed meaning between versions, so it is comparable across versions.
        ///
        /// Samples a stride rather than every word: 32^3 voxels x 2 arrays is 256 KB, and this runs
        /// on a diagnostic path that may fire on a low-end WebGL device.</summary>
        public uint ContentHash() {
            unchecked {
                uint h = 2166136261u;
                h = Mix(h, material);
                h = Mix(h, surface);
                h = Mix(h, occupancyHi);
                h = Mix(h, occupancyGrown);
                return h;
            }
        }

        static uint Mix(uint h, uint[] words) {
            unchecked {
                if (words == null) return h * 16777619u;
                h = (h ^ (uint)words.Length) * 16777619u;
                int stride = words.Length > 4096 ? words.Length / 4096 : 1;
                for (int i = 0; i < words.Length; i += stride) h = (h ^ words[i]) * 16777619u;
                return h;
            }
        }
    }
}
