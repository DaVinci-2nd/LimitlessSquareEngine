using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using LimitlessSquareEngine.Engine;

namespace LimitlessSquareEngine
{
    internal partial class Graphics
    {
        private const string _cloudShaderKey = "Shaders/Builtin/Cloud";

        private const string _uniformCloudTime = "uCloudTime";
        private const string _uniformCloudPlanetCenter = "uCloudPlanetCenter";
        private const string _uniformCloudPlanetCenterWorld = "uCloudPlanetCenterWorld";
        private const string _uniformCloudCameraWorldPos = "uCloudCameraWorldPos";
        private const string _uniformCloudInvViewProjection = "uCloudInvViewProjection";
        private const string _uniformCloudViewProjection = "uCloudViewProjection";
        private const string _uniformCloudFarDepth = "uCloudFarDepth";
        private const string _uniformCloudTanHalfFov = "uCloudTanHalfFov";
        private const string _uniformCloudViewportHeight = "uCloudViewportHeight";
        private const string _uniformCloudLayerNear = "uCloudLayerNear";
        private const string _uniformCloudLayerFar = "uCloudLayerFar";
        private const string _uniformCloudShapeNoise = "uCloudShapeNoise";
        private const string _uniformCloudDetailNoise = "uCloudDetailNoise";

        private static readonly float[] _cloudFullscreenVertices =
        {
            -1f, -1f, 0f, 1f, 1f, 1f, 1f, 0f, 0f,
             1f, -1f, 0f, 1f, 1f, 1f, 1f, 1f, 0f,
             1f,  1f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
             1f,  1f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
            -1f,  1f, 0f, 1f, 1f, 1f, 1f, 0f, 1f,
            -1f, -1f, 0f, 1f, 1f, 1f, 1f, 0f, 0f
        };

        private bool _cloudSupportInitialized = false;
        private uint _cloudFullscreenVao = 0;

        private uint _cloudDownsampleFramebuffer = 0;
        private uint _cloudDownsampleTexture = 0;
        private int _cloudDownsampleWidth = 0;
        private int _cloudDownsampleHeight = 0;
        private uint _cloudCompositeProgram = 0;
        private int _cloudCompositeTextureLoc = -1;
        private readonly List<RenderCommand> _cloudCommandsScratch = new List<RenderCommand>();

