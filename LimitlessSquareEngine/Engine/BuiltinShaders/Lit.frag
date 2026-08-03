#version 430 core

const float PI = 3.1415926535897932384626433832795;

const int LIGHT_KIND_POINT = 0;
const int LIGHT_KIND_BOX = 1;
const int LIGHT_KIND_SPOT = 2;
const int LIGHT_KIND_DIRECTIONAL = 3;
const int LIGHT_KIND_AREA = 4;
const int LIGHT_KIND_LINE = 5;
const int LIGHT_KIND_RAY = 6;

uniform sampler2D uTexture;
uniform vec4 uColor;
uniform int uUseTexture;

uniform int uUseAlphaCutoff;
uniform float uAlphaCutoff;

uniform sampler2D uNormalTexture;
uniform int uUseNormalTexture;
uniform float uNormalStrength;

/* 材质参数 */
uniform float uAmbientStrength;
uniform float uSpecularIntensity;
uniform float uSpecularRange;
uniform float uRimIntensity;
uniform float uRimRange;

uniform vec3 uSpecularColor;
uniform vec3 uRimColor;

uniform float uSmoothness;
uniform float uMetallic;

uniform int uReceiveShadow;
uniform int uCastShadow;
uniform int uReceiveReflection;
uniform int uEnableColorBanding;


uniform int uEnableOutline;
uniform int uOutlinePass;
uniform vec4 uOutlineColor;

uniform int uFogEnabled;
uniform int uFogMode;
uniform vec4 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform int uFogEdgeTransitionToSkybox;

uniform sampler2D uFogCylindricalTexture;
uniform samplerCube uFogSkyboxCube;
uniform mat4 uFogInvViewRotation;

uniform vec3 uCameraPosition;
uniform vec3 uAmbientColor;
uniform float uAmbientIntensity;

uniform vec2 uViewportOrigin;
uniform vec2 uViewportSize;

uniform ivec3 uClusterGridSize;
uniform float uClusterNear;
uniform float uClusterFar;

uniform int uLightCount;

uniform sampler2DShadow uShadowAtlasTexture;

uniform samplerCube uCloudShadowCube;
uniform float uCloudShadowStrength;
uniform vec3 uCloudShadowPlanetCenter;
uniform float uCloudShadowSlant;
uniform float uCloudShadowTexelSize;
uniform int uCloudShadowAffectsAmbient;

uniform sampler2D uReflectionTexture;
uniform samplerCube uReflectionSkyboxCube;
uniform int uReflectionEnabled;
uniform int uReflectionSource;
uniform float uReflectionIntensity;

in vec4 vColor;
in vec2 vTexCoord;
in vec3 vWorldPos;
in vec3 vViewPos;
in vec3 vWorldNormal;
in vec3 vWorldTangent;
in vec3 vWorldBitangent;
flat in int vRenderSpace;

out vec4 FragColor;

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

struct GPUDirectionalShadowCascade
{
    vec4 AtlasRect;
    mat4 ShadowMatrix;
    vec4 SplitRange;
};

layout(std430, binding = 3) readonly buffer DirectionalShadowCascadeBuffer
{
    GPUDirectionalShadowCascade uDirectionalShadowCascades[];
};

layout(std430, binding = 1) readonly buffer ClusterRangeBuffer
{
    uvec2 uClusterRanges[];
};

layout(std430, binding = 2) readonly buffer ClusterIndexBuffer
{
    uint uClusterLightIndices[];
};

struct LightRecord
{
    int Kind;
    float Intensity;
    float CastShadow;
    float AttenuationCurve;

    vec3 Color;
    float Range;

    vec3 Position;
    float InnerAngle;

    vec3 Direction;
    float OuterAngle;

    vec3 BoxSize;
    float AreaWidth;

    vec3 AreaRight;
    float AreaHeight;

    vec3 AreaUp;
    float LineLength;

    vec3 LineDirection;
    float Reserved0;

    int ShadowCascadeStart;
    int ShadowCascadeCount;

    vec4 ShadowAtlasRect;
    mat4 ShadowMatrix;
};

float Saturate(float x)
{
    return clamp(x, 0.0, 1.0);
}

