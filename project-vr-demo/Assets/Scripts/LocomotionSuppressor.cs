using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

namespace Lotec.Demo {
    /// <summary>
    /// Switches the rig's stick-driven locomotion off while a gesture is borrowing a thumbstick for
    /// something else, so pushing the stick to steer the sun or dim a light doesn't also walk or spin the
    /// player. Move and turn providers both go, since the right stick's snap-turn treats a downward push
    /// as "turn around" - exactly the direction the intensity gesture uses.
    ///
    /// Reference counted: two gestures can hold it at once (a grip hold plus a light button) and
    /// locomotion only returns when the last one lets go, restoring each provider to the state it was in
    /// rather than blanket-enabling whatever the rig had switched off.
    /// </summary>
    public static class LocomotionSuppressor {
        static readonly List<LocomotionProvider> s_providers = new();
        static readonly List<bool> s_wasEnabled = new();
        static int s_holds;

        // Statics survive a play session when Enter Play Mode Options skip the domain reload, which would
        // otherwise leave a stale hold count (locomotion dead on the next run).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() {
            s_providers.Clear();
            s_wasEnabled.Clear();
            s_holds = 0;
        }

        /// <summary>Take a hold on the stick. Balance every call with <see cref="Release"/>.</summary>
        public static void Acquire() {
            if (s_holds++ > 0) return;

            // Re-found per gesture rather than cached: the rig can be swapped or respawned between holds,
            // and this runs once per gesture, not per frame.
            s_providers.Clear();
            s_wasEnabled.Clear();
            Collect<ContinuousMoveProvider>();
            Collect<ContinuousTurnProvider>();
            Collect<SnapTurnProvider>();

            for (int i = 0; i < s_providers.Count; i++) {
                s_wasEnabled.Add(s_providers[i].enabled);
                s_providers[i].enabled = false;
            }
        }

        /// <summary>Give the stick back (no-op unless this was the last hold).</summary>
        public static void Release() {
            if (s_holds == 0) return;
            if (--s_holds > 0) return;

            for (int i = 0; i < s_providers.Count; i++) {
                if (s_providers[i] != null) s_providers[i].enabled = s_wasEnabled[i];
            }

            s_providers.Clear();
            s_wasEnabled.Clear();
        }

        static void Collect<T>() where T : LocomotionProvider {
            T[] found = Object.FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < found.Length; i++) s_providers.Add(found[i]);
        }
    }
}
