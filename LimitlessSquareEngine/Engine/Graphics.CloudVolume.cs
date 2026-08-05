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

                if (loc.CloudShapeNoise != -1)
                {
                    const int shapeNoiseUnit = 14;
                    _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + shapeNoiseUnit));
                    _gl.BindTexture(TextureTarget.Texture3D, _cloudNoiseShapeTexture);
                    _gl.Uniform1(loc.CloudShapeNoise, shapeNoiseUnit);
                }

                if (loc.CloudDetailNoise != -1)
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

        private void ReleaseCloudSupportResources()
        {
            if (_cloudFullscreenVao != 0)
            {
                _gl.DeleteVertexArray(_cloudFullscreenVao);
                _cloudFullscreenVao = 0;
            }

            ReleaseCloudNoiseTextures();

            _cloudSupportInitialized = false;
        }
    }
}
