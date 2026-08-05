using Silk.NET.OpenGL;
using System;

namespace LimitlessSquareEngine
{
    internal partial class Graphics
    {
        private const int _cloudShapeNoiseSize = 128;
        private const int _cloudDetailNoiseSize = 32;

        private uint _cloudNoiseShapeTexture = 0;
        private uint _cloudNoiseDetailTexture = 0;

        private const string _cloudNoiseBakeVertexSource = @"#version 430 core
void main()
{
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

        private const string _cloudNoiseBakeFragmentSource = @"#version 430 core

out vec4 FragColor;

uniform int uZSlice;
uniform int uGridSize;
uniform int uBakeMode;

vec3 hash33(vec3 p)
{
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx);
}

float worleyTile(vec3 uvw, float freq)
{
    vec3 p = uvw * freq;
    vec3 id = floor(p);
    vec3 f = fract(p);

    float minD = 8.0;
    for (int x = -1; x <= 1; x++)
    for (int y = -1; y <= 1; y++)
    for (int z = -1; z <= 1; z++)
    {
        vec3 off = vec3(float(x), float(y), float(z));
        vec3 cell = mod(id + off, vec3(freq));
        vec3 feat = hash33(cell);
        vec3 d = off + feat - f;
        minD = min(minD, dot(d, d));
    }
    return 1.0 - clamp(sqrt(minD), 0.0, 1.0);
}

float worleyFbmTile(vec3 uvw, float freq)
{
    return worleyTile(uvw, freq) * 0.625
         + worleyTile(uvw, freq * 2.0) * 0.25
         + worleyTile(uvw, freq * 4.0) * 0.125;
}

float perlinTile(vec3 p, float period)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

    vec3 c000 = mod(i + vec3(0.0, 0.0, 0.0), period);
    vec3 c100 = mod(i + vec3(1.0, 0.0, 0.0), period);
    vec3 c010 = mod(i + vec3(0.0, 1.0, 0.0), period);
    vec3 c110 = mod(i + vec3(1.0, 1.0, 0.0), period);
    vec3 c001 = mod(i + vec3(0.0, 0.0, 1.0), period);
    vec3 c101 = mod(i + vec3(1.0, 0.0, 1.0), period);
    vec3 c011 = mod(i + vec3(0.0, 1.0, 1.0), period);
    vec3 c111 = mod(i + vec3(1.0, 1.0, 1.0), period);

    float n000 = dot(hash33(c000) * 2.0 - 1.0, f - vec3(0.0, 0.0, 0.0));
    float n100 = dot(hash33(c100) * 2.0 - 1.0, f - vec3(1.0, 0.0, 0.0));
    float n010 = dot(hash33(c010) * 2.0 - 1.0, f - vec3(0.0, 1.0, 0.0));
    float n110 = dot(hash33(c110) * 2.0 - 1.0, f - vec3(1.0, 1.0, 0.0));
    float n001 = dot(hash33(c001) * 2.0 - 1.0, f - vec3(0.0, 0.0, 1.0));
    float n101 = dot(hash33(c101) * 2.0 - 1.0, f - vec3(1.0, 0.0, 1.0));
    float n011 = dot(hash33(c011) * 2.0 - 1.0, f - vec3(0.0, 1.0, 1.0));
    float n111 = dot(hash33(c111) * 2.0 - 1.0, f - vec3(1.0, 1.0, 1.0));

    float nx00 = mix(n000, n100, u.x);
    float nx10 = mix(n010, n110, u.x);
    float nx01 = mix(n001, n101, u.x);
    float nx11 = mix(n011, n111, u.x);
    float nxy0 = mix(nx00, nx10, u.y);
    float nxy1 = mix(nx01, nx11, u.y);

