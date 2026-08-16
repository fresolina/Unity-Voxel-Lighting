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
        // 3: thickening split out of bakedNormals into its own flag. A v2 asset cannot be reinterpreted,
        // because in v2 "bakedNormals == false" silently ALSO meant thickened - the material array it
        // stores already has the grown solids baked in, and nothing records that independently.
        public const int Version = 3;

        public int version = Version;
        public int grid;
        public bool isCoarse;      // true = the coarse field slice, false = a fine/detailed field slice
        public bool bakedNormals;  // normal source the bake used (mesh vs occupancy gradient)
        // Whether the raster grew each solid one voxel inward. Independent of bakedNormals since v3, and
        // a required part of the identity: thickening changes `material` itself, so an asset baked with
        // it must never be loaded by an updater running without it (that would restore the very leak the
        // setting exists to close, with no visible sign that the bake disagrees).
        public bool thickened;
        public Vector3 origin;     // grid mapping this field was rasterized against (world min)
        public Vector3 size;       // grid mapping this field was rasterized against (world size)
        // NOTE: baked LIGHTS are not stored here. They are re-stamped from the scene's VoxelLights lists
        // every time this asset is uploaded, which is what lets one be switched off at runtime (and
        // retuned without a re-bake); freezing them into `material` would rule both out.
        [HideInInspector] public uint[] material; // one field slice, VoxelCount words
        [HideInInspector] public uint[] surface;  // one field slice, VoxelCount words
    }
}
