using MoonSharp.Interpreter;
using LimitlessSquareEngine.Engine;
using SharpGLTF.Memory;
using SharpGLTF.Transforms;
using SharpGLTF.Validation;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Gltf = SharpGLTF.Schema2;

namespace LimitlessSquareEngine
{
    internal partial class Graphics
    {
        private readonly Dictionary<string, MeshData> _meshes = new(StringComparer.Ordinal);

        private int _meshRevisionCounter = 0;

        private sealed class MeshSurfaceGpuResource
        {
            public uint Vao { get; init; }
            public uint Vbo { get; init; }
        }

        private readonly Dictionary<string, MeshSurfaceGpuResource> _meshSurfaceGpuCache = new(StringComparer.Ordinal);

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
            public Vector3 LocalBoundsMin { get; }
            public Vector3 LocalBoundsMax { get; }
            public bool IsSkinned { get; }

            public MeshSurfaceData(
                string id,
                float[] vertices,
                PrimitiveType primitiveType,
                int vertexStrideFloats,
                int materialSlot,
                string? defaultMaterialKey = null,
                Vector3 localCenter = default,
                bool vertexColorsAreWhite = false,
                Vector3 localBoundsMin = default,
                Vector3 localBoundsMax = default,
                bool isSkinned = false)
            {
                Id = id;
                Vertices = vertices;
                PrimitiveType = primitiveType;
                VertexStrideFloats = vertexStrideFloats;
                MaterialSlot = materialSlot;
                DefaultMaterialKey = defaultMaterialKey;
                LocalCenter = localCenter;
                VertexColorsAreWhite = vertexColorsAreWhite;
                LocalBoundsMin = localBoundsMin;
                LocalBoundsMax = localBoundsMax;
                IsSkinned = isSkinned;
            }
        }

