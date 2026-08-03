#version 430 core

layout(location = 0) in vec3 aPos;

uniform float uCloudFarDepth;

out vec2 vCloudUv;

void main()
{
    vCloudUv = aPos.xy * 0.5 + 0.5;
    gl_Position = vec4(aPos.xy, uCloudFarDepth, 1.0);
}
