#version 330 core

in vec3 vLocalPos;
in vec2 vTexCoord;

uniform sampler2D uFrontTexture;
uniform sampler2D uBackTexture;
uniform sampler2D uLeftTexture;
uniform sampler2D uRightTexture;
uniform sampler2D uUpTexture;
uniform sampler2D uDownTexture;

out vec4 FragColor;

void main()
{
    vec3 p = vLocalPos;
    vec3 ap = abs(p);
    vec2 uv = vec2(1.0 - vTexCoord.x, 1.0 - vTexCoord.y);

    if (ap.x >= ap.y && ap.x >= ap.z)
    {
        if (p.x > 0.0)
        {
            // +X
            FragColor = texture(uLeftTexture, uv);
        }
        else
        {
            // -X
            FragColor = texture(uRightTexture, uv);
        }
    }
    else if (ap.y >= ap.x && ap.y >= ap.z)
    {
        if (p.y > 0.0)
        {
            // +Y
            FragColor = texture(uUpTexture, uv);
        }
        else
        {
            // -Y
            FragColor = texture(uDownTexture, uv);
        }
    }
    else
    {
        if (p.z > 0.0)
        {
            // +Z
            FragColor = texture(uFrontTexture, uv);
        }
        else
        {
            // -Z
            FragColor = texture(uBackTexture, uv);
        }
    }
}