        private readonly struct MeshData
        {
            public string Id { get; }
            public int Revision { get; }
            public float[] Vertices { get; }
            public PrimitiveType PrimitiveType { get; }
            public int VertexStrideFloats { get; }
            public MeshSurfaceData[] Surfaces { get; }

            public MeshData(string id, float[] vertices, PrimitiveType primitiveType, int vertexStrideFloats, int revision)
            {
                Id = id;
                Revision = revision;
                Vertices = vertices;
                PrimitiveType = primitiveType;
                VertexStrideFloats = vertexStrideFloats;

                Vector3 localCenter = Graphics.ComputeMeshLocalCenter(vertices, vertexStrideFloats);
                Graphics.ComputeMeshLocalBounds(vertices, vertexStrideFloats, out Vector3 localBoundsMin, out Vector3 localBoundsMax);
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
                        vertexColorsAreWhite,
                        localBoundsMin,
                        localBoundsMax)
                };
            }

            public MeshData(string id, MeshSurfaceData[] surfaces, int revision)
            {
                if (surfaces == null || surfaces.Length == 0)
                    throw new ArgumentException("[X] Mesh surfaces cannot be null or empty.", nameof(surfaces));

                Id = id;
                Revision = revision;
                Surfaces = surfaces;

                Vertices = surfaces[0].Vertices;
                PrimitiveType = surfaces[0].PrimitiveType;
                VertexStrideFloats = surfaces[0].VertexStrideFloats;
            }

            public MeshData WithUpdatedSurfaceVertices(int surfaceIndex, float[] vertices)
            {
                if (surfaceIndex < 0 || surfaceIndex >= Surfaces.Length)
                    throw new ArgumentOutOfRangeException(nameof(surfaceIndex));

                MeshSurfaceData[] newSurfaces = (MeshSurfaceData[])Surfaces.Clone();
                MeshSurfaceData old = newSurfaces[surfaceIndex];

                newSurfaces[surfaceIndex] = new MeshSurfaceData(
                    old.Id,
                    vertices,
                    old.PrimitiveType,
                    old.VertexStrideFloats,
                    old.MaterialSlot,
                    old.DefaultMaterialKey,
                    old.LocalCenter,
                    old.VertexColorsAreWhite,
                    old.LocalBoundsMin,
                    old.LocalBoundsMax,
                    old.IsSkinned);

                return new MeshData(Id, newSurfaces, Revision);
            }
        }

        internal readonly struct MeshColliderTriangle
        {
            public Double3 A { get; }
            public Double3 B { get; }
            public Double3 C { get; }

            public MeshColliderTriangle(Double3 a, Double3 b, Double3 c)
            {
                A = a;
                B = b;
                C = c;
            }
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

        private static void ComputeMeshLocalBounds(float[] vertices, int vertexStrideFloats, out Vector3 min, out Vector3 max)
        {
            int vertexCount = vertices.Length / vertexStrideFloats;
            if (vertexCount <= 0)
            {
                min = Vector3.Zero;
                max = Vector3.Zero;
                return;
            }

            min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            for (int i = 0; i < vertexCount; i++)
            {
                int idx = i * vertexStrideFloats;
                Vector3 p = new Vector3(vertices[idx + 0], vertices[idx + 1], vertices[idx + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
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

            if (vertexStrideFloats >= 27)
            {
                _gl.VertexAttribPointer(6, 4, VertexAttribPointerType.Float, false, strideBytes, 19 * sizeof(float));
                _gl.EnableVertexAttribArray(6);

                _gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false, strideBytes, 23 * sizeof(float));
                _gl.EnableVertexAttribArray(7);
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

            if (_meshSurfaceGpuCache.TryGetValue(key, out var cached))
                return cached;

            uint vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);

            bool isSkinned = vertexStrideFloats >= 27;
            BufferUsageARB usage = isSkinned ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw;

            _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)vertices, usage);
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

            InvalidateStaticSceneObjectRenderCachesForMesh(meshId);
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

        [MoonSharpHidden]
        public void UpdateMeshSurfaceVertices(string meshId, string surfaceId, float[] vertices)
        {
            if (string.IsNullOrWhiteSpace(meshId) || string.IsNullOrWhiteSpace(surfaceId))
                return;

            if (!_meshes.TryGetValue(meshId, out MeshData mesh))
                return;

            int surfaceIndex = -1;
            for (int i = 0; i < mesh.Surfaces.Length; i++)
            {
                if (string.Equals(mesh.Surfaces[i].Id, surfaceId, StringComparison.Ordinal))
                {
                    surfaceIndex = i;
                    break;
                }
            }

            if (surfaceIndex < 0)
                return;

            MeshSurfaceData surface = mesh.Surfaces[surfaceIndex];
            int stride = surface.VertexStrideFloats;

            if (vertices == null || vertices.Length == 0 || vertices.Length % stride != 0)
                throw new ArgumentException(
                    "[X] Updated mesh surface vertices must be non-empty and aligned to the surface vertex stride.",
                    nameof(vertices));

            MeshSurfaceGpuResource resource = GetOrCreateMeshSurfaceGpuResource(
                meshId,
                surfaceId,
                vertices,
                stride);

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resource.Vbo);
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (ReadOnlySpan<float>)vertices);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

            _meshes[meshId] = mesh.WithUpdatedSurfaceVertices(surfaceIndex, vertices);
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
            ComputeMeshLocalBounds(vertices, vertexStrideFloats, out Vector3 localBoundsMin, out Vector3 localBoundsMax);
            bool vertexColorsAreWhite = AreMeshVertexColorsWhite(vertices, vertexStrideFloats);

            MeshSurfaceData surface = new MeshSurfaceData(
                id,
                vertices,
                primitiveType,
                vertexStrideFloats,
                0,
                null,
                localCenter,
                vertexColorsAreWhite,
                localBoundsMin,
                localBoundsMax);

            int revision = ++_meshRevisionCounter;
            _meshes[id] = new MeshData(id, new[] { surface }, revision);
        }

        /// <summary>
        /// 移除已注册网格并释放其GPU资源
        /// </summary>
        [MoonSharpHidden]
        public void RemoveMesh(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            InvalidateMeshGpuResources(id);
            _meshes.Remove(id);
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
            int revision = ++_meshRevisionCounter;
            _meshes[meshKey] = new MeshData(meshKey, surfaces.ToArray(), revision);

            Console.WriteLine($"[i] Registered OBJ mesh: {meshKey}.obj ({surfaces.Count} surfaces)");
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
        public void RegisterVrmFromFile(string assetsRoot, string vrmFilePath)
        {
            if (string.IsNullOrWhiteSpace(assetsRoot))
                throw new ArgumentException("[X] Assets root cannot be null or empty.", nameof(assetsRoot));

            if (string.IsNullOrWhiteSpace(vrmFilePath))
                throw new ArgumentException("[X] VRM file path cannot be null or empty.", nameof(vrmFilePath));

            if (!File.Exists(vrmFilePath))
                throw new FileNotFoundException("[X] VRM file not found.", vrmFilePath);

            if (!string.Equals(Path.GetExtension(vrmFilePath), ".vrm", StringComparison.OrdinalIgnoreCase))
                return;

            string meshKey = Path.GetRelativePath(assetsRoot, vrmFilePath).Replace('\\', '/');

            VrmData data = VrmLoader.LoadFromFile(vrmFilePath);

            var avatar = new Avatar(meshKey, meshKey);
            new VrmAvatar(meshKey).MapToAvatar(avatar, data);

            Dictionary<int, string> materialKeysByIndex =
                RegisterVrmGeneratedMaterials(meshKey, assetsRoot, vrmFilePath, data);

            var surfaces = new List<MeshSurfaceData>(avatar.Skins.Count);

            for (int s = 0; s < avatar.Skins.Count; s++)
            {
                AvatarSkin skin = avatar.Skins[s];

                if (skin.BaseVertices.Length == 0 || skin.BaseVertices.Length % skin.VertexStrideFloats != 0)
                    continue;

                int materialIndex = s < data.Surfaces.Count ? data.Surfaces[s].MaterialIndex : 0;

                Vector3 localCenter = ComputeMeshLocalCenter(skin.BaseVertices, skin.VertexStrideFloats);
                ComputeMeshLocalBounds(skin.BaseVertices, skin.VertexStrideFloats, out Vector3 localBoundsMin, out Vector3 localBoundsMax);
                bool vertexColorsAreWhite = AreMeshVertexColorsWhite(skin.BaseVertices, skin.VertexStrideFloats);

                materialKeysByIndex.TryGetValue(materialIndex, out string? defaultMaterialKey);

                surfaces.Add(new MeshSurfaceData(
                    skin.SurfaceId,
                    skin.BaseVertices,
                    PrimitiveType.Triangles,
                    skin.VertexStrideFloats,
                    materialIndex,
                    defaultMaterialKey,
                    localCenter,
                    vertexColorsAreWhite,
                    localBoundsMin,
                    localBoundsMax,
                    true));
            }

            if (surfaces.Count == 0)
                throw new InvalidDataException($"[X] VRM '{vrmFilePath}' did not produce any skinned surfaces.");

            InvalidateMeshGpuResources(meshKey);
            int revision = ++_meshRevisionCounter;
            _meshes[meshKey] = new MeshData(meshKey, surfaces.ToArray(), revision);

            Console.WriteLine($"[i] Registered VRM avatar: {meshKey} ({surfaces.Count} surfaces)");
        }

        [MoonSharpHidden]
        public void RegisterVrmMeshesFromAssets(string assetsRoot)
        {
            if (string.IsNullOrWhiteSpace(assetsRoot) || !Directory.Exists(assetsRoot))
                return;

            string[] vrmFiles = Directory.GetFiles(assetsRoot, "*.vrm", SearchOption.AllDirectories);
            Array.Sort(vrmFiles, StringComparer.OrdinalIgnoreCase);

            foreach (string vrmFile in vrmFiles)
            {
                try
                {
                    RegisterVrmFromFile(assetsRoot, vrmFile);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Failed to import VRM '{vrmFile}': {ex.Message}");
                }
            }
        }

        private Dictionary<int, string> RegisterVrmGeneratedMaterials(
            string meshKey,
            string assetsRoot,
            string vrmFilePath,
            VrmData data)
        {
            var result = new Dictionary<int, string>();

            for (int i = 0; i < data.Materials.Count; i++)
            {
                string key = $"{meshKey}::mat_{i}";
                string json = BuildVrmMaterialJson(data, i, assetsRoot, vrmFilePath);

                Program._generatedMaterialJsonRegistry[key] = json;
                result[i] = key;

                Console.WriteLine($"[i] Registered generated VRM material: {key}");
            }

            return result;
        }

        private string BuildVrmMaterialJson(VrmData data, int materialIndex, string assetsRoot, string vrmFilePath)
        {
            VrmMaterialData material = data.Materials[materialIndex];

            var parameters = new Dictionary<string, object?>();

            parameters["uColor"] = new[]
            {
                material.BaseColor.X,
                material.BaseColor.Y,
                material.BaseColor.Z,
                material.BaseColor.W
            };

            if (material.BaseColorTextureIndex >= 0 && material.BaseColorTextureIndex < data.Textures.Count)
            {
                string? textureKey = SaveVrmTexture(data, material.BaseColorTextureIndex, vrmFilePath, assetsRoot);

                if (!string.IsNullOrWhiteSpace(textureKey))
                {
                    parameters["uUseTexture"] = 1;
                    parameters["uTexture"] = textureKey;
                    parameters["uTextureUV"] = new[] { 1.0f, 1.0f };
                    parameters["uTextureWrap"] = "Repeat";
                }
            }

            if (material.Mtoon != null)
            {
                parameters["uMetallic"] = 0f;
                parameters["uSmoothness"] = 0f;
            }
            else
            {
                parameters["uMetallic"] = material.Metallic;
                parameters["uSmoothness"] = Math.Clamp(1f - material.Roughness, 0f, 1f);
            }

            if (material.NormalTextureIndex >= 0 && material.NormalTextureIndex < data.Textures.Count)
            {
                string? textureKey = SaveVrmTexture(data, material.NormalTextureIndex, vrmFilePath, assetsRoot);

                if (!string.IsNullOrWhiteSpace(textureKey))
                {
                    parameters["uUseNormalTexture"] = 1;
                    parameters["uNormalTexture"] = textureKey;
                    parameters["uNormalStrength"] = material.NormalStrength;
                }
            }

            if (string.Equals(material.AlphaMode, "Mask", StringComparison.OrdinalIgnoreCase))
            {
                parameters["uUseAlphaCutoff"] = 1;
                parameters["uAlphaCutoff"] = material.AlphaCutoff;
            }

            if (material.DoubleSided)
                parameters["uCull"] = "both";

            if (material.Mtoon != null)
            {
                if (material.Mtoon.ShadingToonyFactor > 0.01f)
                    parameters["uEnableColorBanding"] = 1;

                parameters["uSpecularColor"] = new[] { 0f, 0f, 0f };
                parameters["uSpecularIntensity"] = 0f;

                Vector3 rimColor = material.Mtoon.ParametricRimColorFactor;
                bool hasRim = rimColor.LengthSquared() > 0.000001f;
                float rimPower = MathF.Max(0.001f, material.Mtoon.ParametricRimFresnelPowerFactor);

                parameters["uRimColor"] = new[] { rimColor.X, rimColor.Y, rimColor.Z };
                parameters["uRimIntensity"] = hasRim ? 1f + MathF.Max(0f, material.Mtoon.ParametricRimLiftFactor) : 0f;
                parameters["uRimRange"] = MathF.Min(1f, MathF.Max(0f, 1f / (1f + rimPower)));

                parameters["uReceiveShadow"] = 1;

                if (!string.Equals(material.Mtoon.OutlineWidthMode, "none", StringComparison.OrdinalIgnoreCase))
                {
                    parameters["uEnableOutline"] = 1;
                    parameters["uOutlineColor"] = new[]
                    {
                        material.Mtoon.OutlineColorFactor.X,
                        material.Mtoon.OutlineColorFactor.Y,
                        material.Mtoon.OutlineColorFactor.Z,
                        1f
                    };

                    float outlineWidth = material.Mtoon.OutlineWidthFactor;

                    if (string.Equals(material.Mtoon.OutlineWidthMode, "screenCoordinates", StringComparison.OrdinalIgnoreCase))
                        parameters["uOutlineWidth"] = outlineWidth * 1080f;
                    else
                        parameters["uOutlineWidth"] = Math.Max(1f, outlineWidth * 2000f);
                }
            }

            var root = new Dictionary<string, object?>
            {
                ["assetType"] = "Material",
                ["shader"] = "Shaders/Builtin/Lit",
                ["parameters"] = parameters
            };

            return JsonSerializer.Serialize(root, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private string? SaveVrmTexture(VrmData data, int textureIndex, string vrmFilePath, string assetsRoot)
        {
            VrmTextureData texture = data.Textures[textureIndex];

            if (texture.Content == null || texture.Content.Length == 0)
                return null;

            string vrmName = Path.GetFileNameWithoutExtension(vrmFilePath);
            string vrmDir = Path.GetDirectoryName(vrmFilePath) ?? "";
            string textureDir = Path.Combine(vrmDir, vrmName + ".textures");
            Directory.CreateDirectory(textureDir);

            string ext = string.IsNullOrWhiteSpace(texture.FileExtension) ? "png" : texture.FileExtension;

            if (!string.Equals(ext, "png", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, "jpg", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, "jpeg", StringComparison.OrdinalIgnoreCase))
            {
                ext = "png";
            }

            string fileName = $"tex_{textureIndex}.{ext}";
            string fullPath = Path.Combine(textureDir, fileName);

            if (!File.Exists(fullPath))
                File.WriteAllBytes(fullPath, texture.Content);

            string fullAssets = Path.GetFullPath(assetsRoot);
            string fullTexture = Path.GetFullPath(fullPath);

            if (!fullTexture.StartsWith(fullAssets, StringComparison.Ordinal))
                return null;

            return Path.GetRelativePath(assetsRoot, fullTexture).Replace('\\', '/');
        }

        [MoonSharpHidden]
        internal bool TryGetMeshColliderTriangles(
            string meshId,
            out List<MeshColliderTriangle> triangles)
        {
            triangles = new List<MeshColliderTriangle>();

            if (string.IsNullOrWhiteSpace(meshId))
                return false;

            if (!_meshes.TryGetValue(meshId, out MeshData mesh))
            {
                Program.EnsureObjMeshRegistered(meshId, this);
            }

            if (!_meshes.TryGetValue(meshId, out mesh))
                return false;

            foreach (MeshSurfaceData surface in mesh.Surfaces)
            {
                if (surface.PrimitiveType != PrimitiveType.Triangles)
                {
                    throw new InvalidOperationException(
                        $"[X] Mesh collider only supports triangle surfaces. mesh='{meshId}', surface='{surface.Id}'.");
                }

                int stride = surface.VertexStrideFloats;
                float[] vertices = surface.Vertices;

                if (vertices == null || vertices.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"[X] Mesh surface '{surface.Id}' in mesh '{meshId}' contains no vertices.");
                }

                if (vertices.Length % stride != 0)
                {
                    throw new InvalidOperationException(
                        $"[X] Mesh surface '{surface.Id}' in mesh '{meshId}' is not aligned to vertex stride {stride}.");
                }

                int vertexCount = vertices.Length / stride;
                if (vertexCount % 3 != 0)
                {
                    throw new InvalidOperationException(
                        $"[X] Mesh surface '{surface.Id}' in mesh '{meshId}' is not triangle-list data.");
                }

                for (int v = 0; v < vertexCount; v += 3)
                {
                    int i0 = v * stride;
                    int i1 = (v + 1) * stride;
                    int i2 = (v + 2) * stride;

                    Double3 a = new Double3(
                        vertices[i0 + 0],
                        vertices[i0 + 1],
                        vertices[i0 + 2]);

                    Double3 b = new Double3(
                        vertices[i1 + 0],
                        vertices[i1 + 1],
                        vertices[i1 + 2]);

                    Double3 c = new Double3(
                        vertices[i2 + 0],
                        vertices[i2 + 1],
                        vertices[i2 + 2]);

                    triangles.Add(new MeshColliderTriangle(a, b, c));
                }
            }

            return triangles.Count > 0;
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
                        vertices.Add(1f - uv.Y);

                        // normal
                        vertices.Add(normal.X);
                        vertices.Add(normal.Y);
                        vertices.Add(normal.Z);

                        // tangent
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
                ComputeMeshLocalBounds(surfaceVertices, vertexStrideFloats, out Vector3 localBoundsMin, out Vector3 localBoundsMax);
                bool vertexColorsAreWhite = AreMeshVertexColorsWhite(surfaceVertices, vertexStrideFloats);

                surfaces.Add(new MeshSurfaceData(
                    surfaceId,
                    surfaceVertices,
                    PrimitiveType.Triangles,
                    vertexStrideFloats,
                    mesh.MaterialIndex,
                    defaultMaterialKey,
                    localCenter,
                    vertexColorsAreWhite,
                    localBoundsMin,
                    localBoundsMax));
            }

            if (surfaces.Count == 0)
                throw new InvalidDataException($"[X] OBJ '{sourcePath}' did not produce any triangle surfaces.");

            return surfaces;
        }

        private float[] NormalizeRegisteredMeshVertices(float[] vertices, PrimitiveType primitiveType, ref int vertexStrideFloats)
        {
            if (vertexStrideFloats != 9 && vertexStrideFloats != 16 && vertexStrideFloats != 19 && vertexStrideFloats != 27)
                throw new ArgumentException("[X] Supported mesh vertex strides are 9, 16, 19 and 27 floats.", nameof(vertexStrideFloats));

            if (vertices == null || vertices.Length == 0 || vertices.Length % vertexStrideFloats != 0)
                throw new ArgumentException("[X] Mesh vertices must be non-empty and aligned to the declared vertex stride.", nameof(vertices));

            if (vertexStrideFloats == 16 && primitiveType == PrimitiveType.Triangles)
            {
                vertices = BuildOutlineNormalAugmentedVertices(vertices);
                vertexStrideFloats = 19;
            }

            return vertices;
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
            RegisterMesh("builtin/sphere_1x1x1", CreateUnitSphereVertices(), PrimitiveType.Triangles, 16);
        }

        private float[] CreateUnitSphereVertices()
        {
            var data = new List<float>();

            void AddVertex(
                float x, float y, float z,
                float u, float v,
                float nx, float ny, float nz,
                float tx, float ty, float tz, float tw)
            {
                data.Add(x * 0.5f);
                data.Add(y * 0.5f);
                data.Add(z * 0.5f);

                data.Add(1f);
                data.Add(1f);
                data.Add(1f);
                data.Add(1f);

                data.Add(u);
                data.Add(v);

                data.Add(nx);
                data.Add(ny);
                data.Add(nz);

                data.Add(tx);
                data.Add(ty);
                data.Add(tz);
                data.Add(tw);
            }

            void AddTriangle(
                float ax, float ay, float az, float au, float av,
                float bx, float by, float bz, float bu, float bv,
                float cx, float cy, float cz, float cu, float cv,
                float nx, float ny, float nz,
                float tx, float ty, float tz, float tw)
            {
                AddVertex(ax, ay, az, au, av, nx, ny, nz, tx, ty, tz, tw);
                AddVertex(bx, by, bz, bu, bv, nx, ny, nz, tx, ty, tz, tw);
                AddVertex(cx, cy, cz, cu, cv, nx, ny, nz, tx, ty, tz, tw);
            }

            int stacks = 48;
            int slices = 96;

            for (int i = 0; i < stacks; i++)
            {
                float phi0 = (float)i / stacks * MathF.PI;
                float phi1 = (float)(i + 1) / stacks * MathF.PI;

                for (int j = 0; j < slices; j++)
                {
                    float theta0 = (float)j / slices * 2f * MathF.PI;
                    float theta1 = (float)(j + 1) / slices * 2f * MathF.PI;

                    float x00 = MathF.Sin(phi0) * MathF.Cos(theta0);
                    float y00 = MathF.Cos(phi0);
                    float z00 = MathF.Sin(phi0) * MathF.Sin(theta0);

                    float x01 = MathF.Sin(phi0) * MathF.Cos(theta1);
                    float y01 = MathF.Cos(phi0);
                    float z01 = MathF.Sin(phi0) * MathF.Sin(theta1);

                    float x10 = MathF.Sin(phi1) * MathF.Cos(theta0);
                    float y10 = MathF.Cos(phi1);
                    float z10 = MathF.Sin(phi1) * MathF.Sin(theta0);

                    float x11 = MathF.Sin(phi1) * MathF.Cos(theta1);
                    float y11 = MathF.Cos(phi1);
                    float z11 = MathF.Sin(phi1) * MathF.Sin(theta1);

                    float u00 = theta0 / (2f * MathF.PI);
                    float u01 = theta1 / (2f * MathF.PI);
                    float v0 = phi0 / MathF.PI;
                    float v1 = phi1 / MathF.PI;

                    float tx0 = -z00;
                    float tz0 = x00;

                    AddTriangle(
                        x00, y00, z00, u00, v0,
                        x01, y01, z01, u01, v0,
                        x11, y11, z11, u01, v1,
                        x00, y00, z00,
                        tx0, 0f, tz0, 1f);

                    AddTriangle(
                        x00, y00, z00, u00, v0,
                        x11, y11, z11, u01, v1,
                        x10, y10, z10, u00, v1,
                        x00, y00, z00,
                        tx0, 0f, tz0, 1f);
                }
            }

            return data.ToArray();
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
    }

    internal static class VrmLoader
    {
        public static VrmData LoadFromFile(string vrmFilePath)
        {
            VrmData data = new VrmData();

            Gltf.ModelRoot root = Gltf.ModelRoot.Load(vrmFilePath, new Gltf.ReadSettings { Validation = ValidationMode.Skip });

            LoadNodes(root, data);
            LoadSkins(root, data);
            LoadMeshes(root, data);
            LoadTextures(root, data);
            LoadMaterials(root, data);
            ParseVrmExtensionJson(root, data);

            return data;
        }

        private static void LoadNodes(Gltf.ModelRoot root, VrmData data)
        {
            IReadOnlyList<Gltf.Node> nodes = root.LogicalNodes;

            for (int i = 0; i < nodes.Count; i++)
            {
                Gltf.Node node = nodes[i];

                AffineTransform local = node.LocalTransform.GetDecomposed();

                data.Nodes.Add(new VrmSkeletonNode
                {
                    Name = string.IsNullOrWhiteSpace(node.Name) ? $"node_{i}" : node.Name,
                    ParentIndex = node.VisualParent?.LogicalIndex ?? -1,
                    Position = local.Translation,
                    Rotation = local.Rotation,
                    Scale = local.Scale
                });
            }
        }

        private static void LoadSkins(Gltf.ModelRoot root, VrmData data)
        {
            IReadOnlyList<Gltf.Skin> skins = root.LogicalSkins;

            for (int i = 0; i < skins.Count; i++)
            {
                Gltf.Skin skin = skins[i];

                Matrix4x4[] inverseBindMatrices = skin.InverseBindMatrices.ToArray();

                data.Skins.Add(new VrmSkinData
                {
                    Joints = skin.Joints.Select(j => j.LogicalIndex).ToArray(),
                    InverseBindMatrices = inverseBindMatrices
                });
            }
        }

        private static void LoadMeshes(Gltf.ModelRoot root, VrmData data)
        {
            IReadOnlyList<Gltf.Node> nodes = root.LogicalNodes;
            IReadOnlyList<Gltf.Mesh> meshes = root.LogicalMeshes;

            for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
            {
                Gltf.Mesh mesh = meshes[meshIndex];

                Gltf.Node? meshNode = nodes.FirstOrDefault(n => ReferenceEquals(n.Mesh, mesh));
                int nodeIndex = meshNode?.LogicalIndex ?? -1;

                int skinIndex = -1;
                if (meshNode?.Skin != null)
                    skinIndex = meshNode.Skin.LogicalIndex;

                for (int primIndex = 0; primIndex < mesh.Primitives.Count; primIndex++)
                {
                    Gltf.MeshPrimitive primitive = mesh.Primitives[primIndex];

                    if (primitive.DrawPrimitiveType != Gltf.PrimitiveType.TRIANGLES)
                        continue;

                    Gltf.Accessor? positionAccessor = primitive.GetVertexAccessor("POSITION");
                    if (positionAccessor == null)
                        continue;

                    int positionCount = positionAccessor.Count;

                    string surfaceName = mesh.Primitives.Count == 1
                        ? (string.IsNullOrWhiteSpace(mesh.Name) ? $"surface_{meshIndex}" : mesh.Name)
                        : (string.IsNullOrWhiteSpace(mesh.Name) ? $"surface_{meshIndex}_{primIndex}" : $"{mesh.Name}_{primIndex}");

                    VrmMeshSurfaceData surface = new VrmMeshSurfaceData
                    {
                        Name = surfaceName,
                        NodeIndex = nodeIndex,
                        MaterialIndex = primitive.Material?.LogicalIndex ?? 0,
                        SkinIndex = skinIndex,
                        PositionCount = positionCount,
                        Positions = ReadFloatArray(primitive.GetVertexAccessor("POSITION")?.AsVector3Array(), 3, positionCount),
                        Normals = ReadFloatArray(primitive.GetVertexAccessor("NORMAL")?.AsVector3Array(), 3, positionCount),
                        TexCoords = ReadFloatArray(primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array(), 2, positionCount),
                        Tangents = ReadFloatArray(primitive.GetVertexAccessor("TANGENT")?.AsVector4Array(), 4, positionCount),
                        Indices = ReadTriangleIndices(primitive),
                        JointIndices = ReadIntArray(primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array(), positionCount),
                        JointWeights = ReadFloatArray(primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array(), 4, positionCount)
                    };

                    for (int m = 0; m < primitive.MorphTargetsCount; m++)
                    {
                        IReadOnlyDictionary<string, Gltf.Accessor> target = primitive.GetMorphTargetAccessors(m);

                        VrmMorphTargetData morph = new VrmMorphTargetData();

                        if (target.TryGetValue("POSITION", out Gltf.Accessor? positionMorph))
                            morph.PositionDeltas = ReadFloatArray(positionMorph.AsVector3Array(), 3, positionCount);

                        if (target.TryGetValue("NORMAL", out Gltf.Accessor? normalMorph))
                            morph.NormalDeltas = ReadFloatArray(normalMorph.AsVector3Array(), 3, positionCount);

                        surface.MorphTargets.Add(morph);
                    }

                    data.Surfaces.Add(surface);
                }
            }
        }

        private static int[] ReadTriangleIndices(Gltf.MeshPrimitive primitive)
        {
            var list = new List<int>();

            foreach ((int a, int b, int c) in primitive.GetTriangleIndices())
            {
                list.Add(a);
                list.Add(b);
                list.Add(c);
            }

            return list.ToArray();
        }

        private static float[] ReadFloatArray(IAccessorArray<Vector3>? array, int componentCount, int itemCount)
        {
            if (array == null)
                return Array.Empty<float>();

            float[] result = new float[itemCount * 3];

            for (int i = 0; i < itemCount; i++)
            {
                Vector3 value = array[i];
                result[i * 3 + 0] = value.X;
                result[i * 3 + 1] = value.Y;
                result[i * 3 + 2] = value.Z;
            }

            return result;
        }

        private static float[] ReadFloatArray(IAccessorArray<Vector2>? array, int componentCount, int itemCount)
        {
            if (array == null)
                return Array.Empty<float>();

            float[] result = new float[itemCount * 2];

            for (int i = 0; i < itemCount; i++)
            {
                Vector2 value = array[i];
                result[i * 2 + 0] = value.X;
                result[i * 2 + 1] = value.Y;
            }

            return result;
        }

        private static float[] ReadFloatArray(IAccessorArray<Vector4>? array, int componentCount, int itemCount)
        {
            if (array == null)
                return Array.Empty<float>();

            float[] result = new float[itemCount * 4];

            for (int i = 0; i < itemCount; i++)
            {
                Vector4 value = array[i];
                result[i * 4 + 0] = value.X;
                result[i * 4 + 1] = value.Y;
                result[i * 4 + 2] = value.Z;
                result[i * 4 + 3] = value.W;
            }

            return result;
        }

        private static int[] ReadIntArray(IAccessorArray<Vector4>? array, int itemCount)
        {
            if (array == null)
                return Array.Empty<int>();

            int[] result = new int[itemCount * 4];

            for (int i = 0; i < itemCount; i++)
            {
                Vector4 value = array[i];
                result[i * 4 + 0] = (int)value.X;
                result[i * 4 + 1] = (int)value.Y;
                result[i * 4 + 2] = (int)value.Z;
                result[i * 4 + 3] = (int)value.W;
            }

            return result;
        }

        private static float GetChannelFactor(Gltf.MaterialChannel channel, string key, float fallback)
        {
            foreach (Gltf.IMaterialParameter parameter in channel.Parameters)
            {
                if (!string.Equals(parameter.Name, key, StringComparison.Ordinal))
                    continue;

                object? value = parameter.Value;

                if (value is float floatValue)
                    return floatValue;

                if (value is double doubleValue)
                    return (float)doubleValue;

                return fallback;
            }

            return fallback;
        }

        private static void LoadTextures(Gltf.ModelRoot root, VrmData data)
        {
            IReadOnlyList<Gltf.Image> images = root.LogicalImages;

            for (int i = 0; i < images.Count; i++)
            {
                Gltf.Image image = images[i];
                MemoryImage content = image.Content;

                data.Textures.Add(new VrmTextureData
                {
                    Name = string.IsNullOrWhiteSpace(image.Name) ? $"image_{i}" : image.Name,
                    Content = content.Content.ToArray(),
                    FileExtension = string.IsNullOrWhiteSpace(content.FileExtension)
                        ? "png"
                        : content.FileExtension.TrimStart('.')
                });
            }
        }

        private static void LoadMaterials(Gltf.ModelRoot root, VrmData data)
        {
            IReadOnlyList<Gltf.Material> materials = root.LogicalMaterials;

            for (int i = 0; i < materials.Count; i++)
            {
                Gltf.Material material = materials[i];

                VrmMaterialData md = new VrmMaterialData
                {
                    Name = string.IsNullOrWhiteSpace(material.Name) ? $"material_{i}" : material.Name,
                    Unlit = material.Unlit,
                    DoubleSided = material.DoubleSided,
                    AlphaMode = material.Alpha.ToString(),
                    AlphaCutoff = material.AlphaCutoff
                };

                Gltf.MaterialChannel? baseColor = material.FindChannel("BaseColor");
                if (baseColor.HasValue)
                {
                    Vector4 color = baseColor.Value.Color;
                    md.BaseColor = new Vector4(color.X, color.Y, color.Z, color.W);

                    if (baseColor.Value.Texture != null)
                        md.BaseColorTextureIndex = baseColor.Value.Texture.PrimaryImage?.LogicalIndex ?? -1;

                    if (baseColor.Value.TextureTransform != null)                    {
                        md.BaseColorTextureScale = baseColor.Value.TextureTransform.Scale;
                        md.BaseColorTextureOffset = baseColor.Value.TextureTransform.Offset;
                    }
                }

                Gltf.MaterialChannel? metallicRoughness = material.FindChannel("MetallicRoughness");
                if (metallicRoughness.HasValue)
                {
                    md.Metallic = GetChannelFactor(metallicRoughness.Value, "Metallic", 1f);
                    md.Roughness = GetChannelFactor(metallicRoughness.Value, "Roughness", 1f);
                }

                Gltf.MaterialChannel? normal = material.FindChannel("Normal");
                if (normal.HasValue)
                {
                    md.NormalStrength = GetChannelFactor(normal.Value, "Scale", 1f);

                    if (normal.Value.Texture != null)
                        md.NormalTextureIndex = normal.Value.Texture.PrimaryImage?.LogicalIndex ?? -1;

                    if (normal.Value.TextureTransform != null)
                    {
                        md.NormalTextureScale = normal.Value.TextureTransform.Scale;
                        md.NormalTextureOffset = normal.Value.TextureTransform.Offset;
                    }
                }

                Gltf.MaterialChannel? emissive = material.FindChannel("Emissive");
                if (emissive.HasValue)
                {
                    Vector4 color = emissive.Value.Color;
                    md.EmissiveColor = new Vector4(color.X, color.Y, color.Z, color.W);

                    if (emissive.Value.Texture != null)
                        md.EmissiveTextureIndex = emissive.Value.Texture.PrimaryImage?.LogicalIndex ?? -1;
                }

                Gltf.MaterialChannel? occlusion = material.FindChannel("Occlusion");
                if (occlusion.HasValue && occlusion.Value.Texture != null)
                    md.OcclusionTextureIndex = occlusion.Value.Texture.PrimaryImage?.LogicalIndex ?? -1;

                data.Materials.Add(md);
            }
        }

        private static void ParseVrmExtensionJson(Gltf.ModelRoot root, VrmData data)
        {
            string json = root.GetJsonPreview();

            using JsonDocument doc = JsonDocument.Parse(json);

            ParseMtoonMaterials(doc.RootElement, data);

            if (!doc.RootElement.TryGetProperty("extensions", out JsonElement extensions) ||
                extensions.ValueKind != JsonValueKind.Object)
                return;

            if (!extensions.TryGetProperty("VRMC_vrm", out JsonElement vrm) ||
                vrm.ValueKind != JsonValueKind.Object)
                return;

            if (vrm.TryGetProperty("humanoid", out JsonElement humanoid) &&
                humanoid.ValueKind == JsonValueKind.Object &&
                humanoid.TryGetProperty("humanBones", out JsonElement humanBones) &&
                humanBones.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty boneProp in humanBones.EnumerateObject())
                {
                    if (boneProp.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!boneProp.Value.TryGetProperty("node", out JsonElement nodeElem) ||
                        nodeElem.ValueKind != JsonValueKind.Number)
                        continue;

                    data.HumanoidBones.Add(new VrmHumanoidBone
                    {
                        BoneName = boneProp.Name,
                        NodeIndex = nodeElem.GetInt32()
                    });
                }
            }

            if (vrm.TryGetProperty("expressions", out JsonElement expressions) &&
                expressions.ValueKind == JsonValueKind.Object)
            {
                ParseExpressionGroup(expressions, "preset", true, data);
                ParseExpressionGroup(expressions, "custom", false, data);
            }

            if (vrm.TryGetProperty("lookAt", out JsonElement lookAt) &&
                lookAt.ValueKind == JsonValueKind.Object)
            {
                if (lookAt.TryGetProperty("type", out JsonElement typeElem) &&
                    typeElem.ValueKind == JsonValueKind.String)
                    data.LookAt.Type = typeElem.GetString() ?? "bone";

                if (lookAt.TryGetProperty("offsetFromHeadBone", out JsonElement offsetElem) &&
                    TryReadNumberArray(offsetElem, out float[] offset) && offset.Length >= 3)
                {
                    data.LookAt.OffsetFromHeadBone = new Vector3(offset[0], offset[1], offset[2]);
                }

                ReadRangeMap(lookAt, "rangeMapHorizontalInner", data.LookAt.RangeMapHorizontalInner);
                ReadRangeMap(lookAt, "rangeMapHorizontalOuter", data.LookAt.RangeMapHorizontalOuter);
                ReadRangeMap(lookAt, "rangeMapVerticalDown", data.LookAt.RangeMapVerticalDown);
                ReadRangeMap(lookAt, "rangeMapVerticalUp", data.LookAt.RangeMapVerticalUp);
            }

            if (vrm.TryGetProperty("meta", out JsonElement meta) &&
                meta.ValueKind == JsonValueKind.Object)
            {
                if (meta.TryGetProperty("name", out JsonElement nameElem) &&
                    nameElem.ValueKind == JsonValueKind.String)
                    data.Meta.Name = nameElem.GetString() ?? "";

                if (meta.TryGetProperty("version", out JsonElement versionElem) &&
                    versionElem.ValueKind == JsonValueKind.String)
                    data.Meta.Version = versionElem.GetString() ?? "";

                if (meta.TryGetProperty("authors", out JsonElement authorsElem) &&
                    authorsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement authorElem in authorsElem.EnumerateArray())
                    {
                        if (authorElem.ValueKind == JsonValueKind.String)
                            data.Meta.Authors.Add(authorElem.GetString() ?? "");
                    }
                }

                if (meta.TryGetProperty("copyrightInformation", out JsonElement copyrightElem) &&
                    copyrightElem.ValueKind == JsonValueKind.String)
                    data.Meta.CopyrightInformation = copyrightElem.GetString() ?? "";
            }
        }

        private static void ParseMtoonMaterials(JsonElement root, VrmData data)
        {
            if (!root.TryGetProperty("materials", out JsonElement materialsElem) ||
                materialsElem.ValueKind != JsonValueKind.Array)
                return;

            for (int i = 0; i < materialsElem.GetArrayLength() && i < data.Materials.Count; i++)
            {
                JsonElement materialElem = materialsElem[i];
                if (materialElem.ValueKind != JsonValueKind.Object)
                    continue;

                if (!materialElem.TryGetProperty("extensions", out JsonElement extensions) ||
                    extensions.ValueKind != JsonValueKind.Object)
                    continue;

                if (!extensions.TryGetProperty("VRMC_materials_mtoon", out JsonElement mtoonElem) ||
                    mtoonElem.ValueKind != JsonValueKind.Object)
                    continue;

                VrmMtoonData mtoon = new VrmMtoonData();

                ReadStringProperty(mtoonElem, "specVersion", value => mtoon.SpecVersion = value);
                ReadStringProperty(mtoonElem, "outlineWidthMode", value => mtoon.OutlineWidthMode = value);

                if (mtoonElem.TryGetProperty("shadeColorFactor", out JsonElement shadeColorElem) &&
                    TryReadNumberArray(shadeColorElem, out float[] shadeColor) && shadeColor.Length >= 3)
                    mtoon.ShadeColorFactor = new Vector3(shadeColor[0], shadeColor[1], shadeColor[2]);

                if (mtoonElem.TryGetProperty("shadeMultiplyTexture", out JsonElement shadeTexElem) &&
                    shadeTexElem.ValueKind == JsonValueKind.Object &&
                    shadeTexElem.TryGetProperty("index", out JsonElement shadeTexIdx) &&
                    shadeTexIdx.ValueKind == JsonValueKind.Number)
                    mtoon.ShadeMultiplyTextureIndex = shadeTexIdx.GetInt32();

                if (mtoonElem.TryGetProperty("shadingShiftFactor", out JsonElement shadingShiftElem) &&
                    shadingShiftElem.ValueKind == JsonValueKind.Number)
                    mtoon.ShadingShiftFactor = (float)shadingShiftElem.GetDouble();

                if (mtoonElem.TryGetProperty("shadingToonyFactor", out JsonElement shadingToonyElem) &&
                    shadingToonyElem.ValueKind == JsonValueKind.Number)
                    mtoon.ShadingToonyFactor = (float)shadingToonyElem.GetDouble();

                if (mtoonElem.TryGetProperty("parametricRimColorFactor", out JsonElement rimColorElem) &&
                    TryReadNumberArray(rimColorElem, out float[] rimColor) && rimColor.Length >= 3)
                    mtoon.ParametricRimColorFactor = new Vector3(rimColor[0], rimColor[1], rimColor[2]);

                if (mtoonElem.TryGetProperty("parametricRimFresnelPowerFactor", out JsonElement rimPowerElem) &&
                    rimPowerElem.ValueKind == JsonValueKind.Number)
                    mtoon.ParametricRimFresnelPowerFactor = (float)rimPowerElem.GetDouble();

                if (mtoonElem.TryGetProperty("parametricRimLiftFactor", out JsonElement rimLiftElem) &&
                    rimLiftElem.ValueKind == JsonValueKind.Number)
                    mtoon.ParametricRimLiftFactor = (float)rimLiftElem.GetDouble();

                if (mtoonElem.TryGetProperty("rimMultiplyTexture", out JsonElement rimTexElem) &&
                    rimTexElem.ValueKind == JsonValueKind.Object &&
                    rimTexElem.TryGetProperty("index", out JsonElement rimTexIdx) &&
                    rimTexIdx.ValueKind == JsonValueKind.Number)
                    mtoon.RimMultiplyTextureIndex = rimTexIdx.GetInt32();

                if (mtoonElem.TryGetProperty("rimLightingMixFactor", out JsonElement rimMixElem) &&
                    rimMixElem.ValueKind == JsonValueKind.Number)
                    mtoon.RimLightingMixFactor = (float)rimMixElem.GetDouble();

                if (mtoonElem.TryGetProperty("matcapFactor", out JsonElement matcapFactorElem) &&
                    TryReadNumberArray(matcapFactorElem, out float[] matcapFactor) && matcapFactor.Length >= 3)
                    mtoon.MatcapFactor = new Vector3(matcapFactor[0], matcapFactor[1], matcapFactor[2]);

                if (mtoonElem.TryGetProperty("matcapTexture", out JsonElement matcapTexElem) &&
                    matcapTexElem.ValueKind == JsonValueKind.Object &&
                    matcapTexElem.TryGetProperty("index", out JsonElement matcapTexIdx) &&
                    matcapTexIdx.ValueKind == JsonValueKind.Number)
                    mtoon.MatcapTextureIndex = matcapTexIdx.GetInt32();

                if (mtoonElem.TryGetProperty("outlineColorFactor", out JsonElement outlineColorElem) &&
                    TryReadNumberArray(outlineColorElem, out float[] outlineColor) && outlineColor.Length >= 3)
                    mtoon.OutlineColorFactor = new Vector3(outlineColor[0], outlineColor[1], outlineColor[2]);

                if (mtoonElem.TryGetProperty("outlineWidthFactor", out JsonElement outlineWidthElem) &&
                    outlineWidthElem.ValueKind == JsonValueKind.Number)
                    mtoon.OutlineWidthFactor = (float)outlineWidthElem.GetDouble();

                if (mtoonElem.TryGetProperty("outlineLightingMixFactor", out JsonElement outlineMixElem) &&
                    outlineMixElem.ValueKind == JsonValueKind.Number)
                    mtoon.OutlineLightingMixFactor = (float)outlineMixElem.GetDouble();

                if (mtoonElem.TryGetProperty("outlineWidthMultiplyTexture", out JsonElement outlineWidthTexElem) &&
                    outlineWidthTexElem.ValueKind == JsonValueKind.Object &&
                    outlineWidthTexElem.TryGetProperty("index", out JsonElement outlineWidthTexIdx) &&
                    outlineWidthTexIdx.ValueKind == JsonValueKind.Number)
                    mtoon.OutlineWidthMultiplyTextureIndex = outlineWidthTexIdx.GetInt32();

                if (mtoonElem.TryGetProperty("giEqualizationFactor", out JsonElement giEqualElem) &&
                    giEqualElem.ValueKind == JsonValueKind.Number)
                    mtoon.GiEqualizationFactor = (float)giEqualElem.GetDouble();

                if (mtoonElem.TryGetProperty("transparentWithZWrite", out JsonElement zwriteElem))
                {
                    mtoon.TransparentWithZWrite = zwriteElem.ValueKind == JsonValueKind.True ||
                                                  (zwriteElem.ValueKind == JsonValueKind.Number && zwriteElem.GetDouble() != 0);
                }

                if (mtoonElem.TryGetProperty("renderQueueOffsetNumber", out JsonElement queueOffsetElem) &&
                    queueOffsetElem.ValueKind == JsonValueKind.Number)
                    mtoon.RenderQueueOffsetNumber = queueOffsetElem.GetInt32();

                if (mtoonElem.TryGetProperty("uvAnimationScrollXSpeedFactor", out JsonElement uvScrollXElem) &&
                    uvScrollXElem.ValueKind == JsonValueKind.Number)
                    mtoon.UvAnimationScrollXSpeedFactor = (float)uvScrollXElem.GetDouble();

                if (mtoonElem.TryGetProperty("uvAnimationScrollYSpeedFactor", out JsonElement uvScrollYElem) &&
                    uvScrollYElem.ValueKind == JsonValueKind.Number)
                    mtoon.UvAnimationScrollYSpeedFactor = (float)uvScrollYElem.GetDouble();

                if (mtoonElem.TryGetProperty("uvAnimationRotationSpeedFactor", out JsonElement uvRotElem) &&
                    uvRotElem.ValueKind == JsonValueKind.Number)
                    mtoon.UvAnimationRotationSpeedFactor = (float)uvRotElem.GetDouble();

                data.Materials[i].Mtoon = mtoon;
            }
        }

        private static void ParseExpressionGroup(JsonElement expressions, string groupName, bool isPreset, VrmData data)
        {
            if (!expressions.TryGetProperty(groupName, out JsonElement group) ||
                group.ValueKind != JsonValueKind.Object)
                return;

            foreach (JsonProperty prop in group.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;

                JsonElement exprElem = prop.Value;

                VrmRawExpression expression = new VrmRawExpression
                {
                    Name = prop.Name,
                    IsPreset = isPreset
                };

                if (exprElem.TryGetProperty("isBinary", out JsonElement binaryElem))
                {
                    expression.IsBinary = binaryElem.ValueKind == JsonValueKind.True ||
                                          (binaryElem.ValueKind == JsonValueKind.Number && binaryElem.GetDouble() != 0);
                }

                ReadStringProperty(exprElem, "overrideMouth", value => expression.OverrideMouth = value);
                ReadStringProperty(exprElem, "overrideBlink", value => expression.OverrideBlink = value);
                ReadStringProperty(exprElem, "overrideLookAt", value => expression.OverrideLookAt = value);

                if (exprElem.TryGetProperty("morphTargetBinds", out JsonElement morphBinds) &&
                    morphBinds.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement bindElem in morphBinds.EnumerateArray())
                    {
                        if (bindElem.ValueKind != JsonValueKind.Object)
                            continue;

                        if (!bindElem.TryGetProperty("node", out JsonElement nodeElem) ||
                            nodeElem.ValueKind != JsonValueKind.Number)
                            continue;

                        if (!bindElem.TryGetProperty("index", out JsonElement indexElem) ||
                            indexElem.ValueKind != JsonValueKind.Number)
                            continue;

                        float weight = 1f;
                        if (bindElem.TryGetProperty("weight", out JsonElement weightElem) &&
                            weightElem.ValueKind == JsonValueKind.Number)
                            weight = (float)weightElem.GetDouble();

                        expression.MorphTargetBinds.Add(new VrmRawMorphTargetBind
                        {
                            NodeIndex = nodeElem.GetInt32(),
                            MorphIndex = indexElem.GetInt32(),
                            Weight = weight
                        });
                    }
                }

                if (exprElem.TryGetProperty("materialColorBinds", out JsonElement colorBinds) &&
                    colorBinds.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement bindElem in colorBinds.EnumerateArray())
                    {
                        if (bindElem.ValueKind != JsonValueKind.Object)
                            continue;

                        if (!bindElem.TryGetProperty("material", out JsonElement materialElem) ||
                            materialElem.ValueKind != JsonValueKind.Number)
                            continue;

                        string type = "color";
                        if (bindElem.TryGetProperty("type", out JsonElement typeElem) &&
                            typeElem.ValueKind == JsonValueKind.String)
                            type = typeElem.GetString() ?? "color";

                        Vector4 targetValue = Vector4.Zero;
                        if (bindElem.TryGetProperty("targetValue", out JsonElement targetElem) &&
                            TryReadNumberArray(targetElem, out float[] targetNumbers) && targetNumbers.Length >= 4)
                        {
                            targetValue = new Vector4(targetNumbers[0], targetNumbers[1], targetNumbers[2], targetNumbers[3]);
                        }

                        expression.MaterialColorBinds.Add(new VrmRawMaterialColorBind
                        {
                            MaterialIndex = materialElem.GetInt32(),
                            Type = type,
                            TargetValue = targetValue
                        });
                    }
                }

                if (exprElem.TryGetProperty("textureTransformBinds", out JsonElement transformBinds) &&
                    transformBinds.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement bindElem in transformBinds.EnumerateArray())
                    {
                        if (bindElem.ValueKind != JsonValueKind.Object)
                            continue;

                        if (!bindElem.TryGetProperty("material", out JsonElement materialElem) ||
                            materialElem.ValueKind != JsonValueKind.Number)
                            continue;

                        Vector2 scale = Vector2.One;
                        if (bindElem.TryGetProperty("scale", out JsonElement scaleElem) &&
                            TryReadNumberArray(scaleElem, out float[] scaleNumbers) && scaleNumbers.Length >= 2)
                            scale = new Vector2(scaleNumbers[0], scaleNumbers[1]);

                        Vector2 offset = Vector2.Zero;
                        if (bindElem.TryGetProperty("offset", out JsonElement offsetElem) &&
                            TryReadNumberArray(offsetElem, out float[] offsetNumbers) && offsetNumbers.Length >= 2)
                            offset = new Vector2(offsetNumbers[0], offsetNumbers[1]);

                        expression.TextureTransformBinds.Add(new VrmRawTextureTransformBind
                        {
                            MaterialIndex = materialElem.GetInt32(),
                            Scale = scale,
                            Offset = offset
                        });
                    }
                }

                data.Expressions.Add(expression);
            }
        }

        private static void ReadRangeMap(JsonElement lookAt, string propertyName, VrmRangeMap rangeMap)
        {
            if (!lookAt.TryGetProperty(propertyName, out JsonElement mapElem) ||
                mapElem.ValueKind != JsonValueKind.Object)
                return;

            if (mapElem.TryGetProperty("inputMaxValue", out JsonElement inputElem) &&
                inputElem.ValueKind == JsonValueKind.Number)
                rangeMap.InputMaxValue = (float)inputElem.GetDouble();

            if (mapElem.TryGetProperty("outputScale", out JsonElement outputElem) &&
                outputElem.ValueKind == JsonValueKind.Number)
                rangeMap.OutputScale = (float)outputElem.GetDouble();
        }

        private static void ReadStringProperty(JsonElement element, string propertyName, Action<string> setter)
        {
            if (element.TryGetProperty(propertyName, out JsonElement propElem) &&
                propElem.ValueKind == JsonValueKind.String)
                setter(propElem.GetString() ?? "");
        }

        private static bool TryReadNumberArray(JsonElement element, out float[] values)
        {
            values = Array.Empty<float>();

            if (element.ValueKind != JsonValueKind.Array)
                return false;

            var list = new List<float>();

            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number)
                    list.Add((float)item.GetDouble());
            }

            if (list.Count == 0)
                return false;

            values = list.ToArray();
            return true;
        }
    }
}
