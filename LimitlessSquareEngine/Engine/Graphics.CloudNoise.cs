using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LimitlessSquareEngine
{
    internal partial class Graphics
    {
        private const int _cloudShapeNoiseSize = 128;
        private const int _cloudDetailNoiseSize = 32;

        private uint _cloudNoiseShapeTexture = 0;
        private uint _cloudNoiseDetailTexture = 0;
        private CloudNoiseDefinition? _cloudNoiseDefinition = null;

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
uniform int uChannelCount;
uniform int uOp[32];
uniform int uPrim[32];
uniform float uFreq[32];
uniform int uOctaves[32];
uniform float uGain[32];
uniform float uLacunarity[32];
uniform float uWeights[256];
uniform int uSrcA[32];
uniform int uSrcB[32];
uniform int uDriver[32];
uniform float uRangeA[32];
uniform float uRangeB[32];
uniform float uStrength[32];
uniform int uOut[4];

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

float worleyFbmTile(vec3 uvw, float freq, int channel)
{
    float v = 0.0;
    float f = freq;
    for (int i = 0; i < uOctaves[channel]; i++)
    {
        v += worleyTile(uvw, f) * uWeights[channel * 8 + i];
        f *= uLacunarity[channel];
    }
    return clamp(v, 0.0, 1.0);
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

float perlinFbmTile(vec3 uvw, float freq, int channel)
{
    float v = 0.0;
    float amp = 0.5;
    float sum = 0.0;
    float f = freq;
    for (int i = 0; i < uOctaves[channel]; i++)
    {
        v += perlinTile(uvw * f, f) * amp;
        sum += amp;
        f *= uLacunarity[channel];
        amp *= uGain[channel];
    }
    return clamp(v / sum * 0.5 + 0.5, 0.0, 1.0);
}

void main()
{
    vec3 uvw = vec3(gl_FragCoord.xy, float(uZSlice) + 0.5) / float(uGridSize);

    float chVal[32];
    for (int c = 0; c < uChannelCount; c++)
    {
        if (uOp[c] == 0)
        {
            if (uPrim[c] == 0)
                chVal[c] = perlinFbmTile(uvw, uFreq[c], c);
            else
                chVal[c] = worleyFbmTile(uvw, uFreq[c], c);
        }
        else if (uOp[c] == 1)
            chVal[c] = mix(chVal[uSrcA[c]], chVal[uSrcB[c]], smoothstep(uRangeA[c], uRangeB[c], chVal[uDriver[c]]));
        else if (uOp[c] == 2)
            chVal[c] = chVal[uSrcA[c]] * (1.0 - uStrength[c] + uStrength[c] * chVal[uSrcB[c]]);
        else if (uOp[c] == 3)
            chVal[c] = smoothstep(uRangeA[c], uRangeB[c], chVal[uSrcA[c]]);
        else if (uOp[c] == 4)
            chVal[c] = chVal[uSrcA[c]] * chVal[uSrcB[c]];
        else if (uOp[c] == 5)
            chVal[c] = chVal[uSrcA[c]] * uRangeA[c] + uRangeB[c];
        else if (uOp[c] == 6)
            chVal[c] = step(uRangeA[c], chVal[uSrcA[c]]);
        else if (uOp[c] == 7)
            chVal[c] = chVal[uSrcA[c]] + chVal[uSrcB[c]];
        else if (uOp[c] == 8)
            chVal[c] = chVal[uSrcA[c]] - chVal[uSrcB[c]];
        else if (uOp[c] == 9)
            chVal[c] = max(chVal[uSrcA[c]], chVal[uSrcB[c]]);
        else if (uOp[c] == 10)
            chVal[c] = min(chVal[uSrcA[c]], chVal[uSrcB[c]]);
        else if (uOp[c] == 11)
            chVal[c] = clamp(chVal[uSrcA[c]], uRangeA[c], uRangeB[c]);
        else
            chVal[c] = abs(chVal[uSrcA[c]]);
    }

    float outV[4];
    for (int i = 0; i < 4; i++)
        outV[i] = 0.0;
    for (int i = 0; i < 4; i++)
    {
        if (uOut[i] >= 0 && uOut[i] < uChannelCount)
            outV[i] = chVal[uOut[i]];
    }

    if (uBakeMode == 0)
        FragColor = vec4(outV[0], outV[1], outV[2], outV[3]);
    else
        FragColor = vec4(outV[0], outV[1], outV[2], 1.0);
}";

        private void EnsureCloudNoiseTextures()
        {
            if (_cloudNoiseShapeTexture != 0 && _cloudNoiseDetailTexture != 0)
                return;

            InitializeCloudSupportResources();
            if (!EnsureCloudNoiseDefinition())
                return;

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
            int channelCountLoc = _gl.GetUniformLocation(program, "uChannelCount");
            int opLoc = _gl.GetUniformLocation(program, "uOp");
            int primLoc = _gl.GetUniformLocation(program, "uPrim");
            int freqLoc = _gl.GetUniformLocation(program, "uFreq");
            int octavesLoc = _gl.GetUniformLocation(program, "uOctaves");
            int gainLoc = _gl.GetUniformLocation(program, "uGain");
            int lacunarityLoc = _gl.GetUniformLocation(program, "uLacunarity");
            int weightsLoc = _gl.GetUniformLocation(program, "uWeights");
            int srcALoc = _gl.GetUniformLocation(program, "uSrcA");
            int srcBLoc = _gl.GetUniformLocation(program, "uSrcB");
            int driverLoc = _gl.GetUniformLocation(program, "uDriver");
            int rangeALoc = _gl.GetUniformLocation(program, "uRangeA");
            int rangeBLoc = _gl.GetUniformLocation(program, "uRangeB");
            int strengthLoc = _gl.GetUniformLocation(program, "uStrength");
            int outLoc = _gl.GetUniformLocation(program, "uOut");

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

                UploadCloudChannelUniforms(
                    _cloudNoiseDefinition.Shape, _cloudShapeSlotOrder,
                    channelCountLoc, opLoc, primLoc, freqLoc, octavesLoc, gainLoc, lacunarityLoc, weightsLoc,
                    srcALoc, srcBLoc, driverLoc, rangeALoc, rangeBLoc, strengthLoc, outLoc);

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

                UploadCloudChannelUniforms(
                    _cloudNoiseDefinition.Detail, _cloudDetailSlotOrder,
                    channelCountLoc, opLoc, primLoc, freqLoc, octavesLoc, gainLoc, lacunarityLoc, weightsLoc,
                    srcALoc, srcBLoc, driverLoc, rangeALoc, rangeBLoc, strengthLoc, outLoc);

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

        private enum CloudNoisePrimitive
        {
            Perlin,
            Worley
        }

        private static readonly string[] _cloudShapeSlotOrder = { "r", "g", "b", "a" };
        private static readonly string[] _cloudDetailSlotOrder = { "r", "g", "b" };
        private static readonly float[] _worleyDefaultWeights = { 0.625f, 0.25f, 0.125f };

        private void UploadCloudChannelUniforms(
            CloudNoiseTextureDef def,
            string[] slotOrder,
            int channelCountLoc,
            int opLoc,
            int primLoc,
            int freqLoc,
            int octavesLoc,
            int gainLoc,
            int lacunarityLoc,
            int weightsLoc,
            int srcALoc,
            int srcBLoc,
            int driverLoc,
            int rangeALoc,
            int rangeBLoc,
            int strengthLoc,
            int outLoc)
        {
            var ordered = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in def.Channels.Keys)
                TopoVisit(def, name, ordered, visited);

            int count = Math.Min(ordered.Count, 32);

            var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
                indexByName[ordered[i]] = i;

            int[] op = new int[32];
            int[] prim = new int[32];
            float[] freq = new float[32];
            int[] octaves = new int[32];
            float[] gain = new float[32];
            float[] lacunarity = new float[32];
            float[] weights = new float[256];
            int[] srcA = new int[32];
            int[] srcB = new int[32];
            int[] driver = new int[32];
            float[] rangeA = new float[32];
            float[] rangeB = new float[32];
            float[] strength = new float[32];
            int[] outIdx = new int[4];

            for (int i = 0; i < 32; i++)
            {
                srcA[i] = -1;
                srcB[i] = -1;
                driver[i] = -1;
            }
            for (int i = 0; i < 4; i++)
                outIdx[i] = -1;

            for (int i = 0; i < count; i++)
            {
                CloudNoiseChannel channel = def.Channels[ordered[i]];
                op[i] = (int)channel.Op;

                if (channel.Op == CloudNoiseOp.Primitive)
                {
                    prim[i] = channel.Primitive == CloudNoisePrimitive.Perlin ? 0 : 1;
                    freq[i] = channel.Freq;
                    octaves[i] = Math.Clamp(channel.Octaves, 1, 8);
                    gain[i] = channel.Gain;
                    lacunarity[i] = channel.Lacunarity;

                    for (int w = 0; w < 8; w++)
                    {
                        float v = 0f;
                        if (w < octaves[i])
                        {
                            if (channel.Weights != null && w < channel.Weights.Length)
                                v = channel.Weights[w];
                            else if (w < _worleyDefaultWeights.Length)
                                v = _worleyDefaultWeights[w];
                        }
                        weights[i * 8 + w] = v;
                    }
                }
                else
                {
                    if (channel.SourceA != null && indexByName.TryGetValue(channel.SourceA, out int a))
                        srcA[i] = a;
                    if (channel.SourceB != null && indexByName.TryGetValue(channel.SourceB, out int b))
                        srcB[i] = b;
                    if (channel.Driver != null && indexByName.TryGetValue(channel.Driver, out int d))
                        driver[i] = d;
                    rangeA[i] = channel.RangeMin;
                    rangeB[i] = channel.RangeMax;
                    strength[i] = channel.Strength;
                }
            }

            for (int s = 0; s < slotOrder.Length; s++)
            {
                if (def.Outputs.TryGetValue(slotOrder[s], out string? channelName) &&
                    indexByName.TryGetValue(channelName, out int index))
                    outIdx[s] = index;
            }

            _gl.Uniform1(channelCountLoc, count);

            for (int i = 0; i < 32; i++)
            {
                _gl.Uniform1(opLoc + i, op[i]);
                _gl.Uniform1(primLoc + i, prim[i]);
                _gl.Uniform1(freqLoc + i, freq[i]);
                _gl.Uniform1(octavesLoc + i, octaves[i]);
                _gl.Uniform1(gainLoc + i, gain[i]);
                _gl.Uniform1(lacunarityLoc + i, lacunarity[i]);
                _gl.Uniform1(srcALoc + i, srcA[i]);
                _gl.Uniform1(srcBLoc + i, srcB[i]);
                _gl.Uniform1(driverLoc + i, driver[i]);
                _gl.Uniform1(rangeALoc + i, rangeA[i]);
                _gl.Uniform1(rangeBLoc + i, rangeB[i]);
                _gl.Uniform1(strengthLoc + i, strength[i]);
            }
            for (int i = 0; i < 256; i++)
                _gl.Uniform1(weightsLoc + i, weights[i]);
            for (int i = 0; i < 4; i++)
                _gl.Uniform1(outLoc + i, outIdx[i]);
        }

        private static void TopoVisit(
            CloudNoiseTextureDef def,
            string name,
            List<string> ordered,
            HashSet<string> visited)
        {
            if (visited.Contains(name))
                return;
            visited.Add(name);

            if (def.Channels.TryGetValue(name, out CloudNoiseChannel? channel))
            {
                if (channel.SourceA != null && def.Channels.ContainsKey(channel.SourceA))
                    TopoVisit(def, channel.SourceA, ordered, visited);
                if (channel.SourceB != null && def.Channels.ContainsKey(channel.SourceB))
                    TopoVisit(def, channel.SourceB, ordered, visited);
                if (channel.Driver != null && def.Channels.ContainsKey(channel.Driver))
                    TopoVisit(def, channel.Driver, ordered, visited);
            }

            ordered.Add(name);
        }

        internal static bool TryValidateNoiseFieldFile(string filePath, out string error)
        {
            error = "";
            try
            {
                string json = File.ReadAllText(filePath);
                CloudNoiseDefinition.Parse(json);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool EnsureCloudNoiseDefinition()
        {
            if (_cloudNoiseDefinition != null)
                return true;

            string filePath = "";
            foreach (string candidate in Program._noiseFieldFileRegistry.Values)
            {
                filePath = candidate;
                break;
            }

            if (string.IsNullOrEmpty(filePath))
            {
                Program.EnsureAllNoiseFieldKeysDiscovered();
                foreach (string candidate in Program._noiseFieldFileRegistry.Values)
                {
                    filePath = candidate;
                    break;
                }
            }

            if (string.IsNullOrEmpty(filePath))
            {
                Console.WriteLine("[!] No noise field definition found in asset paths.");
                return false;
            }

            string json = File.ReadAllText(filePath);
            _cloudNoiseDefinition = CloudNoiseDefinition.Parse(json);
            return true;
        }

        private enum CloudNoiseOp
        {
            Primitive,
            Mix,
            Multiply,
            Smoothstep,
            Product,
            Linear,
            Step,
            Add,
            Sub,
            Max,
            Min,
            Clamp,
            Abs
        }

        private sealed class CloudNoiseChannel
        {
            public CloudNoiseOp Op { get; init; } = CloudNoiseOp.Primitive;
            public CloudNoisePrimitive Primitive { get; init; }
            public float Freq { get; init; }
            public int Octaves { get; init; }
            public float Gain { get; init; } = 0.5f;
            public float Lacunarity { get; init; } = 2.0f;
            public float[]? Weights { get; init; }
            public string? SourceA { get; init; }
            public string? SourceB { get; init; }
            public string? Driver { get; init; }
            public float RangeMin { get; init; }
            public float RangeMax { get; init; }
            public float Strength { get; init; } = 1f;
        }

        private sealed class CloudNoiseTextureDef
        {
            public int Size { get; init; }
            public IReadOnlyDictionary<string, CloudNoiseChannel> Channels { get; init; }
            public IReadOnlyDictionary<string, string> Outputs { get; init; }
        }

        private sealed class CloudNoiseDefinition
        {
            public CloudNoiseTextureDef Shape { get; init; }
            public CloudNoiseTextureDef Detail { get; init; }

            public static CloudNoiseDefinition Parse(string json)
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                return new CloudNoiseDefinition
                {
                    Shape = ParseTextureDef(root, "shape"),
                    Detail = ParseTextureDef(root, "detail")
                };
            }

            private static CloudNoiseTextureDef ParseTextureDef(JsonElement root, string name)
            {
                if (!root.TryGetProperty(name, out JsonElement element) ||
                    element.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException($"[X] Cloud noise definition missing '{name}'.");

                int size = element.GetProperty("size").GetInt32();

                if (!element.TryGetProperty("channels", out JsonElement channelsElement) ||
                    channelsElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException($"[X] Cloud noise definition '{name}' missing 'channels'.");

                var channels = new Dictionary<string, CloudNoiseChannel>();
                var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in channelsElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                        outputs[property.Name] = property.Value.GetString() ?? "";
                    else
                        channels[property.Name] = ParseChannel(property.Value);
                }

                if (element.TryGetProperty("outputs", out JsonElement outputsElement) &&
                    outputsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in outputsElement.EnumerateObject())
                        outputs[property.Name] = property.Value.GetString() ?? "";
                }
                else
                {
                    foreach (string slot in new[] { "r", "g", "b", "a" })
                    {
                        if (channels.ContainsKey(slot) && !outputs.ContainsKey(slot))
                            outputs[slot] = slot;
                    }
                }

                return new CloudNoiseTextureDef
                {
                    Size = size,
                    Channels = channels,
                    Outputs = outputs
                };
            }

            private static CloudNoiseChannel ParseChannel(JsonElement element)
            {
                if (element.TryGetProperty("primitive", out JsonElement primitiveElement))
                {
                    string primitiveName = primitiveElement.GetString() ?? "";
                    CloudNoisePrimitive primitive = primitiveName.ToLowerInvariant() switch
                    {
                        "perlin" => CloudNoisePrimitive.Perlin,
                        "worley" => CloudNoisePrimitive.Worley,
                        _ => throw new ArgumentException($"[X] Unknown cloud noise primitive '{primitiveName}'.")
                    };

                    float freq = element.GetProperty("freq").GetSingle();
                    int octaves = element.GetProperty("octaves").GetInt32();

                    float gain = 0.5f;
                    float lacunarity = 2.0f;
                    if (element.TryGetProperty("gain", out JsonElement gainElement))
                        gain = gainElement.GetSingle();
                    if (element.TryGetProperty("lacunarity", out JsonElement lacunarityElement))
                        lacunarity = lacunarityElement.GetSingle();

                    float[]? weights = null;
                    if (element.TryGetProperty("weights", out JsonElement weightsElement) &&
                        weightsElement.ValueKind == JsonValueKind.Array)
                    {
                        weights = new float[weightsElement.GetArrayLength()];
                        int index = 0;
                        foreach (JsonElement item in weightsElement.EnumerateArray())
                            weights[index++] = item.GetSingle();
                    }

                    return new CloudNoiseChannel
                    {
                        Primitive = primitive,
                        Freq = freq,
                        Octaves = octaves,
                        Gain = gain,
                        Lacunarity = lacunarity,
                        Weights = weights
                    };
                }

                if (element.TryGetProperty("mix", out JsonElement mixElement) &&
                    mixElement.ValueKind == JsonValueKind.Object)
                {
                    JsonElement channels = mixElement.GetProperty("channels");
                    float[] range = ReadRange(mixElement);
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Mix,
                        SourceA = channels[0].GetString() ?? "",
                        SourceB = channels[1].GetString() ?? "",
                        Driver = mixElement.GetProperty("driver").GetString() ?? "",
                        RangeMin = range[0],
                        RangeMax = range[1]
                    };
                }

                if (element.TryGetProperty("multiply", out JsonElement multiplyElement) &&
                    multiplyElement.ValueKind == JsonValueKind.Object)
                {
                    float strength = 1f;
                    if (multiplyElement.TryGetProperty("strength", out JsonElement strengthElement))
                        strength = strengthElement.GetSingle();
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Multiply,
                        SourceA = multiplyElement.GetProperty("channel").GetString() ?? "",
                        SourceB = multiplyElement.GetProperty("by").GetString() ?? "",
                        Strength = strength
                    };
                }

                if (element.TryGetProperty("smoothstep", out JsonElement smoothstepElement) &&
                    smoothstepElement.ValueKind == JsonValueKind.Object)
                {
                    float[] range = ReadRange(smoothstepElement);
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Smoothstep,
                        SourceA = smoothstepElement.GetProperty("channel").GetString() ?? "",
                        RangeMin = range[0],
                        RangeMax = range[1]
                    };
                }

                if (element.TryGetProperty("product", out JsonElement productElement) &&
                    productElement.ValueKind == JsonValueKind.Array)
                {
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Product,
                        SourceA = productElement[0].GetString() ?? "",
                        SourceB = productElement[1].GetString() ?? ""
                    };
                }

                if (element.TryGetProperty("linear", out JsonElement linearElement) &&
                    linearElement.ValueKind == JsonValueKind.Object)
                {
                    float scale = 1f;
                    float offset = 0f;
                    if (linearElement.TryGetProperty("scale", out JsonElement scaleElement))
                        scale = scaleElement.GetSingle();
                    if (linearElement.TryGetProperty("offset", out JsonElement offsetElement))
                        offset = offsetElement.GetSingle();
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Linear,
                        SourceA = linearElement.GetProperty("channel").GetString() ?? "",
                        RangeMin = scale,
                        RangeMax = offset
                    };
                }

                if (element.TryGetProperty("step", out JsonElement stepElement) &&
                    stepElement.ValueKind == JsonValueKind.Object)
                {
                    float threshold = 0f;
                    if (stepElement.TryGetProperty("threshold", out JsonElement thresholdElement))
                        threshold = thresholdElement.GetSingle();
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Step,
                        SourceA = stepElement.GetProperty("channel").GetString() ?? "",
                        RangeMin = threshold
                    };
                }

                if (element.TryGetProperty("add", out JsonElement addElement) &&
                    addElement.ValueKind == JsonValueKind.Array)
                {
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Add,
                        SourceA = addElement[0].GetString() ?? "",
                        SourceB = addElement[1].GetString() ?? ""
                    };
                }

                if (element.TryGetProperty("sub", out JsonElement subElement) &&
                    subElement.ValueKind == JsonValueKind.Array)
                {
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Sub,
                        SourceA = subElement[0].GetString() ?? "",
                        SourceB = subElement[1].GetString() ?? ""
                    };
                }

                if (element.TryGetProperty("max", out JsonElement maxElement) &&
                    maxElement.ValueKind == JsonValueKind.Array)
                {
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Max,
                        SourceA = maxElement[0].GetString() ?? "",
                        SourceB = maxElement[1].GetString() ?? ""
                    };
                }

                if (element.TryGetProperty("min", out JsonElement minElement) &&
                    minElement.ValueKind == JsonValueKind.Array)
                {
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Min,
                        SourceA = minElement[0].GetString() ?? "",
                        SourceB = minElement[1].GetString() ?? ""
                    };
                }

                if (element.TryGetProperty("clamp", out JsonElement clampElement) &&
                    clampElement.ValueKind == JsonValueKind.Object)
                {
                    float[] range = ReadRange(clampElement);
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Clamp,
                        SourceA = clampElement.GetProperty("channel").GetString() ?? "",
                        RangeMin = range[0],
                        RangeMax = range[1]
                    };
                }

                if (element.TryGetProperty("abs", out JsonElement absElement) &&
                    absElement.ValueKind == JsonValueKind.Object)
                {
                    return new CloudNoiseChannel
                    {
                        Op = CloudNoiseOp.Abs,
                        SourceA = absElement.GetProperty("channel").GetString() ?? ""
                    };
                }

                throw new ArgumentException("[X] Unknown cloud noise channel expression.");
            }

            private static float[] ReadRange(JsonElement parent)
            {
                if (!parent.TryGetProperty("range", out JsonElement rangeElement) ||
                    rangeElement.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("[X] Cloud noise expression missing 'range'.");

                return new[]
                {
                    rangeElement[0].GetSingle(),
                    rangeElement[1].GetSingle()
                };
            }
        }
    }
}
