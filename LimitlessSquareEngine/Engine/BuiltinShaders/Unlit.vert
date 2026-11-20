#version 430 core

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec4 aColor;
layout(location = 2) in vec2 aTexCoord;

uniform int uRenderSpace;   // 0 = Canvas, 1 = Camera
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

layout(std430, binding = 5) readonly buffer InstanceModelMatrices
{
    vec4 iModel[];
};

uniform int uInstanceEnabled;
uniform int uInstanceBase;
uniform vec3 uInstanceWorldOffset;

out vec4 vColor;
out vec2 vTexCoord;
out vec3 vViewPos;
flat out int vRenderSpace;

void main()
{
    vec4 worldPos4;
    if (uInstanceEnabled == 1)
    {
        int i = (uInstanceBase + gl_InstanceID) * 4;
        mat4 instanceModelMatrix = mat4(iModel[i], iModel[i + 1], iModel[i + 2], iModel[i + 3]);
        worldPos4 = instanceModelMatrix * vec4(aPos, 1.0);
        worldPos4.xyz += uInstanceWorldOffset;
    }
    else
    {
        worldPos4 = uModel * vec4(aPos, 1.0);
    }

    vec4 viewPos4 = uView * worldPos4;

    vViewPos = viewPos4.xyz;

    if (uRenderSpace == 0)
    {
        gl_Position = vec4(aPos, 1.0);
    }
    else
    {
        gl_Position = uProjection * viewPos4;
    }

    vColor = aColor;
    vTexCoord = aTexCoord;
    vRenderSpace = uRenderSpace;
}