        private const string _cloudCompositeVertexSource = @"#version 430 core
out vec2 vUv;
void main()
{
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

        private const string _cloudCompositeFragmentSource = @"#version 430 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uCloudTexture;
void main()
{
    FragColor = texture(uCloudTexture, vUv);
}";

        private double _cloudTimeSeconds = 0.0;
        private long _frameId = 0;
        private readonly System.Diagnostics.Stopwatch _cloudTimeStopwatch = System.Diagnostics.Stopwatch.StartNew();

        private void InitializeCloudSupportResources()
        {
            if (_cloudSupportInitialized)
                return;

            _cloudFullscreenVao = _gl.GenVertexArray();

            _cloudSupportInitialized = true;
        }

        private void ApplyCloudSupportUniforms(in RenderCommand cmd)
        {
            ProgramUniformLocationCache loc = GetProgramLocationCache(_currentProgram);

            if (loc.CloudTime != -1)
                _gl.Uniform1(loc.CloudTime, (float)_cloudTimeSeconds);

            if (loc.CloudPlanetCenter != -1)
            {
                _gl.Uniform3(
                    loc.CloudPlanetCenter,
                    cmd.Model.M41,
                    cmd.Model.M42,
                    cmd.Model.M43);
            }

            if (loc.CloudPlanetCenterWorld != -1)
            {
                _gl.Uniform3(
                    loc.CloudPlanetCenterWorld,
                    cmd.CloudPlanetCenterWorldPosition.X,
                    cmd.CloudPlanetCenterWorldPosition.Y,
                    cmd.CloudPlanetCenterWorldPosition.Z);
            }

            if (loc.CloudCameraWorldPos != -1)
            {
                _gl.Uniform3(
                    loc.CloudCameraWorldPos,
                    cmd.CameraWorldPosition.X,
                    cmd.CameraWorldPosition.Y,
                    cmd.CameraWorldPosition.Z);
            }

            if (loc.CloudInvViewProjection != -1)
            {
                Matrix4x4 viewProjection = cmd.View * cmd.Projection;
                if (Matrix4x4.Invert(viewProjection, out Matrix4x4 invViewProjection))
                    SetMatrixUniform(loc.CloudInvViewProjection, invViewProjection);
                else
                    SetMatrixUniform(loc.CloudInvViewProjection, Matrix4x4.Identity);
            }

            if (loc.CloudViewProjection != -1)
                SetMatrixUniform(loc.CloudViewProjection, cmd.View * cmd.Projection);

            if (loc.CloudFarDepth != -1)
                _gl.Uniform1(loc.CloudFarDepth, cmd.UseReverseZ ? -1f : 1f);

            if (loc.CloudTanHalfFov != -1)
            {
                float tanHalfFov = MathF.Abs(cmd.Projection.M22) > 0.000001f
                    ? 1f / cmd.Projection.M22
                    : 1f;
                _gl.Uniform1(loc.CloudTanHalfFov, tanHalfFov);
            }

            if (loc.CloudViewportHeight != -1)
                _gl.Uniform1(loc.CloudViewportHeight, MathF.Max(1f, cmd.ViewportHeight));

            if (loc.CloudLayerNear != -1)
                _gl.Uniform1(loc.CloudLayerNear, cmd.CloudLayerNear);

            if (loc.CloudLayerFar != -1)
                _gl.Uniform1(loc.CloudLayerFar, cmd.CloudLayerFar);

            if (loc.CloudShapeNoise != -1 || loc.CloudDetailNoise != -1)
            {
                EnsureCloudNoiseTextures();

                _gl.UseProgram(_currentProgram);
                _gl.Viewport(
                    cmd.ViewportX,
                    cmd.ViewportY,
                    (uint)Math.Max(1, cmd.ViewportWidth),
                    (uint)Math.Max(1, cmd.ViewportHeight));
                BindCommandGeometry(cmd);

                if (loc.CloudShapeNoise != -1 && _cloudNoiseShapeTexture != 0)
                {
                    const int shapeNoiseUnit = 14;
                    _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + shapeNoiseUnit));
                    _gl.BindTexture(TextureTarget.Texture3D, _cloudNoiseShapeTexture);
                    _gl.Uniform1(loc.CloudShapeNoise, shapeNoiseUnit);
                }

                if (loc.CloudDetailNoise != -1 && _cloudNoiseDetailTexture != 0)
                {
                    const int detailNoiseUnit = 19;
                    _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + detailNoiseUnit));
                    _gl.BindTexture(TextureTarget.Texture3D, _cloudNoiseDetailTexture);
                    _gl.Uniform1(loc.CloudDetailNoise, detailNoiseUnit);
                }
            }
        }

        private static void PrepareCloudRenderCommand(MaterialData material, ref RenderCommand cmd)
        {
            if (material == null || !material.IsCloud)
                return;

            cmd.CullMode = RenderCullMode.Both;
        }

        private void EnsureCloudDownsampleResources(int width, int height)
        {
            if (_cloudDownsampleTexture != 0 &&
                _cloudDownsampleWidth == width &&
                _cloudDownsampleHeight == height)
                return;

            InitializeCloudSupportResources();

            if (_cloudDownsampleTexture != 0)
            {
                _gl.DeleteTexture(_cloudDownsampleTexture);
                _cloudDownsampleTexture = 0;
            }

            if (_cloudDownsampleFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_cloudDownsampleFramebuffer);
                _cloudDownsampleFramebuffer = 0;
            }

            _cloudDownsampleWidth = width;
            _cloudDownsampleHeight = height;

            _cloudDownsampleTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _cloudDownsampleTexture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                ReadOnlySpan<byte>.Empty);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            _cloudDownsampleFramebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudDownsampleFramebuffer);
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _cloudDownsampleTexture,
                0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        private void EnsureCloudCompositeProgram()
        {
            if (_cloudCompositeProgram != 0)
                return;

            uint vs = CompileShader(ShaderType.VertexShader, _cloudCompositeVertexSource);
            uint fs = CompileShader(ShaderType.FragmentShader, _cloudCompositeFragmentSource);

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
                Console.WriteLine($"[X] Cloud composite shader link failed: {infoLog}");
                return;
            }

            _gl.DetachShader(program, vs);
            _gl.DetachShader(program, fs);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);

            _cloudCompositeProgram = program;
            _cloudCompositeTextureLoc = _gl.GetUniformLocation(program, "uCloudTexture");
        }

        private void ExecuteCloudDownsampleAndComposite(in RenderCommand first)
        {
            int cloudWidth = Math.Max(1, first.ViewportWidth / 4);
            int cloudHeight = Math.Max(1, first.ViewportHeight / 4);

            EnsureCloudDownsampleResources(cloudWidth, cloudHeight);
            EnsureCloudCompositeProgram();

            _gl.GetInteger(GLEnum.FramebufferBinding, out int previousFbo);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudDownsampleFramebuffer);
            _gl.Viewport(0, 0, (uint)cloudWidth, (uint)cloudHeight);
            _gl.Disable(GLEnum.DepthTest);
            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.One, GLEnum.OneMinusSrcAlpha);
            _gl.ClearColor(0f, 0f, 0f, 0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            foreach (RenderCommand source in _cloudCommandsScratch)
            {
                RenderCommand cmd = source;
                cmd.ViewportX = 0;
                cmd.ViewportY = 0;
                cmd.ViewportWidth = cloudWidth;
                cmd.ViewportHeight = cloudHeight;
                ExecuteCommand(cmd);
            }

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFbo);
            _gl.Viewport(
                first.ViewportX,
                first.ViewportY,
                (uint)Math.Max(1, first.ViewportWidth),
                (uint)Math.Max(1, first.ViewportHeight));
            _gl.Disable(GLEnum.DepthTest);
            _gl.DepthMask(false);
            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.One, GLEnum.OneMinusSrcAlpha);

            _gl.UseProgram(_cloudCompositeProgram);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _cloudDownsampleTexture);
            _gl.Uniform1(_cloudCompositeTextureLoc, 0);
            _gl.BindVertexArray(_cloudFullscreenVao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindVertexArray(0);
        }

        private void ReleaseCloudSupportResources()
        {
            if (_cloudFullscreenVao != 0)
            {
                _gl.DeleteVertexArray(_cloudFullscreenVao);
                _cloudFullscreenVao = 0;
            }

            if (_cloudCompositeProgram != 0)
            {
                _gl.DeleteProgram(_cloudCompositeProgram);
                _cloudCompositeProgram = 0;
            }

            if (_cloudDownsampleTexture != 0)
            {
                _gl.DeleteTexture(_cloudDownsampleTexture);
                _cloudDownsampleTexture = 0;
            }

            if (_cloudDownsampleFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_cloudDownsampleFramebuffer);
                _cloudDownsampleFramebuffer = 0;
            }

            ReleaseCloudNoiseTextures();

            _cloudSupportInitialized = false;
        }
    }
}
