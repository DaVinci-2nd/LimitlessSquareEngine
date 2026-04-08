using MoonSharp.Interpreter;
using LimitlessSquareEngine.Engine;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine
{
    internal partial class Graphics
    {
        private readonly Dictionary<string, MeshData> _meshes = new(StringComparer.Ordinal);

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

            if (_meshSurfaceGpuCache.TryGetValue(key, out var cached))
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
        internal bool TryGetMeshColliderTriangles(
            string meshId,
            out List<MeshColliderTriangle> triangles)
        {
            triangles = new List<MeshColliderTriangle>();

            if (string.IsNullOrWhiteSpace(meshId))
                return false;

            if (!_meshes.TryGetValue(meshId, out MeshData mesh))
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
    }
}
