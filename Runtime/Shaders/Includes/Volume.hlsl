 // Global shader variables used by all fields.
float3 _VolumePosition;
float3 _VolumeSize;

// Normalise to local [0,1] for texture sampling. Assumes the volume is axis-aligned and starts at _VolumePosition.
// TODO: Maybe support non-axis-aligned to world volumes with _VolumeRotation or _VolumeMatrix?
float3 WorldToVoxelUV(float3 worldPos)
{
    return (worldPos - _VolumePosition) / _VolumeSize;
}
