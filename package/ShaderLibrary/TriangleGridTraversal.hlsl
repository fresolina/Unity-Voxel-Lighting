#ifndef LOTEC_TRIANGLE_GRID_TRAVERSAL_INCLUDED
#define LOTEC_TRIANGLE_GRID_TRAVERSAL_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Shaders/Compute/BufferGiCommonCanary.compute, which includes every header here and fails
// the moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.

// Exact ray/voxel queries against the scene triangles, using the CPU-built uniform grid
// from ExactSdfBake.BuildUniformGrid. Bake-only (only OcclusionFieldTrace.compute uses it, never a
// runtime shader variant), so correctness beats speed everywhere in here.
//
// Why this exists: sphere-tracing an SDF answers "does this ray hit geometry" only
// approximately - trilinear interpolation smooths away sub-voxel-thick walls and overestimates
// distance at convex edges, so rays tunnel through. Offline there is no reason to accept that;
// the grid reduces a ray to a handful of triangle tests, so the exact answer is affordable.

#include "Math.hlsl"

// Uniform grid over the bake bounds, CSR layout (see ExactSdfBake.BuildUniformGrid).
// NOTE: the grid is sized to ~1 triangle per cell and is UNRELATED to the field's voxel
// grid - cells are generally coarser or finer than voxels, so never conflate the two.
StructuredBuffer<float3> _TriVerts;  // 3 float3 per triangle
StructuredBuffer<uint> _CellStart;   // cellCount + 1 prefix-sum offsets
StructuredBuffer<uint> _TriIndices;  // triangle indices grouped by cell
int3 _GridDim;                       // grid cell counts per axis
float _CellSize;                     // cubic cell edge length, world units
float3 _BoundsMin;                   // grid/bounds origin

// Ray hits are accepted from this far along the ray. Origins are always jittered inside a voxel
// the occupancy pass proved EMPTY, so no triangle can be closer than the voxel wall and this only
// guards floating-point noise - it is not a surface bias to tune.
#define LOTEC_RAY_TMIN 1e-4

// Tests every triangle registered in one cell. Returns true on the first hit inside
// (LOTEC_RAY_TMIN, maxDist]. A binary shadow ray only needs SOME hit, not the nearest one, so
// neither cell order nor hit order matters and we can bail immediately. Triangles straddling
// several cells get retested - harmless for the same reason.
// Single-exit on purpose: Unity's Vulkan compiler reports "potentially uninitialized variable" for
// early-return bools consumed by a short-circuiting `&&` at the call site. `dist` is likewise seeded
// rather than left to RayTriangleIntersect's own initialisation, so it is never read undefined.
bool CellBlocksRay(int3 cell, float3 origin, float3 dir, float maxDist) {
    bool blocked = false;
    if (all(cell >= 0) && all(cell < _GridDim)) {
        int cellIndex = cell.x + cell.y * _GridDim.x + cell.z * _GridDim.x * _GridDim.y;
        uint start = _CellStart[cellIndex];
        uint end = _CellStart[cellIndex + 1];

        [loop]
        for (uint k = start; k < end && !blocked; k++) {
            int tri = (int)_TriIndices[k];
            int baseIndex = tri * 3;
            float3 a = _TriVerts[baseIndex + 0];
            float3 b = _TriVerts[baseIndex + 1];
            float3 c = _TriVerts[baseIndex + 2];

            float dist = 1e6;
            if (RayTriangleIntersect(origin, dir, a, b, c, dist) && dist > LOTEC_RAY_TMIN && dist <= maxDist)
                blocked = true;
        }
    }
    return blocked;
}

// 3D-DDA (Amanatides-Woo) over the uniform grid; returns true if anything blocks the ray within
// maxDist. `dir` must be unit length, so the marched t comes out in world units. The origin cell is
// tested BEFORE stepping (unlike an occupancy march, which steps off its own voxel first): grid cells
// are not voxels, so a wall can share a cell with the empty voxel the ray starts in.
bool TraceShadowRay(float3 origin, float3 dir, float maxDist) {
    float3 gridPos = (origin - _BoundsMin) / _CellSize;
    int3 cell = clamp(int3(floor(gridPos)), int3(0, 0, 0), _GridDim - int3(1, 1, 1));

    int3 stepDir = int3(dir.x >= 0.0 ? 1 : -1, dir.y >= 0.0 ? 1 : -1, dir.z >= 0.0 ? 1 : -1);
    // World distance per unit of travel along each axis. Near-zero components produce a huge
    // value, so that axis is simply never the next one crossed.
    float3 invDir = 1.0 / max(abs(dir), 1e-8);
    float3 tDelta = _CellSize * invDir;

    // Distance to the first cell boundary on each axis, measured from the true (unclamped) origin.
    float3 nextBoundary = float3(cell) + float3(stepDir.x > 0 ? 1 : 0, stepDir.y > 0 ? 1 : 0, stepDir.z > 0 ? 1 : 0);
    float3 tMax = abs(nextBoundary - gridPos) * _CellSize * invDir;

    // A ray crosses at most GridDim cells per axis, so the sum bounds any corner-to-corner walk.
    int maxSteps = _GridDim.x + _GridDim.y + _GridDim.z + 3;

    [loop]
    for (int s = 0; s < maxSteps; s++) {
        if (CellBlocksRay(cell, origin, dir, maxDist))
            return true;

        float tNext = min(tMax.x, min(tMax.y, tMax.z));
        if (tNext > maxDist)
            return false; // reached the light before anything blocked us

        if (tMax.x <= tMax.y && tMax.x <= tMax.z) { cell.x += stepDir.x; tMax.x += tDelta.x; }
        else if (tMax.y <= tMax.z)                { cell.y += stepDir.y; tMax.y += tDelta.y; }
        else                                      { cell.z += stepDir.z; tMax.z += tDelta.z; }

        if (any(cell < 0) || any(cell >= _GridDim))
            return false; // escaped the grid -> sky
    }
    return false;
}

