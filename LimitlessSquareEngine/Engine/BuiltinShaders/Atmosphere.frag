#version 430 core

in vec3 vWorldPos;
flat in vec3 vPlanetCenter;
flat in int vRenderSpace;

out vec4 FragColor;

uniform vec3 uCameraPosition;

uniform float uPlanetRadius;
uniform float uAtmosphereRadius;
uniform vec4 uHorizonColor;
uniform vec4 uSkyColor;
uniform vec4 uTwilightColor;
uniform float uDensity;

const float PI = 3.14159265358979323846;
const int NUM_STEPS = 16;

struct GPULight
{
    vec4 Meta0;
    vec4 ColorRange;
    vec4 PositionInner;
    vec4 DirectionOuter;
    vec4 BoxSizeAreaWidth;
    vec4 AreaRightAreaHeight;
    vec4 AreaUpLineLength;
    vec4 LineDirectionReserved;
    vec4 ShadowAtlasRect;
    mat4 ShadowMatrix;
};

layout(std430, binding = 0) readonly buffer LightBuffer
{
    GPULight uLights[];
};

float safeSqrt(float x)
{
    return sqrt(max(x, 0.0));
}

void main()
{
    if (vRenderSpace == 0)
    {
        FragColor = vec4(0.0);
        return;
    }

    vec3 cameraPos = uCameraPosition;
    vec3 fragPos = vWorldPos;
    vec3 planetCenter = vPlanetCenter;
    float atmosphereThickness = uAtmosphereRadius - uPlanetRadius;

    vec3 viewDir = fragPos - cameraPos;
    float viewDist = length(viewDir);

    if (viewDist < 0.000001)
    {
        FragColor = vec4(0.0);
        return;
    }

    viewDir = viewDir / viewDist;

    // ---- Ray-sphere intersection with atmosphere shell ----
    vec3 oc = cameraPos - planetCenter;
    float b = dot(viewDir, oc);
    float c = dot(oc, oc) - uAtmosphereRadius * uAtmosphereRadius;
    float disc = b * b - c;

    if (disc <= 0.0)
    {
        FragColor = vec4(0.0);
        return;
    }

    float sqrtDisc = safeSqrt(disc);
    float tNear = -b - sqrtDisc;
    float tFar  = -b + sqrtDisc;

    float cameraDist = length(oc);
    float pathStart;
    float pathEnd;

    if (cameraDist < uAtmosphereRadius)
    {
        // Camera inside the atmosphere: ray starts at camera, exits at far hit
        pathStart = 0.0;
        pathEnd = tFar;
    }
    else
    {
        // Camera outside the atmosphere
        if (tNear <= 0.0)
        {
            FragColor = vec4(0.0);
            return;
        }
        pathStart = tNear;
        pathEnd = tFar;
    }

    float pathLength = pathEnd - pathStart;

    if (pathLength <= 0.0001)
    {
        FragColor = vec4(0.0);
        return;
    }

    // ---- Numerical integration along the ray ----
    // Use exponential density model: ρ(h) = exp(-h / scaleHeight)
    float scaleHeight = atmosphereThickness * 0.15;

    float opticalDepth = 0.0;
    float stepSize = pathLength / float(NUM_STEPS);

    for (int i = 0; i < NUM_STEPS; i++)
    {
        float t = pathStart + (float(i) + 0.5) * stepSize;
        vec3 samplePos = cameraPos + viewDir * t;
        float altitude = length(samplePos - planetCenter) - uPlanetRadius;

        if (altitude < 0.0)
            altitude = 0.0;

        float densitySample = exp(-altitude / max(scaleHeight, 0.0001));
        opticalDepth += densitySample * stepSize;
    }

    opticalDepth *= uDensity;

    // ---- Reference depths for color mapping ----
    // Zenith reference: integrated density for vertical path from surface
    float refDepthZenith = atmosphereThickness * 0.15;

    // Horizon reference: tangent path from surface to atmosphere exit
    // Approximate via Chapman function scaling
    float refDepthHorizon = refDepthZenith *
        safeSqrt(PI * uPlanetRadius / (2.0 * atmosphereThickness * 0.15));

    // ---- Map optical depth to color ----
    // Normalize so that zenith → 0 (sky color) and horizon → 1 (horizon color)
    float normalizedDepth = clamp(opticalDepth / max(refDepthHorizon, 0.0001), 0.0, 1.0);

    // Smoothstep for natural transition between sky and horizon colors
    float tColor = smoothstep(0.0, 1.0, normalizedDepth);

    vec3 baseColor = mix(uSkyColor.rgb, uHorizonColor.rgb, tColor);

    vec3 upDir = normalize(fragPos - planetCenter);

    float totalBrightness = 0.0;
    for (int i = 0; i < uLights.length(); i++)
    {
        GPULight src = uLights[i];
        int kind = int(src.Meta0.x + 0.5);
        float intensity = src.Meta0.y;
        vec3 lightColor = src.ColorRange.xyz;

        vec3 lightDir;
        float attenuation = 1.0;

        if (kind == 3)
        {
            lightDir = -normalize(src.DirectionOuter.xyz);
        }
        else
        {
            vec3 toLight = src.PositionInner.xyz - fragPos;
            float dist = length(toLight);
            lightDir = toLight / max(dist, 0.0001);
            float rangeVal = src.ColorRange.w;
            if (rangeVal > 0.0)
                attenuation = clamp(1.0 - dist / rangeVal, 0.0, 1.0);
        }

        float ndotL = dot(upDir, lightDir);
        float colorLum = dot(lightColor, vec3(0.299, 0.587, 0.114));
        totalBrightness += max(ndotL, 0.0) * intensity * attenuation * colorLum;
    }

    float clampedBrightness = clamp(totalBrightness, 0.0, 0.8);
    float dayFactor = smoothstep(0.01, 0.5, clampedBrightness);

    float mid = 0.42;
    float spread = 0.3;
    float dist = abs(clampedBrightness - mid);
    float twilightBlend = 1.0 - smoothstep(0.0, spread, dist);

    vec3 finalColor = mix(baseColor, uTwilightColor.rgb, twilightBlend * uTwilightColor.a);

    float alpha = 1.0 - exp(-opticalDepth * 0.00003);
    alpha = clamp(alpha, 0.0, 1.0);
    alpha *= dayFactor;

    FragColor = vec4(finalColor, alpha);
}
