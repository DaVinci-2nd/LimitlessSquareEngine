#version 430 core

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec4 aColor;

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

void main()
{
    if (uInstanceEnabled == 1)
    {
        int i = (uInstanceBase + gl_InstanceID) * 4;
        mat4 instanceModelMatrix = mat4(iModel[i], iModel[i + 1], iModel[i + 2], iModel[i + 3]);
        vec4 worldPos4 = instanceModelMatrix * vec4(aPos, 1.0);
        worldPos4.xyz += uInstanceWorldOffset;

        if (uRenderSpace == 0)
        {
            gl_Position = vec4(aPos, 1.0);
        }
        else
        {
            gl_Position = uProjection * uView * worldPos4;
        }

        vColor = aColor;
        return;
    }

    if (uRenderSpace == 0)
    {
        gl_Position = vec4(aPos, 1.0);
    }
    else
    {
        gl_Position = uProjection * uView * uModel * vec4(aPos, 1.0);
    }

    vColor = aColor;
}