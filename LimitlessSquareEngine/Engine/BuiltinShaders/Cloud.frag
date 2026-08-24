#version 430 core

in vec2 vCloudUv;

out vec4 FragColor;

uniform vec3 uCameraPosition;

uniform float uPlanetRadius;
uniform float uCloudBaseAltitude;
uniform float uCloudThickness;
uniform float uCloudCoverage;
uniform float uCloudExtinction;
uniform float uCloudAlphaMin;
uniform float uCloudAlphaMax;

uniform vec3 uCloudLightColor;
uniform vec3 uCloudShadeColor;
uniform vec3 uCloudTwilightColor;
uniform float uCloudTwilightStrength;

uniform vec2 uCloudWind;
uniform float uCloudTime;
uniform float uCloudEvolution;
uniform float uCloudShapeTile;
uniform float uCloudDetailTile;
uniform float uCloudCoverageScale;
uniform float uCloudStepSize;
uniform int uCloudMaxSteps;
uniform float uCloudDetailStrength;
uniform float uCloudWarpStrength;
uniform float uCloudSilverLining;
uniform float uCloudSilverWidth;
uniform float uCloudCelSoftness;
uniform float uCloudEdgeSoftness;
uniform float uCloudShadowStrength;
uniform float uCloudShadowAffectsAmbient;
uniform float uCloudShadowProbeDist;

uniform sampler3D uCloudShapeNoise;
uniform sampler3D uCloudDetailNoise;

uniform vec3 uCloudPlanetCenter;
uniform dvec3 uCloudPlanetCenterWorld;
uniform dvec3 uCloudCameraWorldPos;
uniform mat4 uCloudInvViewProjection;
uniform mat4 uCloudViewProjection;
uniform float uCloudTanHalfFov;
uniform float uCloudViewportHeight;
uniform float uCloudLayerNear;
uniform float uCloudLayerFar;

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

float interleavedGradientNoise(vec2 pixel)
{
    return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

float remap01(float x, float a)
{
    return clamp((x - a) / max(1.0 - a, 0.0001), 0.0, 1.0);
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
        float t = (conc - threshold) / (1.0 - threshold);
        float levels = 4.0;
        t = floor(t * levels + 0.001) / levels;
        density = t * heightGrad;
    }
    else
    {
        density = 0.0;
    }

    return density;
}

vec3 findSunDir()
{
    vec3 sunDir = vec3(0.0, 1.0, 0.0);
    for (int i = 0; i < uLights.length(); i++)
    {
        GPULight src = uLights[i];
        if (int(src.Meta0.x + 0.5) == 3)
        {
            sunDir = -normalize(src.DirectionOuter.xyz);
            break;
        }
    }
    return vec3(sunDir.x, sunDir.y, -sunDir.z);
}

float stepForT(float t, float minStep)
{
    float footprint = t * 2.0 * max(uCloudTanHalfFov, 0.0001) / max(uCloudViewportHeight, 1.0);
    float s = max(max(uCloudStepSize, footprint * 2.0), minStep);
    return min(s, max(uCloudThickness * 0.35, minStep));
}

