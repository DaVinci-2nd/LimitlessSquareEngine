#version 330 core

in vec2 vTexCoord;

uniform sampler2D uBodyTexture;
uniform vec3 uBodyColor;
uniform float uBodyIntensity;

out vec4 FragColor;

void main()
{
    vec4 texel = texture(uBodyTexture, vTexCoord);
    vec3 emissive = texel.rgb * texel.a * uBodyColor * uBodyIntensity;
    FragColor = vec4(emissive, texel.a);
}
