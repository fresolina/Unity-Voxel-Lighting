using UnityEngine;
using UnityEngine.Rendering;

namespace Lotec.Lighting {
    public static class LightExtensions {
        /// <summary>
        /// The colour URP feeds its own shaders for this light: <c>VisibleLight.finalColor</c> - the
        /// light's colour in the ACTIVE colour space, scaled by intensity and tinted by the colour
        /// temperature. Use this for every light value handed to a shader or compute solve.
        ///
        /// <c>light.color * light.intensity</c> is NOT this: <see cref="Light.color"/> is the authored
        /// (sRGB) colour, so in a linear project it lands in the shader unconverted - a warm light comes
        /// out washed out and too bright (its dark channels most of all), and disagrees with the main
        /// light, which arrives via URP already converted.
        /// </summary>
        public static Vector4 FinalColor(this Light light) {
            if (light == null) return Vector4.zero;
            // Only the COLOUR is colour-space converted; intensity and the temperature tint are plain
            // linear multipliers on top - the order Unity itself uses to build finalColor.
            Color color = GraphicsSettings.lightsUseLinearIntensity ? light.color.linear : light.color;
            if (GraphicsSettings.lightsUseColorTemperature && light.useColorTemperature)
                color *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
            return (Vector4)(color * light.intensity);
        }
    }
}
