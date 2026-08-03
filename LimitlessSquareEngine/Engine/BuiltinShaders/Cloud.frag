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
uniform float uCloudNoiseScale;
uniform float uCloudCoverageScale;

uniform float uCloudStepSize;
uniform int uCloudMaxSteps;
uniform float uCloudDetailStrength;
uniform float uCloudWarpStrength;
uniform float uCloudSilverLining;
uniform float uCloudSilverWidth;
uniform float uCloudCelSoftness;

uniform vec3 uCloudPlanetCenter;
uniform dvec3 uCloudPlanetCenterWorld;
uniform dvec3 uCloudCameraWorldPos;
uniform mat4 uCloudInvViewProjection;
uniform mat4 uCloudViewProjection;
uniform float uCloudTanHalfFov;
uniform float uCloudViewportHeight;

uniform samplerCube uCloudShadowCube;
uniform float uCloudShadowStrength;

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

float sampleDensity(vec3 planetLocal, float h, float t, float stepSize)
{
    float windMix = 0.55 + 0.85 * h;
    vec3 windOffset = vec3(uCloudWind.x, 0.0, uCloudWind.y) * uCloudTime * windMix;
    vec3 p = planetLocal + windOffset;

    float footprint = t * 2.0 * max(uCloudTanHalfFov, 0.0001) / max(uCloudViewportHeight, 1.0);
    float detailAtten = 1.0 / max(1.0, footprint * uCloudNoiseScale * 2.0);

    float warpWavelength = 1.0 / max(uCloudCoverageScale, 0.0000001);
    vec3 warped = p;
    for (int warpIter = 0; warpIter < 2; warpIter++)
    {
        vec3 warpNoise = fbmNoise3(warped * uCloudCoverageScale * (0.5 + 0.5 * warpIter)) - 0.5;
        warped += warpNoise * uCloudWarpStrength * warpWavelength;
    }

    float shape = fbmNoise(warped * uCloudCoverageScale * 2.0);
    shape = smoothstep(0.4, 0.6, shape);

    float coverage = fbmNoise(p * uCloudCoverageScale * 0.5);
    float cov = smoothstep(0.5 - uCloudCoverage * 0.5, 0.5 + uCloudCoverage * 0.5, coverage);

    float density = cov * shape;

    float erosion = fbmNoise(p * uCloudNoiseScale * detailAtten);
    density = max(0.0, density - erosion * uCloudDetailStrength);

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
    return sunDir;
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

    float topRadius = uPlanetRadius + uCloudBaseAltitude + uCloudThickness;
    float baseRadius = uPlanetRadius + uCloudBaseAltitude;

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

    float tPlanetNear;
    float tPlanetFar;
    if (raySphere(cameraPos, viewDir, planetCenter, uPlanetRadius, tPlanetNear, tPlanetFar) &&
        tPlanetNear > tEntry && tPlanetNear < tExit)
    {
        tExit = tPlanetNear;
    }

    float chord = tExit - tEntry;
    if (chord <= 0.0001)
    {
        FragColor = vec4(0.0);
        return;
    }

    float midDist = tEntry + chord * 0.5;
    float pixelStep = midDist * 2.0 * max(uCloudTanHalfFov, 0.0001) / max(uCloudViewportHeight, 1.0);
    float stepSize = max(uCloudStepSize, pixelStep);
    int steps = clamp(int(ceil(chord / stepSize)), 8, max(uCloudMaxSteps, 8));
    stepSize = chord / float(steps);

    vec3 sunDir = findSunDir();
    vec3 upCam = normalize(vec3(uCloudCameraWorldPos - uCloudPlanetCenterWorld));
    float celSoft = clamp(uCloudCelSoftness, 0.001, 0.5);
    float extinct = max(uCloudExtinction, 0.0000001);
    float shadowStrength = clamp(uCloudShadowStrength, 0.0, 1.0);

    float transmittance = 1.0;
    vec3 accumLight = vec3(0.0);

    for (int i = 0; i < steps; i++)
    {
        double tt = double(tEntry) + (double(i) + 0.5) * double(stepSize);
        dvec3 samplePosAbs = uCloudCameraWorldPos + dvec3(viewDirWorld) * tt;
        vec3 planetLocal = vec3(samplePosAbs - uCloudPlanetCenterWorld);
        float alt = float(length(samplePosAbs - uCloudPlanetCenterWorld)) - uPlanetRadius;

        if (alt <= uCloudBaseAltitude || alt >= uCloudBaseAltitude + uCloudThickness)
            continue;

        float h = (alt - uCloudBaseAltitude) / max(uCloudThickness, 0.0001);
        float density = sampleDensity(planetLocal, h, float(tt), stepSize);

        if (density <= 0.001)
            continue;

        float stepTransmittance = exp(-density * stepSize * extinct);
        float densityLight = density * stepSize * extinct;

        vec3 upDir = normalize(planetLocal);
        float ndl = dot(upDir, sunDir);
        float lit = smoothstep(0.0, celSoft, ndl);

        vec3 cloudCol = mix(uCloudShadeColor, uCloudLightColor, lit);

        float cloudOpacity = textureLod(uCloudShadowCube, sunDir, 4.0).r;
        cloudCol *= 1.0 - cloudOpacity * shadowStrength;

        float rim = pow(1.0 - abs(dot(viewDirWorld, sunDir)), uCloudSilverWidth) * uCloudSilverLining;
        cloudCol += uCloudLightColor * rim;

        float sunElev = dot(upCam, sunDir);
        float twilightBand = smoothstep(-0.10, 0.06, sunElev) * (1.0 - smoothstep(0.05, 0.18, sunElev));
        cloudCol = mix(cloudCol, uCloudTwilightColor, twilightBand * uCloudTwilightStrength);

        accumLight += transmittance * cloudCol * densityLight;
        transmittance *= stepTransmittance;

        if (transmittance < 0.02)
            break;
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

    vec4 clipDepth = uCloudViewProjection * vec4(viewDir * tEntry, 1.0);
    gl_FragDepth = clamp(clipDepth.z / max(clipDepth.w, 0.000001) * 0.5 + 0.5, 0.0, 1.0);
}
