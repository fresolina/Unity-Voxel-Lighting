using System.Collections.Generic;
using UnityEngine;

namespace Lotec.Demo {
    /// <summary>
    /// Shows which way the sun points: a small sphere floating in front of the player with an arrow
    /// sticking out of it, aimed at the sun. <see cref="VrSunRotator"/> drives it while the sun is being
    /// steered. In a headset the sun itself is usually out of view (indoors, behind you, or below the
    /// horizon), so without this the gesture gives no feedback beyond the lighting slowly shifting.
    ///
    /// Geometry is built in code: a prefab would need authored art and a wired reference, while Unity's
    /// primitives are always available.
    ///
    /// The material is VoxelLit, NOT the URP Lit the primitives ship with. Both reasons are build-only
    /// problems that cannot reproduce in the editor, where nothing is stripped:
    ///  - Variant survival. A shader_feature variant only reaches the player if some BUILD-TIME material
    ///    uses it. This used to copy URP Lit and switch on _EMISSION at runtime; no material in the build
    ///    has emission on, so that variant was stripped and the indicator rendered as the magenta error
    ///    shader - which, having no multiview support, also showed up in one eye only. Every static mesh
    ///    in the level is VoxelLit with _NORMALMAP, so that exact combination is guaranteed present.
    ///  - Grading. VoxelLit applies the scene's exposure + tonemap in-shader; URP Lit does not, so a URP
    ///    Lit object floats over the image ungraded.
    /// The rule this encodes: never enable a keyword at runtime that no build-time material already uses.
    /// </summary>
    [AddComponentMenu("Lotec/Demo/Sun Rotation Indicator")]
    public class SunRotationIndicator : MonoBehaviour {
        [Tooltip("Metres in front of the head the sphere floats. Near enough to read, far enough to focus on.")]
        [SerializeField] float _distance = 0.5f;
        [Tooltip("Metres below eye level, so it sits under whatever you're looking at instead of over it.")]
        [SerializeField] float _dropBelowEyes = 0.15f;
        [Tooltip("Diameter of the sphere in metres. It's tinted with the sun's colour.")]
        [SerializeField] float _sphereDiameter = 0.06f;
        [Tooltip("Arrow length in metres, measured from the sphere's centre to the tip.")]
        [SerializeField] float _arrowLength = 0.12f;
        [Tooltip("Thickness of the arrow's shaft in metres.")]
        [SerializeField] float _shaftDiameter = 0.012f;
        [Tooltip("Arrow colour. Kept constant so the shaft stays readable however dark the sun goes.")]
        [SerializeField] Color _arrowColor = new Color(1f, 0.78f, 0.25f);
        [Tooltip("Head to float in front of. Empty = Camera.main (the XR rig's camera).")]
        [SerializeField] Camera _head;

        // Arrow head proportions, as fractions of the arrow's length / the shaft's thickness. Not exposed:
        // they're what makes an arrow look like an arrow, not something worth tuning per scene.
        const float HeadLengthFraction = 0.3f;
        const float HeadWidthFactor = 3.2f;
        const int HeadSegments = 12;

        // The one VoxelLit shader_feature combination every material in the level uses, and therefore the
        // only one guaranteed to survive shader stripping into the player. _BumpMap stays at its "bump"
        // default (a flat normal), which shades identically to having no normal map at all.
        const string VoxelLitShaderName = "Lotec/Voxel Lighting/Voxel Lit";
        const string NormalMapKeyword = "_NORMALMAP";

        Transform _root;
        Transform _arrow;
        Material _sphereMaterial;
        Material _arrowMaterial;
        Mesh _headMesh;
        Camera _resolvedHead;

        void OnDisable() {
            Hide();
        }

        void OnDestroy() {
            // Built in code, so nothing else owns these.
            DestroyOwned(_sphereMaterial);
            DestroyOwned(_arrowMaterial);
            DestroyOwned(_headMesh);
        }

        // Destroy() throws in edit mode, and this component gets driven there too (poking Show from an
        // editor script is the only way to eyeball the visual without a headset), so route every teardown
        // through here rather than assuming play mode.
        static void DestroyOwned(Object owned) {
            if (owned == null) return;
            if (Application.isPlaying) Destroy(owned);
            else DestroyImmediate(owned);
        }

        /// <summary>Place the sphere in front of the player and aim its arrow along
        /// <paramref name="towardSun"/> (the direction the sun is IN, not the direction it shines).
        /// Call every frame while the sun is being steered; the visual is built on the first call.</summary>
        public void Show(Vector3 towardSun, Color sunColor) {
            Camera head = ResolveHead();
            if (head == null || towardSun.sqrMagnitude < 1e-6f) {
                Hide();
                return;
            }

            Build();
            _root.gameObject.SetActive(true);

            Transform h = head.transform;
            // Head-locked: rides the view so it can't be lost, but its rotation stays world-aligned - the
            // arrow has to mean a world direction, not one relative to wherever you happen to be looking.
            _root.SetPositionAndRotation(h.position + h.forward * _distance - h.up * _dropBelowEyes,
                Quaternion.identity);
            _arrow.localRotation = Quaternion.FromToRotation(Vector3.up, towardSun);
            _sphereMaterial.SetColor(ShaderIds.BaseColor, sunColor);
        }

