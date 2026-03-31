#version 330 core
in vec4 vColor;
out vec4 FragColor;

void main()
{
    float r = sin(gl_FragCoord.x * 0.01) * 0.5 + 0.5;
    float g = cos(gl_FragCoord.y * 0.01) * 0.5 + 0.5;
    float b = sin(gl_FragCoord.x * 0.01 + gl_FragCoord.y * 0.01) * 0.5 + 0.5;
    
    FragColor = vec4(r, g, b, 1.0);
}