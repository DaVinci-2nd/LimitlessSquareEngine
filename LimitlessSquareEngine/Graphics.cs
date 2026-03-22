using MoonSharp.Interpreter;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Numerics;
using System.Text.Json;

namespace LimitlessSquareEngine
{
    [MoonSharpUserData]
    internal class Graphics
    {
        private GL _gl;
        private IWindow _window;
        private uint _quadVAO;
        private uint _quadVBO;
        private bool _quadInitialized = false;
        private uint _dynamicGeometryVBO = 0;
        private uint _dynamicGeometryVAO9 = 0;
        private uint _dynamicGeometryVAO16 = 0;
        private uint _dynamicGeometryVAO19 = 0;
        private nuint _dynamicGeometryCapacityBytes = 0;
        private float[] _dynamicGeometryScratch = Array.Empty<float>();
        private readonly Dictionary<string, MeshData> _meshes = new(StringComparer.Ordinal);

        private sealed class MeshSurfaceGpuResource
        {
            public uint Vao { get; init; }
            public uint Vbo { get; init; }
        }

        private readonly Dictionary<string, MeshSurfaceGpuResource> _meshSurfaceGpuCache = new(StringComparer.Ordinal);

        private long _sceneBatchCounter = 0;

        // 渲染区域
        private bool _sceneViewportUseFixedAspect = false;
        private float _sceneViewportAspectWidth = 16f;
        private float _sceneViewportAspectHeight = 9f;

        // 图形缓存
        private Dictionary<string, uint> _shaderPrograms = new Dictionary<string, uint>();
        // 纹理缓存
        private struct TextureInfo
        {
            public uint Id;
            public bool HasTransparency;
            public int Width;
            public int Height;
        }

        private Dictionary<string, TextureInfo> _textures = new();
        // 激活的着色器序列
        private uint _currentProgram;
        private readonly Dictionary<string, MaterialData> _materialCache = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, List<ActiveUniformInfo>> _programUniformCache = new();
        private readonly Dictionary<uint, ProgramUniformLocationCache> _programLocationCache = new();
        private readonly Dictionary<uint, ProgramMaterialDefaultsCache> _programMaterialDefaultsCache = new();
        private static readonly JsonElement _emptyJsonObject = JsonDocument.Parse("{}").RootElement.Clone();
        private MaterialData _fallbackMaterial;

        private RenderSpace _activeRenderSpace = RenderSpace.Canvas;

        private Matrix4x4 _activeModelMatrix = Matrix4x4.Identity;
        private Matrix4x4 _activeViewMatrix = Matrix4x4.Identity;
        private Matrix4x4 _activeProjectionMatrix = Matrix4x4.Identity;

        private RenderCullMode _currentCullMode = RenderCullMode.Front;

        // 主环境光，默认灰色
        private Vector3 _ambientLightColor = new Vector3(0.5f, 0.5f, 0.5f);
        private float _ambientLightIntensity = 1f;

        // 灯光集合
        private readonly Dictionary<string, LightData> _lights = new(StringComparer.Ordinal);

        private const uint _clusterLightBufferBinding = 0;
        private const uint _clusterRangeBufferBinding = 1;
        private const uint _clusterIndexBufferBinding = 2;

        private const int _clusterGridSizeX = 16;
        private const int _clusterGridSizeY = 9;
        private const int _clusterGridSizeZ = 24;

        private const string _uniformCameraPosition = "uCameraPosition";
        private const string _uniformAmbientColor = "uAmbientColor";
        private const string _uniformAmbientIntensity = "uAmbientIntensity";
        private const string _uniformViewportOrigin = "uViewportOrigin";
        private const string _uniformViewportSize = "uViewportSize";
        private const string _uniformClusterGridSize = "uClusterGridSize";
        private const string _uniformClusterNear = "uClusterNear";
        private const string _uniformClusterFar = "uClusterFar";
        private const string _uniformLightCount = "uLightCount";
        private const string _uniformShadowAtlasTexture = "uShadowAtlasTexture";
        private const string _uniformReflectionTexture = "uReflectionTexture";
        private const string _uniformReflectionEnabled = "uReflectionEnabled";
        private const string _uniformReflectionSkyboxCube = "uReflectionSkyboxCube";
        private const string _uniformReflectionSource = "uReflectionSource";
        private const string _uniformReflectionIntensity = "uReflectionIntensity";
        private const string _uniformOutlinePass = "uOutlinePass";
        private const string _uniformUseOutlineNormal = "uUseOutlineNormal";

        private uint _clusterLightBuffer = 0;
        private uint _clusterRangeBuffer = 0;
        private uint _clusterIndexBuffer = 0;
        private uint _lightingDummyTexture = 0;
        private bool _lightingSupportInitialized = false;

        private const uint _directionalShadowCascadeBufferBinding = 3;

        private const int _directionalShadowCascadeTileSize = 1024;
        private const int _maxDirectionalShadowLights = 1;
        private const int _gpuDirectionalShadowCascadeStrideFloats = 24;

        private int _directionalShadowCascadeCount = 4;
        private float _directionalShadowCascadeBaseDistance = 2f;
        private float _directionalShadowCascadeScale = 3f;
        private int _directionalShadowAtlasAllocatedSize = 0;

        private Double3 _directionalShadowStableAnchorWorld = Double3.Zero;
        private bool _directionalShadowStableAnchorInitialized = false;

        private uint _shadowAtlasTexture = 0;
        private uint _shadowFramebuffer = 0;
        private uint _shadowDepthProgram = 0;
        private uint _directionalShadowCascadeBuffer = 0;
        private bool _shadowSupportInitialized = false;

        private readonly List<float> _gpuDirectionalShadowCascadeUploadScratch = new(256);

        private sealed class DirectionalShadowCascadeInfo
        {
            public bool Valid { get; init; }
            public Matrix4x4 ShadowMatrix { get; init; }
            public Vector4 AtlasRect { get; init; }
            public float SplitNear { get; init; }
            public float SplitFar { get; init; }
            public int ViewportX { get; init; }
            public int ViewportY { get; init; }
            public int ViewportSize { get; init; }
        }

        private sealed class DirectionalShadowLightBatchData
        {
            public List<DirectionalShadowCascadeInfo> Cascades { get; } = new();
        }

        private sealed class DirectionalShadowBatchData
        {
            public Dictionary<string, DirectionalShadowLightBatchData> ByLightObjectId { get; } = new(StringComparer.Ordinal);
        }

        private readonly Dictionary<long, DirectionalShadowBatchData> _directionalShadowBatchCache = new();

        private const int _reflectionSkyboxCubeSize = 256;

        private uint _reflectionCaptureFramebuffer = 0;
        private uint _reflectionSkyboxCube = 0;
        private uint _reflectionDummyCubeTexture = 0;
        private bool _reflectionCaptureInitialized = false;
        private uint _reflectionPrefilteredCube = 0;
        private uint _reflectionEquirectToCubeProgram = 0;
        private uint _reflectionPrefilterProgram = 0;
        private const int _reflectionPrefilterSampleCount = 256;

        private uint _reflectionCubeVAO = 0;
        private uint _reflectionCubeVBO = 0;
        private int _reflectionCubeVertexCount = 0;

        private uint _reflectionHammersleyLutTexture = 0;

        private int _reflectionEquirectTextureLoc = -1;
        private int _reflectionEquirectViewsLoc = -1;
        private int _reflectionEquirectProjectionLoc = -1;

        private int _reflectionPrefilterEnvironmentLoc = -1;
        private int _reflectionPrefilterHammersleyLoc = -1;
        private int _reflectionPrefilterRoughnessLoc = -1;
        private int _reflectionPrefilterViewsLoc = -1;
        private int _reflectionPrefilterProjectionLoc = -1;

        private sealed class ReflectionTextureEnvironmentCacheEntry
        {
            public string SourcePath { get; init; } = "";
            public uint SourceCube { get; init; }
            public uint PrefilteredCube { get; init; }
            public long SourceLastWriteUtcTicks { get; init; }
        }

        private readonly Dictionary<string, ReflectionTextureEnvironmentCacheEntry> _reflectionTextureEnvironmentCache = new(StringComparer.Ordinal);

        private bool _capturedSkyboxReflectionValid = false;

        private const int _gpuPointLightStrideFloats = 52;
        private const int _clusterCount = _clusterGridSizeX * _clusterGridSizeY * _clusterGridSizeZ;
        private readonly List<float> _gpuLightUploadScratch = new(256);
        private readonly List<uint>[] _clusterLightLists = CreateClusterLightLists();

        private static List<uint>[] CreateClusterLightLists()
        {
            var result = new List<uint>[_clusterCount];
            for (int i = 0; i < result.Length; i++)
                result[i] = new List<uint>(8);

            return result;
        }

        private long _uploadedLightingBatchId = long.MinValue;
        private int _uploadedLightCount = 0;

        private enum RenderQueueType
        {
            Opaque = 0,
            Transparent = 1
        }

        private enum RenderPass
        {
            Scene = 0,
            Canvas = 1
        }

        private enum RenderCullMode
        {
            Front = 0,
            Back = 1,
            Both = 2
        }

        private readonly struct MeshSurfaceData
        {
            public string Id { get; }
            public float[] Vertices { get; }
            public PrimitiveType PrimitiveType { get; }
            public int VertexStrideFloats { get; }
            public int MaterialSlot { get; }
            public string? DefaultMaterialKey { get; }
            public Vector3 LocalCenter { get; }
            public bool VertexColorsAreWhite { get; }

            public MeshSurfaceData(
                string id,
                float[] vertices,
                PrimitiveType primitiveType,
                int vertexStrideFloats,
                int materialSlot,
                string? defaultMaterialKey = null,
                Vector3 localCenter = default,
                bool vertexColorsAreWhite = false)
            {
                Id = id;
                Vertices = vertices;
                PrimitiveType = primitiveType;
                VertexStrideFloats = vertexStrideFloats;
                MaterialSlot = materialSlot;
                DefaultMaterialKey = defaultMaterialKey;
                LocalCenter = localCenter;
                VertexColorsAreWhite = vertexColorsAreWhite;
            }
        }

        private readonly struct MeshData
        {
            public string Id { get; }
            public float[] Vertices { get; }
            public PrimitiveType PrimitiveType { get; }
            public int VertexStrideFloats { get; }
            public MeshSurfaceData[] Surfaces { get; }

            public MeshData(string id, float[] vertices, PrimitiveType primitiveType, int vertexStrideFloats)
            {
                Id = id;
                Vertices = vertices;
                PrimitiveType = primitiveType;
                VertexStrideFloats = vertexStrideFloats;

                Vector3 localCenter = Graphics.ComputeMeshLocalCenter(vertices, vertexStrideFloats);
                bool vertexColorsAreWhite = Graphics.AreMeshVertexColorsWhite(vertices, vertexStrideFloats);

                Surfaces = new[]
                {
                    new MeshSurfaceData(
                        id,
                        vertices,
                        primitiveType,
                        vertexStrideFloats,
                        0,
                        null,
                        localCenter,
                        vertexColorsAreWhite)
                };
            }

            public MeshData(string id, MeshSurfaceData[] surfaces)
            {
                if (surfaces == null || surfaces.Length == 0)
                    throw new ArgumentException("[X] Mesh surfaces cannot be null or empty.", nameof(surfaces));

                Id = id;
                Surfaces = surfaces;

                Vertices = surfaces[0].Vertices;
                PrimitiveType = surfaces[0].PrimitiveType;
                VertexStrideFloats = surfaces[0].VertexStrideFloats;
            }
        }

        private readonly struct ViewportRect
        {
            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }

            public float Aspect => Height <= 0 ? 1f : Width / (float)Height;

            public ViewportRect(int x, int y, int width, int height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }
        }
        private enum MaterialTextureWrapMode
        {
            Repeat = 0,
            Clamp = 1
        }

        private sealed class MaterialData
        {
            public string Id { get; init; } = "";
            public uint Program { get; init; }
            public JsonElement Parameters { get; init; }
            public Vector2 TextureUV { get; init; } = Vector2.One;
            public MaterialTextureWrapMode TextureWrap { get; init; } = MaterialTextureWrapMode.Repeat;
            public RenderCullMode CullMode { get; init; } = RenderCullMode.Front;
        }

        private sealed class SkyboxData
        {
            public string Id { get; init; } = "";
            public uint Program { get; init; }
            public JsonElement Parameters { get; init; }
            public RenderCullMode CullMode { get; init; } = RenderCullMode.Back;
        }

        private static readonly Matrix4x4[] _reflectionCaptureViews = CreateReflectionCaptureViews();

