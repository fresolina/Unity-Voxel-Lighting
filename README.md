# Voxel Lighting for Unity

A voxel-based lighting system playground for Unity. Goal is a performant lighting system with no dependency on Unity lightmaps or shadowmaps.

## Features

* Realtime shadows on static and dynamic objects.
  a) Accuracy mode: Ray marching on every pixel.
  b) Performance mode: Occlusion bitmask in the voxel field.
