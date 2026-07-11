
#ifndef LOTEC_TONEMAP_INCLUDED
#define LOTEC_TONEMAP_INCLUDED

// Display-transform tonemap operators for the lit shader. Each takes linear HDR and returns a
// linear value (the render pipeline applies the sRGB OETF on write), so they are interchangeable
// in the display-transform block. The lit shader selects one at runtime via the _Tonemap global.

// Reinhard: the classic x/(1+x) rolloff. Cheap, but desaturates less and clips highlights softly.
half3 ReinhardTonemap(half3 color) {
    return color / (1.0 + color);
}

// --- AgX -------------------------------------------------------------------------------------
// Minimal AgX (Benjamin Wrensch, https://iolite-engine.com/blog_posts/minimal_agx_implementation).
// Maps linear HDR into the AgX log-sigmoid curve, then the inverse transform linearizes the
// result so the pipeline's sRGB-on-write encoding lands it correctly. Film-like, desaturated
// highlight rolloff.
half3 AgxDefaultContrastApprox(half3 x) {
    half3 x2 = x * x;
    half3 x4 = x2 * x2;
    return + 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4
           - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

half3 AgxTonemap(half3 color) {
    const half3x3 agxMat = half3x3(
        0.842479062253094, 0.0423282422610123, 0.0423756549057051,
        0.0784335999999992, 0.878468636469772,  0.0784336,
        0.0792237451477643, 0.0791661274605434, 0.879142973793104);
    const half3x3 agxMatInv = half3x3(
         1.19687900512017,   -0.0528968517574562, -0.0529716355144438,
        -0.0980208811401368,  1.15190312990417,   -0.0980434501171241,
        -0.0990297440797205, -0.0989611768448433,  1.15107367264116);
    const float minEv = -12.47393;
    const float maxEv = 4.026069;

    // mul(vec, mat) so the GLSL-sourced (column-major-constructed) matrices apply with the same
    // orientation as the reference's `mat * vec`.
    color = mul(color, agxMat);                 // inset
    color = clamp(log2(max(color, 1e-10)), minEv, maxEv);
    color = (color - minEv) / (maxEv - minEv);  // log2 encode to [0,1]
    color = AgxDefaultContrastApprox(color);    // sigmoid
    color = mul(color, agxMatInv);              // outset
    return pow(max(color, 0.0), 2.2);           // linearize for sRGB-on-write
}

// --- ACES ------------------------------------------------------------------------------------
// ACES filmic curve, Krzysztof Narkowicz's fit
// (https://knarkowicz.wordpress.com/2016/01/06/aces-filmic-tone-mapping-curve/). A cheap
// approximation of the ACES RRT+ODT: strong, contrasty highlight rolloff. Linear in / linear out.
half3 AcesTonemap(half3 color) {
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((color * (a * color + b)) / (color * (c * color + d) + e));
}

#endif
