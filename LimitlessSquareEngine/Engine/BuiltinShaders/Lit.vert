#version 430 core

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec4 aColor;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec3 aNormal;
layout(location = 4) in vec4 aTangent;   // xyz = tangent, w = bitangent sign

uniform int uRenderSpace;   // 0 = Canvas, 1 = Camera
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

/* 描边相关 */
uniform int uEnableOutline;   // 材质开关
uniform int uOutlinePass;     // 系统开关：0=正常通道，1=描边通道
uniform vec2 uViewportSize;
uniform float uOutlineWidth;  // 单位：像素，默认 2.0

layout(location = 5) in vec3 aOutlineNormal;
uniform int uUseOutlineNormal;

out vec4 vColor;
out vec2 vTexCoord;
out vec3 vWorldPos;
out vec3 vViewPos;
out vec3 vWorldNormal;
out vec3 vWorldTangent;
out vec3 vWorldBitangent;
flat out int vRenderSpace;

void main()
{
    vec4 worldPos4 = uModel * vec4(aPos, 1.0);
    vec4 viewPos4 = uView * worldPos4;

    vWorldPos = worldPos4.xyz;
    vViewPos = viewPos4.xyz;

    mat3 normalMatrix = transpose(inverse(mat3(uModel)));

    vec3 worldNormal = normalize(normalMatrix * aNormal);
    vec3 worldTangent = normalize(normalMatrix * aTangent.xyz);

    worldTangent = normalize(worldTangent - worldNormal * dot(worldNormal, worldTangent));
    vec3 worldBitangent = normalize(cross(worldNormal, worldTangent) * aTangent.w);

    vWorldNormal = worldNormal;
    vWorldTangent = worldTangent;
    vWorldBitangent = worldBitangent;

    if (uRenderSpace == 0)
    {
        gl_Position = vec4(aPos, 1.0);
    }
    else
    {
        vec4 clipPos = uProjection * viewPos4;

        if (uEnableOutline == 1 && uOutlinePass == 1)
        {
            vec3 outlineObjectNormal = (uUseOutlineNormal == 1) ? aOutlineNormal : aNormal;
            vec3 worldOutlineNormal = normalize(normalMatrix * outlineObjectNormal);
            vec3 viewNormal = normalize(mat3(uView) * worldOutlineNormal);

            vec4 clipPosShifted = uProjection * vec4(viewPos4.xyz + viewNormal, 1.0);

            vec2 ndc0 = clipPos.xy / max(abs(clipPos.w), 0.000001);
            vec2 ndc1 = clipPosShifted.xy / max(abs(clipPosShifted.w), 0.000001);

            vec2 outlineDir = ndc1 - ndc0;
            float lenSq = dot(outlineDir, outlineDir);

            if (lenSq > 0.0000001)
                outlineDir *= inversesqrt(lenSq);
            else
                outlineDir = vec2(0.0, 1.0);

            float outlineScale = max(uViewportSize.y, 1.0) / 1080.0;
            float outlineWidthPx = max(uOutlineWidth, 0.0) * outlineScale;
            vec2 ndcOffset = outlineDir * (outlineWidthPx * 2.0 / max(uViewportSize, vec2(1.0)));
            clipPos.xy += ndcOffset * clipPos.w;
        }

        gl_Position = clipPos;
    }

    vColor = aColor;
    vTexCoord = aTexCoord;
    vRenderSpace = uRenderSpace;
}