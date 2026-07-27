#version 430 core

layout(location = 0) in vec3 aPos;

uniform int uRenderSpace;
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec3 vWorldPos;
flat out vec3 vPlanetCenter;
flat out int vRenderSpace;

void main()
{
    vec4 worldPos4 = uModel * vec4(aPos, 1.0);

    vWorldPos = worldPos4.xyz;
    vPlanetCenter = (uModel * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vRenderSpace = uRenderSpace;

    if (uRenderSpace == 0)
    {
        gl_Position = vec4(aPos, 1.0);
    }
    else
    {
        gl_Position = uProjection * uView * worldPos4;
    }
}
