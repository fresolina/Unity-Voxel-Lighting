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
        public const int Version = 2;

        public int version = Version;
        public int grid;
        public bool isCoarse;      // true = the coarse field slice, false = a fine/detailed field slice
        public bool bakedNormals;  // normal source the bake used (mesh vs gradient+thicken)
        public Vector3 origin;     // grid mapping this field was rasterized against (world min)
        public Vector3 size;       // grid mapping this field was rasterized against (world size)
        [HideInInspector] public uint[] material; // one field slice, VoxelCount words
        [HideInInspector] public uint[] surface;  // one field slice, VoxelCount words
    }
}
