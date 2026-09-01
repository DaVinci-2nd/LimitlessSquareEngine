#version 430 core

in vec2 vCloudUv;

out vec4 FragColor;

uniform float uPlanetRadius;
uniform float uCloudBaseAltitude;
uniform float uCloudThickness;
uniform float uCloudExtinction;

uniform vec2 uCloudWind;
uniform float uCloudTime;
uniform float uCloudEvolution;
uniform float uCloudShapeTile;
uniform float uCloudCoverageScale;
uniform float uCloudStepSize;
uniform int uCloudMaxSteps;
uniform float uCloudWarpStrength;

uniform sampler3D uCloudShapeNoise;

uniform vec3 uCloudShadowSunDir;
uniform vec3 uCloudShadowNearAnchorRel;
uniform vec3 uCloudShadowNearAxisX;
uniform vec3 uCloudShadowNearAxisY;
uniform float uCloudShadowNearHalfExtent;

float safeSqrt(float x)
{
    return sqrt(max(x, 0.0));
}

float cloudLodBias(float t)
{
    return clamp(log2(1.0 + t * 0.0008), 0.0, 4.5);
}

float sampleDensity(vec3 planetLocal, float h, float t, float detailScale)
{
    float windMix = 0.55 + 0.85 * h;
    vec3 windOffset = vec3(uCloudWind.x, 0.0, uCloudWind.y) * uCloudTime * windMix;
    float evo = uCloudTime * uCloudEvolution;

    vec3 p = planetLocal + windOffset;

    float lod = cloudLodBias(t);

    vec3 shapeCoord = (p + vec3(evo * 14.0, evo * 3.0, evo * 8.0)) / max(uCloudShapeTile, 1.0);

    vec3 warpVec = textureLod(uCloudShapeNoise, shapeCoord * 0.125 + vec3(0.0, evo * 0.001, 0.0), 3.5).gba - 0.5;
    vec3 warpedCoord = shapeCoord + warpVec * uCloudWarpStrength * 0.08;

    vec4 shapeTex = textureLod(uCloudShapeNoise, warpedCoord, lod);

    float heightGrad = smoothstep(0.0, 0.10, h) * (1.0 - smoothstep(0.35, 0.9, h));

    float coarse = textureLod(uCloudShapeNoise, shapeCoord * max(uCloudCoverageScale, 0.0001), 1.5).r;

    float conc = clamp(coarse, 0.0, 1.0) * clamp(shapeTex.r, 0.0, 1.0);
    conc = clamp(conc * 8.0, 0.0, 1.0);

    float threshold = 0.1;
    float density;
    if (conc >= threshold)
    {
        float t2 = (conc - threshold) / (1.0 - threshold);
        float levels = 4.0;
        t2 = floor(t2 * levels + 0.001) / levels;
        density = t2 * heightGrad;
    }
    else
    {
        density = 0.0;
    }

    return density;
}

float rayInT(vec3 origin, vec3 dir, float r)
{
    float b = dot(dir, origin);
    float c = dot(origin, origin) - r * r;
    float disc = b * b - c;
    return -b + safeSqrt(disc);
}

void main()
{
    vec2 c = vCloudUv - 0.5;

    vec3 sunDir = uCloudShadowSunDir;

    vec3 worldPoint = uCloudShadowNearAnchorRel
        + uCloudShadowNearAxisX * (c.x * 2.0 * uCloudShadowNearHalfExtent)
        + uCloudShadowNearAxisY * (c.y * 2.0 * uCloudShadowNearHalfExtent);

    float baseRadius = uPlanetRadius + uCloudBaseAltitude;
    float topRadius = baseRadius + uCloudThickness;

    float tBase = rayInT(worldPoint, sunDir, baseRadius);
    float tTop = rayInT(worldPoint, sunDir, topRadius);

    float chord = tTop - tBase;
    if (chord <= 0.0001)
    {
        FragColor = vec4(0.0);
        return;
    }

    int budget = max(uCloudMaxSteps, 8);
    float minStep = max(0.0, chord / float(budget));
    float grid = max(uCloudStepSize, 1.0);
    float stride = max(uCloudStepSize, 1.0);
    stride = min(stride, max(uCloudThickness * 0.35, minStep));

    float jitter = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);

    float tStep = (ceil(tBase / grid) + jitter) * grid;

    float extinct = max(uCloudExtinction, 0.0000001);
    float transmittance = 1.0;

    for (int i = 0; i < budget && tStep < tTop; i++)
    {
        float sampleT = tStep + stride * 0.5;
        vec3 samplePos = worldPoint + sunDir * sampleT;
        float alt = length(samplePos) - uPlanetRadius;

        if (alt <= uCloudBaseAltitude || alt >= uCloudBaseAltitude + uCloudThickness)
        {
            tStep += stride;
            continue;
        }

        float h = (alt - uCloudBaseAltitude) / max(uCloudThickness, 0.0001);
        float density = sampleDensity(samplePos, h, sampleT, 1.0);

        if (density <= 0.001)
        {
            tStep += stride;
            continue;
        }

        transmittance *= exp(-density * stride * extinct);

        if (transmittance < 0.02)
            break;

        tStep += stride;
    }

    float alpha = 1.0 - transmittance;
    FragColor = vec4(alpha, alpha, alpha, 1.0);
}
