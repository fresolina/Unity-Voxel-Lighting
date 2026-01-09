using UnityEngine;

namespace Lotec.Lighting {
    // TODO: Rename to VoxelVolume
    public class SdfVolume : MonoBehaviour {
        [Header("Bake Input")]
        [SerializeField] Transform _bakeRoot;

        [Tooltip("Extra padding added to computed bounds (world units).")]
        [Min(0f)]
        [SerializeField] float _paddingWorld = 0.25f;

        [Header("Bake Resolution")]
        [Tooltip("Maximum voxel resolution along the largest axis.")]
        [Min(4)]
        public int maxResolution = 64;

        [Tooltip("Computed voxel resolution (x,y,z) after bounds/resolution selection.")]
        public Vector3Int bakedResolution = new Vector3Int(32, 32, 32);

        [Tooltip("Computed bounds used for baking.")]
        public Bounds bakedBounds = new Bounds(Vector3.zero, Vector3.one);

        [Header("Output Textures")]
        public Texture3D sdfTexture;
        public Texture3D occlusionBitmaskTexture;

        public Transform BakeRoot { get => _bakeRoot; set => _bakeRoot = value; }

        public bool TryComputeBoundsFromRoot(out Bounds bounds) {
            bounds = default;

            if (_bakeRoot == null)
                return false;

            bool any = false;

            var meshRenderers = _bakeRoot.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var mr in meshRenderers) {
                if (mr == null)
                    continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                    continue;

                if (!any) {
                    bounds = mr.bounds;
                    any = true;
                } else {
                    bounds.Encapsulate(mr.bounds);
                }
            }

            if (!any)
                return false;

            if (_paddingWorld > 0f) {
                bounds.Expand(_paddingWorld * 2f);
            }

            return true;
        }

        public void RecomputeBoundsAndResolution() {
            if (!TryComputeBoundsFromRoot(out Bounds b))
                return;

            bakedBounds = b;
            bakedResolution = ComputeResolutionForBounds(bakedBounds, maxResolution);
        }

        public static Vector3Int ComputeResolutionForBounds(Bounds bounds, int maxRes) {
            maxRes = Mathf.Max(4, maxRes);

            Vector3 size = bounds.size;
            float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            maxAxis = Mathf.Max(0.0001f, maxAxis);

            float voxelSize = maxAxis / maxRes;
            voxelSize = Mathf.Max(0.0001f, voxelSize);

            int rx = Mathf.Clamp(Mathf.CeilToInt(size.x / voxelSize), 4, maxRes);
            int ry = Mathf.Clamp(Mathf.CeilToInt(size.y / voxelSize), 4, maxRes);
            int rz = Mathf.Clamp(Mathf.CeilToInt(size.z / voxelSize), 4, maxRes);

            return new Vector3Int(rx, ry, rz);
        }

        private void OnValidate() {
            maxResolution = Mathf.Max(4, maxResolution);
            _paddingWorld = Mathf.Max(0f, _paddingWorld);

            bakedResolution.x = Mathf.Max(4, bakedResolution.x);
            bakedResolution.y = Mathf.Max(4, bakedResolution.y);
            bakedResolution.z = Mathf.Max(4, bakedResolution.z);
        }

    }
}
