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
uniform vec3 uCloudShadowBasisX;
uniform vec3 uCloudShadowBasisY;

const float PI = 3.1415926535897932384626433832795;

float safeSqrt(float x)
{
    return sqrt(max(x, 0.0));
}

bool raySphere(vec3 ro, vec3 rd, vec3 center, float radius, out float tNear, out float tFar)
{
    vec3 oc = ro - center;
    float b = dot(rd, oc);
    float c = dot(oc, oc) - radius * radius;
    float disc = b * b - c;

    if (disc <= 0.0)
    {
        tNear = 0.0;
        tFar = 0.0;
        return false;
    }

    float s = safeSqrt(disc);
    tNear = -b - s;
    tFar = -b + s;
    return true;
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
    float row = floor(vCloudUv.y * 3.0);
    float col = floor(vCloudUv.x * 4.0);

    float fu = fract(vCloudUv.x * 4.0);
    float fv = fract(vCloudUv.y * 3.0);

    float s = fu * 2.0 - 1.0;
    float t = fv * 2.0 - 1.0;

    vec3 dir;
    if (row == 2.0)
    {
        dir = normalize(vec3(s, 1.0, t));
    }
    else if (row == 0.0)
    {
        dir = normalize(vec3(s, -1.0, -t));
    }
    else
    {
        if (col == 0.0)
            dir = normalize(vec3(-1.0, -t, s));
        else if (col == 1.0)
            dir = normalize(vec3(s, -t, 1.0));
        else if (col == 2.0)
            dir = normalize(vec3(1.0, -t, -s));
        else
            dir = normalize(vec3(-s, -t, -1.0));
    }

    vec3 sunDir = uCloudShadowSunDir;

    float baseRadius = uPlanetRadius + uCloudBaseAltitude;
    float topRadius = baseRadius + uCloudThickness;

    float tBase = rayInT(dir * uPlanetRadius, sunDir, baseRadius);
    float tTop = rayInT(dir * uPlanetRadius, sunDir, topRadius);

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
        vec3 samplePos = dir * uPlanetRadius + sunDir * sampleT;
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
