using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using LimitlessSquareEngine.Engine;

namespace LimitlessSquareEngine
{
    internal partial class Graphics
    {
        private const string _cloudShadowAtlasShaderKey = "Shaders/Builtin/CloudShadowAtlas";
        private const string _cloudShadowNearShaderKey = "Shaders/Builtin/CloudShadowNear";

        private const string _uniformCloudShadowAtlasSunDir = "uCloudShadowSunDir";
        private const string _uniformCloudShadowBasisX = "uCloudShadowBasisX";
        private const string _uniformCloudShadowBasisY = "uCloudShadowBasisY";
        private const string _uniformCloudShadowMap = "uCloudShadowMap";
        private const string _uniformCloudShadowPlanetCenterRel = "uCloudShadowPlanetCenterRel";
        private const string _uniformCloudShadowParamsA = "uCloudShadowParamsA";

        private const string _uniformCloudShadowNearMap = "uCloudShadowNearMap";
        private const string _uniformCloudShadowNearParamsB = "uCloudShadowNearParamsB";
        private const string _uniformCloudShadowNearMap1 = "uCloudShadowNearMap1";
        private const string _uniformCloudShadowNearParamsB1 = "uCloudShadowNearParamsB1";
        private const string _uniformCloudShadowNearMap2 = "uCloudShadowNearMap2";
        private const string _uniformCloudShadowNearParamsB2 = "uCloudShadowNearParamsB2";
        private const string _uniformCloudShadowNearCenterRel = "uCloudShadowNearCenterRel";
        private const string _uniformCloudShadowNearAxisX = "uCloudShadowNearAxisX";
        private const string _uniformCloudShadowNearAxisY = "uCloudShadowNearAxisY";

        private const int _cloudShadowAtlasWidth = 1024;
        private const int _cloudShadowAtlasHeight = 768;

        private const int _cloudShadowNearLevelCount = 3;
        private static readonly uint[] _cloudShadowNearSizes = { 128u, 256u, 512u };
        private static readonly float[] _cloudShadowNearHalfExtents = { 10000f, 100000f, 1000000f };
        private const float _cloudShadowNearBlendStart = 0.72f;
        private const float _cloudShadowNearBlendEnd = 0.96f;

        private uint _cloudShadowAtlasTexture = 0;
        private uint _cloudShadowAtlasFramebuffer = 0;
        private uint _cloudShadowAtlasProgram = 0;

        private readonly uint[] _cloudShadowNearTextures = new uint[_cloudShadowNearLevelCount];
        private readonly uint[] _cloudShadowNearFramebuffers = new uint[_cloudShadowNearLevelCount];
        private uint _cloudShadowNearProgram = 0;
        private Vector3 _cloudShadowNearAnchorRel = Vector3.Zero;
        private Vector3 _cloudShadowNearAxisX = new Vector3(1f, 0f, 0f);
        private Vector3 _cloudShadowNearAxisY = new Vector3(0f, 1f, 0f);
        private bool _cloudShadowNearValid = false;
        private readonly bool[] _cloudShadowNearLevelValid = new bool[_cloudShadowNearLevelCount];
        private readonly bool[] _cloudShadowNearDistanceValid = new bool[_cloudShadowNearLevelCount];
        private Double3 _cloudShadowNearStableCameraWorld = Double3.Zero;
        private bool _cloudShadowNearStableCameraInitialized = false;

        private bool _cloudShadowAtlasDoneThisFrame = false;
        private bool _cloudShadowAtlasReadbackDone = false;

        private sealed class ActiveCloudShadowInfo
        {
            public bool Valid;
            public Double3 PlanetCenterWorld;
            public Vector3 SunDir;
            public MaterialData? CloudMaterial;
        }

        private readonly ActiveCloudShadowInfo _activeCloudShadow = new();

        private static Vector3 FlipCloudShadowZ(Vector3 v)
        {
            return new Vector3(v.X, v.Y, -v.Z);
        }

        private Double3 GetCloudShadowNearStableCamera(Double3 cameraWorldPosition, float gridWorldSize)
        {
            if (gridWorldSize <= 0.0000001f)
                return cameraWorldPosition;

            double grid = gridWorldSize;

            if (!_cloudShadowNearStableCameraInitialized)
            {
                _cloudShadowNearStableCameraWorld = new Double3(
                    Math.Round(cameraWorldPosition.X / grid, MidpointRounding.AwayFromZero) * grid,
                    Math.Round(cameraWorldPosition.Y / grid, MidpointRounding.AwayFromZero) * grid,
                    Math.Round(cameraWorldPosition.Z / grid, MidpointRounding.AwayFromZero) * grid);

                _cloudShadowNearStableCameraInitialized = true;
            }
            else
            {
                double dx = cameraWorldPosition.X - _cloudShadowNearStableCameraWorld.X;
                double dy = cameraWorldPosition.Y - _cloudShadowNearStableCameraWorld.Y;
                double dz = cameraWorldPosition.Z - _cloudShadowNearStableCameraWorld.Z;

                if (Math.Abs(dx) > grid * 0.5)
                    _cloudShadowNearStableCameraWorld.X = Math.Round(cameraWorldPosition.X / grid, MidpointRounding.AwayFromZero) * grid;

                if (Math.Abs(dy) > grid * 0.5)
                    _cloudShadowNearStableCameraWorld.Y = Math.Round(cameraWorldPosition.Y / grid, MidpointRounding.AwayFromZero) * grid;

                if (Math.Abs(dz) > grid * 0.5)
                    _cloudShadowNearStableCameraWorld.Z = Math.Round(cameraWorldPosition.Z / grid, MidpointRounding.AwayFromZero) * grid;
            }

            return _cloudShadowNearStableCameraWorld;
        }

