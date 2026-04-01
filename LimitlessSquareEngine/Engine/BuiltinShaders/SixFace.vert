#version 330 core

layout(location = 0) in vec3 aPos;
layout(location = 2) in vec2 aTexCoord;

uniform int uRenderSpace;
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec3 vLocalPos;
out vec2 vTexCoord;

void main()
{
    vLocalPos = aPos;
    vTexCoord = aTexCoord;

    if (uRenderSpace == 0)
    {
        gl_Position = vec4(aPos, 1.0);
    }
    else
    {
        gl_Position = uProjection * uView * uModel * vec4(aPos, 1.0);
    }
}