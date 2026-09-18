#version 330 core

layout(location = 0) in vec3 aPos;
layout(location = 2) in vec2 aTexCoord;

uniform mat4 uView;
uniform mat4 uProjection;
uniform vec3 uBodyDirection;
uniform float uBodySize;
uniform float uBodyRoll;

out vec2 vTexCoord;

void main()
{
    vec3 camDir = mat3(uView) * uBodyDirection;
    float dirLength = length(camDir);
    camDir = dirLength > 0.000001 ? camDir / dirLength : vec3(0.0, 0.0, -1.0);

    vec3 upRef = abs(camDir.y) > 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    vec3 right = normalize(cross(camDir, upRef));
    vec3 up = cross(right, camDir);

    float rollSin = sin(uBodyRoll);
    float rollCos = cos(uBodyRoll);
    vec2 quad = vec2(
        aPos.x * rollCos - aPos.y * rollSin,
        aPos.x * rollSin + aPos.y * rollCos);

    float halfExtent = tan(radians(uBodySize) * 0.5);
    vec3 pos = camDir + (right * quad.x + up * quad.y) * (halfExtent * 2.0);

    gl_Position = uProjection * vec4(pos, 1.0);
    vTexCoord = vec2(aTexCoord.x, 1.0 - aTexCoord.y);
}
