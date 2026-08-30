using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// The geometry a shadow backend needs, and nothing else: the hi-res occupancy, the two fields'
    /// world boxes, and the grid constants that address them.
    /// <para>
    /// This is the whole contract between the GI and the sun shadow, and it points ONE WAY.
    /// <see cref="BufferGiUpdater"/> implements it; <see cref="VoxelSunShadow"/> consumes it. The
    /// updater never reads anything back from the shadow, which is what makes a future "shadows, no
    /// GI" configuration possible - and what stops a second backend (Unity shadowmaps, a per-pixel
    /// raymarch) from having to know the GI exists. See docs/direct-shadow-extraction.md.
    /// </para>
    /// <para>
    /// Every member here was already public on the updater before the split. That is not a
    /// coincidence worth ignoring: the coupling really was file layout rather than data flow.
    /// </para>
    /// </summary>
    public interface IVoxelOccupancySource {
        /// <summary>1 bit per hi-res cell, concatenated over the fields. Null until a bake lands.</summary>
        ComputeBuffer OccupancyHiBuffer { get; }

        /// <summary>Words of hi-res occupancy per field - the stride a field index multiplies.</summary>
        int OccWordsPerField { get; }

        /// <summary>Resolution of the sun-visibility volume. Owned here rather than by the shadow
        /// because it is clamped against the occupancy resolution (detail beyond the geometry it is
        /// evaluated against is fabricated), and those two are chosen together.</summary>
        int ShadowGrid { get; }

        /// <summary>False when there is only a fine field, in which case the coarse accessors below
        /// mirror the fine ones and no coarse dispatch should be made.</summary>
        bool HasCoarse { get; }

        Vector3 GridOrigin { get; }
        Vector3 GridSize { get; }
        Vector3 VoxelSize { get; }
        Vector3 CoarseOrigin { get; }
        Vector3 CoarseSize { get; }
        Vector3 CoarseVoxelSize { get; }

        /// <summary>Publish the grid/occupancy/shadow resolution constants to a compute shader.
        /// Uniforms are per-ComputeShader, so a kernel in a separate asset must be given these
        /// explicitly or it reads BGI_COUNT as 0 and writes nothing, silently.</summary>
        void BindGridConstants(ComputeShader cs);
    }
}