vec3 SafeNormalize(vec3 v)
{
    float lenSq = dot(v, v);
    if (lenSq <= 0.0000001)
        return vec3(0.0, 0.0, 1.0);
    return v * inversesqrt(lenSq);
}

vec2 DirectionToCylindricalUv(vec3 worldDir)
{
    vec3 d = SafeNormalize(worldDir);
    float u = atan(d.z, d.x) / (2.0 * PI) + 0.5;
    float v = clamp(d.y * 0.5 + 0.5, 0.0, 1.0);
    return vec2(u, v);
}

vec4 SampleFogSkyboxColor(vec3 viewDir)
{
    vec3 worldDir = SafeNormalize((uFogInvViewRotation * vec4(viewDir, 0.0)).xyz);
    vec3 skyRgb = texture(uFogSkyboxCube, worldDir).rgb;
    return vec4(skyRgb, 1.0);
}

vec4 EvaluateFogColorByMode(vec3 viewPos)
{
    vec3 viewDir = SafeNormalize(viewPos);

    if (uFogMode == 1)
    {
        vec3 worldDir = SafeNormalize((uFogInvViewRotation * vec4(viewDir, 0.0)).xyz);
        vec2 fogUv = DirectionToCylindricalUv(worldDir);
        return texture(uFogCylindricalTexture, fogUv);
    }
    else if (uFogMode == 2)
    {
        return SampleFogSkyboxColor(viewDir);
    }

    return uFogColor;
}

vec4 EvaluateFogBackgroundColor(vec3 viewPos)
{
    vec3 viewDir = SafeNormalize(viewPos);
    return SampleFogSkyboxColor(viewDir);
}

vec4 ApplyMaterialFogExact(vec4 srcColor, vec3 viewPos)
{
    if (uFogEnabled != 1)
        return srcColor;

    float distanceToCamera = length(viewPos);

    float fogFactor = clamp(
        (distanceToCamera - uFogStart) / max(uFogEnd - uFogStart, 0.0001),
        0.0,
        1.0);

    vec4 fogColor = EvaluateFogColorByMode(viewPos);
    vec4 backgroundColor = EvaluateFogBackgroundColor(viewPos);

    if (uFogMode != 2 && uFogEdgeTransitionToSkybox != 0)
    {
        float fogRange = max(uFogEnd - uFogStart, 0.0001);
        float skyboxBlendStart = uFogEnd - fogRange * 0.25;

        float skyboxBlendFactor = clamp(
            (distanceToCamera - skyboxBlendStart) / max(uFogEnd - skyboxBlendStart, 0.0001),
            0.0,
            1.0);

        skyboxBlendFactor = smoothstep(0.0, 1.0, skyboxBlendFactor);
        fogColor = mix(fogColor, backgroundColor, skyboxBlendFactor);
    }

    vec3 fogColorPM = fogColor.rgb * srcColor.a;
    vec3 foggedForeground = mix(srcColor.rgb, fogColorPM, fogFactor);
    float foregroundAlpha = srcColor.a;

    return vec4(foggedForeground, srcColor.a);
}

LightRecord ReadLight(uint lightIndex)
{
    GPULight src = uLights[lightIndex];

    LightRecord light;
    light.Kind = int(src.Meta0.x + 0.5);
    light.Intensity = src.Meta0.y;
    light.CastShadow = src.Meta0.z;
    light.AttenuationCurve = src.Meta0.w;

    light.Color = src.ColorRange.xyz;
    light.Range = src.ColorRange.w;

    light.Position = src.PositionInner.xyz;
    light.InnerAngle = src.PositionInner.w;

    light.Direction = SafeNormalize(src.DirectionOuter.xyz);
    light.OuterAngle = src.DirectionOuter.w;

    light.BoxSize = src.BoxSizeAreaWidth.xyz;
    light.AreaWidth = src.BoxSizeAreaWidth.w;
    light.ShadowCascadeStart = (light.Kind == LIGHT_KIND_DIRECTIONAL) ? int(src.BoxSizeAreaWidth.x + 0.5) : 0;
    light.ShadowCascadeCount = (light.Kind == LIGHT_KIND_DIRECTIONAL) ? int(src.BoxSizeAreaWidth.y + 0.5) : 0;

    light.AreaRight = SafeNormalize(src.AreaRightAreaHeight.xyz);
    light.AreaHeight = src.AreaRightAreaHeight.w;

    light.AreaUp = SafeNormalize(src.AreaUpLineLength.xyz);
    light.LineLength = src.AreaUpLineLength.w;

    light.LineDirection = SafeNormalize(src.LineDirectionReserved.xyz);
    light.Reserved0 = src.LineDirectionReserved.w;

    light.ShadowAtlasRect = src.ShadowAtlasRect;
    light.ShadowMatrix = src.ShadowMatrix;

    return light;
}