// Separating-axis test on one candidate axis. A zero axis (degenerate edge) collapses to
// 0 > 0, which passes - never a false rejection.
bool SatAxisOverlaps(float3 axis, float3 v0, float3 v1, float3 v2, float3 boxHalf) {
    float p0 = dot(v0, axis);
    float p1 = dot(v1, axis);
    float p2 = dot(v2, axis);
    float r = dot(boxHalf, abs(axis));
    return !(min(p0, min(p1, p2)) > r || max(p0, max(p1, p2)) < -r);
}

// Conservative triangle-vs-AABB overlap (Akenine-Moller SAT, 13 axes). Used to mark a voxel solid
// when ANY triangle touches it, so the occupancy grid can never report empty where geometry is.
bool TriangleOverlapsVoxel(float3 a, float3 b, float3 c, float3 boxCenter, float3 boxHalf) {
    float3 v0 = a - boxCenter;
    float3 v1 = b - boxCenter;
    float3 v2 = c - boxCenter;

    float3 e0 = v1 - v0;
    float3 e1 = v2 - v1;
    float3 e2 = v0 - v2;

    const float3 ax = float3(1, 0, 0);
    const float3 ay = float3(0, 1, 0);
    const float3 az = float3(0, 0, 1);

    // Single-exit, same reason as CellBlocksRay.
    // 3 box face normals, then the triangle face normal, then 9 edge x box-axis cross products.
    float3 lo = min(v0, min(v1, v2));
    float3 hi = max(v0, max(v1, v2));
    bool overlaps = !(any(lo > boxHalf) || any(hi < -boxHalf));
    overlaps = overlaps && SatAxisOverlaps(cross(e0, e1), v0, v1, v2, boxHalf);
    overlaps = overlaps && SatAxisOverlaps(cross(e0, ax), v0, v1, v2, boxHalf);
    overlaps = overlaps && SatAxisOverlaps(cross(e0, ay), v0, v1, v2, boxHalf);
    overlaps = overlaps && SatAxisOverlaps(cross(e0, az), v0, v1, v2, boxHalf);
    overlaps = overlaps && SatAxisOverlaps(cross(e1, ax), v0, v1, v2, boxHalf);
    overlaps = overlaps && SatAxisOverlaps(cross(e1, ay), v0, v1, v2, boxHalf);
    overlaps = overlaps && SatAxisOverlaps(cross(e1, az), v0, v1, v2, boxHalf);
    overlaps = overlaps && SatAxisOverlaps(cross(e2, ax), v0, v1, v2, boxHalf);
    overlaps = overlaps && SatAxisOverlaps(cross(e2, ay), v0, v1, v2, boxHalf);
    overlaps = overlaps && SatAxisOverlaps(cross(e2, az), v0, v1, v2, boxHalf);
    return overlaps;
}

// True if any triangle registered in the cells overlapping this voxel actually touches it.
bool VoxelIsSolid(float3 boxCenter, float3 boxHalf) {
    int3 cellMin = int3(floor((boxCenter - boxHalf - _BoundsMin) / _CellSize));
    int3 cellMax = int3(floor((boxCenter + boxHalf - _BoundsMin) / _CellSize));
    cellMin = clamp(cellMin, int3(0, 0, 0), _GridDim - int3(1, 1, 1));
    cellMax = clamp(cellMax, int3(0, 0, 0), _GridDim - int3(1, 1, 1));

    bool solid = false;
    [loop]
    for (int z = cellMin.z; z <= cellMax.z && !solid; z++) {
        [loop]
        for (int y = cellMin.y; y <= cellMax.y && !solid; y++) {
            [loop]
            for (int x = cellMin.x; x <= cellMax.x && !solid; x++) {
                int cellIndex = x + y * _GridDim.x + z * _GridDim.x * _GridDim.y;
                uint start = _CellStart[cellIndex];
                uint end = _CellStart[cellIndex + 1];
                [loop]
                for (uint k = start; k < end && !solid; k++) {
                    int tri = (int)_TriIndices[k] * 3;
                    if (TriangleOverlapsVoxel(_TriVerts[tri + 0], _TriVerts[tri + 1], _TriVerts[tri + 2], boxCenter, boxHalf))
                        solid = true;
                }
            }
        }
    }
    return solid;
}

#endif // LOTEC_TRIANGLE_GRID_TRAVERSAL_INCLUDED
