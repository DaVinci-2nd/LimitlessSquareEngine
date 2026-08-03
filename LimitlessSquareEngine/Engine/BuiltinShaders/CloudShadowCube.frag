#version 430 core

out vec4 FragColor;

uniform int uCloudShadowFace;
uniform float uCloudShadowResolution;

uniform float uPlanetRadius;
uniform float uCloudBaseAltitude;
uniform float uCloudThickness;
uniform float uCloudCoverage;
uniform vec2 uCloudWind;
uniform float uCloudTime;
uniform float uCloudNoiseScale;
uniform float uCloudCoverageScale;
uniform float uCloudExtinction;
uniform float uCloudDetailStrength;
uniform float uCloudWarpStrength;
uniform float uCloudStepSize;
uniform int uCloudMaxSteps;

vec3 faceDir(int face, vec2 uv)
{
    vec3 d;
    if (face == 0)
        d = vec3(1.0, 1.0 - 2.0 * uv.y, 2.0 * uv.x - 1.0);
    else if (face == 1)
        d = vec3(-1.0, 1.0 - 2.0 * uv.y, 1.0 - 2.0 * uv.x);
    else if (face == 2)
        d = vec3(2.0 * uv.x - 1.0, 1.0, 1.0 - 2.0 * uv.y);
    else if (face == 3)
        d = vec3(2.0 * uv.x - 1.0, -1.0, 2.0 * uv.y - 1.0);
    else if (face == 4)
        d = vec3(2.0 * uv.x - 1.0, 1.0 - 2.0 * uv.y, 1.0);
    else
        d = vec3(1.0 - 2.0 * uv.x, 1.0 - 2.0 * uv.y, -1.0);
    return normalize(d);
}

float hashNoise(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

float valueNoise(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);

    float n000 = hashNoise(i);
    float n100 = hashNoise(i + vec3(1.0, 0.0, 0.0));
    float n010 = hashNoise(i + vec3(0.0, 1.0, 0.0));
    float n110 = hashNoise(i + vec3(1.0, 1.0, 0.0));
    float n001 = hashNoise(i + vec3(0.0, 0.0, 1.0));
    float n101 = hashNoise(i + vec3(1.0, 0.0, 1.0));
    float n011 = hashNoise(i + vec3(0.0, 1.0, 1.0));
    float n111 = hashNoise(i + vec3(1.0, 1.0, 1.0));

    float nx00 = mix(n000, n100, u.x);
    float nx10 = mix(n010, n110, u.x);
    float nx01 = mix(n001, n101, u.x);
    float nx11 = mix(n011, n111, u.x);
    float nxy0 = mix(nx00, nx10, u.y);
    float nxy1 = mix(nx01, nx11, u.y);

    return mix(nxy0, nxy1, u.z);
}

float fbmNoise(vec3 p)
{
    float value = 0.0;
    float amplitude = 0.5;
    float weightSum = 0.0;
    for (int i = 0; i < 4; i++)
    {
        value += valueNoise(p) * amplitude;
        weightSum += amplitude;
        p *= 2.0;
        amplitude *= 0.5;
    }
    return value / max(weightSum, 0.0001);
}

vec3 hashNoise3(vec3 p)
{
    return vec3(
        hashNoise(p),
        hashNoise(p + vec3(31.7, 17.3, 53.1)),
        hashNoise(p + vec3(71.9, 43.7, 97.3)));
}

vec3 valueNoise3(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);

    vec3 n000 = hashNoise3(i);
    vec3 n100 = hashNoise3(i + vec3(1.0, 0.0, 0.0));
    vec3 n010 = hashNoise3(i + vec3(0.0, 1.0, 0.0));
    vec3 n110 = hashNoise3(i + vec3(1.0, 1.0, 0.0));
    vec3 n001 = hashNoise3(i + vec3(0.0, 0.0, 1.0));
    vec3 n101 = hashNoise3(i + vec3(1.0, 0.0, 1.0));
    vec3 n011 = hashNoise3(i + vec3(0.0, 1.0, 1.0));
    vec3 n111 = hashNoise3(i + vec3(1.0, 1.0, 1.0));

    vec3 nx00 = mix(n000, n100, u.x);
    vec3 nx10 = mix(n010, n110, u.x);
    vec3 nx01 = mix(n001, n101, u.x);
    vec3 nx11 = mix(n011, n111, u.x);
    vec3 nxy0 = mix(nx00, nx10, u.y);
    vec3 nxy1 = mix(nx01, nx11, u.y);

    return mix(nxy0, nxy1, u.z);
}

vec3 fbmNoise3(vec3 p)
{
    vec3 value = vec3(0.0);
    float amplitude = 0.5;
    float weightSum = 0.0;
    for (int i = 0; i < 4; i++)
    {
        value += valueNoise3(p) * amplitude;
        weightSum += amplitude;
        p *= 2.0;
        amplitude *= 0.5;
    }
    return value / max(weightSum, 0.0001);
}

float sampleDensity(vec3 p, float h, float stepSize)
{
    float windMix = 0.55 + 0.85 * h;
    vec3 windOffset = vec3(uCloudWind.x, 0.0, uCloudWind.y) * uCloudTime * windMix;
    vec3 localPos = p + windOffset;

    float detailAtten = 1.0 / max(1.0, stepSize * uCloudNoiseScale * 2.0);

    float warpWavelength = 1.0 / max(uCloudCoverageScale, 0.0000001);
    vec3 warped = localPos;
    for (int warpIter = 0; warpIter < 2; warpIter++)
    {
        vec3 warpNoise = fbmNoise3(warped * uCloudCoverageScale * (0.5 + 0.5 * warpIter)) - 0.5;
        warped += warpNoise * uCloudWarpStrength * warpWavelength;
    }

    float shape = fbmNoise(warped * uCloudCoverageScale * 2.0);
    shape = smoothstep(0.4, 0.6, shape);

    float coverage = fbmNoise(localPos * uCloudCoverageScale * 0.5);
    float cov = smoothstep(0.5 - uCloudCoverage * 0.5, 0.5 + uCloudCoverage * 0.5, coverage);

    float density = cov * shape;

    float erosion = fbmNoise(localPos * uCloudNoiseScale * detailAtten);
    density = max(0.0, density - erosion * uCloudDetailStrength);

    return density;
}

void main()
{
    vec2 uv = gl_FragCoord.xy / uCloudShadowResolution;
    vec3 dir = faceDir(uCloudShadowFace, uv);

    float baseRadius = uPlanetRadius + uCloudBaseAltitude;
    float topRadius = baseRadius + uCloudThickness;

    int steps = clamp(int(ceil(uCloudThickness / max(uCloudStepSize, 1.0))), 12, max(uCloudMaxSteps, 32));
    float stepSize = uCloudThickness / float(steps);

    float opticalDepth = 0.0;
    float extinct = max(uCloudExtinction, 0.0000001);

    for (int i = 0; i < steps; i++)
    {
        float radius = baseRadius + (float(i) + 0.5) * stepSize;
        vec3 p = dir * radius;
        float h = (radius - baseRadius) / max(uCloudThickness, 0.0001);

        float density = sampleDensity(p, h, stepSize);
        opticalDepth += density * stepSize;
    }

    float opacity = 1.0 - exp(-opticalDepth * extinct);
    FragColor = vec4(opacity, opacity, opacity, 1.0);
}