float EvaluateCurveAttenuation(float distance01, float curve01)
{
    float x = Saturate(distance01);
    float c = Saturate(curve01);

    if (abs(c - 0.5) <= 0.000001)
        return 1.0 - x;

    if (c < 0.5)
    {
        float k = c / 0.5;
        float powerValue = 0.25 + (k * 0.75);
        return 1.0 - pow(x, powerValue);
    }
    else
    {
        float k = (c - 0.5) / 0.5;
        float powerValue = 1.0 + (k * 3.0);
        return 1.0 - pow(x, powerValue);
    }
}

float ComputeDistanceAttenuation(float distanceValue, float rangeValue, float curveValue)
{
    if (rangeValue <= 0.000001)
        return 0.0;

    float t = distanceValue / rangeValue;
    return EvaluateCurveAttenuation(t, curveValue);
}

mat3 BuildDirectionBasis(vec3 forwardDir)
{
    vec3 forwardAxis = SafeNormalize(forwardDir);
    vec3 referenceUp = abs(forwardAxis.y) > 0.999 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 rightAxis = SafeNormalize(cross(referenceUp, forwardAxis));
    vec3 upAxis = SafeNormalize(cross(forwardAxis, rightAxis));
    return mat3(rightAxis, upAxis, forwardAxis);
}

vec3 DecodeNormal()
{
    vec3 n = SafeNormalize(vWorldNormal);

    if (uUseNormalTexture != 1)
        return n;

    vec3 tangentNormal = texture(uNormalTexture, vTexCoord).xyz * 2.0 - 1.0;
    tangentNormal.xy *= uNormalStrength;

    mat3 tbn = mat3(
        SafeNormalize(vWorldTangent),
        SafeNormalize(vWorldBitangent),
        SafeNormalize(vWorldNormal));

    return SafeNormalize(tbn * tangentNormal);
}

float ComputeSpecularExponent(float range01)
{
    float r = Saturate(range01);
    return mix(256.0, 4.0, r);
}

float ComputeSpecularValueSmooth(float ndh, float range01)
{
    float specularExponent = ComputeSpecularExponent(range01);
    return pow(Saturate(ndh), specularExponent);
}

float ComputeSpecularValueBanded(float ndh, float range01)
{
    float threshold = 1.0 - Saturate(range01);
    return step(threshold, Saturate(ndh));
}

float ComputeRimValueSmooth(vec3 normalDir, vec3 viewDir, float rimRange01)
{
    float ndv = Saturate(dot(normalDir, viewDir));
    float rimRaw = 1.0 - ndv;

    float threshold = mix(0.85, 0.15, Saturate(rimRange01));
    return smoothstep(threshold, 1.0, rimRaw);
}

float ComputeRimValueBanded(vec3 normalDir, vec3 viewDir, float rimRange01)
{
    float ndv = Saturate(dot(normalDir, viewDir));
    float rimRaw = 1.0 - ndv;

    float threshold = mix(0.85, 0.15, Saturate(rimRange01));
    return step(threshold, rimRaw);
}

float ApplyDiffuseBandFromRawNdl(float rawNdl)
{
    return step(0.0, rawNdl);
}

float ComputeToonSpecRimAttenuation(float rawNdl, float shadowFactor)
{
    bool darkSide = (rawNdl <= 0.0);
    bool inShadow = (shadowFactor < 0.999);

    return (darkSide || inShadow) ? 0.35 : 1.0;
}

vec3 SampleReflectionEnvironment(vec3 dir)
    {
        vec3 d = SafeNormalize(dir);
        return texture(uReflectionSkyboxCube, d).rgb;
    }

