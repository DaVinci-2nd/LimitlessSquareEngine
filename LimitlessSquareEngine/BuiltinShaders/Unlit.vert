#version 430 core

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec4 aColor;
layout(location = 2) in vec2 aTexCoord;

uniform int uRenderSpace;   // 0 = Canvas, 1 = Camera
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec4 vColor;
out vec2 vTexCoord;

void main()
{
    if (uRenderSpace == 0)
    {
        gl_Position = vec4(aPos, 1.0);
    }
    else
    {
        gl_Position = uProjection * uView * uModel * vec4(aPos, 1.0);
    }

    vColor = aColor;
    vTexCoord = aTexCoord;
}