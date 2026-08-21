
#ifndef LOTEC_TONEMAP_INCLUDED
#define LOTEC_TONEMAP_INCLUDED

// ENGINE-AGNOSTIC, like every header in this folder: HLSL intrinsics and our own headers only. No
// URP includes, no vertex/fragment semantics, no Core.hlsl texture macros. That means any of these
// can be included from a fragment shader, a compute shader or the voxelize raster alike.
//
// The engine boundary is the .shader / .compute ENTRY POINTS. VoxelLit.shader includes URP's
// Core.hlsl and Lighting.hlsl and calls GetMainLight(), then hands this library plain values.
// Guarded by Compute/BufferGiCommonCanary.compute, which includes every header here and fails the
// moment one acquires an engine dependency - do not "fix" that by adding an include to the canary.


// Display-transform tonemap operators for the lit shader. Each takes linear HDR and returns a
// linear value (the render pipeline applies the sRGB OETF on write), so they are interchangeable
// in the display-transform block. The lit shader selects one at COMPILE time (TONEMAP_* keyword) -
// only the selected operator ends up in the kernel, because register allocation spans every path a
// kernel contains and AgX's footprint otherwise taxed the cheap operators too.
//
// Everything here stays in `half`: Adreno packs two fp16 values per 32-bit register, so keeping the
// literals half-typed (rather than promoting the chain to fp32) is what keeps that footprint small.

// Reinhard: the classic x/(1+x) rolloff. Cheap, but desaturates less and clips highlights softly.
half3 ReinhardTonemap(half3 color) {
    return color / (1.0h + color);
}

// --- AgX -------------------------------------------------------------------------------------
// Minimal AgX (Benjamin Wrensch, https://iolite-engine.com/blog_posts/minimal_agx_implementation).
// Maps linear HDR into the AgX log-sigmoid curve, then the inverse transform linearizes the
// result so the pipeline's sRGB-on-write encoding lands it correctly. Film-like, desaturated
// highlight rolloff.
half3 AgxDefaultContrastApprox(half3 x) {
    half3 x2 = x * x;
    half3 x4 = x2 * x2;
    // h-suffixed: an unsuffixed literal is float and would promote this whole chain to fp32.
    return + 15.5h * x4 * x2 - 40.14h * x4 * x + 31.96h * x4
           - 6.868h * x2 * x + 0.4298h * x2 + 0.1191h * x - 0.00232h;
}

half3 AgxTonemap(half3 color) {
    // Reference values kept at full precision for provenance; the h suffix is what stops them
    // promoting the arithmetic to fp32 (the compiler rounds them to half either way).
    const half3x3 agxMat = half3x3(
        0.842479062253094h, 0.0423282422610123h, 0.0423756549057051h,
        0.0784335999999992h, 0.878468636469772h,  0.0784336h,
        0.0792237451477643h, 0.0791661274605434h, 0.879142973793104h);
    const half3x3 agxMatInv = half3x3(
         1.19687900512017h,   -0.0528968517574562h, -0.0529716355144438h,
        -0.0980208811401368h,  1.15190312990417h,   -0.0980434501171241h,
        -0.0990297440797205h, -0.0989611768448433h,  1.15107367264116h);
    const half minEv = -12.47393h;
    const half maxEv = 4.026069h;

    // mul(vec, mat) so the GLSL-sourced (column-major-constructed) matrices apply with the same
    // orientation as the reference's `mat * vec`.
    color = mul(color, agxMat);                 // inset
    // 6.1e-5 is fp16's smallest normal. The reference's 1e-10 underflows to 0 in half, giving
    // log2(0) = -INF - the clamp below happens to rescue that, but not on every compiler.
    color = clamp(log2(max(color, 6.1e-5h)), minEv, maxEv);
    color = (color - minEv) / (maxEv - minEv);  // log2 encode to [0,1]
    color = AgxDefaultContrastApprox(color);    // sigmoid
    color = mul(color, agxMatInv);              // outset
    return pow(max(color, 0.0h), 2.2h);         // linearize for sRGB-on-write
}

// --- ACES ------------------------------------------------------------------------------------
// ACES filmic curve, Krzysztof Narkowicz's fit
// (https://knarkowicz.wordpress.com/2016/01/06/aces-filmic-tone-mapping-curve/). A cheap
// approximation of the ACES RRT+ODT: strong, contrasty highlight rolloff. Linear in / linear out.
half3 AcesTonemap(half3 color) {
    const half a = 2.51h;
    const half b = 0.03h;
    const half c = 2.43h;
    const half d = 0.59h;
    const half e = 0.14h;
    // Clamped before squaring: this fit multiplies color by itself, and in fp16 anything past ~256
    // overflows to INF (then INF/INF = NaN). The curve maps everything above ~16 to ~1 anyway, so
    // the clamp costs nothing visually and keeps the half path safe.
    color = min(color, 240.0h);
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

#endif
