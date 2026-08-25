#version 430 core

const float PI = 3.1415926535897932384626433832795;

uniform sampler2D uTexture;
uniform vec4 uColor;
uniform int uUseTexture;

uniform vec4 uTextureScaleOffset;

uniform int uUseAlphaCutoff;
uniform float uAlphaCutoff;

uniform int uFogEnabled;
uniform int uFogMode;
uniform vec4 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform int uFogEdgeTransitionToSkybox;

uniform sampler2D uFogCylindricalTexture;
uniform samplerCube uFogSkyboxCube;
uniform mat4 uFogInvViewRotation;

in vec4 vColor;
in vec2 vTexCoord;
in vec3 vViewPos;
flat in int vRenderSpace;

out vec4 FragColor;

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

void main()
{
    vec2 textureUv = vTexCoord * uTextureScaleOffset.xy + uTextureScaleOffset.zw;

    vec4 baseColor = vColor * uColor;
    if (uUseTexture == 1)
        baseColor *= texture(uTexture, textureUv);

    bool isTransparentFlow = (uColor.a < 0.999999);

    if (uUseAlphaCutoff == 1 && baseColor.a < uAlphaCutoff)
        discard;

    if (vRenderSpace == 0)
    {
        FragColor = baseColor;
        return;
    }

    if (isTransparentFlow)
    {
        float outAlpha = Saturate(baseColor.a);
        vec3 alphaScaledRgb = baseColor.rgb * outAlpha;
        vec4 transparentColor = vec4(alphaScaledRgb, outAlpha);
        FragColor = ApplyMaterialFogExact(transparentColor, vViewPos);
    }
    else
    {
        FragColor = ApplyMaterialFogExact(vec4(baseColor.rgb, baseColor.a), vViewPos);
    }
}