void main()
{
    vec2 ndc = vCloudUv * 2.0 - 1.0;
    vec4 clip = vec4(ndc, 0.0, 1.0);
    vec4 world = uCloudInvViewProjection * clip;
    vec3 viewDir = normalize(world.xyz / max(world.w, 0.000001));
    vec3 viewDirWorld = vec3(viewDir.x, viewDir.y, -viewDir.z);

    vec3 cameraPos = uCameraPosition;
    vec3 planetCenter = uCloudPlanetCenter;

    float baseRadius = uPlanetRadius + uCloudBaseAltitude;
    float topRadius = baseRadius + uCloudThickness;

    float tNear;
    float tFar;
    if (!raySphere(cameraPos, viewDir, planetCenter, topRadius, tNear, tFar))
    {
        FragColor = vec4(0.0);
        return;
    }
    if (tFar <= 0.0)
    {
        FragColor = vec4(0.0);
        return;
    }

    float tEntry = max(tNear, 0.0);
    float tExit = tFar;

    float tInnerNear;
    float tInnerFar;
    if (raySphere(cameraPos, viewDir, planetCenter, baseRadius, tInnerNear, tInnerFar))
    {
        if (tInnerNear > tEntry && tInnerNear < tExit)
            tExit = min(tExit, tInnerNear);
        else if (tInnerNear <= tEntry && tInnerFar > tEntry && tInnerFar < tExit)
            tEntry = tInnerFar;
    }

    float fullEntry = tEntry;
    float fullExit = tExit;

    float cosTheta = max((uCloudViewProjection * vec4(viewDir, 0.0)).w, 0.000001);
    float tLayerNear = uCloudLayerNear / cosTheta;
    float tLayerFar = uCloudLayerFar / cosTheta;
    tEntry = max(tEntry, tLayerNear);
    tExit = min(tExit, tLayerFar);

    float chord = tExit - tEntry;
    if (chord <= 0.0001)
    {
        FragColor = vec4(0.0);
        return;
    }

    vec3 camLocal = vec3(uCloudCameraWorldPos - uCloudPlanetCenterWorld);

    float jitter = interleavedGradientNoise(gl_FragCoord.xy);

    vec3 sunDir = findSunDir();
    vec3 upCam = normalize(camLocal);
    float celSoft = clamp(uCloudCelSoftness, 0.001, 0.5);
    float extinct = max(uCloudExtinction, 0.0000001);

    float transmittance = 1.0;
    vec3 accumLight = vec3(0.0);
    float firstCloudT = -1.0;
    float accumDepth = 0.0;

    int budget = max(uCloudMaxSteps, 8);
    float minStep = max(0.0, (fullExit - fullEntry) / float(budget));
    int emptyStreak = 0;

    float grid = max(uCloudStepSize, 1.0);
    float t = (ceil(tEntry / grid) + jitter) * grid;

    for (int i = 0; i < budget && t < tExit; i++)
    {
        float stepLen = stepForT(t, minStep);
        float stride = emptyStreak >= 2 ? stepLen * 4.0 : stepLen;
        stride = min(stride, tExit - t);
        float sampleT = t + stride * 0.5;

        vec3 samplePos = camLocal + viewDirWorld * sampleT;
        float alt = length(samplePos) - uPlanetRadius;

        if (alt <= uCloudBaseAltitude || alt >= uCloudBaseAltitude + uCloudThickness)
        {
            emptyStreak++;
            t += stride;
            continue;
        }

        float h = (alt - uCloudBaseAltitude) / max(uCloudThickness, 0.0001);
        float density = sampleDensity(samplePos, h, sampleT, 1.0);

        if (density <= 0.001)
        {
            emptyStreak++;
            t += stride;
            continue;
        }

        if (emptyStreak >= 2)
        {
            stride = stepLen;
            sampleT = t + stride * 0.5;
            samplePos = camLocal + viewDirWorld * sampleT;
            alt = length(samplePos) - uPlanetRadius;

            if (alt <= uCloudBaseAltitude || alt >= uCloudBaseAltitude + uCloudThickness)
            {
                emptyStreak = 0;
                t += stride;
                continue;
            }

            h = (alt - uCloudBaseAltitude) / max(uCloudThickness, 0.0001);
            density = sampleDensity(samplePos, h, sampleT, 1.0);

            if (density <= 0.001)
            {
                emptyStreak = 0;
                t += stride;
                continue;
            }
        }

        emptyStreak = 0;

        if (firstCloudT < 0.0)
            firstCloudT = sampleT;

        float stepTransmittance = exp(-density * stride * extinct);
        float densityLight = density * stride * extinct;

        accumDepth += density * stride;

        vec3 upDir = normalize(samplePos);
        float ndl = dot(upDir, sunDir);
        float lit = smoothstep(0.0, celSoft, ndl);

                float probe1 = sampleDensity(samplePos + sunDir * uCloudShadowProbeDist, h, sampleT, 0.0);
                float occlusion = clamp(probe1 * 2.0 * uCloudShadowStrength, 0.0, 1.0);

                float litShadow = clamp(lit - occlusion * 0.8, 0.0, 1.0);
                float ambientKeep = mix(1.0, 1.0 - occlusion * 0.75, clamp(uCloudShadowAffectsAmbient, 0.0, 1.0));

        float depthNorm = clamp(accumDepth * 0.015, 0.0, 1.0);
        float shadeLevel = floor(depthNorm * 3.0 + 0.001) / 3.0;
        vec3 cloudCol = mix(uCloudLightColor, uCloudShadeColor, shadeLevel);

        cloudCol *= mix(0.55, 1.0, litShadow);
        cloudCol *= mix(0.75, 1.0, ambientKeep);

        float sunShade = smoothstep(0.15, 0.75, ndl);
        cloudCol *= mix(0.03, 1.0, sunShade);

        float twilightBlend = 1.0 - smoothstep(0.0, 0.35, abs(ndl - 0.25));
        cloudCol = mix(cloudCol, uCloudTwilightColor, twilightBlend * uCloudTwilightStrength * 0.8);

        accumLight += transmittance * cloudCol * densityLight;
        transmittance *= stepTransmittance;

        if (transmittance < 0.02)
            break;

        t += stride;
    }

    float alpha = 1.0 - transmittance;
    alpha = smoothstep(uCloudAlphaMin, uCloudAlphaMax, alpha);

    if (alpha <= 0.001)
    {
        FragColor = vec4(0.0);
        return;
    }

    vec3 cloudCol = accumLight / max(1.0 - transmittance, 0.0001);
    cloudCol = clamp(cloudCol, 0.0, 1.0);
    FragColor = vec4(cloudCol * alpha, alpha);

    float depthT = firstCloudT >= 0.0 ? firstCloudT : tEntry;
    vec4 clipDepth = uCloudViewProjection * vec4(viewDir * depthT, 1.0);
    float ndcDepth = clipDepth.z / max(clipDepth.w, 0.000001);
    gl_FragDepth = clamp(ndcDepth * 0.5 + 0.5, 0.0, 1.0);
}
