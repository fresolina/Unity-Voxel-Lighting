using UnityEditor;
using UnityEngine;

namespace Lotec.Lighting.Editor {
    /// <summary>
    /// Keeps every field volume's <see cref="VoxelLights"/> list current while authoring, so a Baked or
    /// Mixed point light lights the scene as soon as it is placed - rather than only after someone
    /// remembers to press "Bake Voxelization To Disk".
    ///
    /// The list is DERIVED data, not authored: which lights qualify is decided by
    /// <c>Light.lightmapBakeType</c> and the volume's bounds, and the first of those does not exist in a
    /// player. That is the whole reason membership has to be serialized at all, and it is also why
    /// rebuilding it automatically is the honest behaviour - the bake button writes exactly the same
    /// list, so after this it only has to be pressed to write the DISK ASSET.
    ///
    /// Polls rather than hooking hierarchyChanged: the scan tests every Light in the scene against a
    /// volume's bounds, and doing that on every drag of every object would be felt. The write is
    /// suppressed when the membership is unchanged (see BufferGiUpdaterEditor.SameLights), so a quiet
    /// scene is never dirtied. BufferGiUpdater's own layout-hash poll then notices a membership change
    /// and re-voxelizes, so the two halves compose without either knowing about the other.
    /// </summary>
    [InitializeOnLoad]
    static class VoxelLightsAutoRefresh {
        // Slow on purpose. This is authoring convenience, not a frame-critical path, and the scan
        // allocates - matching the spirit of BufferGiUpdater's own BakedLightPollInterval.
        const double PollInterval = 0.5;

        static double s_nextPoll;

        static VoxelLightsAutoRefresh() {
            EditorApplication.update += Poll;
        }

        static void Poll() {
            // Authoring only. In play mode this list is what the running game reads, and rewriting it
            // would be editing the scene under a live player.
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            // Mid-compile or mid-import the scene is in flux and SerializedObject writes are wasted.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup < s_nextPoll) return;
            s_nextPoll = EditorApplication.timeSinceStartup + PollInterval;

            BufferGiUpdater gi = Object.FindAnyObjectByType<BufferGiUpdater>();
            if (gi == null || gi.Volume == null) return;
            // Null fields is fine - a fine-only level still has its active volume refreshed.
            BufferGiUpdaterEditor.RefreshBakedLights(gi, BufferGiFields.Find(gi.Volume));
        }
    }
}