        /// <summary>Hide the visual (kept built, so the next <see cref="Show"/> is free).</summary>
        public void Hide() {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        Camera ResolveHead() {
            if (_head != null) return _head;
            // Camera.main is the rig's camera in the XR starter assets; the fallback covers a rig whose
            // camera lost the MainCamera tag.
            if (_resolvedHead == null) _resolvedHead = Camera.main ?? FindAnyObjectByType<Camera>();
            return _resolvedHead;
        }

        void Build() {
            if (_root != null) return;

            _root = new GameObject("Sun Direction").transform;
            _root.SetParent(transform, false);

            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Strip(sphere, _root, Vector3.zero, Vector3.one * _sphereDiameter);
            Renderer sphereRenderer = sphere.GetComponent<Renderer>();
            _sphereMaterial = CreateMaterial(sphereRenderer.sharedMaterial, Color.white);
            sphereRenderer.sharedMaterial = _sphereMaterial;

            // The arrow's parts are modelled along +Y and only this pivot is rotated, so aiming the arrow
            // is one FromToRotation per frame.
            _arrow = new GameObject("Arrow").transform;
            _arrow.SetParent(_root, false);

            float headLength = _arrowLength * HeadLengthFraction;
            float shaftLength = _arrowLength - headLength;

            // Shaft starts at the sphere's centre: the part inside the sphere is hidden, so the arrow
            // reads as sticking out of it rather than balancing on it.
            GameObject shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Strip(shaft, _arrow, new Vector3(0f, shaftLength * 0.5f, 0f),
                // Unity's cylinder is 2 units tall and 1 across, hence the halved height.
                new Vector3(_shaftDiameter, shaftLength * 0.5f, _shaftDiameter));
            _arrowMaterial = CreateMaterial(shaft.GetComponent<Renderer>().sharedMaterial, _arrowColor);
            shaft.GetComponent<Renderer>().sharedMaterial = _arrowMaterial;

            _headMesh = BuildConeMesh(HeadSegments);
            var head = new GameObject("Head", typeof(MeshFilter), typeof(MeshRenderer));
            head.GetComponent<MeshFilter>().sharedMesh = _headMesh;
            head.GetComponent<MeshRenderer>().sharedMaterial = _arrowMaterial;
            float headRadius = _shaftDiameter * HeadWidthFactor * 0.5f;
            Strip(head, _arrow, new Vector3(0f, shaftLength, 0f),
                new Vector3(headRadius, headLength, headRadius));
        }

        // Primitives arrive with a collider, which would swallow XRI ray/poke hits, and with shadows,
        // which a HUD gizmo has no business casting into the scene.
        static void Strip(GameObject go, Transform parent, Vector3 localPosition, Vector3 localScale) {
            if (go.TryGetComponent(out Collider collider)) DestroyOwned(collider);
            if (go.TryGetComponent(out Renderer renderer)) {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
        }

        // A tinted VoxelLit material (see the class summary for why not the primitive's URP Lit). No
        // emission: VoxelLit runs the scene's auto-exposure, so in a dark room the indicator brightens
        // along with everything else rather than needing a glow of its own - and adding one would mean
        // switching on _EMISSION_ON, a variant no build-time material uses and stripping would drop.
        // `fallbackSource` is the primitive's own material, used only if the package shader somehow
        // isn't in the build; plain URP Lit with no runtime keywords is itself a safe variant.
        static Material CreateMaterial(Material fallbackSource, Color color) {
            Shader shader = Shader.Find(VoxelLitShaderName);
            Material material = shader != null
                ? new Material(shader) { name = "Sun Indicator (VoxelLit)" }
                : new Material(fallbackSource) { name = fallbackSource.name + " (Sun Indicator)" };
            if (shader != null) material.EnableKeyword(NormalMapKeyword);
            if (material.HasProperty(ShaderIds.BaseColor)) material.SetColor(ShaderIds.BaseColor, color);
            if (material.HasProperty(ShaderIds.Color)) material.SetColor(ShaderIds.Color, color);
            // Runtime-only object: keep it out of any GI the scene bakes or gathers from emissive surfaces.
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            return material;
        }

        // Unit cone (radius 1, height 1, apex at +Y) so the transform's scale sets its size. Winding:
        // a triangle (a, b, c) is visible from the side Cross(b - a, c - a) points at, which is what puts
        // the sides facing outward and the base cap facing away from the tip.
        static Mesh BuildConeMesh(int segments) {
            var vertices = new List<Vector3>(segments * 6);
            var triangles = new List<int>(segments * 6);
            var apex = new Vector3(0f, 1f, 0f);

            for (int i = 0; i < segments; i++) {
                float a0 = (float)i / segments * Mathf.PI * 2f;
                float a1 = (float)(i + 1) / segments * Mathf.PI * 2f;
                var p0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                var p1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));

                // Flat-shaded: each face gets its own vertices, so RecalculateNormals gives crisp facets
                // instead of smearing the tip.
                int side = vertices.Count;
                vertices.Add(apex);
                vertices.Add(p1);
                vertices.Add(p0);
                triangles.Add(side);
                triangles.Add(side + 1);
                triangles.Add(side + 2);

                int cap = vertices.Count;
                vertices.Add(Vector3.zero);
                vertices.Add(p0);
                vertices.Add(p1);
                triangles.Add(cap);
                triangles.Add(cap + 1);
                triangles.Add(cap + 2);
            }

            var mesh = new Mesh { name = "Sun Indicator Arrow Head" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static class ShaderIds {
            public static readonly int BaseColor = Shader.PropertyToID("_BaseColor"); // VoxelLit and URP Lit
            public static readonly int Color = Shader.PropertyToID("_Color");         // legacy fallback path
        }
    }
}