        private void EnsureCloudShadowNearResources()
        {
            if (_cloudShadowNearProgram == 0)
                _cloudShadowNearProgram = ResolveShaderProgramOrFallback(_cloudShadowNearShaderKey);

            for (int level = 0; level < _cloudShadowNearLevelCount; level++)
            {
                if (_cloudShadowNearTextures[level] != 0 && _cloudShadowNearFramebuffers[level] != 0)
                    continue;

                if (_cloudShadowNearTextures[level] != 0)
                {
                    _gl.DeleteTexture(_cloudShadowNearTextures[level]);
                    _cloudShadowNearTextures[level] = 0;
                }

                if (_cloudShadowNearFramebuffers[level] != 0)
                {
                    _gl.DeleteFramebuffer(_cloudShadowNearFramebuffers[level]);
                    _cloudShadowNearFramebuffers[level] = 0;
                }

                _cloudShadowNearTextures[level] = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture2D, _cloudShadowNearTextures[level]);
                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba8,
                    _cloudShadowNearSizes[level],
                    _cloudShadowNearSizes[level],
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    ReadOnlySpan<byte>.Empty);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                _cloudShadowNearFramebuffers[level] = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudShadowNearFramebuffers[level]);
                _gl.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D,
                    _cloudShadowNearTextures[level],
                    0);

                GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                _gl.BindTexture(TextureTarget.Texture2D, 0);

                if (status != GLEnum.FramebufferComplete)
                {
                    Console.WriteLine($"[!] Cloud shadow near framebuffer incomplete: {status}");
                    _gl.DeleteFramebuffer(_cloudShadowNearFramebuffers[level]);
                    _gl.DeleteTexture(_cloudShadowNearTextures[level]);
                    _cloudShadowNearFramebuffers[level] = 0;
                    _cloudShadowNearTextures[level] = 0;
                }
            }
        }

        private void EnsureCloudShadowAtlasResources()
        {
            if (_cloudShadowAtlasProgram == 0)
                _cloudShadowAtlasProgram = ResolveShaderProgramOrFallback(_cloudShadowAtlasShaderKey);

            if (_cloudShadowAtlasTexture != 0 && _cloudShadowAtlasFramebuffer != 0)
                return;

            if (_cloudShadowAtlasTexture != 0)
            {
                _gl.DeleteTexture(_cloudShadowAtlasTexture);
                _cloudShadowAtlasTexture = 0;
            }

            if (_cloudShadowAtlasFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_cloudShadowAtlasFramebuffer);
                _cloudShadowAtlasFramebuffer = 0;
            }

            _cloudShadowAtlasTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _cloudShadowAtlasTexture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                _cloudShadowAtlasWidth,
                _cloudShadowAtlasHeight,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                ReadOnlySpan<byte>.Empty);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.GenerateMipmap(TextureTarget.Texture2D);

            _cloudShadowAtlasFramebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudShadowAtlasFramebuffer);
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _cloudShadowAtlasTexture,
                0);

            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            if (status != GLEnum.FramebufferComplete)
            {
                Console.WriteLine($"[!] Cloud shadow atlas framebuffer incomplete: {status}");
                _gl.DeleteFramebuffer(_cloudShadowAtlasFramebuffer);
                _gl.DeleteTexture(_cloudShadowAtlasTexture);
                _cloudShadowAtlasFramebuffer = 0;
                _cloudShadowAtlasTexture = 0;
            }
        }

        private SceneRenderLightSnapshot? FindMainCloudShadowSun(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId) ||
                !_sceneLightCache.TryGetValue(sceneId, out var lightMap))
                return null;

            return lightMap.Values
                .Where(l =>
                    l.Active &&
                    l.Visible &&
                    l.Settings.LightMode == (int)LightKind.Directional)
                .OrderBy(l => l.ObjectId)
                .FirstOrDefault();
        }

        private bool HasVisibleCloudShadowReceiverInDistance(List<RenderCommand> batchCommands, float maxDistance)
        {
            for (int i = 0; i < batchCommands.Count; i++)
            {
                RenderCommand candidate = batchCommands[i];

                if (candidate.IsSkybox)
                    continue;

                if (candidate.Pass != RenderPass.Scene)
                    continue;

                if (ShouldCullCommandByCameraFrustum(candidate))
                    continue;

                if (CommandIntersectsCameraDepthRange(candidate, 0f, maxDistance))
                    return true;
            }

            return false;
        }

        private void PrepareCloudShadowAtlasPass(in RenderCommand layer0Anchor, List<RenderCommand> batchCommands)
        {
            if (_cloudShadowAtlasDoneThisFrame)
                return;

            RenderCommand cloudSource = default;
            bool hasCloudSource = false;
            for (int i = 0; i < batchCommands.Count; i++)
            {
                RenderCommand candidate = batchCommands[i];
                if (candidate.Material != null && candidate.Material.IsCloud)
                {
                    cloudSource = candidate;
                    hasCloudSource = true;
                    break;
                }
            }

            SceneRenderLightSnapshot? sun = FindMainCloudShadowSun(layer0Anchor.SceneId);

            if (!hasCloudSource || sun == null || cloudSource.Material == null)
            {
                _activeCloudShadow.Valid = false;
                _activeCloudShadow.CloudMaterial = null;
                _cloudShadowAtlasDoneThisFrame = true;
                return;
            }

            EnsureCloudShadowAtlasResources();

            if (_cloudShadowAtlasTexture == 0 || _cloudShadowAtlasFramebuffer == 0 || _cloudShadowAtlasProgram == 0)
            {
                _activeCloudShadow.Valid = false;
                _activeCloudShadow.CloudMaterial = null;
                _cloudShadowAtlasDoneThisFrame = true;
                return;
            }

            Vector3 sunDir = FlipCloudShadowZ(-ExtractDirectionalLightDirection(sun));

            _activeCloudShadow.Valid = true;
            _activeCloudShadow.PlanetCenterWorld = cloudSource.CloudPlanetCenterWorldPosition;
            _activeCloudShadow.SunDir = sunDir;
            _activeCloudShadow.CloudMaterial = cloudSource.Material;

            _cloudShadowNearDistanceValid[0] = HasVisibleCloudShadowReceiverInDistance(batchCommands, _cloudShadowNearHalfExtents[0]);
            _cloudShadowNearDistanceValid[1] = HasVisibleCloudShadowReceiverInDistance(batchCommands, _cloudShadowNearHalfExtents[1]);
            _cloudShadowNearDistanceValid[2] = HasVisibleCloudShadowReceiverInDistance(batchCommands, _cloudShadowNearHalfExtents[2]);

            RenderCloudShadowAtlas(cloudSource, sunDir);
            RenderCloudShadowNear(cloudSource, sunDir);
            RenderCloudShadowNear1(cloudSource, sunDir);
            RenderCloudShadowNear2(cloudSource, sunDir);

            _cloudShadowNearValid =
                _cloudShadowNearLevelValid[0] ||
                _cloudShadowNearLevelValid[1] ||
                _cloudShadowNearLevelValid[2];

            _cloudShadowAtlasDoneThisFrame = true;
        }

        private void RenderCloudShadowNear(in RenderCommand cloudSource, Vector3 sunDir)
        {
            bool nearValid = false;

            if (cloudSource.Material != null)
            {
                float planetRadius = ReadMaterialFloat(cloudSource.Material, "uPlanetRadius", 6371000f);
                Double3 stableCamera = GetCloudShadowNearStableCamera(cloudSource.CameraWorldPosition, 10000f);
                Vector3 cameraLocal = new Vector3(
                    (float)(_activeCloudShadow.PlanetCenterWorld.X - stableCamera.X),
                    (float)(_activeCloudShadow.PlanetCenterWorld.Y - stableCamera.Y),
                    (float)(_activeCloudShadow.PlanetCenterWorld.Z - stableCamera.Z));
                if (_cloudShadowNearDistanceValid[0])
                {
                    int level = 0;
                    EnsureCloudShadowNearResources();

                    if (_cloudShadowNearTextures[0] != 0 && _cloudShadowNearFramebuffers[0] != 0 && _cloudShadowNearProgram != 0)
                    {
                        float planetRelLen = MathF.Max(cameraLocal.Length(), planetRadius * 0.999f);
                        Vector3 upDir = cameraLocal / planetRelLen;
                        Vector3 refUp = MathF.Abs(upDir.Y) > 0.999f
                            ? new Vector3(0f, 0f, 1f)
                            : new Vector3(0f, 1f, 0f);
                        Vector3 axisX = Vector3.Normalize(Vector3.Cross(refUp, upDir));
                        Vector3 axisY = Vector3.Normalize(Vector3.Cross(upDir, axisX));

                        Vector3 anchorRel = -upDir * planetRadius;

                        _cloudShadowNearAnchorRel = anchorRel;
                        _cloudShadowNearAxisX = axisX;
                        _cloudShadowNearAxisY = axisY;

                        uint previousProgram = _currentProgram;
                        RenderSpace previousRenderSpace = _activeRenderSpace;
                        Matrix4x4 previousModel = _activeModelMatrix;
                        Matrix4x4 previousView = _activeViewMatrix;
                        Matrix4x4 previousProjection = _activeProjectionMatrix;

                        _gl.GetInteger(GLEnum.FramebufferBinding, out int previousFbo);
                        int[] previousViewport = new int[4];
                        _gl.GetInteger(GLEnum.Viewport, previousViewport);

                        EnsureCloudNoiseTextures();

                        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudShadowNearFramebuffers[0]);
                        _gl.Viewport(0, 0, _cloudShadowNearSizes[level], _cloudShadowNearSizes[level]);
                        _gl.Disable(GLEnum.DepthTest);
                        _gl.Disable(GLEnum.CullFace);
                        _gl.Disable(GLEnum.Blend);
                        _gl.Disable(GLEnum.ScissorTest);
                        _gl.ClearColor(0f, 0f, 0f, 0f);
                        _gl.Clear(ClearBufferMask.ColorBufferBit);

                        _currentProgram = _cloudShadowNearProgram;
                        _gl.UseProgram(_cloudShadowNearProgram);

                        _activeRenderSpace = cloudSource.RenderSpace;
                        _activeModelMatrix = cloudSource.Model;
                        _activeViewMatrix = cloudSource.View;
                        _activeProjectionMatrix = cloudSource.Projection;

                        ApplyCullMode(RenderCullMode.Both);
                        BindCommandGeometry(cloudSource);

                        if (cloudSource.Material != null)
                            ApplySceneMaterial(cloudSource.Material, cloudSource);
                        else
                            ApplyRenderUniforms(false);

                        _gl.Viewport(0, 0, _cloudShadowNearSizes[level], _cloudShadowNearSizes[level]);

                        ProgramUniformLocationCache loc = GetProgramLocationCache(_cloudShadowNearProgram);

                        if (loc.CloudShadowSunDir != -1)
                            _gl.Uniform3(loc.CloudShadowSunDir, sunDir.X, sunDir.Y, sunDir.Z);

                        if (loc.CloudShadowNearAnchorRel != -1)
                            _gl.Uniform3(loc.CloudShadowNearAnchorRel, anchorRel.X, anchorRel.Y, anchorRel.Z);

                        if (loc.CloudShadowNearAxisX != -1)
                            _gl.Uniform3(loc.CloudShadowNearAxisX, axisX.X, axisX.Y, axisX.Z);

                        if (loc.CloudShadowNearAxisY != -1)
                            _gl.Uniform3(loc.CloudShadowNearAxisY, axisY.X, axisY.Y, axisY.Z);

                        if (loc.CloudShadowNearHalfExtent != -1)
                            _gl.Uniform1(loc.CloudShadowNearHalfExtent, _cloudShadowNearHalfExtents[0]);

                        SubmitDrawArrays(PrimitiveType.Triangles, 0, (uint)(cloudSource.Vertices.Length / cloudSource.VertexStrideFloats));

                        _gl.ActiveTexture(TextureUnit.Texture0);
                        _gl.BindVertexArray(0);
                        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

                        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFbo);
                        _gl.Viewport(
                            Math.Max(0, previousViewport[0]),
                            Math.Max(0, previousViewport[1]),
                            (uint)Math.Max(1, previousViewport[2]),
                            (uint)Math.Max(1, previousViewport[3]));

                        _gl.Enable(GLEnum.DepthTest);
                        _gl.Enable(GLEnum.Blend);
                        _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

                        _currentProgram = previousProgram;
                        _gl.UseProgram(previousProgram);

                        _activeRenderSpace = previousRenderSpace;
                        _activeModelMatrix = previousModel;
                        _activeViewMatrix = previousView;
                        _activeProjectionMatrix = previousProjection;

                        nearValid = true;
                    }
                }
            }

            _cloudShadowNearLevelValid[0] = nearValid;
        }

        private void RenderCloudShadowNear1(in RenderCommand cloudSource, Vector3 sunDir)
        {
            bool nearValid = false;

            if (cloudSource.Material != null)
            {
                float planetRadius = ReadMaterialFloat(cloudSource.Material, "uPlanetRadius", 6371000f);
                Double3 stableCamera = GetCloudShadowNearStableCamera(cloudSource.CameraWorldPosition, 10000f);
                Vector3 cameraLocal = new Vector3(
                    (float)(_activeCloudShadow.PlanetCenterWorld.X - stableCamera.X),
                    (float)(_activeCloudShadow.PlanetCenterWorld.Y - stableCamera.Y),
                    (float)(_activeCloudShadow.PlanetCenterWorld.Z - stableCamera.Z));
                if (_cloudShadowNearDistanceValid[1])
                {
                    int level = 1;
                    EnsureCloudShadowNearResources();

                    if (_cloudShadowNearTextures[1] != 0 && _cloudShadowNearFramebuffers[1] != 0 && _cloudShadowNearProgram != 0)
                    {
                        float planetRelLen = MathF.Max(cameraLocal.Length(), planetRadius * 0.999f);
                        Vector3 upDir = cameraLocal / planetRelLen;
                        Vector3 refUp = MathF.Abs(upDir.Y) > 0.999f
                            ? new Vector3(0f, 0f, 1f)
                            : new Vector3(0f, 1f, 0f);
                        Vector3 axisX = Vector3.Normalize(Vector3.Cross(refUp, upDir));
                        Vector3 axisY = Vector3.Normalize(Vector3.Cross(upDir, axisX));

                        Vector3 anchorRel = -upDir * planetRadius;

                        _cloudShadowNearAnchorRel = anchorRel;
                        _cloudShadowNearAxisX = axisX;
                        _cloudShadowNearAxisY = axisY;

                        uint previousProgram = _currentProgram;
                        RenderSpace previousRenderSpace = _activeRenderSpace;
                        Matrix4x4 previousModel = _activeModelMatrix;
                        Matrix4x4 previousView = _activeViewMatrix;
                        Matrix4x4 previousProjection = _activeProjectionMatrix;

                        _gl.GetInteger(GLEnum.FramebufferBinding, out int previousFbo);
                        int[] previousViewport = new int[4];
                        _gl.GetInteger(GLEnum.Viewport, previousViewport);

                        EnsureCloudNoiseTextures();

                        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudShadowNearFramebuffers[1]);
                        _gl.Viewport(0, 0, _cloudShadowNearSizes[level], _cloudShadowNearSizes[level]);
                        _gl.Disable(GLEnum.DepthTest);
                        _gl.Disable(GLEnum.CullFace);
                        _gl.Disable(GLEnum.Blend);
                        _gl.Disable(GLEnum.ScissorTest);
                        _gl.ClearColor(0f, 0f, 0f, 0f);
                        _gl.Clear(ClearBufferMask.ColorBufferBit);

                        _currentProgram = _cloudShadowNearProgram;
                        _gl.UseProgram(_cloudShadowNearProgram);

                        _activeRenderSpace = cloudSource.RenderSpace;
                        _activeModelMatrix = cloudSource.Model;
                        _activeViewMatrix = cloudSource.View;
                        _activeProjectionMatrix = cloudSource.Projection;

                        ApplyCullMode(RenderCullMode.Both);
                        BindCommandGeometry(cloudSource);

                        if (cloudSource.Material != null)
                            ApplySceneMaterial(cloudSource.Material, cloudSource);
                        else
                            ApplyRenderUniforms(false);

                        _gl.Viewport(0, 0, _cloudShadowNearSizes[level], _cloudShadowNearSizes[level]);

                        ProgramUniformLocationCache loc = GetProgramLocationCache(_cloudShadowNearProgram);

                        if (loc.CloudShadowSunDir != -1)
                            _gl.Uniform3(loc.CloudShadowSunDir, sunDir.X, sunDir.Y, sunDir.Z);

                        if (loc.CloudShadowNearAnchorRel != -1)
                            _gl.Uniform3(loc.CloudShadowNearAnchorRel, anchorRel.X, anchorRel.Y, anchorRel.Z);

                        if (loc.CloudShadowNearAxisX != -1)
                            _gl.Uniform3(loc.CloudShadowNearAxisX, axisX.X, axisX.Y, axisX.Z);

                        if (loc.CloudShadowNearAxisY != -1)
                            _gl.Uniform3(loc.CloudShadowNearAxisY, axisY.X, axisY.Y, axisY.Z);

                        if (loc.CloudShadowNearHalfExtent != -1)
                            _gl.Uniform1(loc.CloudShadowNearHalfExtent, _cloudShadowNearHalfExtents[1]);

                        SubmitDrawArrays(PrimitiveType.Triangles, 0, (uint)(cloudSource.Vertices.Length / cloudSource.VertexStrideFloats));

                        _gl.ActiveTexture(TextureUnit.Texture0);
                        _gl.BindVertexArray(0);
                        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

                        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFbo);
                        _gl.Viewport(
                            Math.Max(0, previousViewport[0]),
                            Math.Max(0, previousViewport[1]),
                            (uint)Math.Max(1, previousViewport[2]),
                            (uint)Math.Max(1, previousViewport[3]));

                        _gl.Enable(GLEnum.DepthTest);
                        _gl.Enable(GLEnum.Blend);
                        _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

                        _currentProgram = previousProgram;
                        _gl.UseProgram(previousProgram);

                        _activeRenderSpace = previousRenderSpace;
                        _activeModelMatrix = previousModel;
                        _activeViewMatrix = previousView;
                        _activeProjectionMatrix = previousProjection;

                        nearValid = true;
                    }
                }
            }

            _cloudShadowNearLevelValid[1] = nearValid;
        }

        private void RenderCloudShadowNear2(in RenderCommand cloudSource, Vector3 sunDir)
        {
            bool nearValid = false;

            if (cloudSource.Material != null)
            {
                float planetRadius = ReadMaterialFloat(cloudSource.Material, "uPlanetRadius", 6371000f);
                Double3 stableCamera = GetCloudShadowNearStableCamera(cloudSource.CameraWorldPosition, 10000f);
                Vector3 cameraLocal = new Vector3(
                    (float)(_activeCloudShadow.PlanetCenterWorld.X - stableCamera.X),
                    (float)(_activeCloudShadow.PlanetCenterWorld.Y - stableCamera.Y),
                    (float)(_activeCloudShadow.PlanetCenterWorld.Z - stableCamera.Z));
                if (_cloudShadowNearDistanceValid[2])
                {
                    int level = 2;
                    EnsureCloudShadowNearResources();

                    if (_cloudShadowNearTextures[2] != 0 && _cloudShadowNearFramebuffers[2] != 0 && _cloudShadowNearProgram != 0)
                    {
                        float planetRelLen = MathF.Max(cameraLocal.Length(), planetRadius * 0.999f);
                        Vector3 upDir = cameraLocal / planetRelLen;
                        Vector3 refUp = MathF.Abs(upDir.Y) > 0.999f
                            ? new Vector3(0f, 0f, 1f)
                            : new Vector3(0f, 1f, 0f);
                        Vector3 axisX = Vector3.Normalize(Vector3.Cross(refUp, upDir));
                        Vector3 axisY = Vector3.Normalize(Vector3.Cross(upDir, axisX));

                        Vector3 anchorRel = -upDir * planetRadius;

                        _cloudShadowNearAnchorRel = anchorRel;
                        _cloudShadowNearAxisX = axisX;
                        _cloudShadowNearAxisY = axisY;

                        uint previousProgram = _currentProgram;
                        RenderSpace previousRenderSpace = _activeRenderSpace;
                        Matrix4x4 previousModel = _activeModelMatrix;
                        Matrix4x4 previousView = _activeViewMatrix;
                        Matrix4x4 previousProjection = _activeProjectionMatrix;

                        _gl.GetInteger(GLEnum.FramebufferBinding, out int previousFbo);
                        int[] previousViewport = new int[4];
                        _gl.GetInteger(GLEnum.Viewport, previousViewport);

                        EnsureCloudNoiseTextures();

                        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudShadowNearFramebuffers[2]);
                        _gl.Viewport(0, 0, _cloudShadowNearSizes[level], _cloudShadowNearSizes[level]);
                        _gl.Disable(GLEnum.DepthTest);
                        _gl.Disable(GLEnum.CullFace);
                        _gl.Disable(GLEnum.Blend);
                        _gl.Disable(GLEnum.ScissorTest);
                        _gl.ClearColor(0f, 0f, 0f, 0f);
                        _gl.Clear(ClearBufferMask.ColorBufferBit);

                        _currentProgram = _cloudShadowNearProgram;
                        _gl.UseProgram(_cloudShadowNearProgram);

                        _activeRenderSpace = cloudSource.RenderSpace;
                        _activeModelMatrix = cloudSource.Model;
                        _activeViewMatrix = cloudSource.View;
                        _activeProjectionMatrix = cloudSource.Projection;

                        ApplyCullMode(RenderCullMode.Both);
                        BindCommandGeometry(cloudSource);

                        if (cloudSource.Material != null)
                            ApplySceneMaterial(cloudSource.Material, cloudSource);
                        else
                            ApplyRenderUniforms(false);

                        _gl.Viewport(0, 0, _cloudShadowNearSizes[level], _cloudShadowNearSizes[level]);

                        ProgramUniformLocationCache loc = GetProgramLocationCache(_cloudShadowNearProgram);

                        if (loc.CloudShadowSunDir != -1)
                            _gl.Uniform3(loc.CloudShadowSunDir, sunDir.X, sunDir.Y, sunDir.Z);

                        if (loc.CloudShadowNearAnchorRel != -1)
                            _gl.Uniform3(loc.CloudShadowNearAnchorRel, anchorRel.X, anchorRel.Y, anchorRel.Z);

                        if (loc.CloudShadowNearAxisX != -1)
                            _gl.Uniform3(loc.CloudShadowNearAxisX, axisX.X, axisX.Y, axisX.Z);

                        if (loc.CloudShadowNearAxisY != -1)
                            _gl.Uniform3(loc.CloudShadowNearAxisY, axisY.X, axisY.Y, axisY.Z);

                        if (loc.CloudShadowNearHalfExtent != -1)
                            _gl.Uniform1(loc.CloudShadowNearHalfExtent, _cloudShadowNearHalfExtents[2]);

                        SubmitDrawArrays(PrimitiveType.Triangles, 0, (uint)(cloudSource.Vertices.Length / cloudSource.VertexStrideFloats));

                        _gl.ActiveTexture(TextureUnit.Texture0);
                        _gl.BindVertexArray(0);
                        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

                        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFbo);
                        _gl.Viewport(
                            Math.Max(0, previousViewport[0]),
                            Math.Max(0, previousViewport[1]),
                            (uint)Math.Max(1, previousViewport[2]),
                            (uint)Math.Max(1, previousViewport[3]));

                        _gl.Enable(GLEnum.DepthTest);
                        _gl.Enable(GLEnum.Blend);
                        _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

                        _currentProgram = previousProgram;
                        _gl.UseProgram(previousProgram);

                        _activeRenderSpace = previousRenderSpace;
                        _activeModelMatrix = previousModel;
                        _activeViewMatrix = previousView;
                        _activeProjectionMatrix = previousProjection;

                        nearValid = true;
                    }
                }
            }

            _cloudShadowNearLevelValid[2] = nearValid;
        }

        private void RenderCloudShadowAtlas(in RenderCommand cloudSource, Vector3 sunDir)
        {
            uint previousProgram = _currentProgram;
            RenderSpace previousRenderSpace = _activeRenderSpace;
            Matrix4x4 previousModel = _activeModelMatrix;
            Matrix4x4 previousView = _activeViewMatrix;
            Matrix4x4 previousProjection = _activeProjectionMatrix;

            _gl.GetInteger(GLEnum.FramebufferBinding, out int previousFbo);
            int[] previousViewport = new int[4];
            _gl.GetInteger(GLEnum.Viewport, previousViewport);

            EnsureCloudNoiseTextures();

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudShadowAtlasFramebuffer);
            _gl.Viewport(0, 0, _cloudShadowAtlasWidth, _cloudShadowAtlasHeight);
            _gl.Disable(GLEnum.DepthTest);
            _gl.Disable(GLEnum.CullFace);
            _gl.Disable(GLEnum.Blend);
            _gl.Disable(GLEnum.ScissorTest);
            _gl.ClearColor(0f, 0f, 0f, 0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            _currentProgram = _cloudShadowAtlasProgram;
            _gl.UseProgram(_cloudShadowAtlasProgram);

            _activeRenderSpace = cloudSource.RenderSpace;
            _activeModelMatrix = cloudSource.Model;
            _activeViewMatrix = cloudSource.View;
            _activeProjectionMatrix = cloudSource.Projection;

            ApplyCullMode(RenderCullMode.Both);
            BindCommandGeometry(cloudSource);

            if (cloudSource.Material != null)
                ApplySceneMaterial(cloudSource.Material, cloudSource);
            else
                ApplyRenderUniforms(false);

            _gl.Viewport(0, 0, _cloudShadowAtlasWidth, _cloudShadowAtlasHeight);

            ProgramUniformLocationCache loc = GetProgramLocationCache(_cloudShadowAtlasProgram);

            float planetRadius = cloudSource.Material != null
                ? ReadMaterialFloat(cloudSource.Material, "uPlanetRadius", 6371000f)
                : 6371000f;
            float baseAltitude = cloudSource.Material != null
                ? ReadMaterialFloat(cloudSource.Material, "uCloudBaseAltitude", 1400f)
                : 1400f;
            float thickness = cloudSource.Material != null
                ? ReadMaterialFloat(cloudSource.Material, "uCloudThickness", 2600f)
                : 2600f;
            float topRadius = planetRadius + baseAltitude + thickness;

            if (loc.CloudShadowSunDir != -1)
                _gl.Uniform3(loc.CloudShadowSunDir, sunDir.X, sunDir.Y, sunDir.Z);

            Vector3 sunDirGpu = FlipCloudShadowZ(sunDir);
            Vector3 rayDirGpu = -sunDirGpu;
            BuildDirectionalLightBasis(rayDirGpu, out Vector3 right, out Vector3 up, out _);

            float uvWorldSize = topRadius * 2f;
            Vector3 basisX = right * uvWorldSize;
            Vector3 basisY = up * uvWorldSize;

            if (loc.CloudShadowBasisX != -1)
                _gl.Uniform3(loc.CloudShadowBasisX, basisX.X, basisX.Y, basisX.Z);

            if (loc.CloudShadowBasisY != -1)
                _gl.Uniform3(loc.CloudShadowBasisY, basisY.X, basisY.Y, basisY.Z);

            SubmitDrawArrays(PrimitiveType.Triangles, 0, (uint)(cloudSource.Vertices.Length / cloudSource.VertexStrideFloats));

            _gl.BindTexture(TextureTarget.Texture2D, _cloudShadowAtlasTexture);
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            if (!_cloudShadowAtlasReadbackDone)
            {
                _cloudShadowAtlasReadbackDone = true;
                byte[] pixels = new byte[_cloudShadowAtlasWidth * _cloudShadowAtlasHeight * 4];
                _gl.BindTexture(TextureTarget.Texture2D, _cloudShadowAtlasTexture);
                _gl.GetTexImage(GLEnum.Texture2D, 0, GLEnum.Rgba, GLEnum.UnsignedByte, (Span<byte>)pixels);
                int minV = 255;
                int maxV = 0;
                long sum = 0;
                int count = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int v = pixels[i];
                    if (v < minV)
                        minV = v;
                    if (v > maxV)
                        maxV = v;
                    sum += v;
                    count++;
                }
                Console.WriteLine($"[readback] atlas R min={minV} max={maxV} avg={sum / Math.Max(count, 1)}");
                _gl.BindTexture(TextureTarget.Texture2D, 0);
            }

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFbo);
            _gl.Viewport(
                Math.Max(0, previousViewport[0]),
                Math.Max(0, previousViewport[1]),
                (uint)Math.Max(1, previousViewport[2]),
                (uint)Math.Max(1, previousViewport[3]));

            _gl.Enable(GLEnum.DepthTest);
            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

            _currentProgram = previousProgram;
            _gl.UseProgram(previousProgram);

            _activeRenderSpace = previousRenderSpace;
            _activeModelMatrix = previousModel;
            _activeViewMatrix = previousView;
            _activeProjectionMatrix = previousProjection;
        }

        private void ApplyCloudShadowSupportUniforms(in RenderCommand cmd)
        {
            ProgramUniformLocationCache loc = GetProgramLocationCache(_currentProgram);
            if (loc.CloudShadowParamsA == -1 &&
                loc.CloudShadowMap == -1 &&
                loc.CloudShadowPlanetCenterRel == -1)
                return;

            bool enabled =
                _activeCloudShadow.Valid &&
                _activeCloudShadow.CloudMaterial != null &&
                _cloudShadowAtlasTexture != 0;

            Vector3 planetCenterRel = Vector3.Zero;

            if (enabled)
            {
                planetCenterRel = new Vector3(
                    (float)(_activeCloudShadow.PlanetCenterWorld.X - cmd.CameraWorldPosition.X),
                    (float)(_activeCloudShadow.PlanetCenterWorld.Y - cmd.CameraWorldPosition.Y),
                    (float)(_activeCloudShadow.PlanetCenterWorld.Z - cmd.CameraWorldPosition.Z));
            }

            if (loc.CloudShadowPlanetCenterRel != -1)
                _gl.Uniform3(loc.CloudShadowPlanetCenterRel, planetCenterRel.X, planetCenterRel.Y, planetCenterRel.Z);

            float uvWorldSize = 1f;
            if (enabled)
            {
                uvWorldSize = _activeCloudShadow.CloudMaterial != null
                    ? (ReadMaterialFloat(_activeCloudShadow.CloudMaterial, "uPlanetRadius", 6371000f)
                       + ReadMaterialFloat(_activeCloudShadow.CloudMaterial, "uCloudBaseAltitude", 1400f)
                       + ReadMaterialFloat(_activeCloudShadow.CloudMaterial, "uCloudThickness", 2600f)) * 2f
                    : 1f;

                Vector3 sunDirGpu = FlipCloudShadowZ(_activeCloudShadow.SunDir);
                Vector3 rayDirGpu = -sunDirGpu;
                BuildDirectionalLightBasis(rayDirGpu, out Vector3 axisX, out Vector3 axisY, out _);

                if (loc.CloudShadowBasisX != -1)
                    _gl.Uniform3(loc.CloudShadowBasisX, axisX.X, axisX.Y, axisX.Z);

                if (loc.CloudShadowBasisY != -1)
                    _gl.Uniform3(loc.CloudShadowBasisY, axisY.X, axisY.Y, axisY.Z);
            }

            if (loc.CloudShadowParamsA != -1)
                _gl.Uniform4(loc.CloudShadowParamsA, enabled ? 1f : 0f, 0f, 0f, uvWorldSize);

            if (loc.CloudShadowMap != -1)
            {
                const int cloudShadowMapUnit = 12;
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + cloudShadowMapUnit));
                if (_cloudShadowAtlasTexture != 0)
                    _gl.BindTexture(TextureTarget.Texture2D, _cloudShadowAtlasTexture);
                _gl.Uniform1(loc.CloudShadowMap, cloudShadowMapUnit);
            }

            if (loc.CloudShadowNearMap != -1)
            {
                const int cloudShadowNearUnit = 11;
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + cloudShadowNearUnit));
                if (_cloudShadowNearTextures[0] != 0)
                    _gl.BindTexture(TextureTarget.Texture2D, _cloudShadowNearTextures[0]);
                _gl.Uniform1(loc.CloudShadowNearMap, cloudShadowNearUnit);
            }

            if (loc.CloudShadowNearMap1 != -1)
            {
                const int cloudShadowNearUnit1 = 20;
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + cloudShadowNearUnit1));
                if (_cloudShadowNearTextures[1] != 0)
                    _gl.BindTexture(TextureTarget.Texture2D, _cloudShadowNearTextures[1]);
                _gl.Uniform1(loc.CloudShadowNearMap1, cloudShadowNearUnit1);
            }

            if (loc.CloudShadowNearMap2 != -1)
            {
                const int cloudShadowNearUnit2 = 21;
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + cloudShadowNearUnit2));
                if (_cloudShadowNearTextures[2] != 0)
                    _gl.BindTexture(TextureTarget.Texture2D, _cloudShadowNearTextures[2]);
                _gl.Uniform1(loc.CloudShadowNearMap2, cloudShadowNearUnit2);
            }

            if (loc.CloudShadowNearParamsB != -1)
                _gl.Uniform4(
                    loc.CloudShadowNearParamsB,
                    enabled && _cloudShadowNearLevelValid[0] ? 1f : 0f,
                    _cloudShadowNearBlendStart,
                    _cloudShadowNearBlendEnd,
                    _cloudShadowNearHalfExtents[0]);

            if (loc.CloudShadowNearParamsB1 != -1)
                _gl.Uniform4(
                    loc.CloudShadowNearParamsB1,
                    enabled && _cloudShadowNearLevelValid[1] ? 1f : 0f,
                    _cloudShadowNearBlendStart,
                    _cloudShadowNearBlendEnd,
                    _cloudShadowNearHalfExtents[1]);

            if (loc.CloudShadowNearParamsB2 != -1)
                _gl.Uniform4(
                    loc.CloudShadowNearParamsB2,
                    enabled && _cloudShadowNearLevelValid[2] ? 1f : 0f,
                    _cloudShadowNearBlendStart,
                    _cloudShadowNearBlendEnd,
                    _cloudShadowNearHalfExtents[2]);

            if (loc.CloudShadowNearCenterRel != -1)
            {
                Vector3 nearCenterRel = Vector3.Zero;
                if (enabled && _cloudShadowNearValid)
                {
                    nearCenterRel = _cloudShadowNearAnchorRel;
                }
                _gl.Uniform3(loc.CloudShadowNearCenterRel, nearCenterRel.X, nearCenterRel.Y, nearCenterRel.Z);
            }

            if (loc.CloudShadowNearAxisX != -1)
            {
                Vector3 nearAxisX = enabled && _cloudShadowNearValid ? _cloudShadowNearAxisX : new Vector3(1f, 0f, 0f);
                _gl.Uniform3(loc.CloudShadowNearAxisX, nearAxisX.X, nearAxisX.Y, nearAxisX.Z);
            }

            if (loc.CloudShadowNearAxisY != -1)
            {
                Vector3 nearAxisY = enabled && _cloudShadowNearValid ? _cloudShadowNearAxisY : new Vector3(0f, 1f, 0f);
                _gl.Uniform3(loc.CloudShadowNearAxisY, nearAxisY.X, nearAxisY.Y, nearAxisY.Z);
            }
        }

        private void ReleaseCloudShadowResources()
        {
            if (_cloudShadowAtlasTexture != 0)
            {
                _gl.DeleteTexture(_cloudShadowAtlasTexture);
                _cloudShadowAtlasTexture = 0;
            }

            if (_cloudShadowAtlasFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_cloudShadowAtlasFramebuffer);
                _cloudShadowAtlasFramebuffer = 0;
            }

            for (int level = 0; level < _cloudShadowNearLevelCount; level++)
            {
                if (_cloudShadowNearTextures[level] != 0)
                {
                    _gl.DeleteTexture(_cloudShadowNearTextures[level]);
                    _cloudShadowNearTextures[level] = 0;
                }

                if (_cloudShadowNearFramebuffers[level] != 0)
                {
                    _gl.DeleteFramebuffer(_cloudShadowNearFramebuffers[level]);
                    _cloudShadowNearFramebuffers[level] = 0;
                }

                _cloudShadowNearLevelValid[level] = false;
            }

            if (_cloudShadowNearProgram != 0)
            {
                _gl.DeleteProgram(_cloudShadowNearProgram);
                _cloudShadowNearProgram = 0;
            }

            if (_cloudShadowAtlasProgram != 0)
            {
                _gl.DeleteProgram(_cloudShadowAtlasProgram);
                _cloudShadowAtlasProgram = 0;
            }

            _activeCloudShadow.Valid = false;
            _activeCloudShadow.CloudMaterial = null;
            _cloudShadowNearValid = false;
            _cloudShadowAtlasDoneThisFrame = false;
        }
    }
}