        private static Matrix4x4[] CreateReflectionCaptureViews()
        {
            return new[]
            {
                Matrix4x4.CreateLookAt(Vector3.Zero,  Vector3.UnitX,  -Vector3.UnitY), // +X
                Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitX,  -Vector3.UnitY), // -X
                Matrix4x4.CreateLookAt(Vector3.Zero,  Vector3.UnitY,   Vector3.UnitZ), // +Y
                Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitY,  -Vector3.UnitZ), // -Y
                Matrix4x4.CreateLookAt(Vector3.Zero,  Vector3.UnitZ,  -Vector3.UnitY), // +Z
                Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ,  -Vector3.UnitY), // -Z
            };
        }

        private void SetMatrixUniformArray(int location, Matrix4x4[] matrices)
        {
            if (location == -1 || matrices == null || matrices.Length == 0)
                return;

            float[] values = new float[matrices.Length * 16];
            int offset = 0;

            for (int i = 0; i < matrices.Length; i++)
            {
                Matrix4x4 m = matrices[i];

                values[offset++] = m.M11; values[offset++] = m.M12; values[offset++] = m.M13; values[offset++] = m.M14;
                values[offset++] = m.M21; values[offset++] = m.M22; values[offset++] = m.M23; values[offset++] = m.M24;
                values[offset++] = m.M31; values[offset++] = m.M32; values[offset++] = m.M33; values[offset++] = m.M34;
                values[offset++] = m.M41; values[offset++] = m.M42; values[offset++] = m.M43; values[offset++] = m.M44;
            }

            _gl.UniformMatrix4(location, (uint)matrices.Length, false, values);
        }
        private void InitializeReflectionCubeGeometry()
        {
            if (_reflectionCubeVAO != 0)
                return;

            if (!_meshes.TryGetValue("builtin/cube_1x1x1", out MeshData cubeMesh))
                throw new Exception("[X] Builtin reflection cube mesh not found.");

            _reflectionCubeVertexCount = cubeMesh.Vertices.Length / cubeMesh.VertexStrideFloats;

            _reflectionCubeVAO = _gl.GenVertexArray();
            _reflectionCubeVBO = _gl.GenBuffer();

            _gl.BindVertexArray(_reflectionCubeVAO);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _reflectionCubeVBO);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)cubeMesh.Vertices, BufferUsageARB.StaticDraw);

            uint strideBytes = (uint)(cubeMesh.VertexStrideFloats * sizeof(float));

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, strideBytes, 0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, strideBytes, 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, strideBytes, 7 * sizeof(float));
            _gl.EnableVertexAttribArray(2);

            if (cubeMesh.VertexStrideFloats >= 16)
            {
                _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, strideBytes, 9 * sizeof(float));
                _gl.EnableVertexAttribArray(3);

                _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, strideBytes, 12 * sizeof(float));
                _gl.EnableVertexAttribArray(4);
            }

            if (cubeMesh.VertexStrideFloats >= 19)
            {
                _gl.VertexAttribPointer(5, 3, VertexAttribPointerType.Float, false, strideBytes, 16 * sizeof(float));
                _gl.EnableVertexAttribArray(5);
            }

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);
        }

        private void BindReflectionCubeGeometry()
        {
            _gl.BindVertexArray(_reflectionCubeVAO);
        }

        private void InitializeDynamicGeometryResources()
        {
            if (_dynamicGeometryVBO != 0)
                return;

            _dynamicGeometryVBO = _gl.GenBuffer();

            _dynamicGeometryVAO9 = CreateDynamicGeometryVAO(9);
            _dynamicGeometryVAO16 = CreateDynamicGeometryVAO(16);
            _dynamicGeometryVAO19 = CreateDynamicGeometryVAO(19);
        }

        private uint CreateDynamicGeometryVAO(int vertexStrideFloats)
        {
            uint vao = _gl.GenVertexArray();

            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _dynamicGeometryVBO);

            uint strideBytes = (uint)(vertexStrideFloats * sizeof(float));

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, strideBytes, 0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, strideBytes, 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, strideBytes, 7 * sizeof(float));
            _gl.EnableVertexAttribArray(2);

            if (vertexStrideFloats >= 16)
            {
                _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, strideBytes, 9 * sizeof(float));
                _gl.EnableVertexAttribArray(3);

                _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, strideBytes, 12 * sizeof(float));
                _gl.EnableVertexAttribArray(4);
            }
            else
            {
                _gl.DisableVertexAttribArray(3);
                _gl.DisableVertexAttribArray(4);
            }

            if (vertexStrideFloats >= 19)
            {
                _gl.VertexAttribPointer(5, 3, VertexAttribPointerType.Float, false, strideBytes, 16 * sizeof(float));
                _gl.EnableVertexAttribArray(5);
            }
            else
            {
                _gl.DisableVertexAttribArray(5);
            }

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);

            return vao;
        }

        private uint GetDynamicGeometryVAO(int vertexStrideFloats)
        {
            return vertexStrideFloats switch
            {
                9 => _dynamicGeometryVAO9,
                16 => _dynamicGeometryVAO16,
                19 => _dynamicGeometryVAO19,
                _ => throw new InvalidOperationException($"[X] Unsupported vertex stride: {vertexStrideFloats}")
            };
        }

        private static string BuildMeshSurfaceGpuKey(string meshId, string surfaceId)
        {
            return meshId + "::" + surfaceId;
        }

        private static Vector3 ComputeMeshLocalCenter(float[] vertices, int vertexStrideFloats)
        {
            int vertexCount = vertices.Length / vertexStrideFloats;
            if (vertexCount <= 0)
                return Vector3.Zero;

            Vector3 center = Vector3.Zero;

            for (int i = 0; i < vertexCount; i++)
            {
                int idx = i * vertexStrideFloats;
                center += new Vector3(vertices[idx + 0], vertices[idx + 1], vertices[idx + 2]);
            }

            return center / vertexCount;
        }

        private static bool AreMeshVertexColorsWhite(float[] vertices, int vertexStrideFloats)
        {
            const float eps = 0.0001f;

            for (int i = 0; i + 6 < vertices.Length; i += vertexStrideFloats)
            {
                if (MathF.Abs(vertices[i + 3] - 1f) > eps ||
                    MathF.Abs(vertices[i + 4] - 1f) > eps ||
                    MathF.Abs(vertices[i + 5] - 1f) > eps ||
                    MathF.Abs(vertices[i + 6] - 1f) > eps)
                {
                    return false;
                }
            }

            return true;
        }

        private uint CreateStaticMeshSurfaceVAO(uint vbo, int vertexStrideFloats)
        {
            uint vao = _gl.GenVertexArray();

            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);

            uint strideBytes = (uint)(vertexStrideFloats * sizeof(float));

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, strideBytes, 0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, strideBytes, 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, strideBytes, 7 * sizeof(float));
            _gl.EnableVertexAttribArray(2);

            if (vertexStrideFloats >= 16)
            {
                _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, strideBytes, 9 * sizeof(float));
                _gl.EnableVertexAttribArray(3);

                _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, strideBytes, 12 * sizeof(float));
                _gl.EnableVertexAttribArray(4);
            }

            if (vertexStrideFloats >= 19)
            {
                _gl.VertexAttribPointer(5, 3, VertexAttribPointerType.Float, false, strideBytes, 16 * sizeof(float));
                _gl.EnableVertexAttribArray(5);
            }

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);

            return vao;
        }

        private MeshSurfaceGpuResource GetOrCreateMeshSurfaceGpuResource(
            string meshId,
            string surfaceId,
            float[] vertices,
            int vertexStrideFloats)
        {
            string key = BuildMeshSurfaceGpuKey(meshId, surfaceId);

            if (_meshSurfaceGpuCache.TryGetValue(key, out MeshSurfaceGpuResource cached))
                return cached;

            uint vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)vertices, BufferUsageARB.StaticDraw);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

            uint vao = CreateStaticMeshSurfaceVAO(vbo, vertexStrideFloats);

            MeshSurfaceGpuResource resource = new MeshSurfaceGpuResource
            {
                Vao = vao,
                Vbo = vbo
            };

            _meshSurfaceGpuCache[key] = resource;
            return resource;
        }

        private void DeleteMeshSurfaceGpuResource(MeshSurfaceGpuResource resource)
        {
            if (resource.Vao != 0)
                _gl.DeleteVertexArray(resource.Vao);

            if (resource.Vbo != 0)
                _gl.DeleteBuffer(resource.Vbo);
        }

        private void InvalidateMeshGpuResources(string meshId)
        {
            if (string.IsNullOrWhiteSpace(meshId))
                return;

            string prefix = meshId + "::";

            List<string> keys = _meshSurfaceGpuCache.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            foreach (string key in keys)
            {
                DeleteMeshSurfaceGpuResource(_meshSurfaceGpuCache[key]);
                _meshSurfaceGpuCache.Remove(key);
            }
        }

        private bool TryBindStaticCommandGeometry(in RenderCommand cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd.MeshId) || string.IsNullOrWhiteSpace(cmd.MeshSurfaceId))
                return false;

            Vector2 uvScale = cmd.Material != null ? cmd.Material.TextureUV : Vector2.One;
            bool needScaleUv =
                MathF.Abs(uvScale.X - 1f) > 0.0001f ||
                MathF.Abs(uvScale.Y - 1f) > 0.0001f;

            if (needScaleUv)
                return false;

            if (cmd.ForceWhiteVertexColor && !cmd.MeshVertexColorsAreWhite)
                return false;

            MeshSurfaceGpuResource resource = GetOrCreateMeshSurfaceGpuResource(
                cmd.MeshId,
                cmd.MeshSurfaceId,
                cmd.Vertices,
                cmd.VertexStrideFloats);

            _gl.BindVertexArray(resource.Vao);
            return true;
        }

        private void UploadDynamicGeometry(ReadOnlySpan<float> vertices)
        {
            nuint requiredBytes = (nuint)(vertices.Length * sizeof(float));

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _dynamicGeometryVBO);

            if (requiredBytes > _dynamicGeometryCapacityBytes)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.DynamicDraw);
                _dynamicGeometryCapacityBytes = requiredBytes;
            }
            else
            {
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, vertices);
            }
        }

        private static float RadicalInverseVdC(uint bits)
        {
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            return bits * 2.3283064365386963e-10f;
        }

        private float[] BuildReflectionHammersleyLutData()
        {
            float[] data = new float[_reflectionPrefilterSampleCount * 2];

            for (uint i = 0; i < _reflectionPrefilterSampleCount; i++)
            {
                int idx = (int)i * 2;
                data[idx + 0] = i / (float)_reflectionPrefilterSampleCount;
                data[idx + 1] = RadicalInverseVdC(i);
            }

            return data;
        }

        private void InitializeReflectionHammersleyLut()
        {
            if (_reflectionHammersleyLutTexture != 0)
                return;

            float[] data = BuildReflectionHammersleyLutData();

            _reflectionHammersleyLutTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture1D, _reflectionHammersleyLutTexture);

            const int GL_RG32F = 0x8230;
            const int GL_RG = 0x8227;

            _gl.TexImage1D(
                TextureTarget.Texture1D,
                0,
                (InternalFormat)GL_RG32F,
                (uint)_reflectionPrefilterSampleCount,
                0,
                (PixelFormat)GL_RG,
                PixelType.Float,
                (ReadOnlySpan<float>)data);

            _gl.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);

            _gl.BindTexture(TextureTarget.Texture1D, 0);
        }
        private void CacheReflectionProgramUniformLocations()
        {
            _reflectionEquirectTextureLoc = _gl.GetUniformLocation(_reflectionEquirectToCubeProgram, "uEquirectTexture");
            _reflectionEquirectViewsLoc = _gl.GetUniformLocation(_reflectionEquirectToCubeProgram, "uViews[0]");
            _reflectionEquirectProjectionLoc = _gl.GetUniformLocation(_reflectionEquirectToCubeProgram, "uProjection");

            _reflectionPrefilterEnvironmentLoc = _gl.GetUniformLocation(_reflectionPrefilterProgram, "uEnvironmentMap");
            _reflectionPrefilterHammersleyLoc = _gl.GetUniformLocation(_reflectionPrefilterProgram, "uHammersleyLut");
            _reflectionPrefilterRoughnessLoc = _gl.GetUniformLocation(_reflectionPrefilterProgram, "uRoughness");
            _reflectionPrefilterViewsLoc = _gl.GetUniformLocation(_reflectionPrefilterProgram, "uViews[0]");
            _reflectionPrefilterProjectionLoc = _gl.GetUniformLocation(_reflectionPrefilterProgram, "uProjection");
        }
        private void ApplySkyboxParametersOnly(SkyboxData skybox)
        {
            Dictionary<string, int> samplerUnits = ApplyMaterialDefaults(_currentProgram);

            if (skybox.Parameters.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in skybox.Parameters.EnumerateObject())
                {
                    ApplySkyboxParameter(prop.Name, prop.Value, samplerUnits);
                }
            }

            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private SkyboxData _screenSkybox;
        private readonly Dictionary<string, SkyboxData> _cameraSkyboxes = new(StringComparer.Ordinal);

        private void InitializeReflectionCaptureResources()
        {
            if (_reflectionCaptureInitialized) return;

            _reflectionSkyboxCube = CreateReflectionCubeTexture(withMipmaps: false);
            _reflectionPrefilteredCube = CreateReflectionCubeTexture(withMipmaps: true);

            _reflectionDummyCubeTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureCubeMap, _reflectionDummyCubeTexture);

            byte[] blackPixel = new byte[] { 0, 0, 0, 255 };
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
                    (ReadOnlySpan<byte>)blackPixel);
            }

            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);

            _reflectionCaptureFramebuffer = _gl.GenFramebuffer();
            _reflectionEquirectToCubeProgram = CreateReflectionEquirectToCubeProgram();
            _reflectionPrefilterProgram = CreateReflectionPrefilterProgram();

            InitializeReflectionCubeGeometry();
            InitializeReflectionHammersleyLut();
            CacheReflectionProgramUniformLocations();

            _reflectionCaptureInitialized = true;
        }

        private uint CreateDirectionalShadowDepthProgram()
        {
            string vertexSource = @"
        #version 430 core
        layout(location = 0) in vec3 aPos;

        uniform mat4 uModel;
        uniform mat4 uLightViewProjection;

        void main()
        {
            gl_Position = uLightViewProjection * uModel * vec4(aPos, 1.0);
        }";

            string fragmentSource = @"
        #version 430 core
        void main()
        {
        }";

            uint vs = CompileShader(ShaderType.VertexShader, vertexSource);
            uint fs = CompileShader(ShaderType.FragmentShader, fragmentSource);

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vs);
            _gl.AttachShader(program, fs);
            _gl.LinkProgram(program);

            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                throw new Exception($"[X] Directional shadow shader link failed: {infoLog}");
            }

            _gl.DetachShader(program, vs);
            _gl.DetachShader(program, fs);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);

            return program;
        }

        private void InitializeShadowSupportResources()
        {
            int requiredAtlasSize = GetDirectionalShadowAtlasSize();

            if (_shadowDepthProgram == 0)
                _shadowDepthProgram = CreateDirectionalShadowDepthProgram();

            if (_directionalShadowCascadeBuffer == 0)
            {
                _directionalShadowCascadeBuffer = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _directionalShadowCascadeBuffer);

                float[] emptyCascade = new float[_gpuDirectionalShadowCascadeStrideFloats];
                _gl.BufferData(
                    BufferTargetARB.ShaderStorageBuffer,
                    (ReadOnlySpan<float>)emptyCascade,
                    BufferUsageARB.DynamicDraw);

                _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
            }

            if (_shadowAtlasTexture != 0 &&
                _shadowFramebuffer != 0 &&
                _directionalShadowAtlasAllocatedSize == requiredAtlasSize)
            {
                _shadowSupportInitialized = true;
                return;
            }

            ReleaseDirectionalShadowAtlasStorage();

            _shadowAtlasTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _shadowAtlasTexture);

            float[] emptyDepth = new float[requiredAtlasSize * requiredAtlasSize];

            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.DepthComponent32f,
                (uint)requiredAtlasSize,
                (uint)requiredAtlasSize,
                0,
                PixelFormat.DepthComponent,
                PixelType.Float,
                (ReadOnlySpan<float>)emptyDepth);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            _shadowFramebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFramebuffer);
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D,
                _shadowAtlasTexture,
                0);

            _gl.DrawBuffer(GLEnum.None);
            _gl.ReadBuffer(GLEnum.None);

            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
                throw new Exception($"[X] Directional shadow framebuffer incomplete: {status}");

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            _directionalShadowAtlasAllocatedSize = requiredAtlasSize;
            _shadowSupportInitialized = true;
        }

        private readonly struct ActiveUniformInfo
        {
            public string Name { get; }
            public int Location { get; }
            public UniformType Type { get; }

            public ActiveUniformInfo(string name, int location, UniformType type)
            {
                Name = name;
                Location = location;
                Type = type;
            }
        }

        private sealed class ProgramUniformLocationCache
        {
            public Dictionary<string, ActiveUniformInfo> ByName { get; } = new(StringComparer.Ordinal);

            public int RenderSpace = -1;
            public int UseTexture = -1;
            public int Color = -1;
            public int Model = -1;
            public int View = -1;
            public int Projection = -1;

            public int CameraPosition = -1;
            public int AmbientColor = -1;
            public int AmbientIntensity = -1;
            public int ViewportOrigin = -1;
            public int ViewportSize = -1;
            public int ClusterGridSize = -1;
            public int ClusterNear = -1;
            public int ClusterFar = -1;
            public int LightCount = -1;
            public int ShadowAtlasTexture = -1;
            public int ReflectionTexture = -1;
            public int ReflectionEnabled = -1;
            public int ReflectionSkyboxCube = -1;
            public int ReflectionSource = -1;
            public int ReflectionIntensity = -1;
            public int OutlinePass = -1;
            public int UseOutlineNormal = -1;

            public int Texture = -1;
        }

        private enum MaterialDefaultCommandKind
        {
            Float1 = 0,
            Float2 = 1,
            Float3 = 2,
            Float4 = 3,
            Int1 = 4,
            Int2 = 5,
            Int3 = 6,
            Int4 = 7,
            Mat4Identity = 8,
            Sampler2D = 9
        }

        private readonly struct MaterialDefaultCommand
        {
            public MaterialDefaultCommandKind Kind { get; }
            public int Location { get; }
            public float X { get; }
            public float Y { get; }
            public float Z { get; }
            public float W { get; }
            public int IX { get; }
            public int IY { get; }
            public int IZ { get; }
            public int IW { get; }
            public int TextureUnit { get; }

            public MaterialDefaultCommand(
                MaterialDefaultCommandKind kind,
                int location,
                float x = 0f,
                float y = 0f,
                float z = 0f,
                float w = 0f,
                int ix = 0,
                int iy = 0,
                int iz = 0,
                int iw = 0,
                int textureUnit = -1)
            {
                Kind = kind;
                Location = location;
                X = x;
                Y = y;
                Z = z;
                W = w;
                IX = ix;
                IY = iy;
                IZ = iz;
                IW = iw;
                TextureUnit = textureUnit;
            }
        }

        private sealed class ProgramMaterialDefaultsCache
        {
            public List<MaterialDefaultCommand> Commands { get; } = new();
            public Dictionary<string, int> SamplerUnits { get; } = new(StringComparer.Ordinal);
        }

        internal sealed class SceneRenderObjectSnapshot
        {
            public string SceneId { get; init; } = "";
            public string ObjectId { get; init; } = "";
            public string Type { get; init; } = "Object";
            public bool Active { get; init; }
            public bool Visible { get; init; }
            public string? Mesh { get; init; }
            public List<string>? Materials { get; init; }
            public string RenderTag { get; init; } = "";

            public Double3 WorldPosition { get; init; }
            public DQuaternion WorldRotation { get; init; }
            public Double3 WorldScale { get; init; }
        }

        internal sealed class SceneRenderCameraSnapshot
        {
            public string SceneId { get; init; } = "";
            public string ObjectId { get; init; } = "";
            public CameraRenderSettings Settings { get; init; } = new();
            public int SubmissionOrder { get; init; }
            public SceneWorldState World { get; init; }
            public bool Active { get; init; }
            public bool Visible { get; init; }
        }

        internal sealed class SceneRenderLightSnapshot
        {
            public string SceneId { get; init; } = "";
            public string ObjectId { get; init; } = "";
            public LightRenderSettings Settings { get; init; } = new();
            public SceneWorldState World { get; init; }
            public bool Active { get; init; }
            public bool Visible { get; init; }
        }

        private readonly Dictionary<string, Dictionary<string, SceneRenderObjectSnapshot>> _sceneObjectCache
            = new(StringComparer.Ordinal);

        private readonly Dictionary<string, List<SceneRenderCameraSnapshot>> _sceneCameraCache
            = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Dictionary<string, SceneRenderLightSnapshot>> _sceneLightCache
            = new(StringComparer.Ordinal);

        private enum LightKind
        {
            Point = 0,
            Box = 1,
            Spot = 2,
            Directional = 3,
            Area = 4,
            Line = 5,
            Ray = 6
        }

        private sealed class LightData
        {
            public string Id { get; init; } = "";
            public LightKind Kind { get; init; } = LightKind.Point;
            public bool Active { get; set; } = true;

            public Vector3 Color { get; set; } = Vector3.One;
            public float Intensity { get; set; } = 1f;

            // 通用位置与方向
            public Double3 Position { get; set; } = new Double3(0, 0, 0);
            public Double3 Direction { get; set; } = new Double3(0, -1, 0);

            // 点光源、盒状灯、聚光灯常用范围
            public double Range { get; set; } = 1.0;

            // 点光源、聚光灯、面光灯、线光灯的衰减曲线
            // 0.5 = 线性衰减
            // 越接近 0 = 前快后慢
            // 越接近 1 = 前慢后快
            public double AttenuationCurve { get; set; } = 0.5;

            // 盒状灯：长方体尺寸
            public Double3 BoxSize { get; set; } = new Double3(1, 1, 1);

            // 聚光灯：内外角
            public double InnerAngle { get; set; } = 15.0;
            public double OuterAngle { get; set; } = 30.0;

            // 面光灯：一个方形面
            public Double3 AreaRight { get; set; } = new Double3(1, 0, 0);
            public Double3 AreaUp { get; set; } = new Double3(0, 1, 0);
            public double AreaWidth { get; set; } = 1.0;
            public double AreaHeight { get; set; } = 1.0;

            // 线光灯：一条线段
            public Double3 LineDirection { get; set; } = new Double3(1, 0, 0);
            public double LineLength { get; set; } = 1.0;

            // 阴影预留
            public bool CastShadow { get; set; } = false;
        }

        
        [MoonSharpHidden]
        public void UpsertSceneObject(SceneRenderObjectSnapshot snapshot)
        {
            if (!_sceneObjectCache.TryGetValue(snapshot.SceneId, out var map))
            {
                map = new Dictionary<string, SceneRenderObjectSnapshot>(StringComparer.Ordinal);
                _sceneObjectCache[snapshot.SceneId] = map;
            }

            map[snapshot.ObjectId] = snapshot;
        }

        [MoonSharpHidden]
        public void ReplaceSceneCameras(string sceneId, List<SceneRenderCameraSnapshot> cameras)
        {
            _sceneCameraCache[sceneId] = cameras
                .OrderBy(c => c.SubmissionOrder)
                .ToList();
        }

        [MoonSharpHidden]
        public void UpsertSceneLight(SceneRenderLightSnapshot snapshot)
        {
            if (!_sceneLightCache.TryGetValue(snapshot.SceneId, out var map))
            {
                map = new Dictionary<string, SceneRenderLightSnapshot>(StringComparer.Ordinal);
                _sceneLightCache[snapshot.SceneId] = map;
            }

            map[snapshot.ObjectId] = snapshot;
        }

        [MoonSharpHidden]
        public void RemoveSceneCache(string sceneId)
        {
            _sceneObjectCache.Remove(sceneId);
            _sceneCameraCache.Remove(sceneId);
            _sceneLightCache.Remove(sceneId);
        }

        private bool TryGetActiveUniformExact(uint program, string uniformName, out ActiveUniformInfo uniform)
        {
            return GetProgramLocationCache(program).ByName.TryGetValue(uniformName, out uniform);
        }

        private struct RenderCommand
        {
            public float[] Vertices;
            public int VertexStrideFloats;
            public PrimitiveType PrimitiveType;
            public uint Program;
            public bool UseTexture;
            public uint TextureId;
            public RenderSpace RenderSpace;

            public Matrix4x4 Model;
            public Matrix4x4 View;
            public Matrix4x4 Projection;

            public Vector3 CameraPosition;
            public float ClusterNear;
            public float ClusterFar;
            public string SceneId;
            public Double3 CameraWorldPosition;

            public RenderQueueType QueueType;
            public float SortDepth;
            public long SubmissionIndex;

            public RenderPass Pass;
            public long BatchId;
            public long BatchSubmissionOrder;

            public int ViewportX;
            public int ViewportY;
            public int ViewportWidth;
            public int ViewportHeight;

            public MaterialData Material;
            public SkyboxData Skybox;

            public bool ForceWhiteVertexColor;
            public bool IsSkybox;

            public RenderCullMode CullMode;

            public string MeshId;
            public string MeshSurfaceId;
            public bool MeshVertexColorsAreWhite;

            public bool UsePremultipliedTransparentBlend;
        }
        private readonly List<RenderCommand> _renderQueue = new();
        private long _submissionCounter = 0;

        private void EnsureDynamicGeometryScratchCapacity(int requiredFloatCount)
        {
            if (_dynamicGeometryScratch.Length >= requiredFloatCount)
                return;

            int newSize = _dynamicGeometryScratch.Length == 0 ? requiredFloatCount : _dynamicGeometryScratch.Length;

            while (newSize < requiredFloatCount)
                newSize *= 2;

            if (newSize < requiredFloatCount)
                newSize = requiredFloatCount;

            _dynamicGeometryScratch = new float[newSize];
        }

        private float ReadMaterialColorAlpha(MaterialData material)
        {
            if (material == null)
                return 1f;

            if (!TryGetMaterialProperty(material, "uColor", out JsonElement value))
                return 1f;

            if (!TryReadNumericArray(value, out double[] numbers) || numbers.Length < 4)
                return 1f;

            return Math.Clamp((float)numbers[3], 0f, 1f);
        }

        private ReadOnlySpan<float> PrepareVerticesForUpload(in RenderCommand cmd)
        {
            bool needWhiteColor = cmd.ForceWhiteVertexColor;

            Vector2 uvScale = cmd.Material != null ? cmd.Material.TextureUV : Vector2.One;
            bool needScaleUv =
                MathF.Abs(uvScale.X - 1f) > 0.0001f ||
                MathF.Abs(uvScale.Y - 1f) > 0.0001f;

            if (!needWhiteColor && !needScaleUv)
                return cmd.Vertices;

            int length = cmd.Vertices.Length;
            EnsureDynamicGeometryScratchCapacity(length);

            cmd.Vertices.AsSpan(0, length).CopyTo(_dynamicGeometryScratch.AsSpan(0, length));

            Span<float> vertices = _dynamicGeometryScratch.AsSpan(0, length);

            // [x, y, z, r, g, b, a, u, v, ...]
            for (int i = 0; i + 8 < length; i += cmd.VertexStrideFloats)
            {
                if (needWhiteColor)
                {
                    vertices[i + 3] = 1f;
                    vertices[i + 4] = 1f;
                    vertices[i + 5] = 1f;
                    vertices[i + 6] = 1f;
                }

                if (needScaleUv)
                {
                    vertices[i + 7] *= uvScale.X;
                    vertices[i + 8] *= uvScale.Y;
                }
            }

            return vertices;
        }

        private bool _cameraContextActive = false;
        private int _activeSceneId = -1;

        /// <summary>
        /// 渲染类型枚举
        /// </summary>
        internal enum RenderSpace
        {
            Canvas = 0,
            Camera = 1
        }

        /// <summary>
        /// 透明类型判断
        /// </summary>
        /// <param name="textured"></param>
        /// <param name="textureHasTransparency"></param>
        /// <returns></returns>
        private bool IsCurrentDrawTransparent(bool textured, bool textureHasTransparency = false)
        {
            if (_currentColor.W < 1f)
                return true;

            if (textured && textureHasTransparency)
                return true;

            return false;
        }

        /// <summary>
        /// 深度排序
        /// </summary>
        /// <param name="vertices"></param>
        /// <param name="model"></param>
        /// <param name="view"></param>
        /// <param name="renderSpace"></param>
        /// <returns></returns>
        private float ComputeSortDepth(float[] vertices, int vertexStrideFloats, Matrix4x4 model, Matrix4x4 view, RenderSpace renderSpace)
        {
            int vertexCount = vertices.Length / vertexStrideFloats;
            if (vertexCount == 0)
                return 0f;

            Vector3 center = Vector3.Zero;

            for (int i = 0; i < vertexCount; i++)
            {
                int idx = i * vertexStrideFloats;
                center += new Vector3(vertices[idx], vertices[idx + 1], vertices[idx + 2]);
            }

            center /= vertexCount;

            if (renderSpace == RenderSpace.Canvas)
            {
                Vector4 canvasPos = Vector4.Transform(new Vector4(center, 1f), model);
                return -canvasPos.Z;
            }

            Vector4 world = Vector4.Transform(new Vector4(center, 1f), model);
            Vector4 viewPos = Vector4.Transform(world, view);
            return -viewPos.Z;
        }

        private float ComputeSortDepth(Vector3 localCenter, Matrix4x4 model, Matrix4x4 view, RenderSpace renderSpace)
        {
            if (renderSpace == RenderSpace.Canvas)
            {
                Vector4 canvasPos = Vector4.Transform(new Vector4(localCenter, 1f), model);
                return -canvasPos.Z;
            }

            Vector4 world = Vector4.Transform(new Vector4(localCenter, 1f), model);
            Vector4 viewPos = Vector4.Transform(world, view);
            return -viewPos.Z;
        }

        /// <summary>
        /// 透视/正交矩阵
        /// </summary>
        /// <param name="fovRadians"></param>
        /// <param name="aspect"></param>
        /// <param name="near"></param>
        /// <param name="far"></param>
        /// <returns></returns>
        public static Matrix4x4 CreatePerspective(float fovRadians, float aspect, float near, float far)
        {
            return Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, aspect, near, far);
        }

        public static Matrix4x4 CreateOrthographic(float width, float height, float near, float far)
        {
            return Matrix4x4.CreateOrthographic(width, height, near, far);
        }

        /// <summary>
        /// 设置场景全屏窗口
        /// </summary>
        [MoonSharpHidden]
        public void SetSceneViewportFillWindow()
        {
            _sceneViewportUseFixedAspect = false;
        }

        [MoonSharpHidden]
        public void SetSceneViewportFixedAspect(float width, float height)
        {
            if (width <= 0f || height <= 0f)
                throw new ArgumentException("[X] Fixed aspect width/height must be > 0.");

            _sceneViewportUseFixedAspect = true;
            _sceneViewportAspectWidth = width;
            _sceneViewportAspectHeight = height;
        }

        private ViewportRect GetSceneViewportRect()
        {
            int windowWidth = _window.Size.X;
            int windowHeight = _window.Size.Y;

            if (!_sceneViewportUseFixedAspect)
                return new ViewportRect(0, 0, windowWidth, windowHeight);

            float targetAspect = _sceneViewportAspectWidth / _sceneViewportAspectHeight;
            float windowAspect = windowWidth / (float)windowHeight;

            if (windowAspect > targetAspect)
            {
                int viewportHeight = windowHeight;
                int viewportWidth = (int)MathF.Round(viewportHeight * targetAspect);
                int viewportX = (windowWidth - viewportWidth) / 2;
                return new ViewportRect(viewportX, 0, viewportWidth, viewportHeight);
            }
            else
            {
                int viewportWidth = windowWidth;
                int viewportHeight = (int)MathF.Round(viewportWidth / targetAspect);
                int viewportY = (windowHeight - viewportHeight) / 2;
                return new ViewportRect(0, viewportY, viewportWidth, viewportHeight);
            }
        }

        /// <summary>
        /// 加载着色器
        /// </summary>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="Exception"></exception>
        private void LoadShaders()
        {
            string shadersPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders");
            if (!Directory.Exists(shadersPath))
                throw new DirectoryNotFoundException("[X] Shaders folder not found.");

            // 获取所有着色器
            string[] vertexFiles = Directory.GetFiles(shadersPath, "*.vert", SearchOption.AllDirectories);
            foreach (string vertFile in vertexFiles)
            {
                string directory = Path.GetDirectoryName(vertFile);
                string name = Path.GetFileNameWithoutExtension(vertFile);
                string fragFile = Path.Combine(directory, name + ".frag");
                if (!File.Exists(fragFile))
                {
                    Console.WriteLine($"[!] The frag file corresponding to {vertFile} cannot be found, Skipped.");
                    continue;
                }

                string vertexSource = File.ReadAllText(vertFile);
                string fragmentSource = File.ReadAllText(fragFile);

                uint vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
                uint fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

                uint program = _gl.CreateProgram();
                _gl.AttachShader(program, vertexShader);
                _gl.AttachShader(program, fragmentShader);
                _gl.LinkProgram(program);

                _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
                if (success == 0)
                {
                    string infoLog = _gl.GetProgramInfoLog(program);
                    throw new Exception($"[X] Shader '{name}' failed to link: {infoLog}");
                }

                _gl.DetachShader(program, vertexShader);
                _gl.DetachShader(program, fragmentShader);
                _gl.DeleteShader(vertexShader);
                _gl.DeleteShader(fragmentShader);

                string relativePath = vertFile.Substring(shadersPath.Length + 1);
                string key = relativePath.Replace(".vert", "").Replace('\\', '/');
                _shaderPrograms[key] = program;
                Console.WriteLine($"[i] has been successfully loaded {key} shader");
            }

            if (_shaderPrograms.Count == 0)
                throw new Exception("[X] No valid shader found");

            // 设置默认程序
            _currentProgram = _shaderPrograms.Values.First();
            _gl.UseProgram(_currentProgram);
            RegisterBuiltInMeshes();
        }

        /// <summary>
        /// 应用着色器
        /// </summary>
        /// <param name="name"></param>
        public void UseShader(string name)
        {
            if (_shaderPrograms.TryGetValue(name, out uint program))
            {
                if (_currentProgram != program)
                {
                    _currentProgram = program;
                    _gl.UseProgram(program);
                }
            }
            else
            {
                // 未找到着色器时用备用着色器代替
                Console.WriteLine($"[X] Shader '{name}' not found.");
                const string fallbackKey = "__internal_fallback_purple__";
                if (!_shaderPrograms.TryGetValue(fallbackKey, out uint fallbackProgram))
                {
                    fallbackProgram = CreateFallbackShaderProgram();
                    _shaderPrograms[fallbackKey] = fallbackProgram;
                }
                if (_currentProgram != fallbackProgram)
                {
                    _currentProgram = fallbackProgram;
                    _gl.UseProgram(fallbackProgram);
                }
            }
        }

        /// <summary>
        /// 备用着色器
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private uint CreateFallbackShaderProgram()
        {
            string vertexSource = @"
                #version 330 core
                layout(location = 0) in vec3 aPos;
                layout(location = 1) in vec4 aColor;

                uniform int uRenderSpace;
                uniform mat4 uModel;
                uniform mat4 uView;
                uniform mat4 uProjection;

                out vec4 vColor;

                void main()
                {
                    if (uRenderSpace == 0)
                    {
                        gl_Position = vec4(aPos, 1.0);
                    }
                    else
                    {
                        gl_Position = uProjection * uView * uModel * vec4(aPos, 1.0);
                    }

                    vColor = aColor;
                }";

            string fragmentSource = @"
                #version 330 core
                out vec4 FragColor;
                void main()
                {
                    FragColor = vec4(1.0, 0.0, 1.0, 1.0);
                }";

            uint vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
            uint fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vertexShader);
            _gl.AttachShader(program, fragmentShader);
            _gl.LinkProgram(program);

            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                throw new Exception($"[X] Default shader loading error: {infoLog}");
            }

            _gl.DetachShader(program, vertexShader);
            _gl.DetachShader(program, fragmentShader);
            _gl.DeleteShader(vertexShader);
            _gl.DeleteShader(fragmentShader);

            return program;
        }

        //顶点数据缓存
        private List<float> _vertexBuffer = new List<float>();
        private uint _vertexArrayObject;
        private uint _vertexBufferObject;
        private bool _isInitialized = false;

        //当前绘制颜色
        private Vector4 _currentColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);

        //背景色
        private Vector4 _backgroundColor = new Vector4(0.0f, 0.0f, 0.0f, 1.0f);


        public Graphics(GL gl, IWindow window)
        {
            _gl = gl;
            _window = window;
        }

        public void SetDirectionalShadowCascadeCount(int count)
        {
            _directionalShadowCascadeCount = Math.Max(1, count);
            ReleaseDirectionalShadowAtlasStorage();
            _shadowSupportInitialized = false;
        }

        public void SetDirectionalShadowCascadeBaseDistance(float distance)
        {
            _directionalShadowCascadeBaseDistance = MathF.Max(0.0001f, distance);
            ReleaseDirectionalShadowAtlasStorage();
            _shadowSupportInitialized = false;
        }

        public void SetDirectionalShadowCascadeScale(float scale)
        {
            _directionalShadowCascadeScale = MathF.Max(1f, scale);
        }

        private int GetDirectionalShadowAtlasTileCount()
        {
            return Math.Max(1, _directionalShadowCascadeCount * _maxDirectionalShadowLights);
        }

        private int GetDirectionalShadowAtlasGrid()
        {
            return (int)MathF.Ceiling(MathF.Sqrt(GetDirectionalShadowAtlasTileCount()));
        }

        private int GetDirectionalShadowAtlasSize()
        {
            return GetDirectionalShadowAtlasGrid() * _directionalShadowCascadeTileSize;
        }

        private float GetDirectionalShadowCascadeMaxDistance(int cascadeIndex)
        {
            float baseDistance = MathF.Max(0.0001f, _directionalShadowCascadeBaseDistance);
            float scale = MathF.Max(1f, _directionalShadowCascadeScale);

            if (cascadeIndex <= 0)
                return baseDistance;

            if (MathF.Abs(scale - 1f) <= 0.000001f)
                return baseDistance * (cascadeIndex + 1);

            float sum = (MathF.Pow(scale, cascadeIndex + 1) - 1f) / (scale - 1f);
            return baseDistance * sum;
        }

        private void ReleaseDirectionalShadowAtlasStorage()
        {
            if (_shadowAtlasTexture != 0)
            {
                _gl.DeleteTexture(_shadowAtlasTexture);
                _shadowAtlasTexture = 0;
            }

            if (_shadowFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_shadowFramebuffer);
                _shadowFramebuffer = 0;
            }

            _directionalShadowAtlasAllocatedSize = 0;
        }

        private void InitializeLightingSupportResources()
        {
            if (_lightingSupportInitialized)
                return;

            _clusterLightBuffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _clusterLightBuffer);
            uint[] emptyLightBufferData = new uint[] { 0 };
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (ReadOnlySpan<uint>)emptyLightBufferData, BufferUsageARB.DynamicDraw);

            _clusterRangeBuffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _clusterRangeBuffer);
            uint[] emptyClusterRanges = new uint[_clusterGridSizeX * _clusterGridSizeY * _clusterGridSizeZ * 2];
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (ReadOnlySpan<uint>)emptyClusterRanges, BufferUsageARB.DynamicDraw);

            _clusterIndexBuffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _clusterIndexBuffer);
            uint[] emptyClusterIndices = new uint[] { 0 };
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (ReadOnlySpan<uint>)emptyClusterIndices, BufferUsageARB.DynamicDraw);

            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

            _lightingDummyTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _lightingDummyTexture);

            byte[] whitePixel = new byte[] { 255, 255, 255, 255 };
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba,
                1,
                1,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                (ReadOnlySpan<byte>)whitePixel);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            _lightingSupportInitialized = true;

            InitializeShadowSupportResources();
            InitializeReflectionCaptureResources();
        }

        private bool ShouldCastShadow(MaterialData material)
        {
            if (material == null)
                return true;

            if (!TryGetMaterialProperty(material, "uCastShadow", out JsonElement value))
                return true;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.GetDouble() != 0.0,
                _ => true
            };
        }

        private Vector3[] ComputeCameraFrustumCorners(in RenderCommand cmd, float nearValue, float farValue)
        {
            bool isOrthographic =
                MathF.Abs(cmd.Projection.M34) < 0.0001f &&
                MathF.Abs(cmd.Projection.M44 - 1f) < 0.0001f;

            nearValue = MathF.Max(0.01f, nearValue);
            farValue = MathF.Max(nearValue + 0.01f, farValue);

            if (!Matrix4x4.Invert(cmd.View, out Matrix4x4 inverseView))
                inverseView = Matrix4x4.Identity;

            Vector3[] viewCorners = new Vector3[8];

            if (isOrthographic)
            {
                float halfWidth = 1f / MathF.Max(MathF.Abs(cmd.Projection.M11), 0.0001f);
                float halfHeight = 1f / MathF.Max(MathF.Abs(cmd.Projection.M22), 0.0001f);

                viewCorners[0] = new Vector3(-halfWidth, -halfHeight, -nearValue);
                viewCorners[1] = new Vector3(halfWidth, -halfHeight, -nearValue);
                viewCorners[2] = new Vector3(halfWidth, halfHeight, -nearValue);
                viewCorners[3] = new Vector3(-halfWidth, halfHeight, -nearValue);

                viewCorners[4] = new Vector3(-halfWidth, -halfHeight, -farValue);
                viewCorners[5] = new Vector3(halfWidth, -halfHeight, -farValue);
                viewCorners[6] = new Vector3(halfWidth, halfHeight, -farValue);
                viewCorners[7] = new Vector3(-halfWidth, halfHeight, -farValue);
            }
            else
            {
                float tanHalfX = 1f / MathF.Max(MathF.Abs(cmd.Projection.M11), 0.0001f);
                float tanHalfY = 1f / MathF.Max(MathF.Abs(cmd.Projection.M22), 0.0001f);

                float nearX = nearValue * tanHalfX;
                float nearY = nearValue * tanHalfY;
                float farX = farValue * tanHalfX;
                float farY = farValue * tanHalfY;

                viewCorners[0] = new Vector3(-nearX, -nearY, -nearValue);
                viewCorners[1] = new Vector3(nearX, -nearY, -nearValue);
                viewCorners[2] = new Vector3(nearX, nearY, -nearValue);
                viewCorners[3] = new Vector3(-nearX, nearY, -nearValue);

                viewCorners[4] = new Vector3(-farX, -farY, -farValue);
                viewCorners[5] = new Vector3(farX, -farY, -farValue);
                viewCorners[6] = new Vector3(farX, farY, -farValue);
                viewCorners[7] = new Vector3(-farX, farY, -farValue);
            }

            Vector3[] worldCorners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                worldCorners[i] = TransformPosition(inverseView, viewCorners[i]);

            return worldCorners;
        }

        private DirectionalShadowCascadeInfo BuildDirectionalShadowCascadeInfo(
            in RenderCommand cmd,
            SceneRenderLightSnapshot light,
            float cascadeNear,
            float cascadeFar,
            int tileIndex)
        {
            if (cascadeFar <= cascadeNear)
            {
                return new DirectionalShadowCascadeInfo
                {
                    Valid = false
                };
            }

            Vector3[] frustumCorners = ComputeCameraFrustumCorners(cmd, cascadeNear, cascadeFar);
            Vector3 lightDirection = ExtractDirectionalLightDirection(light);

            Vector3 frustumCenter = Vector3.Zero;
            for (int i = 0; i < frustumCorners.Length; i++)
                frustumCenter += frustumCorners[i];
            frustumCenter /= frustumCorners.Length;

            float boundingRadius = ComputeFrustumBoundingSphereRadius(frustumCenter, frustumCorners);

            float baseHalfExtent = MathF.Max(0.5f, boundingRadius);

            const float extentQuantize = 2048f;
            baseHalfExtent = MathF.Ceiling(baseHalfExtent * extentQuantize) / extentQuantize;

            float halfExtent = baseHalfExtent;
            float texelWorldSize = (halfExtent * 2f) / _directionalShadowCascadeTileSize;

            const int guardTexels = 2;
            halfExtent += texelWorldSize * guardTexels;

            texelWorldSize = (halfExtent * 2f) / _directionalShadowCascadeTileSize;

            float anchorGridWorldSize = halfExtent * 2f;
            Vector3 stableAnchorRelative = GetDirectionalShadowStableAnchorRelative(
                cmd.CameraWorldPosition,
                anchorGridWorldSize);

            BuildDirectionalLightBasis(lightDirection, out Vector3 lightRight, out Vector3 lightUp, out Vector3 lightForward);

            Vector3 snappedCenter = SnapDirectionalShadowCenterToTexelStable(
                frustumCenter,
                stableAnchorRelative,
                lightRight,
                lightUp,
                lightForward,
                texelWorldSize);

            Vector3 lightPosition = snappedCenter - lightDirection * (cascadeFar + 32f);
            Matrix4x4 lightView = Matrix4x4.CreateLookAt(lightPosition, snappedCenter, lightUp);

            float minZ = float.PositiveInfinity;
            float maxZ = float.NegativeInfinity;

            for (int i = 0; i < frustumCorners.Length; i++)
            {
                Vector3 ls = TransformPosition(lightView, frustumCorners[i]);

                minZ = MathF.Min(minZ, ls.Z);
                maxZ = MathF.Max(maxZ, ls.Z);
            }

            float minX = -halfExtent;
            float maxX = halfExtent;
            float minY = -halfExtent;
            float maxY = halfExtent;

            float zPadding = MathF.Max(4f, (cascadeFar - cascadeNear) * 0.5f);
            float nearPlane = MathF.Max(0.1f, -maxZ - zPadding);
            float farPlane = MathF.Max(nearPlane + 1f, -minZ + zPadding);

            Matrix4x4 lightProjection = Matrix4x4.CreateOrthographicOffCenter(
                minX,
                maxX,
                minY,
                maxY,
                nearPlane,
                farPlane);

            int tileSize = _directionalShadowCascadeTileSize;
            int atlasGrid = GetDirectionalShadowAtlasGrid();
            int atlasSize = GetDirectionalShadowAtlasSize();

            int tileX = (tileIndex % atlasGrid) * tileSize;
            int tileY = (tileIndex / atlasGrid) * tileSize;

            return new DirectionalShadowCascadeInfo
            {
                Valid = true,
                ShadowMatrix = lightView * lightProjection,
                AtlasRect = new Vector4(
                    tileX / (float)atlasSize,
                    tileY / (float)atlasSize,
                    tileSize / (float)atlasSize,
                    tileSize / (float)atlasSize),
                SplitNear = cascadeNear,
                SplitFar = cascadeFar,
                ViewportX = tileX,
                ViewportY = tileY,
                ViewportSize = tileSize
            };
        }

        private void PrepareDirectionalShadowBatch(in RenderCommand batchAnchor, List<RenderCommand> batchCommands)
        {
            InitializeShadowSupportResources();

            DirectionalShadowBatchData batchData = new DirectionalShadowBatchData();
            _directionalShadowBatchCache[batchAnchor.BatchId] = batchData;

            if (string.IsNullOrWhiteSpace(batchAnchor.SceneId))
                return;

            if (!_sceneLightCache.TryGetValue(batchAnchor.SceneId, out var lightMap))
                return;

            List<SceneRenderLightSnapshot> shadowLights = lightMap.Values
                .Where(l =>
                    l.Active &&
                    l.Visible &&
                    l.Settings.CastShadow &&
                    l.Settings.LightMode == (int)LightKind.Directional)
                .OrderBy(l => l.ObjectId)
                .Take(_maxDirectionalShadowLights)
                .ToList();

            if (shadowLights.Count == 0)
                return;

            uint previousProgram = _currentProgram;
            RenderSpace previousRenderSpace = _activeRenderSpace;
            Matrix4x4 previousModel = _activeModelMatrix;
            Matrix4x4 previousView = _activeViewMatrix;
            Matrix4x4 previousProjection = _activeProjectionMatrix;

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFramebuffer);
            _gl.ColorMask(false, false, false, false);
            _gl.Disable(GLEnum.Blend);
            _gl.Enable(GLEnum.DepthTest);
            _gl.DepthFunc(GLEnum.Less);
            _gl.DepthMask(true);
            _gl.Enable(GLEnum.PolygonOffsetFill);
            _gl.PolygonOffset(0.001f, 0.002f);
            _gl.Disable(GLEnum.CullFace);

            _currentProgram = _shadowDepthProgram;
            _gl.UseProgram(_shadowDepthProgram);

            int lightViewProjectionLoc = _gl.GetUniformLocation(_shadowDepthProgram, "uLightViewProjection");
            int modelLoc = _gl.GetUniformLocation(_shadowDepthProgram, "uModel");

            for (int lightIndex = 0; lightIndex < shadowLights.Count; lightIndex++)
            {
                SceneRenderLightSnapshot light = shadowLights[lightIndex];
                DirectionalShadowLightBatchData lightBatchData = new DirectionalShadowLightBatchData();
                batchData.ByLightObjectId[light.ObjectId] = lightBatchData;

                float previousSplitFar = 0f;

                for (int cascadeIndex = 0; cascadeIndex < _directionalShadowCascadeCount; cascadeIndex++)
                {
                    float cascadeMaxDistance = GetDirectionalShadowCascadeMaxDistance(cascadeIndex);
                    float cascadeNear = MathF.Max(batchAnchor.ClusterNear, previousSplitFar);
                    float cascadeFar = MathF.Min(batchAnchor.ClusterFar, cascadeMaxDistance);
                    previousSplitFar = cascadeMaxDistance;

                    if (cascadeFar <= cascadeNear)
                        continue;

                    int tileIndex = lightIndex * _directionalShadowCascadeCount + cascadeIndex;

                    DirectionalShadowCascadeInfo cascadeInfo = BuildDirectionalShadowCascadeInfo(
                        batchAnchor,
                        light,
                        cascadeNear,
                        cascadeFar,
                        tileIndex);

                    if (!cascadeInfo.Valid)
                        continue;

                    lightBatchData.Cascades.Add(cascadeInfo);

                    _gl.Viewport(
                        cascadeInfo.ViewportX,
                        cascadeInfo.ViewportY,
                        (uint)cascadeInfo.ViewportSize,
                        (uint)cascadeInfo.ViewportSize);

                    _gl.Enable(GLEnum.ScissorTest);
                    _gl.Scissor(
                        cascadeInfo.ViewportX,
                        cascadeInfo.ViewportY,
                        (uint)cascadeInfo.ViewportSize,
                        (uint)cascadeInfo.ViewportSize);
                    _gl.Clear(ClearBufferMask.DepthBufferBit);
                    _gl.Disable(GLEnum.ScissorTest);

                    SetMatrixUniform(lightViewProjectionLoc, cascadeInfo.ShadowMatrix);

                    foreach (RenderCommand cmd in batchCommands)
                    {
                        if (cmd.IsSkybox)
                            continue;

                        if (cmd.QueueType != RenderQueueType.Opaque)
                            continue;

                        if (!ShouldCastShadow(cmd.Material))
                            continue;

                        BindCommandGeometry(cmd);
                        SetMatrixUniform(modelLoc, cmd.Model);
                        _gl.DrawArrays(cmd.PrimitiveType, 0, (uint)(cmd.Vertices.Length / cmd.VertexStrideFloats));
                    }

                    if (cascadeFar >= batchAnchor.ClusterFar)
                        break;
                }
            }

            _gl.Disable(GLEnum.PolygonOffsetFill);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.ColorMask(true, true, true, true);

            _gl.Viewport(
                batchAnchor.ViewportX,
                batchAnchor.ViewportY,
                (uint)Math.Max(1, batchAnchor.ViewportWidth),
                (uint)Math.Max(1, batchAnchor.ViewportHeight));

            _gl.Enable(GLEnum.DepthTest);
            _gl.DepthFunc(GLEnum.Less);
            _gl.DepthMask(true);
            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

            _currentProgram = previousProgram;
            _gl.UseProgram(previousProgram);

            _activeRenderSpace = previousRenderSpace;
            _activeModelMatrix = previousModel;
            _activeViewMatrix = previousView;
            _activeProjectionMatrix = previousProjection;

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);
        }

        private static bool TryGetMaterialProperty(MaterialData material, string name, out JsonElement value)
        {
            value = default;

            if (material == null || material.Parameters.ValueKind != JsonValueKind.Object)
                return false;

            return material.Parameters.TryGetProperty(name, out value);
        }

        private static float ReadMaterialFloat(MaterialData material, string name, float defaultValue)
        {
            if (!TryGetMaterialProperty(material, name, out JsonElement value))
                return defaultValue;

            if (value.ValueKind == JsonValueKind.Number)
                return Math.Max(0f, (float)value.GetDouble());

            return defaultValue;
        }

        private static string ReadMaterialString(MaterialData material, string name, string defaultValue)
        {
            if (!TryGetMaterialProperty(material, name, out JsonElement value))
                return defaultValue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? defaultValue;

            return defaultValue;
        }

        private int ReadReflectionSourceMode(MaterialData material)
        {
            string source = ReadMaterialString(material, "uReflectionSource", "Skybox");

            if (string.Equals(source, "Texture", StringComparison.OrdinalIgnoreCase))
                return 1;

            return 0;
        }

        private void UploadEmptyPointLightBuffer()
        {
            float[] emptyLight = new float[_gpuPointLightStrideFloats];

            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _clusterLightBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (ReadOnlySpan<float>)emptyLight,
                BufferUsageARB.DynamicDraw);

            uint[] emptyRanges = new uint[_clusterCount * 2];
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _clusterRangeBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (ReadOnlySpan<uint>)emptyRanges,
                BufferUsageARB.DynamicDraw);

            uint[] emptyIndices = new uint[] { 0 };
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _clusterIndexBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (ReadOnlySpan<uint>)emptyIndices,
                BufferUsageARB.DynamicDraw);

            float[] emptyCascade = new float[_gpuDirectionalShadowCascadeStrideFloats];
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _directionalShadowCascadeBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (ReadOnlySpan<float>)emptyCascade,
                BufferUsageARB.DynamicDraw);

            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

            _uploadedLightCount = 0;
        }

        private int UploadPointLightsForCommand(in RenderCommand cmd)
        {
            if (_uploadedLightingBatchId == cmd.BatchId)
                return _uploadedLightCount;

            _uploadedLightingBatchId = cmd.BatchId;
            _uploadedLightCount = 0;

            if (cmd.Pass != RenderPass.Scene ||
                string.IsNullOrWhiteSpace(cmd.SceneId) ||
                !_sceneLightCache.TryGetValue(cmd.SceneId, out var lightMap) ||
                lightMap.Count == 0)
            {
                UploadEmptyPointLightBuffer();
                return 0;
            }

            _gpuLightUploadScratch.Clear();
            _gpuDirectionalShadowCascadeUploadScratch.Clear();

            for (int i = 0; i < _clusterLightLists.Length; i++)
                _clusterLightLists[i].Clear();

            _directionalShadowBatchCache.TryGetValue(cmd.BatchId, out DirectionalShadowBatchData shadowBatch);

            foreach (SceneRenderLightSnapshot light in lightMap.Values.OrderBy(l => l.ObjectId))
            {
                if (!light.Active || !light.Visible)
                    continue;

                int lightMode = light.Settings.LightMode;
                uint gpuLightIndex = (uint)_uploadedLightCount;

                if (lightMode == (int)LightKind.Point)
                {
                    float range = (float)light.Settings.Range;
                    if (range <= 0.0001f)
                        continue;

                    Double3 relativePosition = light.World.Position - cmd.CameraWorldPosition;
                    Vector3 relativePositionF = new(
                        (float)relativePosition.X,
                        (float)relativePosition.Y,
                        (float)relativePosition.Z);

                    Vector3 viewSpacePosition = TransformPosition(cmd.View, relativePositionF);

                    AppendGpuPointLight(_gpuLightUploadScratch, relativePositionF, light);

                    AssignLightToClustersXYZ(
                        cmd.Projection,
                        viewSpacePosition,
                        range,
                        cmd.ClusterNear,
                        cmd.ClusterFar,
                        gpuLightIndex,
                        _clusterLightLists);

                    _uploadedLightCount++;
                    continue;
                }

                if (lightMode == (int)LightKind.Directional)
                {
                    Vector3 direction = ExtractDirectionalLightDirection(light);

                    DirectionalShadowLightBatchData matchedShadowData = null;
                    if (shadowBatch != null)
                        shadowBatch.ByLightObjectId.TryGetValue(light.ObjectId, out matchedShadowData);

                    int shadowCascadeStart = _gpuDirectionalShadowCascadeUploadScratch.Count / _gpuDirectionalShadowCascadeStrideFloats;
                    int shadowCascadeCount = 0;

                    if (matchedShadowData != null)
                    {
                        foreach (DirectionalShadowCascadeInfo cascade in matchedShadowData.Cascades)
                        {
                            if (!cascade.Valid)
                                continue;

                            AppendGpuDirectionalShadowCascade(_gpuDirectionalShadowCascadeUploadScratch, cascade);
                            shadowCascadeCount++;
                        }
                    }

                    AppendGpuDirectionalLight(
                        _gpuLightUploadScratch,
                        direction,
                        light,
                        shadowCascadeStart,
                        shadowCascadeCount);

                    AssignLightToAllClusters(
                        gpuLightIndex,
                        _clusterLightLists);

                    _uploadedLightCount++;
                    continue;
                }
            }

            if (_uploadedLightCount == 0)
            {
                UploadEmptyPointLightBuffer();
                return 0;
            }

            float[] gpuLightArray = _gpuLightUploadScratch.ToArray();

            BuildClusterBuffers(
                _clusterLightLists,
                out uint[] clusterRanges,
                out uint[] clusterIndices);

            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _clusterLightBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (ReadOnlySpan<float>)gpuLightArray,
                BufferUsageARB.DynamicDraw);

            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _clusterRangeBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (ReadOnlySpan<uint>)clusterRanges,
                BufferUsageARB.DynamicDraw);

            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _clusterIndexBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (ReadOnlySpan<uint>)clusterIndices,
                BufferUsageARB.DynamicDraw);

            float[] gpuDirectionalShadowCascadeArray =
                _gpuDirectionalShadowCascadeUploadScratch.Count > 0
                    ? _gpuDirectionalShadowCascadeUploadScratch.ToArray()
                    : new float[_gpuDirectionalShadowCascadeStrideFloats];

            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _directionalShadowCascadeBuffer);
            _gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (ReadOnlySpan<float>)gpuDirectionalShadowCascadeArray,
                BufferUsageARB.DynamicDraw);

            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

            return _uploadedLightCount;
        }

        private static void AppendGpuPointLight(
            List<float> dst,
            Vector3 relativePosition,
            SceneRenderLightSnapshot light)
        {
            static void AddVec4(List<float> buffer, float x, float y, float z, float w)
            {
                buffer.Add(x);
                buffer.Add(y);
                buffer.Add(z);
                buffer.Add(w);
            }

            static void AddIdentityMat4(List<float> buffer)
            {
                buffer.Add(1f); buffer.Add(0f); buffer.Add(0f); buffer.Add(0f);
                buffer.Add(0f); buffer.Add(1f); buffer.Add(0f); buffer.Add(0f);
                buffer.Add(0f); buffer.Add(0f); buffer.Add(1f); buffer.Add(0f);
                buffer.Add(0f); buffer.Add(0f); buffer.Add(0f); buffer.Add(1f);
            }

            // Meta0: x=Kind, y=Intensity, z=CastShadow, w=AttenuationCurve
            AddVec4(
                dst,
                0f,
                (float)light.Settings.Intensity,
                light.Settings.CastShadow ? 1f : 0f,
                (float)light.Settings.AttenuationCurve);

            // ColorRange: xyz=Color, w=Range
            AddVec4(
                dst,
                (float)light.Settings.Color.X,
                (float)light.Settings.Color.Y,
                (float)light.Settings.Color.Z,
                (float)light.Settings.Range);

            // PositionInner: xyz=Position, w=InnerAngle
            AddVec4(
                dst,
                relativePosition.X,
                relativePosition.Y,
                relativePosition.Z,
                0f);

            // DirectionOuter
            AddVec4(dst, 0f, -1f, 0f, 0f);

            // BoxSizeAreaWidth
            AddVec4(dst, 0f, 0f, 0f, 0f);

            // AreaRightAreaHeight
            AddVec4(dst, 1f, 0f, 0f, 0f);

            // AreaUpLineLength
            AddVec4(dst, 0f, 1f, 0f, 0f);

            // LineDirectionReserved
            AddVec4(dst, 1f, 0f, 0f, 0f);

            // ShadowAtlasRect
            AddVec4(dst, 0f, 0f, 0f, 0f);

            // ShadowMatrix
            AddIdentityMat4(dst);
        }

        private static Vector3 ExtractDirectionalLightDirection(SceneRenderLightSnapshot light)
        {
            Quaternion rotation = light.World.Rotation.ToSingle();

            Vector3 direction = Vector3.Transform(-Vector3.UnitY, rotation);

            if (direction.LengthSquared() <= 0.0000001f)
                direction = -Vector3.UnitY;

            return Vector3.Normalize(direction);
        }

        private static void AppendGpuDirectionalShadowCascade(
            List<float> dst,
            DirectionalShadowCascadeInfo cascade)
        {
            static void AddVec4(List<float> buffer, float x, float y, float z, float w)
            {
                buffer.Add(x);
                buffer.Add(y);
                buffer.Add(z);
                buffer.Add(w);
            }

            static void AddMat4(List<float> buffer, Matrix4x4 m)
            {
                buffer.Add(m.M11); buffer.Add(m.M12); buffer.Add(m.M13); buffer.Add(m.M14);
                buffer.Add(m.M21); buffer.Add(m.M22); buffer.Add(m.M23); buffer.Add(m.M24);
                buffer.Add(m.M31); buffer.Add(m.M32); buffer.Add(m.M33); buffer.Add(m.M34);
                buffer.Add(m.M41); buffer.Add(m.M42); buffer.Add(m.M43); buffer.Add(m.M44);
            }

            AddVec4(dst, cascade.AtlasRect.X, cascade.AtlasRect.Y, cascade.AtlasRect.Z, cascade.AtlasRect.W);
            AddMat4(dst, cascade.ShadowMatrix);
            AddVec4(dst, cascade.SplitNear, cascade.SplitFar, 0f, 0f);
        }

        private static void AppendGpuDirectionalLight(
    List<float> dst,
    Vector3 direction,
    SceneRenderLightSnapshot light,
    int shadowCascadeStart,
    int shadowCascadeCount)
        {
            static void AddVec4(List<float> buffer, float x, float y, float z, float w)
            {
                buffer.Add(x);
                buffer.Add(y);
                buffer.Add(z);
                buffer.Add(w);
            }

            static void AddIdentityMat4(List<float> buffer)
            {
                buffer.Add(1f); buffer.Add(0f); buffer.Add(0f); buffer.Add(0f);
                buffer.Add(0f); buffer.Add(1f); buffer.Add(0f); buffer.Add(0f);
                buffer.Add(0f); buffer.Add(0f); buffer.Add(1f); buffer.Add(0f);
                buffer.Add(0f); buffer.Add(0f); buffer.Add(0f); buffer.Add(1f);
            }

            bool hasShadow = shadowCascadeCount > 0;

            AddVec4(
                dst,
                (float)LightKind.Directional,
                (float)light.Settings.Intensity,
                hasShadow ? 1f : 0f,
                0.5f);

            AddVec4(
                dst,
                (float)light.Settings.Color.X,
                (float)light.Settings.Color.Y,
                (float)light.Settings.Color.Z,
                0f);

            AddVec4(dst, 0f, 0f, 0f, 0f);
            AddVec4(dst, direction.X, direction.Y, direction.Z, 0f);
            AddVec4(dst, shadowCascadeStart, shadowCascadeCount, 0f, 0f);
            AddVec4(dst, 0f, 0f, 0f, 0f);
            AddVec4(dst, 0f, 0f, 0f, 0f);
            AddVec4(dst, 0f, 0f, 0f, 0f);
            AddVec4(dst, 0f, 0f, 0f, 0f);
            AddIdentityMat4(dst);
        }

        private void AssignLightToAllClusters(
            uint lightIndex,
            List<uint>[] clusterLightLists)
        {
            for (int i = 0; i < clusterLightLists.Length; i++)
                clusterLightLists[i].Add(lightIndex);
        }

        private void AssignLightToClustersXYZ(
            Matrix4x4 projectionMatrix,
            Vector3 viewSpacePosition,
            float range,
            float clusterNear,
            float clusterFar,
            uint lightIndex,
            List<uint>[] clusterLightLists)
        {
            if (!TryGetProjectedTileBounds(
                    projectionMatrix,
                    viewSpacePosition,
                    range,
                    out int minTileX,
                    out int maxTileX,
                    out int minTileY,
                    out int maxTileY))
            {
                minTileX = 0;
                maxTileX = _clusterGridSizeX - 1;
                minTileY = 0;
                maxTileY = _clusterGridSizeY - 1;
            }

            if (!TryGetDepthSliceBounds(
                    viewSpacePosition,
                    range,
                    clusterNear,
                    clusterFar,
                    out int minSliceZ,
                    out int maxSliceZ))
            {
                return;
            }

            if (minTileX > maxTileX || minTileY > maxTileY || minSliceZ > maxSliceZ)
                return;

            for (int z = minSliceZ; z <= maxSliceZ; z++)
            {
                for (int y = minTileY; y <= maxTileY; y++)
                {
                    for (int x = minTileX; x <= maxTileX; x++)
                    {
                        int clusterIndex = GetClusterLinearIndex(x, y, z);
                        clusterLightLists[clusterIndex].Add(lightIndex);
                    }
                }
            }
        }

        private bool TryGetProjectedTileBounds(
            Matrix4x4 projectionMatrix,
            Vector3 viewSpacePosition,
            float range,
            out int minTileX,
            out int maxTileX,
            out int minTileY,
            out int maxTileY)
        {
            minTileX = 0;
            maxTileX = _clusterGridSizeX - 1;
            minTileY = 0;
            maxTileY = _clusterGridSizeY - 1;

            float depth = -viewSpacePosition.Z;

            if (depth + range <= 0f)
            {
                minTileX = 1;
                maxTileX = 0;
                minTileY = 1;
                maxTileY = 0;
                return true;
            }

            if (depth <= 0.001f || depth <= range)
                return false;

            Vector4 clip = Vector4.Transform(new Vector4(viewSpacePosition, 1f), projectionMatrix);

            if (MathF.Abs(clip.W) <= 0.00001f)
                return false;

            float centerNdcX = clip.X / clip.W;
            float centerNdcY = clip.Y / clip.W;

            float radiusNdcX = MathF.Abs(projectionMatrix.M11) * range / depth;
            float radiusNdcY = MathF.Abs(projectionMatrix.M22) * range / depth;

            float minNdcX = centerNdcX - radiusNdcX;
            float maxNdcX = centerNdcX + radiusNdcX;
            float minNdcY = centerNdcY - radiusNdcY;
            float maxNdcY = centerNdcY + radiusNdcY;

            if (maxNdcX < -1f || minNdcX > 1f || maxNdcY < -1f || minNdcY > 1f)
            {
                minTileX = 1;
                maxTileX = 0;
                minTileY = 1;
                maxTileY = 0;
                return true;
            }

            minNdcX = Math.Clamp(minNdcX, -1f, 1f);
            maxNdcX = Math.Clamp(maxNdcX, -1f, 1f);
            minNdcY = Math.Clamp(minNdcY, -1f, 1f);
            maxNdcY = Math.Clamp(maxNdcY, -1f, 1f);

            minTileX = NdcToTileMin(minNdcX, _clusterGridSizeX);
            maxTileX = NdcToTileMax(maxNdcX, _clusterGridSizeX);
            minTileY = NdcToTileMin(minNdcY, _clusterGridSizeY);
            maxTileY = NdcToTileMax(maxNdcY, _clusterGridSizeY);

            return true;
        }

        private bool TryGetDepthSliceBounds(
            Vector3 viewSpacePosition,
            float range,
            float clusterNear,
            float clusterFar,
            out int minSliceZ,
            out int maxSliceZ)
        {
            minSliceZ = 0;
            maxSliceZ = _clusterGridSizeZ - 1;

            if (_clusterGridSizeZ <= 0)
                return false;

            if (clusterFar <= clusterNear)
                return false;
            float centerDepth = -viewSpacePosition.Z;

            // 灯球在视空间里的深度范围
            float minDepth = centerDepth - range;
            float maxDepth = centerDepth + range;

            // 完全不在 cluster 深度范围内
            if (maxDepth < clusterNear || minDepth > clusterFar)
                return false;

            // 裁到 cluster 深度区间
            minDepth = Math.Clamp(minDepth, clusterNear, clusterFar);
            maxDepth = Math.Clamp(maxDepth, clusterNear, clusterFar);

            minSliceZ = DepthToClusterZMin(minDepth, clusterNear, clusterFar, _clusterGridSizeZ);
            maxSliceZ = DepthToClusterZMax(maxDepth, clusterNear, clusterFar, _clusterGridSizeZ);

            return minSliceZ <= maxSliceZ;
        }

        private static int DepthToClusterZMin(
            float depth,
            float clusterNear,
            float clusterFar,
            int sliceCount)
        {
            float normalized = (depth - clusterNear) / (clusterFar - clusterNear);
            normalized = Math.Clamp(normalized, 0f, 1f);

            int slice = (int)MathF.Floor(normalized * sliceCount);
            return Math.Clamp(slice, 0, sliceCount - 1);
        }

        private static int DepthToClusterZMax(
            float depth,
            float clusterNear,
            float clusterFar,
            int sliceCount)
        {
            float normalized = (depth - clusterNear) / (clusterFar - clusterNear);
            normalized = Math.Clamp(normalized, 0f, 1f);

            int slice = (int)MathF.Ceiling(normalized * sliceCount) - 1;
            return Math.Clamp(slice, 0, sliceCount - 1);
        }

        private static int NdcToTileMin(float ndc, int tileCount)
        {
            float normalized = ndc * 0.5f + 0.5f;
            int tile = (int)MathF.Floor(normalized * tileCount);
            return Math.Clamp(tile, 0, tileCount - 1);
        }

        private static int NdcToTileMax(float ndc, int tileCount)
        {
            float normalized = ndc * 0.5f + 0.5f;
            int tile = (int)MathF.Ceiling(normalized * tileCount) - 1;
            return Math.Clamp(tile, 0, tileCount - 1);
        }

        private static Vector3 TransformPosition(Matrix4x4 matrix, Vector3 position)
        {
            Vector4 result = Vector4.Transform(new Vector4(position, 1f), matrix);
            return new Vector3(result.X, result.Y, result.Z);
        }

        private static void BuildDirectionalLightBasis(Vector3 lightDirection, out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            forward = lightDirection.LengthSquared() <= 0.0000001f
                ? -Vector3.UnitY
                : Vector3.Normalize(lightDirection);

            if (forward.Z < -0.9999999f)
            {
                right = new Vector3(0f, -1f, 0f);
                up = new Vector3(-1f, 0f, 0f);
                return;
            }

            float a = 1f / (1f + forward.Z);
            float b = -forward.X * forward.Y * a;

            right = new Vector3(
                1f - forward.X * forward.X * a,
                b,
                -forward.X);

            up = new Vector3(
                b,
                1f - forward.Y * forward.Y * a,
                -forward.Y);

            right = Vector3.Normalize(right);
            up = Vector3.Normalize(up);
        }

        private static float ComputeFrustumBoundingSphereRadius(Vector3 center, Vector3[] corners)
        {
            float radiusSq = 0f;

            for (int i = 0; i < corners.Length; i++)
            {
                float distSq = Vector3.DistanceSquared(center, corners[i]);
                if (distSq > radiusSq)
                    radiusSq = distSq;
            }

            return MathF.Sqrt(radiusSq);
        }

        private Vector3 GetDirectionalShadowStableAnchorRelative(
    Double3 cameraWorldPosition,
    float gridWorldSize)
        {
            if (gridWorldSize <= 0.0000001f)
                return Vector3.Zero;

            double grid = gridWorldSize;

            if (!_directionalShadowStableAnchorInitialized)
            {
                _directionalShadowStableAnchorWorld = new Double3(
                    Math.Round(cameraWorldPosition.X / grid, MidpointRounding.AwayFromZero) * grid,
                    Math.Round(cameraWorldPosition.Y / grid, MidpointRounding.AwayFromZero) * grid,
                    Math.Round(cameraWorldPosition.Z / grid, MidpointRounding.AwayFromZero) * grid);

                _directionalShadowStableAnchorInitialized = true;
            }
            else
            {
                double dx = cameraWorldPosition.X - _directionalShadowStableAnchorWorld.X;
                double dy = cameraWorldPosition.Y - _directionalShadowStableAnchorWorld.Y;
                double dz = cameraWorldPosition.Z - _directionalShadowStableAnchorWorld.Z;

                if (Math.Abs(dx) > grid * 0.5)
                    _directionalShadowStableAnchorWorld.X = Math.Round(cameraWorldPosition.X / grid, MidpointRounding.AwayFromZero) * grid;

                if (Math.Abs(dy) > grid * 0.5)
                    _directionalShadowStableAnchorWorld.Y = Math.Round(cameraWorldPosition.Y / grid, MidpointRounding.AwayFromZero) * grid;

                if (Math.Abs(dz) > grid * 0.5)
                    _directionalShadowStableAnchorWorld.Z = Math.Round(cameraWorldPosition.Z / grid, MidpointRounding.AwayFromZero) * grid;
            }

            Double3 relative = _directionalShadowStableAnchorWorld - cameraWorldPosition;

            return new Vector3(
                (float)relative.X,
                (float)relative.Y,
                (float)relative.Z);
        }

        private static Vector3 SnapDirectionalShadowCenterToTexelStable(
            Vector3 centerCameraRelative,
            Vector3 stableAnchorCameraRelative,
            Vector3 right,
            Vector3 up,
            Vector3 forward,
            float texelWorldSize)
        {
            if (texelWorldSize <= 0.0000001f)
                return centerCameraRelative;

            double texel = texelWorldSize;

            double centerLocalX =
                (double)centerCameraRelative.X * right.X +
                (double)centerCameraRelative.Y * right.Y +
                (double)centerCameraRelative.Z * right.Z;

            double centerLocalY =
                (double)centerCameraRelative.X * up.X +
                (double)centerCameraRelative.Y * up.Y +
                (double)centerCameraRelative.Z * up.Z;

            double centerLocalZ =
                (double)centerCameraRelative.X * forward.X +
                (double)centerCameraRelative.Y * forward.Y +
                (double)centerCameraRelative.Z * forward.Z;

            double anchorLocalX =
                (double)stableAnchorCameraRelative.X * right.X +
                (double)stableAnchorCameraRelative.Y * right.Y +
                (double)stableAnchorCameraRelative.Z * right.Z;

            double anchorLocalY =
                (double)stableAnchorCameraRelative.X * up.X +
                (double)stableAnchorCameraRelative.Y * up.Y +
                (double)stableAnchorCameraRelative.Z * up.Z;

            double deltaX = centerLocalX - anchorLocalX;
            double deltaY = centerLocalY - anchorLocalY;

            double snappedDeltaX = Math.Round(deltaX / texel, MidpointRounding.AwayFromZero) * texel;
            double snappedDeltaY = Math.Round(deltaY / texel, MidpointRounding.AwayFromZero) * texel;

            double snappedLocalX = anchorLocalX + snappedDeltaX;
            double snappedLocalY = anchorLocalY + snappedDeltaY;

            return
                right * (float)snappedLocalX +
                up * (float)snappedLocalY +
                forward * (float)centerLocalZ;
        }

        private int GetClusterLinearIndex(int x, int y, int z)
        {
            return x + y * _clusterGridSizeX + z * _clusterGridSizeX * _clusterGridSizeY;
        }

        private void BuildClusterBuffers(
            List<uint>[] clusterLightLists,
            out uint[] clusterRanges,
            out uint[] clusterIndices)
        {
            clusterRanges = new uint[_clusterCount * 2];

            int totalIndexCount = 0;
            for (int i = 0; i < clusterLightLists.Length; i++)
                totalIndexCount += clusterLightLists[i].Count;

            if (totalIndexCount == 0)
            {
                clusterIndices = new uint[] { 0 };
                return;
            }

            clusterIndices = new uint[totalIndexCount];

            uint writeCursor = 0;
            for (int i = 0; i < clusterLightLists.Length; i++)
            {
                List<uint> list = clusterLightLists[i];

                clusterRanges[i * 2 + 0] = writeCursor;
                clusterRanges[i * 2 + 1] = (uint)list.Count;

                for (int j = 0; j < list.Count; j++)
                    clusterIndices[writeCursor++] = list[j];
            }
        }

        private void ApplyLightingSupportUniforms(in RenderCommand cmd)
        {
            InitializeLightingSupportResources();

            ProgramUniformLocationCache loc = GetProgramLocationCache(_currentProgram);

            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, _clusterLightBufferBinding, _clusterLightBuffer);
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, _clusterRangeBufferBinding, _clusterRangeBuffer);
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, _clusterIndexBufferBinding, _clusterIndexBuffer);
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, _directionalShadowCascadeBufferBinding, _directionalShadowCascadeBuffer);

            if (loc.CameraPosition != -1)
                _gl.Uniform3(loc.CameraPosition, cmd.CameraPosition.X, cmd.CameraPosition.Y, cmd.CameraPosition.Z);

            if (loc.AmbientColor != -1)
                _gl.Uniform3(loc.AmbientColor, _ambientLightColor.X, _ambientLightColor.Y, _ambientLightColor.Z);

            if (loc.AmbientIntensity != -1)
                _gl.Uniform1(loc.AmbientIntensity, _ambientLightIntensity);

            if (loc.ViewportOrigin != -1)
                _gl.Uniform2(loc.ViewportOrigin, (float)cmd.ViewportX, (float)cmd.ViewportY);

            if (loc.ViewportSize != -1)
                _gl.Uniform2(loc.ViewportSize, (float)cmd.ViewportWidth, (float)cmd.ViewportHeight);

            if (loc.ClusterGridSize != -1)
                _gl.Uniform3(loc.ClusterGridSize, _clusterGridSizeX, _clusterGridSizeY, _clusterGridSizeZ);

            if (loc.ClusterNear != -1)
                _gl.Uniform1(loc.ClusterNear, cmd.ClusterNear);

            if (loc.ClusterFar != -1)
                _gl.Uniform1(loc.ClusterFar, cmd.ClusterFar);

            int uploadedLightCount = UploadPointLightsForCommand(cmd);

            if (loc.LightCount != -1)
                _gl.Uniform1(loc.LightCount, uploadedLightCount);

            bool hasDirectionalShadow =
                _directionalShadowBatchCache.TryGetValue(cmd.BatchId, out DirectionalShadowBatchData shadowBatch) &&
                shadowBatch.ByLightObjectId.Values.Any(v => v.Cascades.Count > 0);

            if (loc.ShadowAtlasTexture != -1)
            {
                const int shadowAtlasUnit = 13;
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + shadowAtlasUnit));
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    hasDirectionalShadow ? _shadowAtlasTexture : _lightingDummyTexture);
                _gl.Uniform1(loc.ShadowAtlasTexture, shadowAtlasUnit);
            }

            int reflectionSourceMode = cmd.Material != null ? ReadReflectionSourceMode(cmd.Material) : 0;
            float reflectionIntensity = cmd.Material != null ? ReadMaterialFloat(cmd.Material, "uReflectionIntensity", 1f) : 1f;

            bool reflectionEnabled = false;
            uint reflectionCubeToBind = _reflectionDummyCubeTexture;
            TextureInfo reflectionMapTexture = default;

            if (reflectionSourceMode == 1 && cmd.Material != null)
            {
                if (TryGetReflectionEnvironmentCube(cmd.Material, cmd, out uint textureReflectionCube, out reflectionMapTexture))
                {
                    reflectionCubeToBind = textureReflectionCube;
                    reflectionEnabled = true;
                }
            }
            else
            {
                if (_capturedSkyboxReflectionValid)
                {
                    reflectionCubeToBind = _reflectionPrefilteredCube;
                    reflectionEnabled = true;
                }
            }

            if (loc.ReflectionTexture != -1)
            {
                const int reflection2DUnit = 15;
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + reflection2DUnit));

                if (reflectionSourceMode == 1 && reflectionMapTexture.Id != 0)
                    _gl.BindTexture(TextureTarget.Texture2D, reflectionMapTexture.Id);
                else
                    _gl.BindTexture(TextureTarget.Texture2D, _lightingDummyTexture);

                _gl.Uniform1(loc.ReflectionTexture, reflection2DUnit);
            }

            if (loc.ReflectionSkyboxCube != -1)
            {
                const int reflectionCubeUnit = 16;
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + reflectionCubeUnit));
                _gl.BindTexture(TextureTarget.TextureCubeMap, reflectionCubeToBind);
                _gl.Uniform1(loc.ReflectionSkyboxCube, reflectionCubeUnit);
            }

            if (loc.ReflectionSource != -1)
                _gl.Uniform1(loc.ReflectionSource, reflectionSourceMode);

            if (loc.ReflectionEnabled != -1)
                _gl.Uniform1(loc.ReflectionEnabled, reflectionEnabled ? 1 : 0);

            if (loc.ReflectionIntensity != -1)
                _gl.Uniform1(loc.ReflectionIntensity, reflectionEnabled ? reflectionIntensity : 0f);

            if (loc.UseOutlineNormal != -1)
                _gl.Uniform1(loc.UseOutlineNormal, cmd.VertexStrideFloats >= 19 ? 1 : 0);

            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        public void SetScreenSkybox(string shaderName, string parametersJson = "{}")
        {
            _screenSkybox = BuildSkyboxData("__screen__", shaderName, parametersJson);
        }

        public void ClearScreenSkybox()
        {
            _screenSkybox = null;
        }

        public void SetCameraSkybox(string cameraObjectId, string shaderName, string parametersJson = "{}")
        {
            if (string.IsNullOrWhiteSpace(cameraObjectId))
                throw new ArgumentException("[X] Camera skybox target id cannot be null or empty.", nameof(cameraObjectId));

            string key = cameraObjectId.Trim();
            _cameraSkyboxes[key] = BuildSkyboxData(key, shaderName, parametersJson);
        }

        public void ClearCameraSkybox(string cameraObjectId)
        {
            if (string.IsNullOrWhiteSpace(cameraObjectId))
                return;

            _cameraSkyboxes.Remove(cameraObjectId.Trim());
        }

        /// <summary>
        /// 初始化渲染资源
        /// </summary>
        private void InitQuadRenderer()
        {
            if (_quadInitialized) return;

            _quadVAO = _gl.GenVertexArray();
            _quadVBO = _gl.GenBuffer();

            _gl.BindVertexArray(_quadVAO);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVBO);

            float[] vertices = new float[6 * 9];

            _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)vertices, BufferUsageARB.DynamicDraw);

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 9 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 9 * sizeof(float), 7 * sizeof(float));
            _gl.EnableVertexAttribArray(2);

            _gl.BindVertexArray(0);

            _quadInitialized = true;
        }

        /// <summary>
        /// 初始化OpenGL资源
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;
            InitQuadRenderer();
            InitializeDynamicGeometryResources();
            // 创建VAO和VBO
            _vertexArrayObject = _gl.GenVertexArray();
            _vertexBufferObject = _gl.GenBuffer();

            _gl.BindVertexArray(_vertexArrayObject);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBufferObject);

            // 设置顶点属性指针 (位置: 3 floats, 颜色: 4 floats)
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 9 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 9 * sizeof(float), 7 * sizeof(float));
            _gl.EnableVertexAttribArray(2);


            // _shaderProgram = CreateShaderProgram();
            LoadShaders();
            InitializeLightingSupportResources();
            const GLEnum TextureCubeMapSeamless = (GLEnum)0x884F;
            _gl.Enable(TextureCubeMapSeamless);
            _gl.Enable(GLEnum.DepthTest);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);
            _gl.Enable(GLEnum.Blend);
            _gl.FrontFace(FrontFaceDirection.Ccw);
            ApplyCullMode(RenderCullMode.Front);

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);

            _isInitialized = true;
        }

        private int GetReflectionCubeMipCount()
        {
            return 1 + (int)MathF.Floor(MathF.Log2(_reflectionSkyboxCubeSize));
        }

        private uint CreateReflectionCubeTexture(bool withMipmaps)
        {
            int mipCount = withMipmaps ? GetReflectionCubeMipCount() : 1;

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureCubeMap, texture);

            for (int mip = 0; mip < mipCount; mip++)
            {
                int mipSize = Math.Max(1, _reflectionSkyboxCubeSize >> mip);
                float[] emptyFace = new float[mipSize * mipSize * 4];

                for (int face = 0; face < 6; face++)
                {
                    TextureTarget faceTarget = (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face);
                    _gl.TexImage2D(
                        (GLEnum)faceTarget,
                        mip,
                        (int)InternalFormat.Rgba16f,
                        (uint)mipSize,
                        (uint)mipSize,
                        0,
                        (GLEnum)PixelFormat.Rgba,
                        (GLEnum)PixelType.Float,
                        in emptyFace[0]);
                }
            }

            _gl.TexParameter(
                TextureTarget.TextureCubeMap,
                TextureParameterName.TextureMinFilter,
                withMipmaps ? (int)TextureMinFilter.LinearMipmapLinear : (int)TextureMinFilter.Linear);

            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
            return texture;
        }

        private uint CreateReflectionEquirectToCubeProgram()
        {
            string vertexSource = @"#version 430 core
                layout(location = 0) in vec3 aPos;
                out vec3 vLocalPos;

                void main()
                {
                    vLocalPos = aPos;
                    gl_Position = vec4(aPos, 1.0);
                }";

            string geometrySource = @"#version 430 core
                layout(triangles) in;
                layout(triangle_strip, max_vertices = 18) out;

                in vec3 vLocalPos[];
                out vec3 gLocalDir;

                uniform mat4 uViews[6];
                uniform mat4 uProjection;

                void main()
                {
                    for (int face = 0; face < 6; ++face)
                    {
                        gl_Layer = face;

                        for (int i = 0; i < 3; ++i)
                        {
                            gLocalDir = vLocalPos[i];
                            gl_Position = uProjection * uViews[face] * vec4(vLocalPos[i], 1.0);
                            EmitVertex();
                        }

                        EndPrimitive();
                    }
                }";

            string fragmentSource = @"#version 430 core
                out vec4 FragColor;
                in vec3 gLocalDir;

                uniform sampler2D uEquirectTexture;

                const vec2 InvAtan = vec2(0.15915494309189535, 0.3183098861837907);

                vec2 SampleSphericalMap(vec3 v)
                {
                    vec3 d = normalize(v);
                    vec2 uv = vec2(atan(d.z, d.x), asin(clamp(d.y, -1.0, 1.0)));
                    uv *= InvAtan;
                    uv += 0.5;
                    return uv;
                }

                void main()
                {
                    vec2 uv = SampleSphericalMap(normalize(gLocalDir));
                    vec3 color = texture(uEquirectTexture, uv).rgb;
                    FragColor = vec4(color, 1.0);
                }";

            uint vs = CompileShader(ShaderType.VertexShader, vertexSource);
            uint gs = CompileShader(ShaderType.GeometryShader, geometrySource);
            uint fs = CompileShader(ShaderType.FragmentShader, fragmentSource);

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vs);
            _gl.AttachShader(program, gs);
            _gl.AttachShader(program, fs);
            _gl.LinkProgram(program);

            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                throw new Exception($"[X] Reflection equirect-to-cube shader link failed: {infoLog}");
            }

            _gl.DetachShader(program, vs);
            _gl.DetachShader(program, gs);
            _gl.DetachShader(program, fs);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(gs);
            _gl.DeleteShader(fs);

            return program;
        }

        private uint CreateReflectionPrefilterProgram()
        {
            string vertexSource = @"#version 430 core
                layout(location = 0) in vec3 aPos;
                out vec3 vLocalPos;

                void main()
                {
                    vLocalPos = aPos;
                    gl_Position = vec4(aPos, 1.0);
                }";

            string geometrySource = @"#version 430 core
                layout(triangles) in;
                layout(triangle_strip, max_vertices = 18) out;

                in vec3 vLocalPos[];
                out vec3 gLocalDir;

                uniform mat4 uViews[6];
                uniform mat4 uProjection;

                void main()
                {
                    for (int face = 0; face < 6; ++face)
                    {
                        gl_Layer = face;

                        for (int i = 0; i < 3; ++i)
                        {
                            gLocalDir = vLocalPos[i];
                            gl_Position = uProjection * uViews[face] * vec4(vLocalPos[i], 1.0);
                            EmitVertex();
                        }

                        EndPrimitive();
                    }
                }";

            string fragmentSource = $@"#version 430 core
                out vec4 FragColor;
                in vec3 gLocalDir;

                uniform samplerCube uEnvironmentMap;
                uniform sampler1D uHammersleyLut;
                uniform float uRoughness;

                const float PI = 3.1415926535897932384626433832795;
                const int SAMPLE_COUNT = {_reflectionPrefilterSampleCount};

                vec3 ImportanceSampleGGX(vec2 Xi, vec3 N, vec3 T, vec3 B, float a)
                {{
                    float phi = 2.0 * PI * Xi.x;
                    float a2 = a * a;

                    float cosTheta = sqrt((1.0 - Xi.y) / (1.0 + (a2 - 1.0) * Xi.y));
                    float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));

                    vec3 Ht;
                    Ht.x = cos(phi) * sinTheta;
                    Ht.y = sin(phi) * sinTheta;
                    Ht.z = cosTheta;

                    return normalize(T * Ht.x + B * Ht.y + N * Ht.z);
                }}

                void main()
                {{
                    vec3 N = normalize(gLocalDir);

                    if (uRoughness <= 0.0001)
                    {{
                        FragColor = vec4(texture(uEnvironmentMap, N).rgb, 1.0);
                        return;
                    }}

                    float a = max(uRoughness * uRoughness, 0.001);

                    vec3 up = abs(N.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
                    vec3 T = normalize(cross(up, N));
                    vec3 B = cross(N, T);

                    vec3 prefilteredColor = vec3(0.0);
                    float totalWeight = 0.0;

                    for (int i = 0; i < SAMPLE_COUNT; ++i)
                    {{
                        vec2 Xi = texelFetch(uHammersleyLut, i, 0).rg;
                        vec3 H = ImportanceSampleGGX(Xi, N, T, B, a);
                        vec3 L = normalize(2.0 * dot(N, H) * H - N);

                        float NdotL = max(dot(N, L), 0.0);
                        if (NdotL > 0.0)
                        {{
                            prefilteredColor += texture(uEnvironmentMap, L).rgb * NdotL;
                            totalWeight += NdotL;
                        }}
                    }}

                    prefilteredColor /= max(totalWeight, 0.0001);
                    FragColor = vec4(prefilteredColor, 1.0);
                }}";

            uint vs = CompileShader(ShaderType.VertexShader, vertexSource);
            uint gs = CompileShader(ShaderType.GeometryShader, geometrySource);
            uint fs = CompileShader(ShaderType.FragmentShader, fragmentSource);

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vs);
            _gl.AttachShader(program, gs);
            _gl.AttachShader(program, fs);
            _gl.LinkProgram(program);

            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                throw new Exception($"[X] Reflection prefilter shader link failed: {infoLog}");
            }

            _gl.DetachShader(program, vs);
            _gl.DetachShader(program, gs);
            _gl.DetachShader(program, fs);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(gs);
            _gl.DeleteShader(fs);

            return program;
        }

        private void RenderEquirectTextureToCube(uint equirectTextureId, uint targetCube)
        {
            if (equirectTextureId == 0 || targetCube == 0)
                return;

            Matrix4x4 captureProjection = CreatePerspective(MathF.PI / 2f, 1f, 0.1f, 1f);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _reflectionCaptureFramebuffer);
            _gl.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, targetCube, 0);

            _gl.Viewport(0, 0, (uint)_reflectionSkyboxCubeSize, (uint)_reflectionSkyboxCubeSize);
            _gl.Disable(GLEnum.ScissorTest);
            _gl.Disable(GLEnum.Blend);
            _gl.Disable(GLEnum.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(GLEnum.CullFace);

            _currentProgram = _reflectionEquirectToCubeProgram;
            _gl.UseProgram(_reflectionEquirectToCubeProgram);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, equirectTextureId);

            if (_reflectionEquirectTextureLoc != -1)
                _gl.Uniform1(_reflectionEquirectTextureLoc, 0);

            if (_reflectionEquirectProjectionLoc != -1)
                SetMatrixUniform(_reflectionEquirectProjectionLoc, captureProjection);

            if (_reflectionEquirectViewsLoc != -1)
                SetMatrixUniformArray(_reflectionEquirectViewsLoc, _reflectionCaptureViews);

            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            _activeRenderSpace = RenderSpace.Camera;
            _activeModelMatrix = Matrix4x4.Identity;
            _activeViewMatrix = Matrix4x4.Identity;
            _activeProjectionMatrix = captureProjection;

            BindReflectionCubeGeometry();
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_reflectionCubeVertexCount);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        private void PrefilterReflectionCube(uint sourceCube, uint targetCube)
        {
            if (sourceCube == 0 || targetCube == 0)
                return;

            int mipLevels = GetReflectionCubeMipCount();

            for (uint face = 0; face < 6; face++)
            {
                _gl.CopyImageSubData(
                    sourceCube,
                    CopyImageSubDataTarget.TextureCubeMap,
                    0,
                    0,
                    0,
                    (int)face,
                    targetCube,
                    CopyImageSubDataTarget.TextureCubeMap,
                    0,
                    0,
                    0,
                    (int)face,
                    (uint)_reflectionSkyboxCubeSize,
                    (uint)_reflectionSkyboxCubeSize,
                    1);
            }

            Matrix4x4 captureProjection = CreatePerspective(MathF.PI / 2f, 1f, 0.1f, 1f);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _reflectionCaptureFramebuffer);
            _gl.Disable(GLEnum.ScissorTest);
            _gl.Disable(GLEnum.Blend);
            _gl.Disable(GLEnum.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(GLEnum.CullFace);

            _currentProgram = _reflectionPrefilterProgram;
            _gl.UseProgram(_reflectionPrefilterProgram);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.TextureCubeMap, sourceCube);

            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 1));
            _gl.BindTexture(TextureTarget.Texture1D, _reflectionHammersleyLutTexture);

            if (_reflectionPrefilterEnvironmentLoc != -1)
                _gl.Uniform1(_reflectionPrefilterEnvironmentLoc, 0);

            if (_reflectionPrefilterHammersleyLoc != -1)
                _gl.Uniform1(_reflectionPrefilterHammersleyLoc, 1);

            if (_reflectionPrefilterProjectionLoc != -1)
                SetMatrixUniform(_reflectionPrefilterProjectionLoc, captureProjection);

            if (_reflectionPrefilterViewsLoc != -1)
                SetMatrixUniformArray(_reflectionPrefilterViewsLoc, _reflectionCaptureViews);

            _activeRenderSpace = RenderSpace.Camera;
            _activeModelMatrix = Matrix4x4.Identity;
            _activeViewMatrix = Matrix4x4.Identity;
            _activeProjectionMatrix = captureProjection;

            BindReflectionCubeGeometry();

            for (int mip = 1; mip < mipLevels; mip++)
            {
                int mipSize = Math.Max(1, _reflectionSkyboxCubeSize >> mip);
                float mipT = mip / (float)(mipLevels - 1);
                float roughness = mipT * mipT;

                _gl.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, targetCube, mip);
                _gl.Viewport(0, 0, (uint)mipSize, (uint)mipSize);

                if (_reflectionPrefilterRoughnessLoc != -1)
                    _gl.Uniform1(_reflectionPrefilterRoughnessLoc, roughness);

                _gl.ClearColor(0f, 0f, 0f, 1f);
                _gl.Clear(ClearBufferMask.ColorBufferBit);

                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_reflectionCubeVertexCount);
            }

            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 1));
            _gl.BindTexture(TextureTarget.Texture1D, 0);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
        }

        private void RestoreStateAfterReflectionUtility(
    in RenderCommand cmd,
    uint previousProgram,
    RenderSpace previousRenderSpace,
    Matrix4x4 previousModel,
    Matrix4x4 previousView,
    Matrix4x4 previousProjection)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Viewport(
                cmd.ViewportX,
                cmd.ViewportY,
                (uint)Math.Max(1, cmd.ViewportWidth),
                (uint)Math.Max(1, cmd.ViewportHeight));

            _gl.Disable(GLEnum.ScissorTest);
            _gl.Enable(GLEnum.DepthTest);
            _gl.DepthFunc(GLEnum.Less);
            _gl.DepthMask(true);
            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

            _currentProgram = previousProgram;
            _gl.UseProgram(previousProgram);

            _activeRenderSpace = previousRenderSpace;
            _activeModelMatrix = previousModel;
            _activeViewMatrix = previousView;
            _activeProjectionMatrix = previousProjection;

            ApplyCullMode(cmd.CullMode);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
        }

        private bool TryGetReflectionEnvironmentCube(
            MaterialData material,
            in RenderCommand cmd,
            out uint prefilteredCube,
            out TextureInfo sourceTexture)
        {
            prefilteredCube = 0;
            sourceTexture = default;

            if (material == null)
                return false;

            string mapPath = ReadMaterialString(material, "uReflectionMap", "");
            if (string.IsNullOrWhiteSpace(mapPath))
                return false;

            if (!TryResolveTexturePath(mapPath, out string fullPath))
                return false;

            if (!File.Exists(fullPath))
                return false;

            long sourceLastWriteUtcTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;

            if (_reflectionTextureEnvironmentCache.TryGetValue(fullPath, out ReflectionTextureEnvironmentCacheEntry cached))
            {
                if (cached.SourceLastWriteUtcTicks == sourceLastWriteUtcTicks)
                {
                    sourceTexture = LoadTexture(fullPath);
                    prefilteredCube = cached.PrefilteredCube;
                    return prefilteredCube != 0;
                }

                InvalidateReflectionTextureEnvironmentCache(fullPath);
            }

            sourceTexture = LoadTexture(fullPath);
            if (sourceTexture.Id == 0)
                return false;

            InitializeReflectionCaptureResources();

            uint previousProgram = _currentProgram;
            RenderSpace previousRenderSpace = _activeRenderSpace;
            Matrix4x4 previousModel = _activeModelMatrix;
            Matrix4x4 previousView = _activeViewMatrix;
            Matrix4x4 previousProjection = _activeProjectionMatrix;

            uint sourceCube = CreateReflectionCubeTexture(withMipmaps: false);
            uint targetPrefilteredCube = CreateReflectionCubeTexture(withMipmaps: true);

            RenderEquirectTextureToCube(sourceTexture.Id, sourceCube);
            PrefilterReflectionCube(sourceCube, targetPrefilteredCube);

            RestoreStateAfterReflectionUtility(
                cmd,
                previousProgram,
                previousRenderSpace,
                previousModel,
                previousView,
                previousProjection);

            _reflectionTextureEnvironmentCache[fullPath] = new ReflectionTextureEnvironmentCacheEntry
            {
                SourcePath = fullPath,
                SourceCube = sourceCube,
                PrefilteredCube = targetPrefilteredCube,
                SourceLastWriteUtcTicks = sourceLastWriteUtcTicks
            };

            prefilteredCube = targetPrefilteredCube;
            return true;
        }

        private void InvalidateReflectionTextureEnvironmentCache(string fullPath)
        {
            if (_reflectionTextureEnvironmentCache.TryGetValue(fullPath, out ReflectionTextureEnvironmentCacheEntry entry))
            {
                if (entry.SourceCube != 0)
                    _gl.DeleteTexture(entry.SourceCube);

                if (entry.PrefilteredCube != 0)
                    _gl.DeleteTexture(entry.PrefilteredCube);

                _reflectionTextureEnvironmentCache.Remove(fullPath);
            }

            if (_textures.TryGetValue(fullPath, out TextureInfo texture))
            {
                if (texture.Id != 0)
                    _gl.DeleteTexture(texture.Id);

                _textures.Remove(fullPath);
            }
        }

        /// <summary>
        /// 编译着色器
        /// </summary>
        private uint CompileShader(ShaderType type, string source)
        {
            uint shader = _gl.CreateShader(type);
            _gl.ShaderSource(shader, source);
            _gl.CompileShader(shader);

            // 检查编译错误
            _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetShaderInfoLog(shader);
                throw new Exception($"[X] Shader compilation failed: {infoLog}");
            }
            return shader;
        }

        /// <summary>
        /// 设置当前绘制颜色 (分量0-1)
        /// </summary>
        public void SetColor(float r, float g, float b, float a = 1.0f)
        {
            _currentColor = new Vector4(r, g, b, a);
        }

        /// <summary>
        /// 设置当前绘制颜色 (整数0-255)
        /// </summary>
        public void SetColorRGB(int r, int g, int b, int a = 255)
        {
            _currentColor = new Vector4(r / 255.0f, g / 255.0f, b / 255.0f, a / 255.0f);
        }

        /// <summary>
        /// 设置背景色
        /// </summary>
        public void SetBackgroundColor(float r, float g, float b, float a = 1.0f)
        {
            _backgroundColor = new Vector4(r, g, b, a);
        }

        /// <summary>
        /// 执行清屏
        /// </summary>
        [MoonSharpHidden]
        public void ClearBackground()
        {
            _gl.ClearColor(_backgroundColor.X, _backgroundColor.Y, _backgroundColor.Z, _backgroundColor.W);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        }


        /// <summary>
        /// 绘制单个点
        /// </summary>
        public void DrawPoint(float x, float y, float z = 0)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();
            AddVertex(x, y, z, 0f, 0f);
            Flush(PrimitiveType.Points);
        }

        /// <summary>
        /// 批量绘制多个点
        /// </summary>
        public void DrawPoints(Table points)
        {
            EnsureLuaCanvasMode();
            // 清空缓冲
            _vertexBuffer.Clear();

            // 将Lua表转换为顶点数据
            for (int i = 1; i <= points.Length; i += 3)
            {
                float x = (float)points.Get(i).Number;
                float y = (float)points.Get(i + 1).Number;
                float z = (float)points.Get(i + 2).Number;

                AddVertex(x, y, z);
            }

            // 批量绘制
            Flush(PrimitiveType.Points);
        }

        /// <summary>
        /// 绘制线条（两点一线）
        /// </summary>
        public void DrawLine(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();
            AddVertex(x1, y1, z1);
            AddVertex(x2, y2, z2);
            Flush(PrimitiveType.Lines);
        }

        /// <summary>
        /// 绘制连续线条（折线）
        /// </summary>
        public void DrawLineStrip(Table points)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();

            for (int i = 1; i <= points.Length; i += 3)
            {
                float x = (float)points.Get(i).Number;
                float y = (float)points.Get(i + 1).Number;
                float z = (float)points.Get(i + 2).Number;

                AddVertex(x, y, z);
            }

            Flush(PrimitiveType.LineStrip);
        }

        /// <summary>
        /// 绘制三角形
        /// </summary>
        public void DrawTriangle(float x1, float y1, float z1,
                                 float x2, float y2, float z2,
                                 float x3, float y3, float z3)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();
            AddVertex(x1, y1, z1);
            AddVertex(x2, y2, z2);
            AddVertex(x3, y3, z3);
            Flush(PrimitiveType.Triangles);
        }

        /// <summary>
        /// 绘制四边形
        /// </summary>
        public void DrawQuad(
            float x1, float y1, float z1,
            float x2, float y2, float z2,
            float x3, float y3, float z3,
            float x4, float y4, float z4)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();

            // triangle 1
            AddVertex(x1, y1, z1);
            AddVertex(x2, y2, z2);
            AddVertex(x3, y3, z3);

            // triangle 2
            AddVertex(x3, y3, z3);
            AddVertex(x4, y4, z4);
            AddVertex(x1, y1, z1);

            Flush(PrimitiveType.Triangles);
        }

        /// <summary>
        /// 绘制矩形（2D平面）
        /// </summary>
        public void DrawRect(float x, float y, float width, float height)
        {
            EnsureLuaCanvasMode();
            DrawQuad(x, y, 0,
                    x + width, y, 0,
                    x + width, y + height, 0,
                    x, y + height, 0);
        }
        /// <summary>
        /// 绘制纹理面
        /// </summary>
        /// <param name="x1"></param>
        /// <param name="y1"></param>
        /// <param name="z1"></param>
        /// <param name="x2"></param>
        /// <param name="y2"></param>
        /// <param name="z2"></param>
        /// <param name="x3"></param>
        /// <param name="y3"></param>
        /// <param name="z3"></param>
        /// <param name="x4"></param>
        /// <param name="y4"></param>
        /// <param name="z4"></param>
        /// <param name="texturePath"></param>
        public void DrawTextured(
            float x1, float y1, float z1,
            float x2, float y2, float z2,
            float x3, float y3, float z3,
            float x4, float y4, float z4,
            string texturePath)
        {
            EnsureLuaCanvasMode();

            int texLoc = GetProgramLocationCache(_currentProgram).Texture;
            if (texLoc == -1)
            {
                Console.WriteLine("[X] Current shader does not support texture");
                return;
            }

            if (!TryResolveTexturePath(texturePath, out string fullPath))
            {
                Console.WriteLine($"[X] Texture not indexed: {texturePath}");
                return;
            }

            TextureInfo tex = LoadTexture(fullPath);
            if (tex.Id == 0)
            {
                Console.WriteLine($"[X] Texture not found: {fullPath}");
                return;
            }

            float[] vertices =
            {
                x1,y1,z1, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0,0,
                x4,y4,z4, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0,1,
                x3,y3,z3, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1,1,

                x3,y3,z3, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1,1,
                x2,y2,z2, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1,0,
                x1,y1,z1, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0,0
            };

            bool transparent = IsCurrentDrawTransparent(true, tex.HasTransparency);

            var cmd = new RenderCommand
            {
                Vertices = vertices,
                PrimitiveType = PrimitiveType.Triangles,
                Program = _currentProgram,
                UseTexture = true,
                TextureId = tex.Id,
                SceneId = "",
                CameraWorldPosition = Double3.Zero,
                VertexStrideFloats = 9,
                CameraPosition = Vector3.Zero,
                ClusterNear = 0.1f,
                ClusterFar = 1f,
                RenderSpace = _activeRenderSpace,
                Model = _activeModelMatrix,
                View = _activeViewMatrix,
                Projection = _activeProjectionMatrix,
                QueueType = transparent ? RenderQueueType.Transparent : RenderQueueType.Opaque,
                SortDepth = ComputeSortDepth(vertices, 9, _activeModelMatrix, _activeViewMatrix, _activeRenderSpace),
                SubmissionIndex = _submissionCounter++,
                Pass = RenderPass.Canvas,
                BatchId = -1,
                BatchSubmissionOrder = -1,
                ViewportX = 0,
                ViewportY = 0,
                ViewportWidth = _window.Size.X,
                ViewportHeight = _window.Size.Y,
                Material = null,
                Skybox = null,
                ForceWhiteVertexColor = false,
                IsSkybox = false,
                CullMode = _currentCullMode,
                MeshId = "",
                MeshSurfaceId = "",
                MeshVertexColorsAreWhite = false
            };

            _renderQueue.Add(cmd);
        }

        /// <summary>
        /// 绘制带纹理的四边形
        /// </summary>
        public void DrawTexturedQuad(float x1, float y1, float x2, float y2, string texturePath)
        {
            EnsureLuaCanvasMode();

            int texLoc = GetProgramLocationCache(_currentProgram).Texture;
            if (texLoc == -1)
            {
                Console.WriteLine("[X] Current shader does not support texture");
                return;
            }

            if (!TryResolveTexturePath(texturePath, out string fullPath))
            {
                Console.WriteLine($"[X] Texture not indexed: {texturePath}");
                return;
            }

            TextureInfo tex = LoadTexture(fullPath);
            if (tex.Id == 0)
            {
                Console.WriteLine($"[X] The texture file does not exist: {fullPath}");
                return;
            }

            float[] vertices =
            {
                x1, y1, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0, 0,
                x2, y1, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1, 0,
                x2, y2, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1, 1,

                x2, y2, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1, 1,
                x1, y2, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0, 1,
                x1, y1, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0, 0
            };

            bool transparent = IsCurrentDrawTransparent(true, tex.HasTransparency);

            var cmd = new RenderCommand
            {
                Vertices = vertices,
                PrimitiveType = PrimitiveType.Triangles,
                Program = _currentProgram,
                UseTexture = true,
                TextureId = tex.Id,
                SceneId = "",
                CameraWorldPosition = Double3.Zero,
                VertexStrideFloats = 9,
                CameraPosition = Vector3.Zero,
                ClusterNear = 0.1f,
                ClusterFar = 1f,
                RenderSpace = _activeRenderSpace,
                Model = _activeModelMatrix,
                View = _activeViewMatrix,
                Projection = _activeProjectionMatrix,
                QueueType = transparent ? RenderQueueType.Transparent : RenderQueueType.Opaque,
                SortDepth = ComputeSortDepth(vertices, 9, _activeModelMatrix, _activeViewMatrix, _activeRenderSpace),
                SubmissionIndex = _submissionCounter++,
                Pass = RenderPass.Canvas,
                BatchId = -1,
                BatchSubmissionOrder = -1,
                ViewportX = 0,
                ViewportY = 0,
                ViewportWidth = _window.Size.X,
                ViewportHeight = _window.Size.Y,
                Material = null,
                Skybox = null,
                ForceWhiteVertexColor = false,
                IsSkybox = false,
                CullMode = _currentCullMode,
                MeshId = "",
                MeshSurfaceId = "",
                MeshVertexColorsAreWhite = false
            };

            _renderQueue.Add(cmd);
        }


        /// <summary>
        /// 从文件加载纹理
        /// </summary>
        private TextureInfo LoadTexture(string path)
        {
            if (_textures.TryGetValue(path, out TextureInfo existingTex))
                return existingTex;

            if (!File.Exists(path))
            {
                Console.WriteLine($"[X] The texture file does not exist: {path}");
                return default;
            }

            try
            {
                using (Image<Rgba32> image = Image.Load<Rgba32>(path))
                {
                    image.Mutate(x => x.Flip(FlipMode.Vertical));

                    uint texture = _gl.GenTexture();
                    _gl.BindTexture(TextureTarget.Texture2D, texture);

                    int pixelCount = image.Width * image.Height;
                    Rgba32[] pixels = new Rgba32[pixelCount];
                    image.CopyPixelDataTo(pixels);

                    byte[] pixelBytes = new byte[pixelCount * 4];
                    bool hasTransparency = false;

                    for (int i = 0; i < pixelCount; i++)
                    {
                        pixelBytes[i * 4] = pixels[i].R;
                        pixelBytes[i * 4 + 1] = pixels[i].G;
                        pixelBytes[i * 4 + 2] = pixels[i].B;
                        pixelBytes[i * 4 + 3] = pixels[i].A;

                        if (pixels[i].A < 255)
                            hasTransparency = true;
                    }

                    _gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.Rgba,
                        (uint)image.Width,
                        (uint)image.Height,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        (ReadOnlySpan<byte>)pixelBytes);

                    _gl.GenerateMipmap(TextureTarget.Texture2D);

                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                    TextureInfo info = new TextureInfo
                    {
                        Id = texture,
                        HasTransparency = hasTransparency,
                        Width = image.Width,
                        Height = image.Height
                    };

                    _textures[path] = info;
                    return info;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[X] Failed to load texture {path}: {ex.Message}");
                return default;
            }
        }

        private string NormalizeMaterialKey(string raw)
        {
            string key = raw.Replace('\\', '/').Trim();

            if (key.StartsWith("/"))
                key = key[1..];

            if (key.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                key = key["Assets/".Length..];

            if (key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                key = key[..^5];

            return key;
        }

        private uint GetFallbackProgram()
        {
            const string fallbackKey = "__internal_fallback_purple__";

            if (!_shaderPrograms.TryGetValue(fallbackKey, out uint fallbackProgram))
            {
                fallbackProgram = CreateFallbackShaderProgram();
                _shaderPrograms[fallbackKey] = fallbackProgram;
            }

            return fallbackProgram;
        }

        private MaterialData GetFallbackMaterial()
        {
            if (_fallbackMaterial != null)
                return _fallbackMaterial;

            _fallbackMaterial = new MaterialData
            {
                Id = "__internal_fallback_purple__",
                Program = GetFallbackProgram(),
                Parameters = _emptyJsonObject,
                CullMode = RenderCullMode.Front
            };

            return _fallbackMaterial;
        }
        private JsonElement ParseSkyboxParameters(string parametersJson)
        {
            string json = string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson;

            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("[X] Skybox parameters must be a JSON object.");

            return doc.RootElement.Clone();
        }

        private SkyboxData BuildSkyboxData(string id, string shaderKey, string parametersJson)
        {
            JsonElement parameters = ParseSkyboxParameters(parametersJson);
            return new SkyboxData
            {
                Id = id,
                Program = ResolveShaderProgramOrFallback(shaderKey),
                Parameters = parameters,
                CullMode = ReadMaterialCullMode(parameters, RenderCullMode.Back)
            };
        }

        private string ResolveMainScreenCameraId()
        {
            foreach (SceneData scene in Scene.GetLoadedScenes())
            {
                IReadOnlyList<SceneCameraQueueItem> cameraQueue = Scene.GetCameraQueue(scene.SceneId);

                foreach (SceneCameraQueueItem cameraItem in cameraQueue.OrderBy(c => c.SubmissionOrder))
                {
                    if (cameraItem.Settings.RenderMode != 0)
                        continue;

                    if (cameraItem.Settings.IsMainCamera)
                        return cameraItem.ObjectId;
                }
            }

            return null;
        }

        private SkyboxData ResolveSkyboxForCamera(string cameraObjectId, int renderMode, string mainScreenCameraId)
        {
            if (renderMode == 0)
            {
                if (_screenSkybox == null)
                    return null;

                if (string.IsNullOrWhiteSpace(mainScreenCameraId))
                    return null;

                return string.Equals(cameraObjectId, mainScreenCameraId, StringComparison.Ordinal)
                    ? _screenSkybox
                    : null;
            }

            if (_cameraSkyboxes.TryGetValue(cameraObjectId, out SkyboxData skybox))
                return skybox;

            return null;
        }

        private uint ResolveShaderProgramOrFallback(string shaderKey)
        {
            if (!string.IsNullOrWhiteSpace(shaderKey) &&
                _shaderPrograms.TryGetValue(shaderKey, out uint program))
            {
                return program;
            }

            return GetFallbackProgram();
        }

        private bool TryBuildMaterialDataFromJson(string materialKey, string json, out MaterialData material)
        {
            material = null;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);

                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                string shaderKey = "";
                if (root.TryGetProperty("shader", out JsonElement shaderElement) &&
                    shaderElement.ValueKind == JsonValueKind.String)
                {
                    shaderKey = shaderElement.GetString() ?? "";
                }

                JsonElement parameters = _emptyJsonObject;
                if (root.TryGetProperty("parameters", out JsonElement parametersElement) &&
                    parametersElement.ValueKind == JsonValueKind.Object)
                {
                    parameters = parametersElement.Clone();
                }

                Vector2 textureUV = ReadMaterialTextureUV(parameters);
                MaterialTextureWrapMode textureWrap = ReadMaterialTextureWrap(parameters);
                RenderCullMode cullMode = ReadMaterialCullMode(parameters, RenderCullMode.Front);

                material = new MaterialData
                {
                    Id = materialKey,
                    Program = ResolveShaderProgramOrFallback(shaderKey),
                    Parameters = parameters,
                    TextureUV = textureUV,
                    TextureWrap = textureWrap,
                    CullMode = cullMode
                };

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Failed to parse material '{materialKey}': {ex.Message}");
                return false;
            }
        }

        private bool TryLoadMaterial(string materialKey, out MaterialData material)
        {
            material = null;

            string key = NormalizeMaterialKey(materialKey);

            if (_materialCache.TryGetValue(key, out MaterialData cached))
            {
                material = cached;
                return true;
            }

            if (Program._generatedMaterialJsonRegistry.TryGetValue(key, out string generatedJson))
            {
                if (TryBuildMaterialDataFromJson(key, generatedJson, out material))
                {
                    _materialCache[key] = material;
                    return true;
                }

                return false;
            }

            if (!Program._materialFileRegistry.TryGetValue(key, out string filePath))
                return false;

            if (!File.Exists(filePath))
                return false;

            try
            {
                string json = File.ReadAllText(filePath);

                if (!TryBuildMaterialDataFromJson(key, json, out material))
                    return false;

                _materialCache[key] = material;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Failed to load material '{materialKey}': {ex.Message}");
                return false;
            }
        }

        private sealed class ParsedMtlMaterial
        {
            public string Name { get; set; } = "";
            public Vector3 DiffuseColor { get; set; } = Vector3.One;
            public Vector3 SpecularColor { get; set; } = Vector3.One;
            public Vector3 AmbientColor { get; set; } = Vector3.One;
            public float Alpha { get; set; } = 1f;
            public float Shininess { get; set; } = 0f;

            public string? DiffuseMapRaw { get; set; }
            public string? NormalMapRaw { get; set; }
            public string? SpecularMapRaw { get; set; }
        }

        private static bool TryParseMtlFloat(string raw, out float value)
        {
            return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseMtlVec3(string raw, out Vector3 value)
        {
            value = Vector3.One;

            string[] parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return false;

            if (!TryParseMtlFloat(parts[0], out float x)) return false;
            if (!TryParseMtlFloat(parts[1], out float y)) return false;
            if (!TryParseMtlFloat(parts[2], out float z)) return false;

            value = new Vector3(x, y, z);
            return true;
        }

        private Dictionary<string, ParsedMtlMaterial> ParseMtlFile(string mtlFilePath)
        {
            var result = new Dictionary<string, ParsedMtlMaterial>(StringComparer.Ordinal);
            ParsedMtlMaterial? current = null;

            foreach (string rawLine in File.ReadAllLines(mtlFilePath))
            {
                string line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int split = line.IndexOfAny(new[] { ' ', '\t' });
                string keyword = split >= 0 ? line[..split] : line;
                string rest = split >= 0 ? line[(split + 1)..].Trim() : "";

                switch (keyword)
                {
                    case "newmtl":
                        if (!string.IsNullOrWhiteSpace(rest))
                        {
                            current = new ParsedMtlMaterial { Name = rest };
                            result[current.Name] = current;
                        }
                        break;

                    case "Kd":
                        if (current != null && TryParseMtlVec3(rest, out Vector3 kd))
                            current.DiffuseColor = kd;
                        break;

                    case "Ka":
                        if (current != null && TryParseMtlVec3(rest, out Vector3 ka))
                            current.AmbientColor = ka;
                        break;

                    case "Ks":
                        if (current != null && TryParseMtlVec3(rest, out Vector3 ks))
                            current.SpecularColor = ks;
                        break;

                    case "Ns":
                        if (current != null && TryParseMtlFloat(rest, out float ns))
                            current.Shininess = ns;
                        break;

                    case "d":
                        if (current != null && TryParseMtlFloat(rest, out float d))
                            current.Alpha = Math.Clamp(d, 0f, 1f);
                        break;

                    case "Tr":
                        if (current != null && TryParseMtlFloat(rest, out float tr))
                            current.Alpha = Math.Clamp(1f - tr, 0f, 1f);
                        break;

                    case "map_Kd":
                        if (current != null)
                            current.DiffuseMapRaw = ExtractMtlTexturePath(rest);
                        break;

                    case "map_Ks":
                        if (current != null)
                            current.SpecularMapRaw = ExtractMtlTexturePath(rest);
                        break;

                    case "map_Bump":
                    case "map_bump":
                    case "bump":
                    case "norm":
                        if (current != null)
                            current.NormalMapRaw = ExtractMtlTexturePath(rest);
                        break;
                }
            }

            return result;
        }

        private string? TryResolveMtlTextureToAssetRelative(string assetsRoot, string mtlFilePath, string? rawTexturePath)
        {
            if (string.IsNullOrWhiteSpace(rawTexturePath))
                return null;

            string assetsRootFull = Path.GetFullPath(assetsRoot);
            string mtlDir = Path.GetDirectoryName(mtlFilePath) ?? assetsRoot;

            string fullPath = rawTexturePath!;
            if (!Path.IsPathRooted(fullPath))
                fullPath = Path.GetFullPath(Path.Combine(mtlDir, fullPath));

            if (!File.Exists(fullPath))
                return null;

            string fullNormalized = Path.GetFullPath(fullPath);

            if (!fullNormalized.StartsWith(assetsRootFull, StringComparison.Ordinal))
                return null;

            return Path.GetRelativePath(assetsRoot, fullNormalized).Replace('\\', '/');
        }

        private static bool IsLikelyNormalMapPath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            string name = Path.GetFileNameWithoutExtension(rawPath)
                .Replace('\\', '/')
                .ToLowerInvariant();

            return name.Contains("_nor") ||
                   name.Contains("_nrm") ||
                   name.Contains("_normal") ||
                   name.EndsWith("nor") ||
                   name.EndsWith("nrm") ||
                   name.Contains("normal");
        }

        private static bool IsLikelyColorMapPath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            string name = Path.GetFileNameWithoutExtension(rawPath)
                .Replace('\\', '/')
                .ToLowerInvariant();

            return name.Contains("_col") ||
                   name.Contains("_basecolor") ||
                   name.Contains("basecolor") ||
                   name.Contains("albedo") ||
                   name.Contains("diffuse") ||
                   name.EndsWith("col");
        }

        private string BuildLitMaterialJsonFromMtl(string assetsRoot, string mtlFilePath, ParsedMtlMaterial src)
        {
            var parameters = new Dictionary<string, object?>();

            parameters["uColor"] = new[]
            {
        src.DiffuseColor.X,
        src.DiffuseColor.Y,
        src.DiffuseColor.Z,
        src.Alpha
    };

            string? chosenDiffuseRaw = src.DiffuseMapRaw;
            bool diffuseLooksNormal = IsLikelyNormalMapPath(chosenDiffuseRaw);
            bool normalLooksNormal = IsLikelyNormalMapPath(src.NormalMapRaw);

            if (diffuseLooksNormal)
            {
                if (IsLikelyColorMapPath(src.SpecularMapRaw))
                    chosenDiffuseRaw = src.SpecularMapRaw;
                else if (normalLooksNormal &&
                         !string.IsNullOrWhiteSpace(src.NormalMapRaw) &&
                         string.Equals(
                             Path.GetFileName(src.DiffuseMapRaw ?? ""),
                             Path.GetFileName(src.NormalMapRaw ?? ""),
                             StringComparison.OrdinalIgnoreCase))
                    chosenDiffuseRaw = null;
            }

            string? diffuseTextureKey = TryResolveMtlTextureToAssetRelative(assetsRoot, mtlFilePath, chosenDiffuseRaw);
            if (!string.IsNullOrWhiteSpace(diffuseTextureKey))
            {
                parameters["uUseTexture"] = 1;
                parameters["uTexture"] = diffuseTextureKey;
                parameters["uTextureUV"] = new[] { 1.0f, 1.0f };
                parameters["uTextureWrap"] = "Repeat";
            }

            string? normalTextureRaw = src.NormalMapRaw;

            if (string.IsNullOrWhiteSpace(normalTextureRaw) && IsLikelyNormalMapPath(src.DiffuseMapRaw))
                normalTextureRaw = src.DiffuseMapRaw;

            string? normalTextureKey = TryResolveMtlTextureToAssetRelative(assetsRoot, mtlFilePath, normalTextureRaw);
            if (!string.IsNullOrWhiteSpace(normalTextureKey))
            {
                parameters["uUseNormalTexture"] = 1;
                parameters["uNormalTexture"] = normalTextureKey;
                parameters["uNormalStrength"] = 1.0f;
            }

            float ambientStrength = Math.Clamp(
                (src.AmbientColor.X + src.AmbientColor.Y + src.AmbientColor.Z) / 3f,
                0f,
                1f);

            parameters["uAmbientStrength"] = ambientStrength;

            float specularIntensity = Math.Clamp(
                (src.SpecularColor.X + src.SpecularColor.Y + src.SpecularColor.Z) / 3f,
                0f,
                1f);

            parameters["uSpecularIntensity"] = specularIntensity;
            parameters["uSpecularColor"] = new[]
            {
                src.SpecularColor.X,
                src.SpecularColor.Y,
                src.SpecularColor.Z
            };

            float smoothness = Math.Clamp(src.Shininess / 1000f, 0f, 1f);
            parameters["uSmoothness"] = smoothness;

            var root = new Dictionary<string, object?>
            {
                ["assetType"] = "Material",
                ["shader"] = "Lit",
                ["parameters"] = parameters
            };

            return JsonSerializer.Serialize(root, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private string BuildGeneratedMtlMaterialKey(string assetsRoot, string mtlFilePath, string materialName)
        {
            string relMtl = Path.GetRelativePath(assetsRoot, mtlFilePath).Replace('\\', '/');
            return $"{relMtl}::{materialName}";
        }

        private Dictionary<int, string> RegisterGeneratedMaterialsFromMtl(
            string assetsRoot,
            string objFilePath,
            Assimp.Scene importedScene)
        {
            var result = new Dictionary<int, string>();

            string mtlFilePath = Path.ChangeExtension(objFilePath, ".mtl");
            if (!File.Exists(mtlFilePath))
                return result;

            Dictionary<string, ParsedMtlMaterial> parsed = ParseMtlFile(mtlFilePath);
            if (parsed.Count == 0)
                return result;

            List<ParsedMtlMaterial> parsedInOrder = parsed.Values.ToList();

            for (int i = 0; i < importedScene.MaterialCount; i++)
            {
                Assimp.Material? assimpMaterial = importedScene.Materials[i];
                string assimpName = assimpMaterial?.Name?.Trim() ?? "";

                ParsedMtlMaterial? src = null;

                if (!string.IsNullOrWhiteSpace(assimpName) &&
                    parsed.TryGetValue(assimpName, out ParsedMtlMaterial namedMatch))
                {
                    src = namedMatch;
                }
                else if (i >= 0 && i < parsedInOrder.Count)
                {
                    src = parsedInOrder[i];
                }

                if (src == null)
                    continue;

                string key = BuildGeneratedMtlMaterialKey(assetsRoot, mtlFilePath, src.Name);
                string json = BuildLitMaterialJsonFromMtl(assetsRoot, mtlFilePath, src);

                Program._generatedMaterialJsonRegistry[key] = json;
                result[i] = key;

                Console.WriteLine($"[i] Registered generated MTL material: {key}");
            }

            return result;
        }

        private static string ExtractMtlTexturePath(string raw)
        {
            string text = raw.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return "";

            int firstQuote = text.IndexOf('"');
            int lastQuote = text.LastIndexOf('"');
            if (firstQuote >= 0 && lastQuote > firstQuote)
                return text.Substring(firstQuote + 1, lastQuote - firstQuote - 1);

            if (!text.StartsWith("-", StringComparison.Ordinal))
                return text;

            string[] parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "";

            return parts[^1];
        }

        private float[] NormalizeRegisteredMeshVertices(float[] vertices, PrimitiveType primitiveType, ref int vertexStrideFloats)
        {
            if (vertexStrideFloats != 9 && vertexStrideFloats != 16 && vertexStrideFloats != 19)
                throw new ArgumentException("[X] Supported mesh vertex strides are 9, 16 and 19 floats.", nameof(vertexStrideFloats));

            if (vertices == null || vertices.Length == 0 || vertices.Length % vertexStrideFloats != 0)
                throw new ArgumentException("[X] Mesh vertices must be non-empty and aligned to the declared vertex stride.", nameof(vertices));

            if (vertexStrideFloats == 16 && primitiveType == PrimitiveType.Triangles)
            {
                vertices = BuildOutlineNormalAugmentedVertices(vertices);
                vertexStrideFloats = 19;
            }

            return vertices;
        }

        private string? ResolveSceneMaterialKey(SceneRenderObjectSnapshot obj, MeshSurfaceData surface)
        {
            if (obj.Materials != null && obj.Materials.Count > 0)
            {
                if (surface.MaterialSlot >= 0 &&
                    surface.MaterialSlot < obj.Materials.Count &&
                    !string.IsNullOrWhiteSpace(obj.Materials[surface.MaterialSlot]))
                {
                    return obj.Materials[surface.MaterialSlot];
                }

                if (!string.IsNullOrWhiteSpace(obj.Materials[0]))
                    return obj.Materials[0];
            }

            if (!string.IsNullOrWhiteSpace(surface.DefaultMaterialKey))
                return surface.DefaultMaterialKey;

            return null;
        }

        private MaterialData ResolveSceneMaterial(SceneRenderObjectSnapshot obj, MeshSurfaceData surface)
        {
            string? materialKey = ResolveSceneMaterialKey(obj, surface);

            if (!string.IsNullOrWhiteSpace(materialKey) &&
                TryLoadMaterial(materialKey, out MaterialData loaded))
            {
                return loaded;
            }

            return GetFallbackMaterial();
        }

        private List<ActiveUniformInfo> GetActiveUniforms(uint program)
        {
            if (_programUniformCache.TryGetValue(program, out List<ActiveUniformInfo> cached))
                return cached;

            _gl.GetProgram(program, ProgramPropertyARB.ActiveUniforms, out int count);

            var result = new List<ActiveUniformInfo>(count);

            for (uint i = 0; i < (uint)count; i++)
            {
                string name = _gl.GetActiveUniform(program, i, out int _, out UniformType type);

                if (name.EndsWith("[0]", StringComparison.Ordinal))
                    name = name[..^3];

                int location = _gl.GetUniformLocation(program, name);
                result.Add(new ActiveUniformInfo(name, location, type));
            }

            _programUniformCache[program] = result;
            return result;
        }

        private ProgramUniformLocationCache GetProgramLocationCache(uint program)
        {
            if (_programLocationCache.TryGetValue(program, out ProgramUniformLocationCache cached))
                return cached;

            ProgramUniformLocationCache cache = new ProgramUniformLocationCache();

            foreach (ActiveUniformInfo uniform in GetActiveUniforms(program))
            {
                cache.ByName[uniform.Name] = uniform;
            }

            int GetLoc(string name)
            {
                return cache.ByName.TryGetValue(name, out ActiveUniformInfo info) ? info.Location : -1;
            }

            cache.RenderSpace = GetLoc("uRenderSpace");
            cache.UseTexture = GetLoc("uUseTexture");
            cache.Color = GetLoc("uColor");
            cache.Model = GetLoc("uModel");
            cache.View = GetLoc("uView");
            cache.Projection = GetLoc("uProjection");

            cache.CameraPosition = GetLoc(_uniformCameraPosition);
            cache.AmbientColor = GetLoc(_uniformAmbientColor);
            cache.AmbientIntensity = GetLoc(_uniformAmbientIntensity);
            cache.ViewportOrigin = GetLoc(_uniformViewportOrigin);
            cache.ViewportSize = GetLoc(_uniformViewportSize);
            cache.ClusterGridSize = GetLoc(_uniformClusterGridSize);
            cache.ClusterNear = GetLoc(_uniformClusterNear);
            cache.ClusterFar = GetLoc(_uniformClusterFar);
            cache.LightCount = GetLoc(_uniformLightCount);
            cache.ShadowAtlasTexture = GetLoc(_uniformShadowAtlasTexture);
            cache.ReflectionTexture = GetLoc(_uniformReflectionTexture);
            cache.ReflectionEnabled = GetLoc(_uniformReflectionEnabled);
            cache.ReflectionSkyboxCube = GetLoc(_uniformReflectionSkyboxCube);
            cache.ReflectionSource = GetLoc(_uniformReflectionSource);
            cache.ReflectionIntensity = GetLoc(_uniformReflectionIntensity);
            cache.OutlinePass = GetLoc(_uniformOutlinePass);
            cache.UseOutlineNormal = GetLoc(_uniformUseOutlineNormal);

            cache.Texture = GetLoc("uTexture");

            _programLocationCache[program] = cache;
            return cache;
        }

        private ProgramMaterialDefaultsCache GetProgramMaterialDefaultsCache(uint program)
        {
            if (_programMaterialDefaultsCache.TryGetValue(program, out ProgramMaterialDefaultsCache cached))
                return cached;

            ProgramMaterialDefaultsCache cache = new ProgramMaterialDefaultsCache();
            int nextSamplerUnit = 0;

            foreach (ActiveUniformInfo uniform in GetActiveUniforms(program))
            {
                if (uniform.Location == -1)
                    continue;

                if (IsEngineManagedUniform(uniform.Name))
                    continue;

                switch (uniform.Type)
                {
                    case UniformType.Float:
                        if (string.Equals(uniform.Name, "uNormalStrength", StringComparison.Ordinal))
                        {
                            cache.Commands.Add(new MaterialDefaultCommand(
                                MaterialDefaultCommandKind.Float1,
                                uniform.Location,
                                x: 1f));
                        }
                        else if (string.Equals(uniform.Name, "uAmbientStrength", StringComparison.Ordinal))
                        {
                            cache.Commands.Add(new MaterialDefaultCommand(
                                MaterialDefaultCommandKind.Float1,
                                uniform.Location,
                                x: 1f));
                        }
                        else if (string.Equals(uniform.Name, "uAlphaCutoff", StringComparison.Ordinal))
                        {
                            cache.Commands.Add(new MaterialDefaultCommand(
                                MaterialDefaultCommandKind.Float1,
                                uniform.Location,
                                x: 0.5f));
                        }
                        else if (string.Equals(uniform.Name, "uSpecularRange", StringComparison.Ordinal) ||
                                 string.Equals(uniform.Name, "uRimRange", StringComparison.Ordinal))
                        {
                            cache.Commands.Add(new MaterialDefaultCommand(
                                MaterialDefaultCommandKind.Float1,
                                uniform.Location,
                                x: 0.5f));
                        }
                        else
                        {
                            cache.Commands.Add(new MaterialDefaultCommand(
                                MaterialDefaultCommandKind.Float1,
                                uniform.Location,
                                x: 0f));
                        }
                        break;

                    case UniformType.FloatVec2:
                        cache.Commands.Add(new MaterialDefaultCommand(
                            MaterialDefaultCommandKind.Float2,
                            uniform.Location,
                            x: 0f,
                            y: 0f));
                        break;

                    case UniformType.FloatVec3:
                        cache.Commands.Add(new MaterialDefaultCommand(
                            MaterialDefaultCommandKind.Float3,
                            uniform.Location,
                            x: 0f,
                            y: 0f,
                            z: 0f));
                        break;

                    case UniformType.FloatVec4:
                        if (string.Equals(uniform.Name, "uColor", StringComparison.Ordinal))
                        {
                            cache.Commands.Add(new MaterialDefaultCommand(
                                MaterialDefaultCommandKind.Float4,
                                uniform.Location,
                                x: 1f,
                                y: 1f,
                                z: 1f,
                                w: 1f));
                        }
                        else
                        {
                            cache.Commands.Add(new MaterialDefaultCommand(
                                MaterialDefaultCommandKind.Float4,
                                uniform.Location,
                                x: 0.5f,
                                y: 0.5f,
                                z: 0.5f,
                                w: 1f));
                        }
                        break;

                    case UniformType.Int:
                    case UniformType.Bool:
                        if (string.Equals(uniform.Name, "uReceiveShadow", StringComparison.Ordinal) ||
                            string.Equals(uniform.Name, "uCastShadow", StringComparison.Ordinal) ||
                            string.Equals(uniform.Name, "uReceiveReflection", StringComparison.Ordinal))
                        {
                            cache.Commands.Add(new MaterialDefaultCommand(
                                MaterialDefaultCommandKind.Int1,
                                uniform.Location,
                                ix: 1));
                        }
                        else
                        {
                            cache.Commands.Add(new MaterialDefaultCommand(
                                MaterialDefaultCommandKind.Int1,
                                uniform.Location,
                                ix: 0));
                        }
                        break;

                    case UniformType.IntVec2:
                    case UniformType.BoolVec2:
                        cache.Commands.Add(new MaterialDefaultCommand(
                            MaterialDefaultCommandKind.Int2,
                            uniform.Location,
                            ix: 0,
                            iy: 0));
                        break;

                    case UniformType.IntVec3:
                    case UniformType.BoolVec3:
                        cache.Commands.Add(new MaterialDefaultCommand(
                            MaterialDefaultCommandKind.Int3,
                            uniform.Location,
                            ix: 0,
                            iy: 0,
                            iz: 0));
                        break;

                    case UniformType.IntVec4:
                    case UniformType.BoolVec4:
                        cache.Commands.Add(new MaterialDefaultCommand(
                            MaterialDefaultCommandKind.Int4,
                            uniform.Location,
                            ix: 0,
                            iy: 0,
                            iz: 0,
                            iw: 0));
                        break;

                    case UniformType.FloatMat4:
                        cache.Commands.Add(new MaterialDefaultCommand(
                            MaterialDefaultCommandKind.Mat4Identity,
                            uniform.Location));
                        break;

                    case UniformType.Sampler2D:
                        cache.SamplerUnits[uniform.Name] = nextSamplerUnit;
                        cache.Commands.Add(new MaterialDefaultCommand(
                            MaterialDefaultCommandKind.Sampler2D,
                            uniform.Location,
                            textureUnit: nextSamplerUnit));
                        nextSamplerUnit++;
                        break;
                }
            }

            _programMaterialDefaultsCache[program] = cache;
            return cache;
        }

        private bool IsEngineManagedUniform(string uniformName)
        {
            return string.Equals(uniformName, "uRenderSpace", StringComparison.Ordinal) ||
                   string.Equals(uniformName, "uModel", StringComparison.Ordinal) ||
                   string.Equals(uniformName, "uView", StringComparison.Ordinal) ||
                   string.Equals(uniformName, "uProjection", StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformCameraPosition, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformAmbientColor, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformAmbientIntensity, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformViewportOrigin, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformViewportSize, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformClusterGridSize, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformClusterNear, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformClusterFar, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformLightCount, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformShadowAtlasTexture, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformReflectionTexture, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformReflectionSkyboxCube, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformReflectionEnabled, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformReflectionSource, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformReflectionIntensity, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformOutlinePass, StringComparison.Ordinal) ||
                   string.Equals(uniformName, _uniformUseOutlineNormal, StringComparison.Ordinal);
        }

        private void CaptureSkyboxReflectionForBatch(in RenderCommand batchCmd, SkyboxData skybox)
        {
            InitializeReflectionCaptureResources();

            if (skybox == null)
            {
                _capturedSkyboxReflectionValid = false;
                return;
            }

            _capturedSkyboxReflectionValid = false;

            uint previousProgram = _currentProgram;
            RenderSpace previousRenderSpace = _activeRenderSpace;
            Matrix4x4 previousModel = _activeModelMatrix;
            Matrix4x4 previousView = _activeViewMatrix;
            Matrix4x4 previousProjection = _activeProjectionMatrix;

            Matrix4x4 captureProjection = CreatePerspective(MathF.PI / 2f, 1f, 0.1f, MathF.Max(1f, batchCmd.ClusterFar));
            Matrix4x4 captureModel = Matrix4x4.CreateScale(MathF.Max(1f, batchCmd.ClusterFar * 0.5f));

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _reflectionCaptureFramebuffer);
            _gl.Viewport(0, 0, (uint)_reflectionSkyboxCubeSize, (uint)_reflectionSkyboxCubeSize);

            _gl.Disable(GLEnum.ScissorTest);
            _gl.Disable(GLEnum.Blend);
            _gl.Disable(GLEnum.DepthTest);
            _gl.DepthMask(false);

            _currentProgram = skybox.Program;
            _gl.UseProgram(skybox.Program);

            BindReflectionCubeGeometry();
            ApplyCullMode(skybox.CullMode);
            ApplySkyboxParametersOnly(skybox);

            for (int face = 0; face < 6; face++)
            {
                TextureTarget faceTarget = (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face);

                _gl.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    faceTarget,
                    _reflectionSkyboxCube,
                    0);

                _gl.ClearColor(0f, 0f, 0f, 1f);
                _gl.Clear(ClearBufferMask.ColorBufferBit);

                _activeRenderSpace = RenderSpace.Camera;
                _activeModelMatrix = captureModel;
                _activeViewMatrix = _reflectionCaptureViews[face];
                _activeProjectionMatrix = captureProjection;

                ApplyCoreSceneUniforms();

                _gl.DrawArrays(
                    PrimitiveType.Triangles,
                    0,
                    (uint)_reflectionCubeVertexCount);
            }

            UpdatePrefilteredReflectionCube();

            RestoreStateAfterReflectionCapture(
                batchCmd,
                previousProgram,
                previousRenderSpace,
                previousModel,
                previousView,
                previousProjection);

            _capturedSkyboxReflectionValid = true;
        }

        private void UpdatePrefilteredReflectionCube()
        {
            if (_reflectionSkyboxCube == 0 || _reflectionPrefilteredCube == 0) return;
            PrefilterReflectionCube(_reflectionSkyboxCube, _reflectionPrefilteredCube);
        }

        private void ApplyCoreSceneUniforms()
        {
            ProgramUniformLocationCache loc = GetProgramLocationCache(_currentProgram);

            if (loc.RenderSpace != -1)
                _gl.Uniform1(loc.RenderSpace, (int)_activeRenderSpace);

            if (loc.Model != -1)
                SetMatrixUniform(loc.Model, _activeModelMatrix);

            if (loc.View != -1)
                SetMatrixUniform(loc.View, _activeViewMatrix);

            if (loc.Projection != -1)
                SetMatrixUniform(loc.Projection, _activeProjectionMatrix);
        }

        private Dictionary<string, int> ApplyMaterialDefaults(uint program)
        {
            ProgramMaterialDefaultsCache cache = GetProgramMaterialDefaultsCache(program);

            foreach (MaterialDefaultCommand command in cache.Commands)
            {
                switch (command.Kind)
                {
                    case MaterialDefaultCommandKind.Float1:
                        _gl.Uniform1(command.Location, command.X);
                        break;

                    case MaterialDefaultCommandKind.Float2:
                        _gl.Uniform2(command.Location, command.X, command.Y);
                        break;

                    case MaterialDefaultCommandKind.Float3:
                        _gl.Uniform3(command.Location, command.X, command.Y, command.Z);
                        break;

                    case MaterialDefaultCommandKind.Float4:
                        _gl.Uniform4(command.Location, command.X, command.Y, command.Z, command.W);
                        break;

                    case MaterialDefaultCommandKind.Int1:
                        _gl.Uniform1(command.Location, command.IX);
                        break;

                    case MaterialDefaultCommandKind.Int2:
                        _gl.Uniform2(command.Location, command.IX, command.IY);
                        break;

                    case MaterialDefaultCommandKind.Int3:
                        _gl.Uniform3(command.Location, command.IX, command.IY, command.IZ);
                        break;

                    case MaterialDefaultCommandKind.Int4:
                        _gl.Uniform4(command.Location, command.IX, command.IY, command.IZ, command.IW);
                        break;

                    case MaterialDefaultCommandKind.Mat4Identity:
                        SetMatrixUniform(command.Location, Matrix4x4.Identity);
                        break;

                    case MaterialDefaultCommandKind.Sampler2D:
                        _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + command.TextureUnit));
                        _gl.BindTexture(TextureTarget.Texture2D, 0);
                        _gl.Uniform1(command.Location, command.TextureUnit);
                        break;
                }
            }

            _gl.ActiveTexture(TextureUnit.Texture0);
            return cache.SamplerUnits;
        }

        private bool TryResolveTexturePath(string rawPath, out string fullPath)
        {
            fullPath = string.Empty;

            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            string key = rawPath.Replace('\\', '/').Trim();

            while (key.StartsWith("/"))
                key = key[1..];

            if (key.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                key = key["Assets/".Length..];

            if (Program._textureFileRegistry.TryGetValue(key, out fullPath))
                return true;

            string assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
            string candidate = Path.GetFullPath(
                Path.Combine(assetsRoot, key.Replace('/', Path.DirectorySeparatorChar)));

            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }

            return false;
        }

        private bool TryReadNumericArray(JsonElement element, out double[] values)
        {
            values = Array.Empty<double>();

            if (element.ValueKind != JsonValueKind.Array)
                return false;

            var list = new List<double>();
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number)
                    return false;

                list.Add(item.GetDouble());
            }

            values = list.ToArray();
            return true;
        }

        private Vector2 ReadMaterialTextureUV(JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
                return Vector2.One;

            if (!parameters.TryGetProperty("uTextureUV", out JsonElement uvElement))
                return Vector2.One;

            if (!TryReadNumericArray(uvElement, out double[] numbers) || numbers.Length != 2)
                return Vector2.One;

            float u = (float)numbers[0];
            float v = (float)numbers[1];

            if (u <= 0f) u = 1f;
            if (v <= 0f) v = 1f;

            return new Vector2(u, v);
        }

        private MaterialTextureWrapMode ReadMaterialTextureWrap(JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
                return MaterialTextureWrapMode.Repeat;

            if (!parameters.TryGetProperty("uTextureWrap", out JsonElement wrapElement))
                return MaterialTextureWrapMode.Repeat;

            if (wrapElement.ValueKind != JsonValueKind.String)
                return MaterialTextureWrapMode.Repeat;

            string wrap = wrapElement.GetString() ?? "";

            if (string.Equals(wrap, "Clamp", StringComparison.Ordinal))
                return MaterialTextureWrapMode.Clamp;

            return MaterialTextureWrapMode.Repeat;
        }

        private RenderCullMode ReadMaterialCullMode(JsonElement parameters, RenderCullMode defaultValue)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
                return defaultValue;

            if (!parameters.TryGetProperty("uCull", out JsonElement cullElement))
                return defaultValue;

            if (cullElement.ValueKind != JsonValueKind.String)
            {
                Console.WriteLine("[!] Material parameter 'uCull' must be 'front', 'back', or 'both'.");
                return defaultValue;
            }

            string raw = cullElement.GetString() ?? "";

            switch (raw)
            {
                case "front":
                    return RenderCullMode.Front;

                case "back":
                    return RenderCullMode.Back;

                case "both":
                    return RenderCullMode.Both;

                default:
                    Console.WriteLine($"[!] Invalid uCull value '{raw}', only 'front', 'back', or 'both' are allowed.");
                    return defaultValue;
            }
        }

        private void ApplyCullMode(RenderCullMode mode)
        {
            switch (mode)
            {
                case RenderCullMode.Front:
                    _gl.Enable(GLEnum.CullFace);
                    _gl.CullFace(TriangleFace.Back);
                    break;

                case RenderCullMode.Back:
                    _gl.Enable(GLEnum.CullFace);
                    _gl.CullFace(TriangleFace.Front);
                    break;

                case RenderCullMode.Both:
                    _gl.Disable(GLEnum.CullFace);
                    break;
            }
        }

        private void ApplyTextureWrapMode(uint textureId, MaterialTextureWrapMode wrapMode)
        {
            if (textureId == 0)
                return;

            int wrapValue = wrapMode == MaterialTextureWrapMode.Clamp
                ? (int)TextureWrapMode.ClampToEdge
                : (int)TextureWrapMode.Repeat;

            _gl.BindTexture(TextureTarget.Texture2D, textureId);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrapValue);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrapValue);
        }

        private void ApplyMaterialParameter(string uniformName, JsonElement value, Dictionary<string, int> samplerUnits, MaterialData material)
        {
            if (IsEngineManagedUniform(uniformName))
                return;

            if (!TryGetActiveUniformExact(_currentProgram, uniformName, out ActiveUniformInfo uniform))
                return;

            int location = uniform.Location;
            if (location == -1)
                return;

            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                case JsonValueKind.False:
                    {
                        int boolValue = value.GetBoolean() ? 1 : 0;

                        switch (uniform.Type)
                        {
                            case UniformType.Bool:
                            case UniformType.Int:
                                _gl.Uniform1(location, boolValue);
                                break;
                        }

                        return;
                    }

                case JsonValueKind.Number:
                    {
                        if (value.TryGetInt32(out int intValue))
                        {
                            switch (uniform.Type)
                            {
                                case UniformType.Bool:
                                case UniformType.Int:
                                case UniformType.Sampler2D:
                                    _gl.Uniform1(location, intValue);
                                    break;

                                case UniformType.Float:
                                    _gl.Uniform1(location, (float)intValue);
                                    break;
                            }
                        }
                        else
                        {
                            float floatValue = (float)value.GetDouble();

                            if (uniform.Type == UniformType.Float)
                                _gl.Uniform1(location, floatValue);
                        }

                        return;
                    }

                case JsonValueKind.String:
                    if (samplerUnits.TryGetValue(uniformName, out int unit))
                    {
                        string requestedPath = value.GetString() ?? "";

                        if (!TryResolveTexturePath(requestedPath, out string texturePath))
                        {
                            Console.WriteLine($"[X] Texture not indexed: {requestedPath}");
                            return;
                        }

                        TextureInfo tex = LoadTexture(texturePath);

                        if (tex.Id != 0)
                        {
                            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                            _gl.BindTexture(TextureTarget.Texture2D, tex.Id);
                            ApplyTextureWrapMode(tex.Id, material.TextureWrap);
                            _gl.Uniform1(location, unit);
                            _gl.ActiveTexture(TextureUnit.Texture0);
                        }
                    }
                    return;

                case JsonValueKind.Array:
                    {
                        if (!TryReadNumericArray(value, out double[] numbers))
                            return;

                        if (numbers.Length == 2)
                        {
                            if (uniform.Type == UniformType.FloatVec2)
                                _gl.Uniform2(location, (float)numbers[0], (float)numbers[1]);
                            else if (uniform.Type == UniformType.IntVec2 || uniform.Type == UniformType.BoolVec2)
                                _gl.Uniform2(location, (int)numbers[0], (int)numbers[1]);
                        }
                        else if (numbers.Length == 3)
                        {
                            if (uniform.Type == UniformType.FloatVec3)
                                _gl.Uniform3(location, (float)numbers[0], (float)numbers[1], (float)numbers[2]);
                            else if (uniform.Type == UniformType.IntVec3 || uniform.Type == UniformType.BoolVec3)
                                _gl.Uniform3(location, (int)numbers[0], (int)numbers[1], (int)numbers[2]);
                        }
                        else if (numbers.Length == 4)
                        {
                            if (uniform.Type == UniformType.FloatVec4)
                                _gl.Uniform4(location, (float)numbers[0], (float)numbers[1], (float)numbers[2], (float)numbers[3]);
                            else if (uniform.Type == UniformType.IntVec4 || uniform.Type == UniformType.BoolVec4)
                                _gl.Uniform4(location, (int)numbers[0], (int)numbers[1], (int)numbers[2], (int)numbers[3]);
                        }
                        else if (numbers.Length == 16)
                        {
                            if (uniform.Type == UniformType.FloatMat4)
                            {
                                float[] matrixValues = numbers.Select(v => (float)v).ToArray();
                                _gl.UniformMatrix4(location, 1, false, matrixValues);
                            }
                        }

                        return;
                    }
            }
        }

        private void ApplySceneMaterial(MaterialData material, in RenderCommand cmd)
        {
            Dictionary<string, int> samplerUnits = ApplyMaterialDefaults(_currentProgram);
            ApplyCoreSceneUniforms();
            ApplyLightingSupportUniforms(cmd);

            if (material.Parameters.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in material.Parameters.EnumerateObject())
                {
                    ApplyMaterialParameter(prop.Name, prop.Value, samplerUnits, material);
                }
            }

            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private bool ShouldRenderOutline(RenderCommand cmd)
        {
            if (cmd.Material == null)
                return false;

            if (cmd.Material.Parameters.ValueKind != JsonValueKind.Object)
                return false;

            if (!cmd.Material.Parameters.TryGetProperty("uEnableOutline", out JsonElement outlineElement))
                return false;

            bool enabled = outlineElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => outlineElement.GetDouble() != 0.0,
                _ => false
            };

            if (!enabled)
                return false;

            return TryGetActiveUniformExact(cmd.Program, _uniformOutlinePass, out _);
        }

        private void SetOutlinePassUniform(uint program, int passValue)
        {
            int loc = GetProgramLocationCache(program).OutlinePass;
            if (loc != -1)
                _gl.Uniform1(loc, passValue);
        }

        private void ApplySkyboxParameter(string uniformName, JsonElement value, Dictionary<string, int> samplerUnits)
        {
            if (IsEngineManagedUniform(uniformName))
                return;

            if (!TryGetActiveUniformExact(_currentProgram, uniformName, out ActiveUniformInfo uniform))
                return;

            int location = uniform.Location;
            if (location == -1)
                return;

            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                case JsonValueKind.False:
                    {
                        int boolValue = value.GetBoolean() ? 1 : 0;

                        switch (uniform.Type)
                        {
                            case UniformType.Bool:
                            case UniformType.Int:
                                _gl.Uniform1(location, boolValue);
                                break;
                        }

                        return;
                    }

                case JsonValueKind.Number:
                    {
                        if (value.TryGetInt32(out int intValue))
                        {
                            switch (uniform.Type)
                            {
                                case UniformType.Bool:
                                case UniformType.Int:
                                case UniformType.Sampler2D:
                                    _gl.Uniform1(location, intValue);
                                    break;

                                case UniformType.Float:
                                    _gl.Uniform1(location, (float)intValue);
                                    break;
                            }
                        }
                        else
                        {
                            float floatValue = (float)value.GetDouble();

                            if (uniform.Type == UniformType.Float)
                                _gl.Uniform1(location, floatValue);
                        }

                        return;
                    }

                case JsonValueKind.String:
                    {
                        if (samplerUnits.TryGetValue(uniformName, out int unit))
                        {
                            string requestedPath = value.GetString() ?? "";

                            if (!TryResolveTexturePath(requestedPath, out string texturePath))
                            {
                                Console.WriteLine($"[X] Texture not indexed: {requestedPath}");
                                return;
                            }

                            TextureInfo tex = LoadTexture(texturePath);

                            if (tex.Id != 0)
                            {
                                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                                _gl.BindTexture(TextureTarget.Texture2D, tex.Id);
                                _gl.Uniform1(location, unit);
                                _gl.ActiveTexture(TextureUnit.Texture0);
                            }
                        }

                        return;
                    }

                case JsonValueKind.Array:
                    {
                        if (!TryReadNumericArray(value, out double[] numbers))
                            return;

                        if (numbers.Length == 2)
                        {
                            if (uniform.Type == UniformType.FloatVec2)
                                _gl.Uniform2(location, (float)numbers[0], (float)numbers[1]);
                            else if (uniform.Type == UniformType.IntVec2 || uniform.Type == UniformType.BoolVec2)
                                _gl.Uniform2(location, (int)numbers[0], (int)numbers[1]);
                        }
                        else if (numbers.Length == 3)
                        {
                            if (uniform.Type == UniformType.FloatVec3)
                                _gl.Uniform3(location, (float)numbers[0], (float)numbers[1], (float)numbers[2]);
                            else if (uniform.Type == UniformType.IntVec3 || uniform.Type == UniformType.BoolVec3)
                                _gl.Uniform3(location, (int)numbers[0], (int)numbers[1], (int)numbers[2]);
                        }
                        else if (numbers.Length == 4)
                        {
                            if (uniform.Type == UniformType.FloatVec4)
                                _gl.Uniform4(location, (float)numbers[0], (float)numbers[1], (float)numbers[2], (float)numbers[3]);
                            else if (uniform.Type == UniformType.IntVec4 || uniform.Type == UniformType.BoolVec4)
                                _gl.Uniform4(location, (int)numbers[0], (int)numbers[1], (int)numbers[2], (int)numbers[3]);
                        }
                        else if (numbers.Length == 16)
                        {
                            if (uniform.Type == UniformType.FloatMat4)
                            {
                                float[] matrixValues = numbers.Select(v => (float)v).ToArray();
                                _gl.UniformMatrix4(location, 1, false, matrixValues);
                            }
                        }

                        return;
                    }
            }
        }

        private void ApplySkybox(SkyboxData skybox)
        {
            Dictionary<string, int> samplerUnits = ApplyMaterialDefaults(_currentProgram);
            ApplyCoreSceneUniforms();

            if (skybox.Parameters.ValueKind != JsonValueKind.Object)
            {
                _gl.ActiveTexture(TextureUnit.Texture0);
                return;
            }

            foreach (JsonProperty prop in skybox.Parameters.EnumerateObject())
            {
                ApplySkyboxParameter(prop.Name, prop.Value, samplerUnits);
            }

            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        // ==================== UI 绘制方法 ====================

        /// <summary>
        /// 将屏幕像素坐标转换为NDC坐标
        /// </summary>
        private (float ndcX, float ndcY) PixelToNDC(float pixelX, float pixelY)
        {
            float halfWidth = _window.Size.X / 2.0f;
            float halfHeight = _window.Size.Y / 2.0f;
            float ndcX = (pixelX - halfWidth) / halfWidth;
            float ndcY = (halfHeight - pixelY) / halfHeight;
            return (ndcX, ndcY);
        }

        /// <summary>
        /// 绘制一个UI元素树
        /// </summary>
        public void DrawUI(UIElement root)
        {
            UseCanvasSpace();
            DrawUIElement(root);
        }

        /// <summary>
        /// 递归绘制UI元素
        /// </summary>
        private void DrawUIElement(UIElement element)
        {
            if (!element.Visible)
                return;

            Vector4 oldColor = _currentColor;

            if (element.BackgroundColor.W > 0)
            {
                SetColor(element.BackgroundColor.X, element.BackgroundColor.Y, element.BackgroundColor.Z, element.BackgroundColor.W);
                (float x1, float y1) = PixelToNDC(element.X, element.Y);
                (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                DrawQuad(x1, y1, 0, x2, y1, 0, x2, y2, 0, x1, y2, 0);
            }

            switch (element.Type)
            {
                case UIElementType.Label:
                case UIElementType.Button:
                    if (!string.IsNullOrEmpty(element.Text))
                    {
                        SetColor(element.TextColor.X, element.TextColor.Y, element.TextColor.Z, element.TextColor.W);
                        (float tx1, float ty1) = PixelToNDC(element.X + 5, element.Y + 5);
                        (float tx2, float ty2) = PixelToNDC(element.X + element.Width - 5, element.Y + element.Height - 5);
                        DrawQuad(tx1, ty1, 0, tx2, ty1, 0, tx2, ty2, 0, tx1, ty2, 0);
                    }
                    break;

                case UIElementType.Image:
                    if (!string.IsNullOrEmpty(element.ImageSource))
                    {
                        if (!TryResolveTexturePath(element.ImageSource, out string fullPath))
                        {
                            SetColor(element.BackgroundColor.X, element.BackgroundColor.Y, element.BackgroundColor.Z, element.BackgroundColor.W);
                            (float x1, float y1) = PixelToNDC(element.X, element.Y);
                            (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                            DrawQuad(x1, y1, 0, x2, y1, 0, x2, y2, 0, x1, y2, 0);
                            break;
                        }

                        TextureInfo tex = LoadTexture(fullPath);
                        uint texId = tex.Id;
                        if (texId != 0)
                        {
                            (float x1, float y1) = PixelToNDC(element.X, element.Y);
                            (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                            DrawTexturedQuad(x1, y1, x2, y2, element.ImageSource);
                        }
                        else
                        {
                            // 纹理加载失败，用背景色填充
                            SetColor(element.BackgroundColor.X, element.BackgroundColor.Y, element.BackgroundColor.Z, element.BackgroundColor.W);
                            (float x1, float y1) = PixelToNDC(element.X, element.Y);
                            (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                            DrawQuad(x1, y1, 0, x2, y1, 0, x2, y2, 0, x1, y2, 0);
                        }
                    }
                    else
                    {
                        // 没有图片源，用背景色填充
                        SetColor(element.BackgroundColor.X, element.BackgroundColor.Y, element.BackgroundColor.Z, element.BackgroundColor.W);
                        (float x1, float y1) = PixelToNDC(element.X, element.Y);
                        (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                        DrawQuad(x1, y1, 0, x2, y1, 0, x2, y2, 0, x1, y2, 0);
                    }
                    break;
            }

            SetColor(oldColor.X, oldColor.Y, oldColor.Z, oldColor.W);

            foreach (var child in element.Children)
            {
                DrawUIElement(child);
            }
        }

        // 相机系统调用接口
        public void BeginCameraRender(Matrix4x4 view, Matrix4x4 projection, int sceneId = -1)
        {
            _cameraContextActive = true;
            _activeRenderSpace = RenderSpace.Camera;
            _activeViewMatrix = view;
            _activeProjectionMatrix = projection;
            _activeModelMatrix = Matrix4x4.Identity;
            _activeSceneId = sceneId;
        }

        public void SetModelMatrix(Matrix4x4 model)
        {
            _activeModelMatrix = model;
        }

        public void EndCameraRender()
        {
            _cameraContextActive = false;
            _activeRenderSpace = RenderSpace.Canvas;
            _activeViewMatrix = Matrix4x4.Identity;
            _activeProjectionMatrix = Matrix4x4.Identity;
            _activeModelMatrix = Matrix4x4.Identity;
            _activeSceneId = -1;
        }

        [MoonSharpHidden]
        public void QueueLoadedSceneRender()
        {
            if (!_isInitialized)
                Initialize();

            RegisterBuiltInMeshes();

            string mainScreenCameraId = ResolveMainScreenCameraId();

            foreach (var pair in _sceneCameraCache)
            {
                string sceneId = pair.Key;

                if (!_sceneObjectCache.TryGetValue(sceneId, out var objectMap))
                    continue;

                foreach (var camera in pair.Value.OrderBy(c => c.SubmissionOrder))
                {
                    QueueSceneCamera(sceneId, objectMap.Values, camera, mainScreenCameraId);
                }
            }
        }

        private void QueueSceneCamera(
            string sceneId,
            IEnumerable<SceneRenderObjectSnapshot> objects,
            SceneRenderCameraSnapshot cameraItem,
            string mainScreenCameraId)
        {
            if (!cameraItem.Active || !cameraItem.Visible)
                return;

            if (cameraItem.Settings.RenderMode != 0)
                return;

            SceneWorldState cameraWorld = cameraItem.World;

            ViewportRect viewport = GetSceneViewportRect();
            Matrix4x4 view = CreateSceneViewMatrix(cameraWorld);
            Matrix4x4 projection = CreateSceneProjection(cameraItem.Settings, viewport.Aspect);

            long batchId = ++_sceneBatchCounter;

            SkyboxData skybox = ResolveSkyboxForCamera(cameraItem.ObjectId, cameraItem.Settings.RenderMode, mainScreenCameraId);

            if (skybox != null)
            {
                if (!_meshes.TryGetValue("builtin/cube_1x1x1", out MeshData skyboxMesh))
                    throw new Exception("[X] Builtin skybox cube mesh not found.");

                MeshSurfaceData skyboxSurface = skyboxMesh.Surfaces[0];

                float skyboxScale = MathF.Max(1f, (float)cameraItem.Settings.FarClip * 0.5f);
                Matrix4x4 skyboxModel = Matrix4x4.CreateScale(skyboxScale);

                _renderQueue.Add(new RenderCommand
                {
                    Vertices = skyboxSurface.Vertices,
                    PrimitiveType = skyboxSurface.PrimitiveType,
                    Program = skybox.Program,
                    UseTexture = false,
                    TextureId = 0,
                    VertexStrideFloats = skyboxSurface.VertexStrideFloats,
                    CameraPosition = Vector3.Zero,
                    ClusterNear = (float)cameraItem.Settings.NearClip,
                    ClusterFar = (float)cameraItem.Settings.FarClip,
                    RenderSpace = RenderSpace.Camera,
                    Model = skyboxModel,
                    View = view,
                    Projection = projection,
                    QueueType = RenderQueueType.Opaque,
                    SortDepth = 0f,
                    SubmissionIndex = _submissionCounter++,
                    Pass = RenderPass.Scene,
                    BatchId = batchId,
                    BatchSubmissionOrder = cameraItem.SubmissionOrder,
                    ViewportX = viewport.X,
                    ViewportY = viewport.Y,
                    ViewportWidth = viewport.Width,
                    ViewportHeight = viewport.Height,
                    Material = null,
                    Skybox = skybox,
                    ForceWhiteVertexColor = true,
                    IsSkybox = true,
                    SceneId = sceneId,
                    CameraWorldPosition = cameraWorld.Position,
                    CullMode = skybox.CullMode,
                    MeshId = skyboxMesh.Id,
                    MeshSurfaceId = skyboxSurface.Id,
                    MeshVertexColorsAreWhite = skyboxSurface.VertexColorsAreWhite
                });
            }

            foreach (var obj in objects)
            {
                if (!obj.Active || !obj.Visible)
                    continue;

                if (obj.ObjectId == cameraItem.ObjectId)
                    continue;

                if (string.Equals(obj.Type, "Camera", StringComparison.Ordinal))
                    continue;

                if (string.Equals(obj.Type, "Light", StringComparison.Ordinal))
                    continue;

                if (string.IsNullOrWhiteSpace(obj.Mesh))
                    continue;

                if (!_meshes.TryGetValue(obj.Mesh, out MeshData mesh))
                {
                    if (!TryRegisterSceneMeshOnDemand(obj.Mesh) ||
                        !_meshes.TryGetValue(obj.Mesh, out mesh))
                    {
                        Console.WriteLine($"[!] Mesh '{obj.Mesh}' not found for object '{obj.ObjectId}'.");
                        continue;
                    }
                }

                Double3 relativePosition = obj.WorldPosition - cameraWorld.Position;

                Matrix4x4 model =
                    Matrix4x4.CreateScale((float)obj.WorldScale.X, (float)obj.WorldScale.Y, (float)obj.WorldScale.Z) *
                    Matrix4x4.CreateFromQuaternion(obj.WorldRotation.ToSingle()) *
                    Matrix4x4.CreateTranslation((float)relativePosition.X, (float)relativePosition.Y, (float)relativePosition.Z);

                foreach (MeshSurfaceData surface in mesh.Surfaces)
                {
                    MaterialData material = ResolveSceneMaterial(obj, surface);

                    float materialAlpha = ReadMaterialColorAlpha(material);
                    bool transparentByColorAlpha = materialAlpha < 0.9999f;

                    _renderQueue.Add(new RenderCommand
                    {
                        Vertices = surface.Vertices,
                        PrimitiveType = surface.PrimitiveType,
                        Program = material.Program,
                        UseTexture = false,
                        TextureId = 0,
                        VertexStrideFloats = surface.VertexStrideFloats,
                        CameraPosition = Vector3.Zero,
                        ClusterNear = (float)cameraItem.Settings.NearClip,
                        ClusterFar = (float)cameraItem.Settings.FarClip,
                        SceneId = sceneId,
                        CameraWorldPosition = cameraWorld.Position,
                        RenderSpace = RenderSpace.Camera,
                        Model = model,
                        View = view,
                        Projection = projection,
                        QueueType = transparentByColorAlpha ? RenderQueueType.Transparent : RenderQueueType.Opaque,
                        UsePremultipliedTransparentBlend = transparentByColorAlpha,
                        SortDepth = ComputeSortDepth(surface.LocalCenter, model, view, RenderSpace.Camera),
                        SubmissionIndex = _submissionCounter++,
                        Pass = RenderPass.Scene,
                        BatchId = batchId,
                        BatchSubmissionOrder = cameraItem.SubmissionOrder,
                        ViewportX = viewport.X,
                        ViewportY = viewport.Y,
                        ViewportWidth = viewport.Width,
                        ViewportHeight = viewport.Height,
                        Material = material,
                        Skybox = null,
                        ForceWhiteVertexColor = true,
                        IsSkybox = false,
                        CullMode = material.CullMode,
                        MeshId = obj.Mesh ?? "",
                        MeshSurfaceId = surface.Id,
                        MeshVertexColorsAreWhite = surface.VertexColorsAreWhite
                    });
                }
            }
        }

        private void ApplyBlendModeForCommand(in RenderCommand cmd)
        {
            if (cmd.QueueType == RenderQueueType.Transparent &&
                cmd.UsePremultipliedTransparentBlend)
            {
                _gl.BlendFunc(GLEnum.One, GLEnum.OneMinusSrcAlpha);
            }
            else
            {
                _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);
            }
        }

        private Matrix4x4 CreateSceneViewMatrix(SceneWorldState cameraWorld)
        {
            Quaternion cameraRotation = cameraWorld.Rotation.ToSingle();
            Quaternion inverse = Quaternion.Inverse(cameraRotation);
            return Matrix4x4.CreateFromQuaternion(inverse);
        }

        private Matrix4x4 CreateSceneProjection(CameraRenderSettings settings, float aspect)
        {
            float near = (float)settings.NearClip;
            float far = (float)settings.FarClip;

            if (settings.ProjectionType == 1)
            {
                // 正交
                float height = (float)settings.FovOrSize;
                float width = height * aspect;
                return CreateOrthographic(width, height, near, far);
            }
            else
            {
                // 透视
                float fovRadians = (float)(settings.FovOrSize * Math.PI / 180.0);
                return CreatePerspective(fovRadians, aspect, near, far);
            }
        }

        // 上传辅助函数
        private void ApplyRenderUniforms(bool useTexture)
        {
            ProgramUniformLocationCache loc = GetProgramLocationCache(_currentProgram);

            if (loc.RenderSpace != -1)
                _gl.Uniform1(loc.RenderSpace, (int)_activeRenderSpace);

            if (loc.UseTexture != -1)
                _gl.Uniform1(loc.UseTexture, useTexture ? 1 : 0);

            if (loc.Color != -1)
                _gl.Uniform4(loc.Color, 1f, 1f, 1f, 1f);

            if (loc.Model != -1)
                SetMatrixUniform(loc.Model, _activeModelMatrix);

            if (loc.View != -1)
                SetMatrixUniform(loc.View, _activeViewMatrix);

            if (loc.Projection != -1)
                SetMatrixUniform(loc.Projection, _activeProjectionMatrix);
        }
        // 矩阵上传函数
        private void SetMatrixUniform(int location, Matrix4x4 matrix)
        {
            float[] values =
            {
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44
    };

            _gl.UniformMatrix4(location, 1, false, values);
        }

        /// <summary>
        /// 在Canvas层
        /// </summary>
        private void UseCanvasSpace()
        {
            _activeRenderSpace = RenderSpace.Canvas;
            _activeModelMatrix = Matrix4x4.Identity;
            _activeViewMatrix = Matrix4x4.Identity;
            _activeProjectionMatrix = Matrix4x4.Identity;
        }

        private void EnsureLuaCanvasMode()
        {
            if (!_cameraContextActive)
                UseCanvasSpace();
        }

        public void SetCull(string mode)
        {
            _currentCullMode = mode switch
            {
                "front" => RenderCullMode.Front,
                "back" => RenderCullMode.Back,
                "both" => RenderCullMode.Both,
                _ => throw new ArgumentException("[X] Cull mode must be 'front', 'back', or 'both'.", nameof(mode))
            };
        }

        public void SetCullFront()
        {
            _currentCullMode = RenderCullMode.Front;
        }

        public void SetCullBack()
        {
            _currentCullMode = RenderCullMode.Back;
        }

        public void SetCullBoth()
        {
            _currentCullMode = RenderCullMode.Both;
        }

        [MoonSharpHidden]
        public void ExecuteRenderQueue()
        {
            if (!_isInitialized)
                Initialize();

            _directionalShadowBatchCache.Clear();

            if (_renderQueue.Count == 0)
                return;

            InitQuadRenderer();

            List<RenderCommand> sceneCommands = _renderQueue
                .Where(c => c.Pass == RenderPass.Scene)
                .ToList();

            List<RenderCommand> canvasCommands = _renderQueue
                .Where(c => c.Pass == RenderPass.Canvas)
                .ToList();

            ExecuteScenePass(sceneCommands);
            ExecuteCanvasPass(canvasCommands);

            _renderQueue.Clear();
        }

        private void ExecuteScenePass(List<RenderCommand> sceneCommands)
        {
            if (sceneCommands.Count == 0)
                return;

            var batches = sceneCommands
                .GroupBy(c => c.BatchId)
                .OrderBy(g => g.First().BatchSubmissionOrder)
                .ToList();

            foreach (var batch in batches)
            {
                List<RenderCommand> batchCommands = batch.ToList();
                RenderCommand first = batchCommands.First();

                int vpX = first.ViewportX;
                int vpY = first.ViewportY;
                uint vpW = (uint)Math.Max(1, first.ViewportWidth);
                uint vpH = (uint)Math.Max(1, first.ViewportHeight);

                _gl.Viewport(vpX, vpY, vpW, vpH);

                _gl.Enable(GLEnum.ScissorTest);
                _gl.Scissor(vpX, vpY, vpW, vpH);
                _gl.ClearColor(_backgroundColor.X, _backgroundColor.Y, _backgroundColor.Z, _backgroundColor.W);
                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                _gl.Disable(GLEnum.ScissorTest);

                SkyboxData batchSkybox = batchCommands
                .Where(c => c.IsSkybox && c.Skybox != null)
                .Select(c => c.Skybox)
                .FirstOrDefault();

                CaptureSkyboxReflectionForBatch(first, batchSkybox);
                PrepareDirectionalShadowBatch(first, batchCommands);
                ExecuteSortedCommands(batchCommands);
            }

            // 场景结束后恢复整窗viewport并清一次深度
            _gl.Viewport(0, 0, (uint)_window.Size.X, (uint)_window.Size.Y);
            _gl.Clear(ClearBufferMask.DepthBufferBit);
        }

        private void ExecuteCanvasPass(List<RenderCommand> canvasCommands)
        {
            if (canvasCommands.Count == 0)
                return;

            _gl.Viewport(0, 0, (uint)_window.Size.X, (uint)_window.Size.Y);
            ExecuteSortedCommands(canvasCommands);
        }

        private void BindCommandGeometry(RenderCommand cmd)
        {
            if (TryBindStaticCommandGeometry(cmd))
                return;

            InitializeDynamicGeometryResources();

            ReadOnlySpan<float> uploadVertices = PrepareVerticesForUpload(cmd);
            UploadDynamicGeometry(uploadVertices);

            uint vao = GetDynamicGeometryVAO(cmd.VertexStrideFloats);
            _gl.BindVertexArray(vao);
        }

        private void ExecuteSortedCommands(List<RenderCommand> commands)
        {
            var skyboxes = commands
                .Where(c => c.IsSkybox)
                .OrderBy(c => c.SubmissionIndex)
                .ToList();

            var opaque = commands
                .Where(c => !c.IsSkybox && c.QueueType == RenderQueueType.Opaque)
                .OrderBy(c => c.SubmissionIndex)
                .ToList();

            var transparent = commands
                .Where(c => !c.IsSkybox && c.QueueType == RenderQueueType.Transparent)
                .OrderByDescending(c => c.SortDepth)
                .ThenBy(c => c.SubmissionIndex)
                .ToList();

            _gl.Enable(GLEnum.DepthTest);
            _gl.DepthFunc(GLEnum.Less);
            _gl.DepthMask(true);
            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

            foreach (var cmd in skyboxes)
                ExecuteSkyboxCommand(cmd);

            foreach (var cmd in opaque)
                ExecuteCommand(cmd);

            _gl.Enable(GLEnum.DepthTest);
            _gl.DepthFunc(GLEnum.Lequal);
            _gl.DepthMask(false);
            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

            foreach (var cmd in transparent)
                ExecuteCommand(cmd);

            _gl.DepthMask(true);
            _gl.DepthFunc(GLEnum.Less);
        }

        private void ExecuteSkyboxCommand(RenderCommand cmd)
        {
            _currentProgram = cmd.Program;
            _gl.UseProgram(cmd.Program);

            _activeRenderSpace = cmd.RenderSpace;
            _activeModelMatrix = cmd.Model;
            _activeViewMatrix = cmd.View;
            _activeProjectionMatrix = cmd.Projection;

            BindCommandGeometry(cmd);
            ApplyCullMode(cmd.CullMode);

            _gl.Disable(GLEnum.DepthTest);
            _gl.DepthMask(false);

            ApplySkybox(cmd.Skybox);

            _gl.DrawArrays(cmd.PrimitiveType, 0, (uint)(cmd.Vertices.Length / cmd.VertexStrideFloats));

            _gl.DepthMask(true);
            _gl.Enable(GLEnum.DepthTest);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);
        }

        private void ExecuteCommand(RenderCommand cmd)
        {
            _currentProgram = cmd.Program;
            _gl.UseProgram(cmd.Program);

            _activeRenderSpace = cmd.RenderSpace;
            _activeModelMatrix = cmd.Model;
            _activeViewMatrix = cmd.View;
            _activeProjectionMatrix = cmd.Projection;

            BindCommandGeometry(cmd);
            ApplyBlendModeForCommand(cmd);

            bool hasOutline = ShouldRenderOutline(cmd);
            bool isTransparentQueue = cmd.QueueType == RenderQueueType.Transparent;
            bool useStencilMaskedTransparentOutline = hasOutline && isTransparentQueue;

            void ApplyBaseDepthState()
            {
                if (isTransparentQueue)
                {
                    _gl.DepthFunc(GLEnum.Lequal);
                    _gl.DepthMask(false);
                }
                else
                {
                    _gl.DepthFunc(GLEnum.Less);
                    _gl.DepthMask(true);
                }
            }

            void ApplyBaseMaterialAndState()
            {
                ApplyCullMode(cmd.CullMode);
                SetOutlinePassUniform(cmd.Program, 0);

                if (cmd.Material != null)
                {
                    ApplySceneMaterial(cmd.Material, cmd);
                }
                else
                {
                    ApplyRenderUniforms(cmd.UseTexture);

                    if (cmd.UseTexture)
                    {
                        int texLoc = GetProgramLocationCache(cmd.Program).Texture;
                        if (texLoc != -1)
                        {
                            _gl.ActiveTexture(TextureUnit.Texture0);
                            _gl.BindTexture(TextureTarget.Texture2D, cmd.TextureId);
                            _gl.Uniform1(texLoc, 0);
                        }
                    }
                    else
                    {
                        _gl.BindTexture(TextureTarget.Texture2D, 0);
                    }
                }
            }

            void DrawBasePass()
            {
                ApplyBaseDepthState();
                ApplyBaseMaterialAndState();
                _gl.DrawArrays(cmd.PrimitiveType, 0, (uint)(cmd.Vertices.Length / cmd.VertexStrideFloats));
            }

            void DrawOutlinePass()
            {
                SetOutlinePassUniform(cmd.Program, 1);

                _gl.Enable(GLEnum.CullFace);
                _gl.CullFace(TriangleFace.Front);

                _gl.DepthFunc(GLEnum.Lequal);
                _gl.DepthMask(false);

                if (cmd.Material != null)
                    ApplySceneMaterial(cmd.Material, cmd);

                _gl.DrawArrays(cmd.PrimitiveType, 0, (uint)(cmd.Vertices.Length / cmd.VertexStrideFloats));

                ApplyBaseDepthState();
                ApplyCullMode(cmd.CullMode);
                SetOutlinePassUniform(cmd.Program, 0);
            }

            if (useStencilMaskedTransparentOutline)
            {
                ApplyBaseDepthState();

                _gl.ClearStencil(0);
                _gl.Clear(ClearBufferMask.StencilBufferBit);

                _gl.Enable(GLEnum.StencilTest);

                _gl.StencilMask(0xFF);
                _gl.StencilFunc(StencilFunction.Always, 1, 0xFF);
                _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);

                _gl.ColorMask(false, false, false, false);

                ApplyBaseMaterialAndState();
                _gl.DrawArrays(cmd.PrimitiveType, 0, (uint)(cmd.Vertices.Length / cmd.VertexStrideFloats));

                _gl.ColorMask(true, true, true, true);

                _gl.StencilMask(0x00);
                _gl.StencilFunc(StencilFunction.Notequal, 1, 0xFF);
                _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);

                DrawOutlinePass();

                _gl.Disable(GLEnum.StencilTest);
                _gl.StencilMask(0xFF);

                DrawBasePass();
            }
            else
            {
                DrawBasePass();

                if (hasOutline)
                    DrawOutlinePass();
            }

            _gl.Disable(GLEnum.StencilTest);
            _gl.StencilMask(0xFF);
            _gl.ColorMask(true, true, true, true);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);
        }

        /// <summary>
        /// 网格注册器
        /// </summary>
        /// <param name="id"></param>
        /// <param name="vertices"></param>
        /// <param name="primitiveType"></param>
        /// <exception cref="ArgumentException"></exception>
        [MoonSharpHidden]
        public void RegisterMesh(string id, float[] vertices, PrimitiveType primitiveType = PrimitiveType.Triangles, int vertexStrideFloats = 9)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("[X] Mesh id cannot be null or empty.", nameof(id));

            vertices = NormalizeRegisteredMeshVertices(vertices, primitiveType, ref vertexStrideFloats);

            InvalidateMeshGpuResources(id);

            Vector3 localCenter = ComputeMeshLocalCenter(vertices, vertexStrideFloats);
            bool vertexColorsAreWhite = AreMeshVertexColorsWhite(vertices, vertexStrideFloats);

            MeshSurfaceData surface = new MeshSurfaceData(
                id,
                vertices,
                primitiveType,
                vertexStrideFloats,
                0,
                null,
                localCenter,
                vertexColorsAreWhite);

            _meshes[id] = new MeshData(id, new[] { surface });
        }

        [MoonSharpHidden]
        public void RegisterObjMeshFromFile(string assetsRoot, string objFilePath)
        {
            if (string.IsNullOrWhiteSpace(assetsRoot))
                throw new ArgumentException("[X] Assets root cannot be null or empty.", nameof(assetsRoot));

            if (string.IsNullOrWhiteSpace(objFilePath))
                throw new ArgumentException("[X] OBJ file path cannot be null or empty.", nameof(objFilePath));

            if (!File.Exists(objFilePath))
                throw new FileNotFoundException("[X] OBJ file not found.", objFilePath);

            if (!string.Equals(Path.GetExtension(objFilePath), ".obj", StringComparison.OrdinalIgnoreCase))
                return;

            string meshKey = Path.GetRelativePath(assetsRoot, objFilePath).Replace('\\', '/');
            if (meshKey.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                meshKey = meshKey[..^4];

            using var importer = new Assimp.AssimpContext();

            Assimp.Scene importedScene = importer.ImportFile(
                objFilePath,
                Assimp.PostProcessSteps.Triangulate |
                Assimp.PostProcessSteps.JoinIdenticalVertices |
                Assimp.PostProcessSteps.SortByPrimitiveType |
                Assimp.PostProcessSteps.GenerateSmoothNormals |
                Assimp.PostProcessSteps.CalculateTangentSpace);

            if (importedScene == null || importedScene.MeshCount == 0)
                throw new InvalidDataException($"[X] OBJ '{objFilePath}' does not contain any importable mesh.");

            Dictionary<int, string> generatedMaterialKeysByIndex =
                RegisterGeneratedMaterialsFromMtl(assetsRoot, objFilePath, importedScene);

            List<MeshSurfaceData> surfaces = BuildObjSurfacesFromAssimpScene(
                importedScene,
                objFilePath,
                generatedMaterialKeysByIndex);

            InvalidateMeshGpuResources(meshKey);
            _meshes[meshKey] = new MeshData(meshKey, surfaces.ToArray());

            Console.WriteLine($"[i] Registered OBJ mesh: {meshKey} -> {objFilePath} ({surfaces.Count} surfaces)");
        }

        [MoonSharpHidden]
        public void RegisterObjMeshesFromAssets(string assetsRoot)
        {
            if (string.IsNullOrWhiteSpace(assetsRoot) || !Directory.Exists(assetsRoot))
                return;

            string[] objFiles = Directory.GetFiles(assetsRoot, "*.obj", SearchOption.AllDirectories);
            Array.Sort(objFiles, StringComparer.OrdinalIgnoreCase);

            foreach (string objFile in objFiles)
            {
                try
                {
                    RegisterObjMeshFromFile(assetsRoot, objFile);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Failed to import OBJ '{objFile}': {ex.Message}");
                }
            }
        }

        [MoonSharpHidden]
        private bool TryRegisterSceneMeshOnDemand(string meshId)
        {
            if (string.IsNullOrWhiteSpace(meshId))
                return false;

            string assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
            string objFilePath = Path.Combine(
                assetsRoot,
                meshId.Replace('/', Path.DirectorySeparatorChar) + ".obj");

            if (!File.Exists(objFilePath))
                return false;

            try
            {
                RegisterObjMeshFromFile(assetsRoot, objFilePath);
                return _meshes.ContainsKey(meshId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Failed to register mesh '{meshId}' from '{objFilePath}': {ex.Message}");
                return false;
            }
        }

        private List<MeshSurfaceData> BuildObjSurfacesFromAssimpScene(
            Assimp.Scene importedScene,
            string sourcePath,
            Dictionary<int, string> generatedMaterialKeysByIndex)
        {
            var surfaces = new List<MeshSurfaceData>(importedScene.MeshCount);

            for (int meshIndex = 0; meshIndex < importedScene.MeshCount; meshIndex++)
            {
                Assimp.Mesh mesh = importedScene.Meshes[meshIndex];

                if (mesh == null || mesh.VertexCount == 0 || mesh.FaceCount == 0)
                    continue;

                var vertices = new List<float>(mesh.FaceCount * 3 * 16);

                for (int faceIndex = 0; faceIndex < mesh.FaceCount; faceIndex++)
                {
                    Assimp.Face face = mesh.Faces[faceIndex];

                    // 跳过非三角
                    if (face == null || face.IndexCount != 3)
                        continue;

                    for (int i = 0; i < 3; i++)
                    {
                        int index = face.Indices[i];

                        Assimp.Vector3D pos = mesh.Vertices[index];

                        Assimp.Vector3D normal = mesh.HasNormals
                            ? mesh.Normals[index]
                            : new Assimp.Vector3D(0f, 0f, 1f);

                        Assimp.Vector3D uv = mesh.HasTextureCoords(0)
                            ? mesh.TextureCoordinateChannels[0][index]
                            : new Assimp.Vector3D(0f, 0f, 0f);

                        Assimp.Vector3D tangent = mesh.HasTangentBasis
                            ? mesh.Tangents[index]
                            : new Assimp.Vector3D(1f, 0f, 0f);

                        // position
                        vertices.Add(pos.X);
                        vertices.Add(pos.Y);
                        vertices.Add(pos.Z);

                        // color -> 固定白色
                        vertices.Add(1f);
                        vertices.Add(1f);
                        vertices.Add(1f);
                        vertices.Add(1f);

                        // uv
                        vertices.Add(uv.X);
                        vertices.Add(uv.Y);

                        // normal
                        vertices.Add(normal.X);
                        vertices.Add(normal.Y);
                        vertices.Add(normal.Z);

                        // tangent + handedness
                        vertices.Add(tangent.X);
                        vertices.Add(tangent.Y);
                        vertices.Add(tangent.Z);
                        vertices.Add(1f);
                    }
                }

                if (vertices.Count == 0)
                    continue;

                float[] surfaceVertices = vertices.ToArray();
                int vertexStrideFloats = 16;

                surfaceVertices = NormalizeRegisteredMeshVertices(
                    surfaceVertices,
                    PrimitiveType.Triangles,
                    ref vertexStrideFloats);

                string surfaceId = string.IsNullOrWhiteSpace(mesh.Name)
                    ? $"surface_{meshIndex}"
                    : mesh.Name;

                generatedMaterialKeysByIndex.TryGetValue(mesh.MaterialIndex, out string? defaultMaterialKey);

                Vector3 localCenter = ComputeMeshLocalCenter(surfaceVertices, vertexStrideFloats);
                bool vertexColorsAreWhite = AreMeshVertexColorsWhite(surfaceVertices, vertexStrideFloats);

                surfaces.Add(new MeshSurfaceData(
                    surfaceId,
                    surfaceVertices,
                    PrimitiveType.Triangles,
                    vertexStrideFloats,
                    mesh.MaterialIndex,
                    defaultMaterialKey,
                    localCenter,
                    vertexColorsAreWhite));
            }

            if (surfaces.Count == 0)
                throw new InvalidDataException($"[X] OBJ '{sourcePath}' did not produce any triangle surfaces.");

            return surfaces;
        }

        private float[] BuildOutlineNormalAugmentedVertices(float[] src)
        {
            int vertexCount = src.Length / 16;

            static (long X, long Y, long Z) MakeKey(float x, float y, float z)
            {
                const float scale = 1000000f;
                return ((long)MathF.Round(x * scale), (long)MathF.Round(y * scale), (long)MathF.Round(z * scale));
            }

            var accum = new Dictionary<(long X, long Y, long Z), Vector3>();

            for (int i = 0; i < vertexCount; i += 3)
            {
                int ia = i * 16;
                int ib = (i + 1) * 16;
                int ic = (i + 2) * 16;

                Vector3 a = new(src[ia + 0], src[ia + 1], src[ia + 2]);
                Vector3 b = new(src[ib + 0], src[ib + 1], src[ib + 2]);
                Vector3 c = new(src[ic + 0], src[ic + 1], src[ic + 2]);

                Vector3 ab = b - a;
                Vector3 ac = c - a;
                Vector3 faceNormal = Vector3.Cross(ab, ac);

                if (faceNormal.LengthSquared() <= 0.0000001f)
                    continue;

                var ka = MakeKey(a.X, a.Y, a.Z);
                var kb = MakeKey(b.X, b.Y, b.Z);
                var kc = MakeKey(c.X, c.Y, c.Z);

                accum[ka] = accum.TryGetValue(ka, out var na) ? na + faceNormal : faceNormal;
                accum[kb] = accum.TryGetValue(kb, out var nb) ? nb + faceNormal : faceNormal;
                accum[kc] = accum.TryGetValue(kc, out var nc) ? nc + faceNormal : faceNormal;
            }

            float[] dst = new float[vertexCount * 19];

            for (int i = 0; i < vertexCount; i++)
            {
                int s = i * 16;
                int d = i * 19;

                for (int k = 0; k < 16; k++)
                    dst[d + k] = src[s + k];

                Vector3 p = new(src[s + 0], src[s + 1], src[s + 2]);
                var key = MakeKey(p.X, p.Y, p.Z);

                Vector3 outlineNormal;
                if (accum.TryGetValue(key, out var sum) && sum.LengthSquared() > 0.0000001f)
                    outlineNormal = Vector3.Normalize(sum);
                else
                    outlineNormal = Vector3.Normalize(new Vector3(src[s + 9], src[s + 10], src[s + 11]));

                dst[d + 16] = outlineNormal.X;
                dst[d + 17] = outlineNormal.Y;
                dst[d + 18] = outlineNormal.Z;
            }

            return dst;
        }

        private void RegisterBuiltInMeshes()
        {
            if (_meshes.ContainsKey("builtin/cube_1x1x1"))
                return;

            RegisterMesh("builtin/cube_1x1x1", CreateUnitCubeVertices(), PrimitiveType.Triangles, 16);
        }

        /// <summary>
        /// 默认立方体
        /// </summary>
        /// <returns></returns>
        private float[] CreateUnitCubeVertices()
        {
            var data = new List<float>(36 * 16);

            void AddVertex(
                float x, float y, float z,
                float u, float v,
                float nx, float ny, float nz,
                float tx, float ty, float tz, float tw)
            {
                // position
                data.Add(x);
                data.Add(y);
                data.Add(z);

                // color
                data.Add(1f);
                data.Add(1f);
                data.Add(1f);
                data.Add(1f);

                // uv
                data.Add(u);
                data.Add(v);

                // normal
                data.Add(nx);
                data.Add(ny);
                data.Add(nz);

                // tangent
                data.Add(tx);
                data.Add(ty);
                data.Add(tz);
                data.Add(tw);
            }

            void AddQuad(
                float ax, float ay, float az, float au, float av,
                float bx, float by, float bz, float bu, float bv,
                float cx, float cy, float cz, float cu, float cv,
                float dx, float dy, float dz, float du, float dv,
                float nx, float ny, float nz,
                float tx, float ty, float tz, float tw)
            {
                AddVertex(ax, ay, az, au, av, nx, ny, nz, tx, ty, tz, tw);
                AddVertex(bx, by, bz, bu, bv, nx, ny, nz, tx, ty, tz, tw);
                AddVertex(cx, cy, cz, cu, cv, nx, ny, nz, tx, ty, tz, tw);

                AddVertex(cx, cy, cz, cu, cv, nx, ny, nz, tx, ty, tz, tw);
                AddVertex(dx, dy, dz, du, dv, nx, ny, nz, tx, ty, tz, tw);
                AddVertex(ax, ay, az, au, av, nx, ny, nz, tx, ty, tz, tw);
            }

            float n = 0.5f;

            // +Z
            AddQuad(
                -n, -n, n, 0f, 0f,
                 n, -n, n, 1f, 0f,
                 n, n, n, 1f, 1f,
                -n, n, n, 0f, 1f,
                 0f, 0f, 1f,
                 1f, 0f, 0f, 1f);

            // -Z
            AddQuad(
                 n, -n, -n, 0f, 0f,
                -n, -n, -n, 1f, 0f,
                -n, n, -n, 1f, 1f,
                 n, n, -n, 0f, 1f,
                 0f, 0f, -1f,
                -1f, 0f, 0f, 1f);

            // -X
            AddQuad(
                -n, -n, -n, 0f, 0f,
                -n, -n, n, 1f, 0f,
                -n, n, n, 1f, 1f,
                -n, n, -n, 0f, 1f,
                -1f, 0f, 0f,
                 0f, 0f, 1f, 1f);

            // +X
            AddQuad(
                 n, -n, n, 0f, 0f,
                 n, -n, -n, 1f, 0f,
                 n, n, -n, 1f, 1f,
                 n, n, n, 0f, 1f,
                 1f, 0f, 0f,
                 0f, 0f, -1f, 1f);

            // +Y
            AddQuad(
                -n, n, n, 0f, 0f,
                 n, n, n, 1f, 0f,
                 n, n, -n, 1f, 1f,
                -n, n, -n, 0f, 1f,
                 0f, 1f, 0f,
                 1f, 0f, 0f, 1f);

            // -Y
            AddQuad(
                -n, -n, -n, 0f, 0f,
                 n, -n, -n, 1f, 0f,
                 n, -n, n, 1f, 1f,
                -n, -n, n, 0f, 1f,
                 0f, -1f, 0f,
                 1f, 0f, 0f, 1f);

            return data.ToArray();
        }

        public void SetAmbientLight(float r, float g, float b, float intensity = 1.0f)
        {
            _ambientLightColor = new Vector3(
                Math.Clamp(r, 0f, 1f),
                Math.Clamp(g, 0f, 1f),
                Math.Clamp(b, 0f, 1f));

            _ambientLightIntensity = Math.Max(0f, intensity);
        }

        public void SetAmbientLightRGB(int r, int g, int b, float intensity = 1.0f)
        {
            SetAmbientLight(r / 255f, g / 255f, b / 255f, intensity);
        }

        /// <summary>
        /// 添加带UV顶点到缓冲区
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="z"></param>
        /// <param name="u"></param>
        /// <param name="v"></param>
        private void AddVertex(float x, float y, float z, float u = 0f, float v = 0f)
        {
            _vertexBuffer.Add(x); _vertexBuffer.Add(y); _vertexBuffer.Add(z);
            _vertexBuffer.Add(_currentColor.X); _vertexBuffer.Add(_currentColor.Y);
            _vertexBuffer.Add(_currentColor.Z); _vertexBuffer.Add(_currentColor.W);
            _vertexBuffer.Add(u); _vertexBuffer.Add(v);
        }

        /// <summary>
        /// 刷新缓冲区到GPU并绘制
        /// </summary>
        /// <param name="primitiveType"></param>
        private void Flush(PrimitiveType primitiveType)
        {
            if (_vertexBuffer.Count == 0) return;
            if (!_isInitialized) Initialize();

            var vertices = _vertexBuffer.ToArray();

            bool transparent = IsCurrentDrawTransparent(false);

            var cmd = new RenderCommand
            {
                Vertices = vertices,
                PrimitiveType = primitiveType,
                Program = _currentProgram,
                UseTexture = false,
                TextureId = 0,
                SceneId = "",
                CameraWorldPosition = Double3.Zero,
                VertexStrideFloats = 9,
                CameraPosition = Vector3.Zero,
                ClusterNear = 0.1f,
                ClusterFar = 1f,
                RenderSpace = _activeRenderSpace,
                Model = _activeModelMatrix,
                View = _activeViewMatrix,
                Projection = _activeProjectionMatrix,
                QueueType = transparent ? RenderQueueType.Transparent : RenderQueueType.Opaque,
                SortDepth = ComputeSortDepth(vertices, 9, _activeModelMatrix, _activeViewMatrix, _activeRenderSpace),
                SubmissionIndex = _submissionCounter++,
                Pass = RenderPass.Canvas,
                BatchId = -1,
                BatchSubmissionOrder = -1,
                ViewportX = 0,
                ViewportY = 0,
                ViewportWidth = _window.Size.X,
                ViewportHeight = _window.Size.Y,
                Material = null,
                Skybox = null,
                ForceWhiteVertexColor = false,
                IsSkybox = false,
                CullMode = _currentCullMode,
                MeshId = "",
                MeshSurfaceId = "",
                MeshVertexColorsAreWhite = false
            };

            _renderQueue.Add(cmd);
        }

        private void RestoreStateAfterReflectionCapture(in RenderCommand batchCmd, uint previousProgram, RenderSpace previousRenderSpace, Matrix4x4 previousModel, Matrix4x4 previousView, Matrix4x4 previousProjection)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            _gl.Viewport(
                batchCmd.ViewportX,
                batchCmd.ViewportY,
                (uint)Math.Max(1, batchCmd.ViewportWidth),
                (uint)Math.Max(1, batchCmd.ViewportHeight));

            _gl.Disable(GLEnum.ScissorTest);

            _gl.Enable(GLEnum.DepthTest);
            _gl.DepthFunc(GLEnum.Less);
            _gl.DepthMask(true);

            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

            _currentProgram = previousProgram;
            _gl.UseProgram(previousProgram);

            _activeRenderSpace = previousRenderSpace;
            _activeModelMatrix = previousModel;
            _activeViewMatrix = previousView;
            _activeProjectionMatrix = previousProjection;

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        [MoonSharpHidden]
        public void Cleanup()
        {
            if (_isInitialized)
            {
                _gl.DeleteVertexArray(_vertexArrayObject);
                _gl.DeleteBuffer(_vertexBufferObject);
                _isInitialized = false;
            }

            if (_quadInitialized)
            {
                _gl.DeleteVertexArray(_quadVAO);
                _gl.DeleteBuffer(_quadVBO);
                _quadInitialized = false;
            }

            if (_clusterLightBuffer != 0)
            {
                _gl.DeleteBuffer(_clusterLightBuffer);
                _clusterLightBuffer = 0;
            }

            if (_clusterRangeBuffer != 0)
            {
                _gl.DeleteBuffer(_clusterRangeBuffer);
                _clusterRangeBuffer = 0;
            }

            if (_clusterIndexBuffer != 0)
            {
                _gl.DeleteBuffer(_clusterIndexBuffer);
                _clusterIndexBuffer = 0;
            }

            if (_lightingDummyTexture != 0)
            {
                _gl.DeleteTexture(_lightingDummyTexture);
                _lightingDummyTexture = 0;
            }

            _lightingSupportInitialized = false;

            _uploadedLightingBatchId = long.MinValue;
            _uploadedLightCount = 0;

            if (_reflectionSkyboxCube != 0)
            {
                _gl.DeleteTexture(_reflectionSkyboxCube);
                _reflectionSkyboxCube = 0;
            }

            if (_reflectionPrefilteredCube != 0)
            {
                _gl.DeleteTexture(_reflectionPrefilteredCube);
                _reflectionPrefilteredCube = 0;
            }

            if (_reflectionDummyCubeTexture != 0)
            {
                _gl.DeleteTexture(_reflectionDummyCubeTexture);
                _reflectionDummyCubeTexture = 0;
            }

            if (_reflectionCaptureFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_reflectionCaptureFramebuffer);
                _reflectionCaptureFramebuffer = 0;
            }

            foreach (ReflectionTextureEnvironmentCacheEntry entry in _reflectionTextureEnvironmentCache.Values)
            {
                if (entry.SourceCube != 0) _gl.DeleteTexture(entry.SourceCube);
                if (entry.PrefilteredCube != 0) _gl.DeleteTexture(entry.PrefilteredCube);
            }
            _reflectionTextureEnvironmentCache.Clear();

            if (_reflectionCaptureFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_reflectionCaptureFramebuffer);
                _reflectionCaptureFramebuffer = 0;
            }

            if (_reflectionEquirectToCubeProgram != 0)
            {
                _gl.DeleteProgram(_reflectionEquirectToCubeProgram);
                _reflectionEquirectToCubeProgram = 0;
            }

            if (_reflectionPrefilterProgram != 0)
            {
                _gl.DeleteProgram(_reflectionPrefilterProgram);
                _reflectionPrefilterProgram = 0;
            }

            if (_reflectionCubeVAO != 0)
            {
                _gl.DeleteVertexArray(_reflectionCubeVAO);
                _reflectionCubeVAO = 0;
            }

            if (_reflectionCubeVBO != 0)
            {
                _gl.DeleteBuffer(_reflectionCubeVBO);
                _reflectionCubeVBO = 0;
            }

            if (_reflectionHammersleyLutTexture != 0)
            {
                _gl.DeleteTexture(_reflectionHammersleyLutTexture);
                _reflectionHammersleyLutTexture = 0;
            }

            _reflectionCubeVertexCount = 0;

            if (_dynamicGeometryVAO9 != 0)
            {
                _gl.DeleteVertexArray(_dynamicGeometryVAO9);
                _dynamicGeometryVAO9 = 0;
            }

            if (_dynamicGeometryVAO16 != 0)
            {
                _gl.DeleteVertexArray(_dynamicGeometryVAO16);
                _dynamicGeometryVAO16 = 0;
            }

            if (_dynamicGeometryVAO19 != 0)
            {
                _gl.DeleteVertexArray(_dynamicGeometryVAO19);
                _dynamicGeometryVAO19 = 0;
            }

            if (_dynamicGeometryVBO != 0)
            {
                _gl.DeleteBuffer(_dynamicGeometryVBO);
                _dynamicGeometryVBO = 0;
            }

            foreach (MeshSurfaceGpuResource resource in _meshSurfaceGpuCache.Values)
            {
                DeleteMeshSurfaceGpuResource(resource);
            }
            _meshSurfaceGpuCache.Clear();

            _dynamicGeometryCapacityBytes = 0;
            _dynamicGeometryScratch = Array.Empty<float>();

            _reflectionCaptureInitialized = false;
            _capturedSkyboxReflectionValid = false;
            _programUniformCache.Clear();
            _programLocationCache.Clear();
            _programMaterialDefaultsCache.Clear();

            if (_shadowAtlasTexture != 0)
            {
                _gl.DeleteTexture(_shadowAtlasTexture);
                _shadowAtlasTexture = 0;
            }

            if (_shadowFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_shadowFramebuffer);
                _shadowFramebuffer = 0;
            }

            if (_shadowDepthProgram != 0)
            {
                _gl.DeleteProgram(_shadowDepthProgram);
                _shadowDepthProgram = 0;
            }

            if (_directionalShadowCascadeBuffer != 0)
            {
                _gl.DeleteBuffer(_directionalShadowCascadeBuffer);
                _directionalShadowCascadeBuffer = 0;
            }

            _shadowSupportInitialized = false;
            _directionalShadowAtlasAllocatedSize = 0;
            _directionalShadowBatchCache.Clear();
        }
    }
}
