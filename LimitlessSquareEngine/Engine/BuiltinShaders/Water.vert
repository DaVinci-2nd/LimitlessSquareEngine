#version 430 core

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec4 aColor;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec3 aNormal;
layout(location = 4) in vec4 aTangent;

uniform int uRenderSpace;
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

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
        gl_Position = vec4(aPos, 1.0);
    else
        gl_Position = uProjection * viewPos4;

    vColor = aColor;
    vTexCoord = aTexCoord;
    vRenderSpace = uRenderSpace;
}
