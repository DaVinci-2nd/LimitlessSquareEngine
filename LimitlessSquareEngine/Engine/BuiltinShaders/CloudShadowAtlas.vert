#version 430 core

layout(location = 0) in vec3 aPos;

out vec2 vCloudUv;

void main()
{
    vCloudUv = aPos.xy * 0.5 + 0.5;
    gl_Position = vec4(aPos.xy, 0.0, 1.0);
}