    return mix(nxy0, nxy1, u.z);
}

float perlinFbmTile(vec3 uvw, float freq)
{
    float v = 0.0;
    float amp = 0.5;
    float sum = 0.0;
    for (int i = 0; i < 4; i++)
    {
        v += perlinTile(uvw * freq, freq) * amp;
        sum += amp;
        freq *= 2.0;
        amp *= 0.5;
    }
    return clamp(v / sum * 0.5 + 0.5, 0.0, 1.0);
}

float perlinWorley(vec3 uvw, float freq)
{
    float w = worleyFbmTile(uvw, freq);
    float p = perlinFbmTile(uvw, freq);
    return clamp((p - (1.0 - w)) / max(w, 0.0001), 0.0, 1.0);
}

void main()
{
    vec3 uvw = vec3(gl_FragCoord.xy, float(uZSlice) + 0.5) / float(uGridSize);

    if (uBakeMode == 0)
    {
        float r = perlinFbmTile(uvw, 5.0);
        float g = worleyFbmTile(uvw, 8.0);
        float b = worleyFbmTile(uvw, 16.0);
        float a = worleyFbmTile(uvw, 32.0);
        FragColor = vec4(r, g, b, a);
    }
    else
    {
        float r = worleyFbmTile(uvw, 4.0);
        float g = worleyFbmTile(uvw, 8.0);
        float b = worleyFbmTile(uvw, 16.0);
        FragColor = vec4(r, g, b, 1.0);
    }
}";

