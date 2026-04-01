#version 430 core

uniform sampler2D uTexture;
uniform vec4 uColor;
uniform int uUseTexture;

in vec4 vColor;
in vec2 vTexCoord;
out vec4 FragColor;

void main()
{
    vec4 baseColor = vColor * uColor;
    if (uUseTexture == 1)
        FragColor = texture(uTexture, vTexCoord) * baseColor;
    else
        FragColor = baseColor;
}
