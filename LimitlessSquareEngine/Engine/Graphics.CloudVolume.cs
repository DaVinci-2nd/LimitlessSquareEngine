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

        private const string _uniformCloudShadowCube = "uCloudShadowCube";
        private const string _uniformCloudShadowStrength = "uCloudShadowStrength";
        private const string _uniformCloudShadowPlanetCenter = "uCloudShadowPlanetCenter";
        private const string _uniformCloudShadowSlant = "uCloudShadowSlant";
        private const string _uniformCloudShadowTexelSize = "uCloudShadowTexelSize";
        private const string _uniformCloudShadowAffectsAmbient = "uCloudShadowAffectsAmbient";
        private const string _uniformCloudTime = "uCloudTime";
        private const string _uniformCloudPlanetCenter = "uCloudPlanetCenter";
        private const string _uniformCloudPlanetCenterWorld = "uCloudPlanetCenterWorld";
        private const string _uniformCloudCameraWorldPos = "uCloudCameraWorldPos";
        private const string _uniformCloudInvViewProjection = "uCloudInvViewProjection";
        private const string _uniformCloudViewProjection = "uCloudViewProjection";
        private const string _uniformCloudFarDepth = "uCloudFarDepth";
        private const string _uniformCloudTanHalfFov = "uCloudTanHalfFov";
        private const string _uniformCloudViewportHeight = "uCloudViewportHeight";

        private static readonly float[] _cloudFullscreenVertices =
        {
            -1f, -1f, 0f, 1f, 1f, 1f, 1f, 0f, 0f,
             1f, -1f, 0f, 1f, 1f, 1f, 1f, 1f, 0f,
             1f,  1f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
             1f,  1f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
            -1f,  1f, 0f, 1f, 1f, 1f, 1f, 0f, 1f,
            -1f, -1f, 0f, 1f, 1f, 1f, 1f, 0f, 0f
        };

        private const int _cloudShadowResolution = 512;
        private const int _cloudShadowTextureUnit = 14;

        private bool _cloudSupportInitialized = false;
        private uint _cloudShadowCube = 0;
        private uint _cloudDummyCube = 0;
        private uint _cloudShadowFramebuffer = 0;
        private uint _cloudFullscreenVao = 0;
        private uint _cloudShadowProgram = 0;

        private int _cloudShadowFaceLoc = -1;
        private int _cloudShadowResolutionLoc = -1;
        private int _cloudShadowPlanetRadiusLoc = -1;
        private int _cloudShadowBaseAltitudeLoc = -1;
        private int _cloudShadowThicknessLoc = -1;
        private int _cloudShadowCoverageLoc = -1;
        private int _cloudShadowWindLoc = -1;
        private int _cloudShadowTimeLoc = -1;
        private int _cloudShadowNoiseScaleLoc = -1;
        private int _cloudShadowCoverageScaleLoc = -1;
        private int _cloudShadowExtinctionLoc = -1;
        private int _cloudShadowDetailLoc = -1;
        private int _cloudShadowWarpLoc = -1;
        private int _cloudShadowStepSizeLoc = -1;
        private int _cloudShadowMaxStepsLoc = -1;

        private long _cloudBakedFrameId = -1;
        private double _cloudTimeSeconds = 0.0;
        private long _frameId = 0;
        private readonly System.Diagnostics.Stopwatch _cloudTimeStopwatch = System.Diagnostics.Stopwatch.StartNew();

        private sealed class CloudShadowBatchData
        {
            public bool Valid;
            public float Strength;
            public bool AffectsAmbient;
            public Vector3 PlanetCenterCameraRelative;
            public Double3 PlanetCenterWorld;
            public float PlanetRadius;
            public float SlantFactor;
            public float TexelSize;
        }

        private readonly Dictionary<long, CloudShadowBatchData> _cloudShadowBatchCache = new();

        private void InitializeCloudSupportResources()
        {
            if (_cloudSupportInitialized)
                return;

            _cloudDummyCube = CreateCloudDummyCubeTexture();
            _cloudFullscreenVao = _gl.GenVertexArray();

            _cloudShadowCube = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureCubeMap, _cloudShadowCube);

            byte[] emptyFace = new byte[_cloudShadowResolution * _cloudShadowResolution * 2];
            const int GL_RG8 = 0x822B;
            const int GL_RG = 0x8227;

            for (int face = 0; face < 6; face++)
            {
                TextureTarget faceTarget = (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face);
                _gl.TexImage2D(
                    faceTarget,
                    0,
                    (InternalFormat)GL_RG8,
                    (uint)_cloudShadowResolution,
                    (uint)_cloudShadowResolution,
                    0,
                    (PixelFormat)GL_RG,
                    PixelType.UnsignedByte,
                    (ReadOnlySpan<byte>)emptyFace);
            }

            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);

            _cloudShadowFramebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudShadowFramebuffer);
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.TextureCubeMapPositiveX,
                _cloudShadowCube,
                0);
            _gl.DrawBuffer(GLEnum.ColorAttachment0);
            _gl.ReadBuffer(GLEnum.None);

            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
                throw new Exception($"[X] Cloud shadow framebuffer incomplete: {status}");

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            _cloudShadowProgram = ResolveShaderProgramOrFallback("Shaders/Builtin/CloudShadowCube");

            _cloudShadowFaceLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudShadowFace");
            _cloudShadowResolutionLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudShadowResolution");
            _cloudShadowPlanetRadiusLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uPlanetRadius");
            _cloudShadowBaseAltitudeLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudBaseAltitude");
            _cloudShadowThicknessLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudThickness");
            _cloudShadowCoverageLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudCoverage");
            _cloudShadowWindLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudWind");
            _cloudShadowTimeLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudTime");
            _cloudShadowNoiseScaleLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudNoiseScale");
            _cloudShadowCoverageScaleLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudCoverageScale");
            _cloudShadowExtinctionLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudExtinction");
            _cloudShadowDetailLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudDetailStrength");
            _cloudShadowWarpLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudWarpStrength");
            _cloudShadowStepSizeLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudStepSize");
            _cloudShadowMaxStepsLoc = _gl.GetUniformLocation(_cloudShadowProgram, "uCloudMaxSteps");

            _cloudSupportInitialized = true;
        }

        private uint CreateCloudDummyCubeTexture()
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureCubeMap, texture);

            byte[] whitePixel = new byte[] { 255, 255, 255, 255 };
            for (int face = 0; face < 6; face++)
            {
                TextureTarget faceTarget = (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face);
                _gl.TexImage2D(
                    faceTarget,
                    0,
                    InternalFormat.Rgba,
                    1,
                    1,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    (ReadOnlySpan<byte>)whitePixel);
            }

            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
            return texture;
        }

        private void PrepareCloudShadowBatch(in RenderCommand batchAnchor, List<RenderCommand> batchCommands)
        {
            MaterialData? cloudMaterial = null;
            RenderCommand cloudCmd = default;
            bool found = false;

            for (int i = 0; i < batchCommands.Count; i++)
            {
                RenderCommand cmd = batchCommands[i];
                if (cmd.Material != null && cmd.Material.IsCloud)
                {
                    cloudMaterial = cmd.Material;
                    cloudCmd = cmd;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                _cloudShadowBatchCache.Remove(batchAnchor.BatchId);
                return;
            }

            float planetRadius = ReadMaterialFloat(cloudMaterial, "uPlanetRadius", 6371000f);
            float baseAltitude = ReadMaterialFloat(cloudMaterial, "uCloudBaseAltitude", 1500f);
            float thickness = ReadMaterialFloat(cloudMaterial, "uCloudThickness", 2000f);
            float coverage = ReadMaterialFloat(cloudMaterial, "uCloudCoverage", 0.5f);
            float strength = ReadMaterialFloat(cloudMaterial, "uCloudShadowStrength", 0.5f);
            float noiseScale = ReadMaterialFloat(cloudMaterial, "uCloudNoiseScale", 0.002f);
            float coverageScale = ReadMaterialFloat(cloudMaterial, "uCloudCoverageScale", 0.00025f);
            float extinction = ReadMaterialFloat(cloudMaterial, "uCloudExtinction", 0.0002f);
            float detailStrength = ReadMaterialFloat(cloudMaterial, "uCloudDetailStrength", 0.25f);
            float warpStrength = ReadMaterialFloat(cloudMaterial, "uCloudWarpStrength", 0.35f);
            float stepSize = ReadMaterialFloat(cloudMaterial, "uCloudStepSize", 280f);
            int maxSteps = ReadCloudMaterialInt(cloudMaterial, "uCloudMaxSteps", 64);
            int affectsAmbient = ReadCloudMaterialInt(cloudMaterial, "uCloudShadowAffectsAmbient", 1);

            ReadCloudMaterialWind(cloudMaterial, out float windX, out float windY);

            Vector3 planetCenterCameraRelative = new Vector3(
                cloudCmd.Model.M41,
                cloudCmd.Model.M42,
                cloudCmd.Model.M43);

            CloudShadowBatchData data = new CloudShadowBatchData
            {
                Valid = true,
                Strength = strength,
                AffectsAmbient = affectsAmbient != 0,
                PlanetCenterCameraRelative = planetCenterCameraRelative,
                PlanetCenterWorld = cloudCmd.CloudPlanetCenterWorldPosition,
                PlanetRadius = planetRadius,
                SlantFactor = (baseAltitude + thickness * 0.5f) / MathF.Max(planetRadius, 1f),
                TexelSize = MathF.PI * 0.5f * (planetRadius + baseAltitude + thickness) / _cloudShadowResolution
            };

            _cloudShadowBatchCache[batchAnchor.BatchId] = data;

            if (_cloudBakedFrameId == _frameId)
                return;

            _cloudBakedFrameId = _frameId;
            InitializeCloudSupportResources();
            BakeCloudShadowCube(
                data,
                baseAltitude,
                thickness,
                coverage,
                windX,
                windY,
                noiseScale,
                coverageScale,
                extinction,
                detailStrength,
                warpStrength,
                stepSize,
                maxSteps);
        }

        private void BakeCloudShadowCube(
            CloudShadowBatchData data,
            float baseAltitude,
            float thickness,
            float coverage,
            float windX,
            float windY,
            float noiseScale,
            float coverageScale,
            float extinction,
            float detailStrength,
            float warpStrength,
            float stepSize,
            int maxSteps)
        {
            _gl.GetInteger(GLEnum.FramebufferBinding, out int previousFramebuffer);
            int[] previousViewport = new int[4];
            _gl.GetInteger(GLEnum.Viewport, previousViewport);
            _gl.GetInteger(GLEnum.CurrentProgram, out int previousProgram);
            _gl.GetInteger(GLEnum.ActiveTexture, out int previousActiveTexture);
            _gl.GetInteger(GLEnum.ArrayBufferBinding, out int previousArrayBuffer);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int previousVertexArray);
            bool previousBlendEnabled = _gl.IsEnabled(GLEnum.Blend);
            bool previousDepthTestEnabled = _gl.IsEnabled(GLEnum.DepthTest);
            bool previousCullFaceEnabled = _gl.IsEnabled(GLEnum.CullFace);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudShadowFramebuffer);
            _gl.Viewport(0, 0, (uint)_cloudShadowResolution, (uint)_cloudShadowResolution);
            _gl.Disable(GLEnum.Blend);
            _gl.Disable(GLEnum.DepthTest);
            _gl.Disable(GLEnum.CullFace);
            _gl.ColorMask(true, true, true, true);

            _currentProgram = _cloudShadowProgram;
            _gl.UseProgram(_cloudShadowProgram);

            _gl.BindVertexArray(_cloudFullscreenVao);

            _gl.Uniform1(_cloudShadowResolutionLoc, _cloudShadowResolution);
            _gl.Uniform1(_cloudShadowPlanetRadiusLoc, data.PlanetRadius);
            _gl.Uniform1(_cloudShadowBaseAltitudeLoc, baseAltitude);
            _gl.Uniform1(_cloudShadowThicknessLoc, thickness);
            _gl.Uniform1(_cloudShadowCoverageLoc, coverage);
            _gl.Uniform2(_cloudShadowWindLoc, windX, windY);
            _gl.Uniform1(_cloudShadowTimeLoc, (float)_cloudTimeSeconds);
            _gl.Uniform1(_cloudShadowNoiseScaleLoc, noiseScale);
            _gl.Uniform1(_cloudShadowCoverageScaleLoc, coverageScale);
            _gl.Uniform1(_cloudShadowExtinctionLoc, extinction);
            _gl.Uniform1(_cloudShadowDetailLoc, detailStrength);
            _gl.Uniform1(_cloudShadowWarpLoc, warpStrength);
            _gl.Uniform1(_cloudShadowStepSizeLoc, stepSize);
            _gl.Uniform1(_cloudShadowMaxStepsLoc, maxSteps);

            for (int face = 0; face < 6; face++)
            {
                TextureTarget faceTarget = (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face);

                _gl.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    faceTarget,
                    _cloudShadowCube,
                    0);

                _gl.Uniform1(_cloudShadowFaceLoc, face);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }

            _gl.BindTexture(TextureTarget.TextureCubeMap, _cloudShadowCube);
            _gl.GenerateMipmap(TextureTarget.TextureCubeMap);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFramebuffer);
            _gl.Viewport(previousViewport[0], previousViewport[1], (uint)previousViewport[2], (uint)previousViewport[3]);

            if (previousBlendEnabled)
                _gl.Enable(GLEnum.Blend);
            else
                _gl.Disable(GLEnum.Blend);

            if (previousDepthTestEnabled)
                _gl.Enable(GLEnum.DepthTest);
            else
                _gl.Disable(GLEnum.DepthTest);

            if (previousCullFaceEnabled)
                _gl.Enable(GLEnum.CullFace);
            else
                _gl.Disable(GLEnum.CullFace);

            _gl.ActiveTexture((TextureUnit)previousActiveTexture);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)previousArrayBuffer);
            _gl.BindVertexArray((uint)previousVertexArray);

            if (previousProgram != 0)
            {
                _currentProgram = (uint)previousProgram;
                _gl.UseProgram((uint)previousProgram);
            }
        }

        private void ApplyCloudSupportUniforms(in RenderCommand cmd)
        {
            ProgramUniformLocationCache loc = GetProgramLocationCache(_currentProgram);

            bool hasCloudBatch =
                _cloudShadowBatchCache.TryGetValue(cmd.BatchId, out CloudShadowBatchData? cloudBatch) &&
                cloudBatch.Valid;

            if (!hasCloudBatch)
                cloudBatch = null;

            if (loc.CloudShadowCube != -1)
            {
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + _cloudShadowTextureUnit));
                _gl.BindTexture(
                    TextureTarget.TextureCubeMap,
                    hasCloudBatch ? _cloudShadowCube : _cloudDummyCube);
                _gl.Uniform1(loc.CloudShadowCube, _cloudShadowTextureUnit);
            }

            if (loc.CloudShadowStrength != -1)
                _gl.Uniform1(loc.CloudShadowStrength, hasCloudBatch && cloudBatch != null ? cloudBatch.Strength : 0f);

            if (loc.CloudShadowPlanetCenter != -1)
            {
                _gl.Uniform3(
                    loc.CloudShadowPlanetCenter,
                    hasCloudBatch && cloudBatch != null ? cloudBatch.PlanetCenterCameraRelative.X : 0f,
                    hasCloudBatch && cloudBatch != null ? cloudBatch.PlanetCenterCameraRelative.Y : 0f,
                    hasCloudBatch && cloudBatch != null ? cloudBatch.PlanetCenterCameraRelative.Z : 0f);
            }

            if (loc.CloudShadowSlant != -1)
                _gl.Uniform1(loc.CloudShadowSlant, hasCloudBatch && cloudBatch != null ? cloudBatch.SlantFactor : 0f);

            if (loc.CloudShadowTexelSize != -1)
                _gl.Uniform1(loc.CloudShadowTexelSize, hasCloudBatch && cloudBatch != null ? cloudBatch.TexelSize : 9770f);

            if (loc.CloudShadowAffectsAmbient != -1)
                _gl.Uniform1(loc.CloudShadowAffectsAmbient, hasCloudBatch && cloudBatch != null && cloudBatch.AffectsAmbient ? 1 : 0);

            if (loc.CloudTime != -1)
                _gl.Uniform1(loc.CloudTime, (float)_cloudTimeSeconds);

            if (loc.CloudPlanetCenter != -1)
            {
                _gl.Uniform3(
                    loc.CloudPlanetCenter,
                    hasCloudBatch && cloudBatch != null ? cloudBatch.PlanetCenterCameraRelative.X : 0f,
                    hasCloudBatch && cloudBatch != null ? cloudBatch.PlanetCenterCameraRelative.Y : 0f,
                    hasCloudBatch && cloudBatch != null ? cloudBatch.PlanetCenterCameraRelative.Z : 0f);
            }

            if (loc.CloudPlanetCenterWorld != -1)
            {
                _gl.Uniform3(
                    loc.CloudPlanetCenterWorld,
                    hasCloudBatch && cloudBatch != null ? cloudBatch.PlanetCenterWorld.X : 0.0,
                    hasCloudBatch && cloudBatch != null ? cloudBatch.PlanetCenterWorld.Y : 0.0,
                    hasCloudBatch && cloudBatch != null ? cloudBatch.PlanetCenterWorld.Z : 0.0);
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
                _gl.Uniform1(loc.CloudFarDepth, cmd.UseReverseZ ? 0f : 1f);

            if (loc.CloudTanHalfFov != -1)
            {
                float tanHalfFov = MathF.Abs(cmd.Projection.M22) > 0.000001f
                    ? 1f / cmd.Projection.M22
                    : 1f;
                _gl.Uniform1(loc.CloudTanHalfFov, tanHalfFov);
            }

            if (loc.CloudViewportHeight != -1)
                _gl.Uniform1(loc.CloudViewportHeight, MathF.Max(1f, cmd.ViewportHeight));
        }

        private static void PrepareCloudRenderCommand(MaterialData material, ref RenderCommand cmd)
        {
            if (material == null || !material.IsCloud)
                return;

            cmd.CullMode = RenderCullMode.Both;
        }

        private void ReleaseCloudSupportResources()
        {
            _cloudShadowBatchCache.Clear();
            _cloudBakedFrameId = -1;

            if (_cloudShadowCube != 0)
            {
                _gl.DeleteTexture(_cloudShadowCube);
                _cloudShadowCube = 0;
            }

            if (_cloudDummyCube != 0)
            {
                _gl.DeleteTexture(_cloudDummyCube);
                _cloudDummyCube = 0;
            }

            if (_cloudShadowFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_cloudShadowFramebuffer);
                _cloudShadowFramebuffer = 0;
            }

            if (_cloudFullscreenVao != 0)
            {
                _gl.DeleteVertexArray(_cloudFullscreenVao);
                _cloudFullscreenVao = 0;
            }

            _cloudSupportInitialized = false;
        }

        private static int ReadCloudMaterialInt(MaterialData material, string name, int defaultValue)
        {
            if (!TryGetMaterialProperty(material, name, out JsonElement value))
                return defaultValue;

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt32(),
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                _ => defaultValue
            };
        }

        private void ReadCloudMaterialWind(MaterialData material, out float x, out float y)
        {
            x = 0f;
            y = 0f;

            if (!TryGetMaterialProperty(material, "uCloudWind", out JsonElement value))
                return;

            if (!TryReadNumericArray(value, out double[] numbers) || numbers.Length < 2)
                return;

            x = (float)numbers[0];
            y = (float)numbers[1];
        }
    }
}
