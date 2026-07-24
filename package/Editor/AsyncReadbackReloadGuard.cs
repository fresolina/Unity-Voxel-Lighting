using UnityEditor;
using UnityEngine.Rendering;

namespace Lotec.Lighting.Editor {
    /// <summary>
    /// Drains any in-flight <see cref="AsyncGPUReadback"/> requests before a domain reload. The GI
    /// updaters pump per-frame luminance readbacks (see AutoExposure) even in edit mode - via the
    /// BufferGiUpdater editor player-loop pump - so a request carrying a managed completion callback is
    /// routinely outstanding when scripts recompile. Left alone, Unity tries to resolve/release that
    /// callback's GCHandle after the NEW domain has loaded and logs "Resolve/Release of invalid GC
    /// handle. The handle is from a previous domain." asserts. Flushing here lets the callbacks run in
    /// the current domain first, so nothing dangles across the reload. Editor-only: no runtime cost.
    /// </summary>
    [InitializeOnLoad]
    static class AsyncReadbackReloadGuard {
        static AsyncReadbackReloadGuard() {
            AssemblyReloadEvents.beforeAssemblyReload += AsyncGPUReadback.WaitAllRequests;
        }
    }
}