        private void EnsureCloudNoiseTextures()
        {
            if (_cloudNoiseShapeTexture != 0 && _cloudNoiseDetailTexture != 0)
                return;

            InitializeCloudSupportResources();

            uint vs = CompileShader(ShaderType.VertexShader, _cloudNoiseBakeVertexSource);
            uint fs = CompileShader(ShaderType.FragmentShader, _cloudNoiseBakeFragmentSource);

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vs);
            _gl.AttachShader(program, fs);
            _gl.LinkProgram(program);

            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linkSuccess);
            if (linkSuccess == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                _gl.DetachShader(program, vs);
                _gl.DetachShader(program, fs);
                _gl.DeleteShader(vs);
                _gl.DeleteShader(fs);
                _gl.DeleteProgram(program);
                Console.WriteLine($"[X] Cloud noise bake shader link failed: {infoLog}");
                return;
            }

            _gl.DetachShader(program, vs);
            _gl.DetachShader(program, fs);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);

            int zSliceLoc = _gl.GetUniformLocation(program, "uZSlice");
            int gridSizeLoc = _gl.GetUniformLocation(program, "uGridSize");
            int bakeModeLoc = _gl.GetUniformLocation(program, "uBakeMode");

            _gl.GetInteger(GLEnum.FramebufferBinding, out int previousFbo);
            bool depthWasEnabled = _gl.IsEnabled(GLEnum.DepthTest);
            bool blendWasEnabled = _gl.IsEnabled(GLEnum.Blend);
            bool cullWasEnabled = _gl.IsEnabled(GLEnum.CullFace);
            bool scissorWasEnabled = _gl.IsEnabled(GLEnum.ScissorTest);

            uint fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            _gl.UseProgram(program);
            _gl.BindVertexArray(_cloudFullscreenVao);

            _gl.Disable(GLEnum.DepthTest);
            _gl.Disable(GLEnum.Blend);
            _gl.Disable(GLEnum.CullFace);
            _gl.Disable(GLEnum.ScissorTest);

            if (_cloudNoiseShapeTexture == 0)
            {
                _cloudNoiseShapeTexture = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture3D, _cloudNoiseShapeTexture);
                _gl.TexImage3D(
                    TextureTarget.Texture3D,
                    0,
                    InternalFormat.Rgba8,
                    (uint)_cloudShapeNoiseSize,
                    (uint)_cloudShapeNoiseSize,
                    (uint)_cloudShapeNoiseSize,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    ReadOnlySpan<byte>.Empty);

                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.Repeat);

                _gl.Viewport(0, 0, (uint)_cloudShapeNoiseSize, (uint)_cloudShapeNoiseSize);
                _gl.Uniform1(gridSizeLoc, _cloudShapeNoiseSize);
                _gl.Uniform1(bakeModeLoc, 0);

                for (int z = 0; z < _cloudShapeNoiseSize; z++)
                {
                    _gl.FramebufferTextureLayer(
                        FramebufferTarget.Framebuffer,
                        FramebufferAttachment.ColorAttachment0,
                        _cloudNoiseShapeTexture,
                        0,
                        z);
                    _gl.Uniform1(zSliceLoc, z);
                    _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                }

                _gl.GenerateMipmap(TextureTarget.Texture3D);

                byte[] probePixels = new byte[_cloudShapeNoiseSize * _cloudShapeNoiseSize * _cloudShapeNoiseSize * 4];
                _gl.GetTexImage(GLEnum.Texture3D, 0, GLEnum.Rgba, GLEnum.UnsignedByte, (Span<byte>)probePixels);
                int probeMin = 255;
                int probeMax = 0;
                long probeSum = 0;
                for (int i = 0; i < probePixels.Length; i += 4)
                {
                    int r = probePixels[i];
                    probeSum += r;
                    if (r < probeMin) probeMin = r;
                    if (r > probeMax) probeMax = r;
                }
                Console.WriteLine($"[i] Cloud shape noise baked: R min={probeMin} max={probeMax} avg={probeSum / (probePixels.Length / 4)}");
            }

            if (_cloudNoiseDetailTexture == 0)
            {
                _cloudNoiseDetailTexture = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture3D, _cloudNoiseDetailTexture);
                _gl.TexImage3D(
                    TextureTarget.Texture3D,
                    0,
                    InternalFormat.Rgb8,
                    (uint)_cloudDetailNoiseSize,
                    (uint)_cloudDetailNoiseSize,
                    (uint)_cloudDetailNoiseSize,
                    0,
                    PixelFormat.Rgb,
                    PixelType.UnsignedByte,
                    ReadOnlySpan<byte>.Empty);

                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.Repeat);

                _gl.Viewport(0, 0, (uint)_cloudDetailNoiseSize, (uint)_cloudDetailNoiseSize);
                _gl.Uniform1(gridSizeLoc, _cloudDetailNoiseSize);
                _gl.Uniform1(bakeModeLoc, 1);

                for (int z = 0; z < _cloudDetailNoiseSize; z++)
                {
                    _gl.FramebufferTextureLayer(
                        FramebufferTarget.Framebuffer,
                        FramebufferAttachment.ColorAttachment0,
                        _cloudNoiseDetailTexture,
                        0,
                        z);
                    _gl.Uniform1(zSliceLoc, z);
                    _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                }

                _gl.GenerateMipmap(TextureTarget.Texture3D);
            }

            _gl.BindTexture(TextureTarget.Texture3D, 0);
            _gl.FramebufferTextureLayer(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                0,
                0,
                0);

            if (depthWasEnabled) _gl.Enable(GLEnum.DepthTest);
            if (blendWasEnabled) _gl.Enable(GLEnum.Blend);
            if (cullWasEnabled) _gl.Enable(GLEnum.CullFace);
            if (scissorWasEnabled) _gl.Enable(GLEnum.ScissorTest);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFbo);
            _gl.DeleteFramebuffer(fbo);
            _gl.DeleteProgram(program);
        }

        private void ReleaseCloudNoiseTextures()
        {
            if (_cloudNoiseShapeTexture != 0)
            {
                _gl.DeleteTexture(_cloudNoiseShapeTexture);
                _cloudNoiseShapeTexture = 0;
            }

            if (_cloudNoiseDetailTexture != 0)
            {
                _gl.DeleteTexture(_cloudNoiseDetailTexture);
                _cloudNoiseDetailTexture = 0;
            }
        }
    }
}
