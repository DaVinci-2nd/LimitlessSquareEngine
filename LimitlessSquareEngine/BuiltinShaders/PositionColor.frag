#version 330 core
in vec4 vColor;
out vec4 FragColor;

void main()
{
    // 基于屏幕坐标生成动态颜色
    // gl_FragCoord.xy 是当前像素在窗口中的坐标（以像素为单位）
    // 使用正弦/余弦函数产生平滑变化，使颜色随位置波动
    float r = sin(gl_FragCoord.x * 0.01) * 0.5 + 0.5;
    float g = cos(gl_FragCoord.y * 0.01) * 0.5 + 0.5;
    float b = sin(gl_FragCoord.x * 0.01 + gl_FragCoord.y * 0.01) * 0.5 + 0.5;
    
    FragColor = vec4(r, g, b, 1.0);
}