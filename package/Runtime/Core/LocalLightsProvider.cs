using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Lighting {
    /// <summary>
    /// Per-level list of dynamic point/spot lights for the persistent <see cref="LocalLightsPublisher"/>
    /// to publish. Put this on the level's settings GameObject (the one carrying
    /// <see cref="BufferGiFields"/>) and list the lights that belong to the LEVEL rather than to the
    /// bootstrap: a torch on a wall, a lamp the player can switch, a pickup that spawns with the level.
    ///
    /// It exists because the publisher is a singleton in the bootstrap scene, so its own serialized list
    /// can only reach lights in that scene - a level scene cannot be referenced across the scene
    /// boundary. Same problem BufferGiFields solves for the GI field inputs, and the same shape of
    /// answer, except this one uses a self-registry rather than a scene scan: every enabled provider adds
    /// itself to <see cref="All"/>, so additively loaded levels just work and there is no manager
    /// dependency to race.
    ///
    /// A listed light is switched by enabling/disabling the Light (or its GameObject) - the publisher
    /// skips lights that aren't <c>isActiveAndEnabled</c>. Disabling THIS component takes the whole list
    /// out at once. Lights created after load can be added with <see cref="Add"/>.
    ///
    /// Only the first <see cref="LocalLightData.MaxPointLights"/> point and
    /// <see cref="LocalLightData.MaxSpotLights"/> spot lights are published. The publisher's own list is
    /// collected FIRST, so a level cannot crowd out the player's flashlight; beyond that, providers are
    /// collected in registration order and the publisher warns when it has to drop any.
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways] // must also register in edit mode - the GI solve and direct lighting run there
    [AddComponentMenu("Lotec/Voxel Lighting/Local Lights Provider")]
    public class LocalLightsProvider : MonoBehaviour {
        // Self-registry: every enabled provider adds itself, so the publisher needs no scene scan and
        // no load-order guarantee (mirrors VoxelVolume's registry).
        static readonly List<LocalLightsProvider> s_all = new List<LocalLightsProvider>();

        /// <summary>All enabled providers, in registration order - which is the order the publisher
        /// collects them in once its own list is done.</summary>
        public static IReadOnlyList<LocalLightsProvider> All => s_all;

        [Tooltip("This level's dynamic point/spot lights, published for fragment direct lighting + GI " +
                 "by the LocalLightsPublisher in the bootstrap scene. Disable a light to switch it off; " +
                 "disable this component to switch the whole list off.")]
        [SerializeField] List<Light> _lights = new List<Light>();

        /// <summary>This level's lights. Entries can be null if one was deleted; every reader skips those.</summary>
        public IReadOnlyList<Light> Lights => _lights;

        void OnEnable() {
            if (!s_all.Contains(this))
                s_all.Add(this);
        }

        void OnDisable() {
            s_all.Remove(this);
        }

        /// <summary>Add a light created after load (a spawned pickup, a dropped torch). Ignores nulls and
        /// duplicates. Lights that ship WITH the level belong in the serialized list instead.</summary>
        public void Add(Light light) {
            if (light != null && !_lights.Contains(light))
                _lights.Add(light);
        }

        /// <summary>Remove a light added by <see cref="Add"/> (or a serialized one). Returns false if it
        /// wasn't listed. Not needed just to switch a light off - disable the Light for that.</summary>
        public bool Remove(Light light) => _lights.Remove(light);
    }
}