vec3 SampleReflectionEnvironmentSurfaceBlur(vec3 dir, float perceptualRoughness)
{
    vec3 d = SafeNormalize(dir);

    int mipCount = textureQueryLevels(uReflectionSkyboxCube);
    if (mipCount <= 1)
        return texture(uReflectionSkyboxCube, d).rgb;

    float lod = Saturate(perceptualRoughness) * float(mipCount - 1);
    return textureLod(uReflectionSkyboxCube, d, lod).rgb;
}

float ComputeReflectionVisibilityLinear(vec3 normalDir, vec3 viewDir, float smoothness, float metallic)
{
    float s = Saturate(smoothness);
    float m = Saturate(metallic);
    float ndv = Saturate(dot(normalDir, viewDir));

    float frontReflect = max(s * 2.0 - 1.0, 0.0);
    float edgeReflect = s;
    float angle01 = 1.0 - ndv;
    float baseVisibility = mix(frontReflect, edgeReflect, angle01);

    return mix(baseVisibility, 1.0, m);
}

vec3 BuildPerpendicular(vec3 n)
{
    vec3 up = abs(n.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    return SafeNormalize(cross(up, n));
}

int GetClusterIndex()
{
    vec2 localFrag = gl_FragCoord.xy - uViewportOrigin;
    vec2 tileSize = uViewportSize / vec2(uClusterGridSize.xy);

    int clusterX = clamp(int(floor(localFrag.x / tileSize.x)), 0, uClusterGridSize.x - 1);
    int clusterY = clamp(int(floor(localFrag.y / tileSize.y)), 0, uClusterGridSize.y - 1);

    float nearValue = max(uClusterNear, 0.0001);
    float farValue = max(uClusterFar, nearValue + 0.0001);
    float viewDepth = clamp(-vViewPos.z, nearValue, farValue);

    float linearDepth = (viewDepth - nearValue) / (farValue - nearValue);
    int clusterZ = clamp(int(floor(linearDepth * float(uClusterGridSize.z))), 0, uClusterGridSize.z - 1);

    return
        clusterX +
        clusterY * uClusterGridSize.x +
        clusterZ * uClusterGridSize.x * uClusterGridSize.y;
}

float SampleDirectionalShadowCascadeAtIndex(int cascadeBufferIndex, vec3 normalDir, vec3 lightDir)
{
    GPUDirectionalShadowCascade cascade = uDirectionalShadowCascades[cascadeBufferIndex];

    if (cascade.AtlasRect.z <= 0.0 || cascade.AtlasRect.w <= 0.0)
        return 1.0;

    vec4 clipPos = cascade.ShadowMatrix * vec4(vWorldPos, 1.0);
    if (abs(clipPos.w) <= 0.000001)
        return 1.0;

    vec3 ndc = clipPos.xyz / clipPos.w;
    vec2 localUv = ndc.xy * 0.5 + 0.5;
    float currentDepth = ndc.z * 0.5 + 0.5;

    if (localUv.x < 0.0 || localUv.x > 1.0 || localUv.y < 0.0 || localUv.y > 1.0)
        return 1.0;

    if (currentDepth < 0.0 || currentDepth > 1.0)
        return 1.0;

    vec2 atlasUvMin = cascade.AtlasRect.xy;
    vec2 atlasUvSize = cascade.AtlasRect.zw;
    vec2 atlasUv = atlasUvMin + localUv * atlasUvSize;

    vec2 shadowTexSize = vec2(textureSize(uShadowAtlasTexture, 0));
    vec2 texelSize = 1.0 / shadowTexSize;

    vec2 tileMin = atlasUvMin + texelSize * 0.5;
    vec2 tileMax = atlasUvMin + atlasUvSize - texelSize * 0.5;

    float ndl = Saturate(dot(normalDir, lightDir));
    float bias = mix(0.002, 0.0001, ndl);

    vec2 offsets[9] = vec2[](
        vec2(-1.0, -1.0),
        vec2( 0.0, -1.0),
        vec2( 1.0, -1.0),
        vec2(-1.0,  0.0),
        vec2( 0.0,  0.0),
        vec2( 1.0,  0.0),
        vec2(-1.0,  1.0),
        vec2( 0.0,  1.0),
        vec2( 1.0,  1.0)
    );

    float weights[9] = float[](
        1.0, 2.0, 1.0,
        2.0, 4.0, 2.0,
        1.0, 2.0, 1.0
    );

    const float weightSum = 16.0;
    float shadow = 0.0;

    for (int i = 0; i < 9; i++)
    {
        vec2 sampleUv = clamp(atlasUv + offsets[i] * texelSize, tileMin, tileMax);
        float lit = texture(uShadowAtlasTexture, vec3(sampleUv, currentDepth - bias));
        shadow += lit * weights[i];
    }

    return shadow / weightSum;
}

float SampleDirectionalShadow(LightRecord light, vec3 normalDir, vec3 lightDir)
{
    if (light.ShadowCascadeCount <= 0)
        return 1.0;

    float viewDepth = max(-vViewPos.z, 0.0);
    int selectedCascadeOffset = -1;

    for (int i = 0; i < light.ShadowCascadeCount; i++)
    {
        int cascadeBufferIndex = light.ShadowCascadeStart + i;
        GPUDirectionalShadowCascade cascade = uDirectionalShadowCascades[cascadeBufferIndex];

        if (viewDepth <= cascade.SplitRange.y)
        {
            selectedCascadeOffset = i;
            break;
        }
    }

    if (selectedCascadeOffset < 0)
        return 1.0;

    int currentCascadeIndex = light.ShadowCascadeStart + selectedCascadeOffset;
    GPUDirectionalShadowCascade currentCascade = uDirectionalShadowCascades[currentCascadeIndex];

    float shadowCurrent = SampleDirectionalShadowCascadeAtIndex(currentCascadeIndex, normalDir, lightDir);

    int lastCascadeOffset = light.ShadowCascadeCount - 1;
    int lastCascadeIndex = light.ShadowCascadeStart + lastCascadeOffset;

    if (selectedCascadeOffset == lastCascadeOffset)
    {
        float fadeWidth = max((currentCascade.SplitRange.y - currentCascade.SplitRange.x) * 0.15, 0.05);
        float fadeT = 1.0 - smoothstep(currentCascade.SplitRange.y - fadeWidth, currentCascade.SplitRange.y, viewDepth);
        return mix(1.0, shadowCurrent, fadeT);
    }

    float blendWidth = max((currentCascade.SplitRange.y - currentCascade.SplitRange.x) * 0.15, 0.05);

    if (viewDepth < currentCascade.SplitRange.y - blendWidth)
        return shadowCurrent;

    int nextCascadeIndex = light.ShadowCascadeStart + selectedCascadeOffset + 1;
    float shadowNext = SampleDirectionalShadowCascadeAtIndex(nextCascadeIndex, normalDir, lightDir);
    float blendT = smoothstep(currentCascade.SplitRange.y - blendWidth, currentCascade.SplitRange.y, viewDepth);

    return mix(shadowCurrent, shadowNext, blendT);
}

float SampleShadow(LightRecord light, vec3 normalDir, vec3 lightDir)
{
    if (uReceiveShadow != 1)
        return 1.0;

    if (light.CastShadow < 0.5)
        return 1.0;

    if (light.Kind != LIGHT_KIND_DIRECTIONAL)
        return 1.0;

    return SampleDirectionalShadow(light, normalDir, lightDir);
}

void BuildPointLight(LightRecord light, out vec3 lightDir, out float attenuation)
{
    vec3 toLight = light.Position - vWorldPos;
    float distanceValue = length(toLight);

    lightDir = (distanceValue > 0.000001) ? (toLight / distanceValue) : vec3(0.0, 0.0, 1.0);
    attenuation = ComputeDistanceAttenuation(distanceValue, light.Range, light.AttenuationCurve);
}

void BuildBoxLight(LightRecord light, out vec3 lightDir, out float attenuation)
{
    mat3 basis = BuildDirectionBasis(light.Direction);
    vec3 localPos = transpose(basis) * (vWorldPos - light.Position);

    vec3 halfSize = max(light.BoxSize * 0.5, vec3(0.000001));
    vec3 clampedLocal = clamp(localPos, -halfSize, halfSize);
    vec3 closestWorld = light.Position + basis * clampedLocal;

    vec3 toLight = closestWorld - vWorldPos;
    float distanceValue = length(toLight);

    lightDir = (distanceValue > 0.000001) ? (toLight / distanceValue) : SafeNormalize(light.Position - vWorldPos);
    attenuation = ComputeDistanceAttenuation(distanceValue, light.Range, light.AttenuationCurve);
}

void BuildSpotLight(LightRecord light, out vec3 lightDir, out float attenuation)
{
    vec3 toLight = light.Position - vWorldPos;
    float distanceValue = length(toLight);

    lightDir = (distanceValue > 0.000001) ? (toLight / distanceValue) : vec3(0.0, 0.0, 1.0);

    float distanceAtt = ComputeDistanceAttenuation(distanceValue, light.Range, light.AttenuationCurve);

    vec3 spotForward = SafeNormalize(-light.Direction);
    float angleCos = dot(spotForward, lightDir);

    float innerCos = cos(radians(light.InnerAngle));
    float outerCos = cos(radians(light.OuterAngle));

    float coneAtt = 0.0;
    if (innerCos != outerCos)
        coneAtt = clamp((angleCos - outerCos) / (innerCos - outerCos), 0.0, 1.0);
    else
        coneAtt = step(outerCos, angleCos);

    attenuation = distanceAtt * coneAtt;
}

void BuildDirectionalLight(LightRecord light, out vec3 lightDir, out float attenuation)
{
    lightDir = SafeNormalize(-light.Direction);
    attenuation = 1.0;
}

void BuildAreaLight(LightRecord light, out vec3 lightDir, out float attenuation)
{
    vec3 rightAxis = SafeNormalize(light.AreaRight);
    vec3 upAxis = SafeNormalize(light.AreaUp);
    vec3 centerToPixel = vWorldPos - light.Position;

    float halfWidth = max(light.AreaWidth * 0.5, 0.000001);
    float halfHeight = max(light.AreaHeight * 0.5, 0.000001);

    float localX = clamp(dot(centerToPixel, rightAxis), -halfWidth, halfWidth);
    float localY = clamp(dot(centerToPixel, upAxis), -halfHeight, halfHeight);

    vec3 closestPoint = light.Position + rightAxis * localX + upAxis * localY;

    vec3 toLight = closestPoint - vWorldPos;
    float distanceValue = length(toLight);

    lightDir = (distanceValue > 0.000001) ? (toLight / distanceValue) : SafeNormalize(light.Position - vWorldPos);

    vec3 emitNormal = SafeNormalize(cross(rightAxis, upAxis));
    float facing = Saturate(dot(emitNormal, -lightDir));

    attenuation = ComputeDistanceAttenuation(distanceValue, light.Range, light.AttenuationCurve) * facing;
}

void BuildLineLight(LightRecord light, out vec3 lightDir, out float attenuation)
{
    vec3 lineDir = SafeNormalize(light.LineDirection);
    float halfLength = max(light.LineLength * 0.5, 0.000001);

    vec3 a = light.Position - lineDir * halfLength;
    vec3 b = light.Position + lineDir * halfLength;

    vec3 ab = b - a;
    float abLenSq = max(dot(ab, ab), 0.000001);
    float t = clamp(dot(vWorldPos - a, ab) / abLenSq, 0.0, 1.0);
    vec3 closestPoint = a + ab * t;

    vec3 toLight = closestPoint - vWorldPos;
    float distanceValue = length(toLight);

    lightDir = (distanceValue > 0.000001) ? (toLight / distanceValue) : SafeNormalize(light.Position - vWorldPos);
    attenuation = ComputeDistanceAttenuation(distanceValue, light.Range, light.AttenuationCurve);
}

void BuildRayLight(LightRecord light, out vec3 lightDir, out float attenuation)
{
    vec3 toLight = light.Position - vWorldPos;
    float distanceValue = length(toLight);

    lightDir = (distanceValue > 0.000001) ? (toLight / distanceValue) : vec3(0.0, 0.0, 1.0);
    attenuation = 1.0;
}

void BuildLight(LightRecord light, out vec3 lightDir, out float attenuation)
{
    if (light.Kind == LIGHT_KIND_POINT)
    {
        BuildPointLight(light, lightDir, attenuation);
    }
    else if (light.Kind == LIGHT_KIND_BOX)
    {
        BuildBoxLight(light, lightDir, attenuation);
    }
    else if (light.Kind == LIGHT_KIND_SPOT)
    {
        BuildSpotLight(light, lightDir, attenuation);
    }
    else if (light.Kind == LIGHT_KIND_DIRECTIONAL)
    {
        BuildDirectionalLight(light, lightDir, attenuation);
    }
    else if (light.Kind == LIGHT_KIND_AREA)
    {
        BuildAreaLight(light, lightDir, attenuation);
    }
    else if (light.Kind == LIGHT_KIND_LINE)
    {
        BuildLineLight(light, lightDir, attenuation);
    }
    else
    {
        BuildRayLight(light, lightDir, attenuation);
    }
}

void main()
{
    vec4 baseColor = vColor * uColor;

    if (uUseTexture == 1)
        baseColor *= texture(uTexture, vTexCoord);

    bool isTransparentFlow = (uColor.a < 0.999999);

    if (!isTransparentFlow && uUseAlphaCutoff == 1 && baseColor.a < uAlphaCutoff)
        discard;

    if (vRenderSpace == 0)
    {
        FragColor = baseColor;
        return;
    }

    if (uEnableOutline == 1 && uOutlinePass == 1)
    {
        vec3 outlineAmbient =
            uOutlineColor.rgb *
            uAmbientColor *
            max(uAmbientIntensity, 0.0);

        vec4 outlineColor = vec4(outlineAmbient, uOutlineColor.a);
        FragColor = ApplyMaterialFogExact(outlineColor, vViewPos);
        return;
    }

    vec3 normalDir = DecodeNormal();
    vec3 viewDir = SafeNormalize(uCameraPosition - vWorldPos);

    vec3 ambientTerm =
        baseColor.rgb *
        uAmbientColor *
        uAmbientIntensity *
        max(uAmbientStrength, 0.0);

    vec3 diffuseAccum = vec3(0.0);
    vec3 specularAccum = vec3(0.0);
    vec3 rimAccum = vec3(0.0);

    int clusterIndex = GetClusterIndex();
    uvec2 clusterRange = uClusterRanges[clusterIndex];
    uint start = clusterRange.x;
    uint count = clusterRange.y;

    for (uint i = 0u; i < count; i++)
    {
        uint lightIndex = uClusterLightIndices[start + i];
        if (lightIndex >= uint(uLightCount))
            continue;

        LightRecord light = ReadLight(lightIndex);

        vec3 lightDir;
        float attenuation;
        BuildLight(light, lightDir, attenuation);

        if (attenuation <= 0.000001)
            continue;

        float rawNdl = dot(normalDir, lightDir);
        float ndl = Saturate(rawNdl);

        if (uEnableColorBanding != 1)
        {
            if (ndl <= 0.000001)
                continue;
        }

        float shadowFactor = SampleShadow(light, normalDir, lightDir);

        vec3 halfVector = SafeNormalize(lightDir + viewDir);
        float ndh = Saturate(dot(normalDir, halfVector));

        float specularShape;
        float rimShape;
        float specularRimAttenuation = 1.0;

        if (uEnableColorBanding == 1)
        {
            ndl = ApplyDiffuseBandFromRawNdl(rawNdl);

            specularRimAttenuation = ComputeToonSpecRimAttenuation(rawNdl, shadowFactor);

            specularShape = ComputeSpecularValueBanded(ndh, uSpecularRange);
            rimShape = ComputeRimValueBanded(normalDir, viewDir, uRimRange);
        }
        else
        {
            specularShape = ComputeSpecularValueSmooth(ndh, uSpecularRange);
            rimShape = ComputeRimValueSmooth(normalDir, viewDir, uRimRange);
        }

        float specularValue = specularShape * max(uSpecularIntensity, 0.0);
        float rimValue = rimShape * max(uRimIntensity, 0.0);

        vec3 lightColor = light.Color * light.Intensity;
        float lightFactor = attenuation * shadowFactor;

        diffuseAccum += baseColor.rgb * lightColor * ndl * lightFactor;
        specularAccum +=
            (lightColor * uSpecularColor) *
            specularValue *
            attenuation *
            specularRimAttenuation;

        rimAccum +=
            (lightColor * uRimColor) *
            rimValue *
            attenuation *
            specularRimAttenuation;
    }

    if (uCloudShadowStrength > 0.0001)
    {
        vec3 cloudSunDir = vec3(0.0, 1.0, 0.0);
        bool cloudSunFound = false;

        for (int i = 0; i < uLightCount; i++)
        {
            GPULight src = uLights[i];
            if (int(src.Meta0.x + 0.5) == LIGHT_KIND_DIRECTIONAL)
            {
                cloudSunDir = SafeNormalize(-src.DirectionOuter.xyz);
                cloudSunFound = true;
                break;
            }
        }

        if (cloudSunFound)
        {
            vec3 toCenter = vWorldPos - uCloudShadowPlanetCenter;
            float centerDist = length(toCenter);

            if (centerDist > 0.000001)
            {
                vec3 upDir = toCenter / centerDist;
                float elevation = dot(upDir, cloudSunDir);
                float elevFactor = smoothstep(0.0, 0.12, elevation);

                if (elevFactor > 0.0001)
                {
                    vec3 sunHorizontal = SafeNormalize(cloudSunDir - upDir * elevation);
                    float slant = uCloudShadowSlant / max(elevation, 0.02);
                    vec3 sampleDir = SafeNormalize(upDir * cos(slant) + sunHorizontal * sin(slant));

                    float camDist = max(length(vWorldPos - uCameraPosition), 0.0001);
                    float lod = clamp(log2(max(1.0, camDist / max(uCloudShadowTexelSize, 0.0001))), 0.0, 10.0);

                    float cloudOpacity = textureLod(uCloudShadowCube, sampleDir, lod).r;
                    float cloudShadowFactor = 1.0 - cloudOpacity * uCloudShadowStrength * elevFactor;

                    diffuseAccum *= cloudShadowFactor;
                    if (uCloudShadowAffectsAmbient == 1)
                        ambientTerm *= cloudShadowFactor;
                }
            }
        }
    }

    vec3 reflectionAccum = vec3(0.0);

    if (uReceiveReflection == 1 && uReflectionEnabled == 1 && uReflectionIntensity > 0.0)
    {
        vec3 reflectDir = SafeNormalize(reflect(-viewDir, normalDir));

        float smoothness = Saturate(uSmoothness);
        float perceptualRoughness = 1.0 - smoothness;

        vec3 reflectionColor = SampleReflectionEnvironmentSurfaceBlur(reflectDir, perceptualRoughness);

        float reflectionVisibility = ComputeReflectionVisibilityLinear(normalDir, viewDir, smoothness, uMetallic);
        float metallic = Saturate(uMetallic);

        vec3 dielectricReflection = reflectionColor * reflectionVisibility * uReflectionIntensity;
        vec3 metallicReflection = reflectionColor * baseColor.rgb * reflectionVisibility * uReflectionIntensity;

        reflectionAccum = mix(dielectricReflection, metallicReflection, metallic);
    }

    float metallic = Saturate(uMetallic);

    vec3 baseLit = ambientTerm + diffuseAccum;
    vec3 highlightLit = specularAccum + rimAccum;

    vec3 opaqueRgb = mix(baseLit + reflectionAccum, reflectionAccum, metallic) + highlightLit;

    if (isTransparentFlow)
    {
        float outAlpha = Saturate(baseColor.a);

        vec3 alphaScaledRgb = baseLit * (1.0 - metallic) * outAlpha;
        vec3 nonAlphaScaledRgb = reflectionAccum + highlightLit;

        vec4 transparentColor = vec4(alphaScaledRgb + nonAlphaScaledRgb, outAlpha);
        FragColor = ApplyMaterialFogExact(transparentColor, vViewPos);
    }
    else
    {
        FragColor = ApplyMaterialFogExact(vec4(opaqueRgb, baseColor.a), vViewPos);
    }
}