using System;
using System.Collections.Generic;
using System.Text.Json;
using MoonSharp.Interpreter;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimitlessSquareEngine.Engine.Terrain
{
    // 地形节点
    [MoonSharpUserData]
    public sealed class Terrain
    {
        public string SceneId { get; }
        public string ObjectId { get; }

        public double Radius { get; private set; } = 6371000.0;

        public uint Seed { get; private set; } = 12345;

        public readonly TerrainProfile Profile = new();
        public readonly TerrainStreamer Streamer = new();

        public CubeMapData? HeightMap { get; private set; }

        public CubeMapData? SplatMap { get; private set; }

        public readonly CompositeHeightSource Height = new();

        public bool StreamingEnabled { get; private set; }

        private readonly CameraInterestSource _cameraInterest = new();
        private readonly ManualInterestSource _manualRenderInterest = new();
        private readonly ManualInterestSource _manualPhysicsInterest = new();

        private Double3 _center = Double3.Zero;
        private bool _centerDirty = true;
        private double _mapMinHeight = -11000.0;
        private double _mapMaxHeight = 8848.0;
        private ITerrainHeightRule? _heightRule = null;
        private bool _detailNoiseConfigured = false;
        private double _detailNoiseFrequency = 0.001;
        private int _detailNoiseOctaves = 4;
        private double _detailNoiseAmplitude = 50.0;

        private readonly TerrainMeshBuilder _meshBuilder;
        private bool _atlasConfigured = false;
        private string _resolvedAtlasTextureKey = "";
        private string _terrainMaterialKey = "";

        public string AtlasTextureKey { get; private set; } = "";
        public int AtlasTilesX { get; private set; } = 2;
        public int AtlasTilesY { get; private set; } = 2;
        public double TilingPerFace { get; private set; } = 32.0;
        public string[] MaterialTags { get; private set; } = new[] { "terrain" };
        public string TerrainMaterialKey => _terrainMaterialKey;

        public Terrain(string sceneId, string objectId)
        {
            SceneId = sceneId ?? throw new ArgumentNullException(nameof(sceneId));
            ObjectId = objectId ?? throw new ArgumentNullException(nameof(objectId));

            Streamer.Profile = Profile;
            Streamer.PlanetRadius = Radius;
            Streamer.RenderInterestSources.Add(_cameraInterest);
            Streamer.RenderInterestSources.Add(_manualRenderInterest);
            Streamer.PhysicsInterestSources.Add(_manualPhysicsInterest);

            _meshBuilder = new TerrainMeshBuilder(this);
            Streamer.RenderBuilder = _meshBuilder.Build;
            Streamer.RenderCommitter = CommitRender;
            Streamer.RenderUnloader = UnloadRender;
            Streamer.PhysicsBuilder = null;
            Streamer.PhysicsCommitter = null;
        }

        // 配置

        public void SetRadius(double radius)
        {
            if (radius <= 0)
                throw new ArgumentOutOfRangeException(nameof(radius));
            Radius = radius;
            Streamer.PlanetRadius = radius;
            _centerDirty = true;
        }

        public void SetSeed(uint seed)
        {
            Seed = seed;
            RebuildHeightSource();
        }

        // 设置6面高度图
        public void SetHeightMap(CubeMapData? map, double minHeight = -11000.0, double maxHeight = 8848.0)
        {
            HeightMap = map;
            _mapMinHeight = minHeight;
            _mapMaxHeight = maxHeight;
            RebuildHeightSource();
        }

        // 配置细节噪声
        public void ConfigureDetailNoise(double frequency, int octaves, double amplitude)
        {
            _detailNoiseConfigured = true;
            _detailNoiseFrequency = frequency;
            _detailNoiseOctaves = octaves;
            _detailNoiseAmplitude = amplitude;
            RebuildHeightSource();
        }

        public void DisableDetailNoise()
        {
            _detailNoiseConfigured = false;
            RebuildHeightSource();
        }

        // 设置程序化高度规则
        public void SetHeightRule(ITerrainHeightRule? rule)
        {
            _heightRule = rule;
            RebuildHeightSource();
        }

        // 生成整体噪声高度图
        public void GenerateGlobalNoiseMap(int seed)
        {
            PerlinHeightRule rule = TerrainMapGenerator.CreatePerlinRule(
                seed <= 0 ? null : (uint)seed);
            SetHeightRule(rule);
        }

        // 配置纹理图集
        public void ConfigureAtlas(string? textureKey, int tilesX, int tilesY, double tilingPerFace, string[]? tags)
        {
            AtlasTextureKey = textureKey ?? "";
            AtlasTilesX = Math.Max(1, tilesX);
            AtlasTilesY = Math.Max(1, tilesY);
            if (tilingPerFace > 0.0)
                TilingPerFace = tilingPerFace;
            if (tags != null && tags.Length > 0)
                MaterialTags = tags;
            _atlasConfigured = false;
        }

        // 返回顶点材质图集块索引
        [MoonSharpHidden]
        public int GetMaterialTile(in Double3 worldPos, in Double3 dir)
        {
            return 0;
        }

        // 返回材质图集块对应的标签
        [MoonSharpHidden]
        public string GetMaterialTag(int tileIndex)
        {
            string[] tags = MaterialTags;
            if (tags != null && tags.Length > 0)
                return tags[Math.Min(tileIndex, tags.Length - 1)];
            return "terrain";
        }

        // 确保渲染资源就绪
        private void EnsureRenderResources()
        {
            Graphics? graphics = Scene.BoundGraphics;
            if (graphics == null)
                return;

            if (!_atlasConfigured)
            {
                if (string.IsNullOrWhiteSpace(AtlasTextureKey))
                {
                    string key = "__terrain_atlas:" + SceneId + ":" + ObjectId;
                    using Image<Rgba32> image = BuildDefaultAtlasImage(256, AtlasTilesX, AtlasTilesY);
                    graphics.RegisterTextureFromMemory(key, image);
                    _resolvedAtlasTextureKey = key;
                }
                else
                {
                    _resolvedAtlasTextureKey = AtlasTextureKey;
                }
                _atlasConfigured = true;
            }

            if (string.IsNullOrEmpty(_terrainMaterialKey))
                _terrainMaterialKey = "__terrain:" + SceneId + ":" + ObjectId;

            if (!Program._generatedMaterialJsonRegistry.ContainsKey(_terrainMaterialKey))
            {
                Program._generatedMaterialJsonRegistry[_terrainMaterialKey] =
                    BuildTerrainMaterialJson(_resolvedAtlasTextureKey);
            }
        }

        // 提交渲染产物
        private void CommitRender(TerrainTile tile, int lod, object? buildData)
        {
            if (buildData is not TerrainMeshBuildResult result)
                return;

            Graphics? graphics = Scene.BoundGraphics;
            if (graphics == null)
                return;

            EnsureRenderResources();

            int layerCount = result.Layers.Count;
            if (layerCount == 0)
                return;

            var meshIds = new string[layerCount];
            var objectIds = new string[layerCount];
            var tags = new string[layerCount];

            for (int i = 0; i < layerCount; i++)
            {
                TerrainLayerMesh layer = result.Layers[i];
                string meshId = BuildMeshId(tile.Key, i);
                string objectId = BuildObjectId(tile.Key, i);
                meshIds[i] = meshId;
                objectIds[i] = objectId;
                tags[i] = layer.Tag;

                graphics.RegisterMesh(meshId, layer.Vertices, Silk.NET.OpenGL.PrimitiveType.Triangles, 16);

                graphics.UpsertSceneObject(new Graphics.SceneRenderObjectSnapshot
                {
                    SceneId = SceneId,
                    ObjectId = objectId,
                    Type = "Object",
                    Active = true,
                    Visible = true,
                    Mesh = meshId,
                    Materials = new List<string> { _terrainMaterialKey },
                    RenderTag = layer.Tag,
                    WorldPosition = result.Origin,
                    WorldRotation = DQuaternion.Identity,
                    WorldScale = Double3.One,
                    StaticRenderEligible = true,
                    TransformRevision = 0
                });
            }

            TerrainRenderArtifact artifact = tile.EnsureRender();
            artifact.Lod = lod;
            artifact.MeshIds = meshIds;
            artifact.ObjectIds = objectIds;
            artifact.LayerTags = tags;
            artifact.LayerCount = layerCount;
            artifact.BuiltStitchLevels = result.StitchLevels;
            artifact.BuildData = buildData;
        }

        // 卸载渲染产物
        private void UnloadRender(TerrainTile tile)
        {
            Graphics? graphics = Scene.BoundGraphics;
            if (graphics == null)
                return;

            if (tile.Render == null)
                return;

            foreach (string meshId in tile.Render.MeshIds)
                graphics.RemoveMesh(meshId);

            foreach (string objectId in tile.Render.ObjectIds)
                graphics.RemoveSceneObject(SceneId, objectId);
        }

        private string BuildMeshId(TileKey key, int layer)
        {
            return "terrain:" + SceneId + ":" + ObjectId +
                ":L" + key.Level + ":" + key.Face + ":" + key.LX + ":" + key.LY + ":l" + layer;
        }

        private string BuildObjectId(TileKey key, int layer)
        {
            return "terrain:" + SceneId + ":" + ObjectId +
                ":o" + key.Face + ":" + key.Level + ":" + key.LX + ":" + key.LY + ":l" + layer;
        }

        private static Image<Rgba32> BuildDefaultAtlasImage(int tileSize, int tilesX, int tilesY)
        {
            var image = new Image<Rgba32>(tileSize * tilesX, tileSize * tilesY);

            var baseColors = new[]
            {
                new Rgba32(96, 160, 80),
                new Rgba32(120, 110, 100),
                new Rgba32(200, 180, 130),
                new Rgba32(235, 235, 245)
            };

            for (int y = 0; y < image.Height; y++)
            {
                int ty = y / tileSize;
                for (int x = 0; x < image.Width; x++)
                {
                    int tx = x / tileSize;
                    Rgba32 baseColor = baseColors[(ty * tilesX + tx) % baseColors.Length];
                    bool checker = ((x / 16) + (y / 16)) % 2 == 0;
                    int shade = checker ? 3 : 1;
                    image[x, y] = new Rgba32(
                        (byte)Math.Clamp(baseColor.R * shade / 4, 0, 255),
                        (byte)Math.Clamp(baseColor.G * shade / 4, 0, 255),
                        (byte)Math.Clamp(baseColor.B * shade / 4, 0, 255),
                        255);
                }
            }

            return image;
        }

        private static string BuildTerrainMaterialJson(string atlasKey)
        {
            var parameters = new Dictionary<string, object?>
            {
                ["uColor"] = new[] { 1.0, 1.0, 1.0, 1.0 },
                ["uUseTexture"] = 1,
                ["uTexture"] = atlasKey,
                ["uTextureWrap"] = "Clamp",
                ["uUseAlphaCutoff"] = 0,
                ["uUseNormalTexture"] = 0,
                ["uAmbientStrength"] = 1.0,
                ["uSpecularIntensity"] = 0.0,
                ["uSpecularColor"] = new[] { 1.0, 1.0, 1.0 },
                ["uRimIntensity"] = 0.0,
                ["uSmoothness"] = 0.0,
                ["uMetallic"] = 0.0,
                ["uReceiveShadow"] = 1,
                ["uCastShadow"] = 1,
                ["uReceiveReflection"] = 0,
                ["uEnableColorBanding"] = 0,
                ["uEnableOutline"] = 0,
                ["uCull"] = "front"
            };

            var root = new Dictionary<string, object?>
            {
                ["assetType"] = "Material",
                ["shader"] = "Shaders/Builtin/Lit",
                ["parameters"] = parameters
            };

            return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        }

        public double MapMinHeight => _mapMinHeight;
        public double MapMaxHeight => _mapMaxHeight;

        // 设置地图高度范围
        public void ConfigureMapHeightRange(double minHeight, double maxHeight)
        {
            _mapMinHeight = minHeight;
            _mapMaxHeight = maxHeight;
            RebuildHeightSource();
        }

        /// <summary>
        /// 从6张资产路径加载高度图
        /// </summary>
        public bool LoadHeightMapFromPaths(string[] facePaths, double minHeight = -11000.0, double maxHeight = 8848.0)
        {
            if (facePaths == null || facePaths.Length != 6)
                throw new ArgumentException("[X] Height map requires exactly 6 face paths.", nameof(facePaths));

            var faces = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>[6];

            for (int i = 0; i < 6; i++)
            {
                string fullPath = facePaths[i];
                if (!File.Exists(fullPath) && Program.EnsureTextureRegistered(facePaths[i], out string resolved))
                    fullPath = resolved;

                if (!File.Exists(fullPath))
                {
                    Console.WriteLine($"[X] Height map face {i} not found: {facePaths[i]}");
                    return false;
                }

                faces[i] = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(fullPath);
            }

            SetHeightMap(CubeMapData.FromImages(faces), minHeight, maxHeight);
            return true;
        }

        /// <summary>
        /// 从等距圆柱全景图生成高度图
        /// </summary>
        public bool LoadHeightMapFromEquirectangular(string equirectPath, int faceSize, double minHeight = -11000.0, double maxHeight = 8848.0)
        {
            string fullPath = equirectPath;
            if (!File.Exists(fullPath) && Program.EnsureTextureRegistered(equirectPath, out string resolved))
                fullPath = resolved;

            if (!File.Exists(fullPath))
            {
                Console.WriteLine($"[X] Height map equirect not found: {equirectPath}");
                return false;
            }

            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(fullPath);
            SetHeightMap(CubeMapData.FromEquirectangular(image, faceSize), minHeight, maxHeight);
            return true;
        }

        private void RebuildHeightSource()
        {
            Height.SetEditProvider(null);
            Height.ClearLayers();

            if (_heightRule != null)
            {
                Height.AddLayer(new RuleHeightSource(_heightRule), 1.0, 0.0);
            }
            else if (HeightMap != null)
            {
                Height.AddLayer(new MapHeightSource(HeightMap, _mapMinHeight, _mapMaxHeight), 1.0, 0.0);
            }
            else
            {
                Height.AddLayer(new ConstantHeightSource(0.0), 1.0, 0.0);
            }

            if (_detailNoiseConfigured)
            {
                Height.AddLayer(
                    new NoiseHeightSource(Seed ^ 0x9E3779B9u, _detailNoiseFrequency, _detailNoiseOctaves),
                    _detailNoiseAmplitude,
                    0.0);
            }
        }

        // 采样接口

        // 按经纬度采样地形高度
        public double SampleHeightAtLatLon(double latDeg, double lonDeg)
        {
            Double3 dir = LatLonToDirection(latDeg, lonDeg);
            return Height.SampleDirection(dir);
        }

        // 按方向采样地形高度
        public double SampleHeightDirection(double x, double y, double z)
        {
            return Height.SampleDirection(Normalize(new Double3(x, y, z)));
        }

        // 按经纬度采样材质图
        public void SampleSplatAtLatLon(double latDeg, double lonDeg, out float r, out float g, out float b, out float a)
        {
            if (SplatMap == null)
            {
                r = g = b = a = 0f;
                return;
            }
            SplatMap.SampleDirection(LatLonToDirection(latDeg, lonDeg), out r, out g, out b, out a);
        }

        private static Double3 LatLonToDirection(double latDeg, double lonDeg)
        {
            double lat = latDeg * Math.PI / 180.0;
            double lon = lonDeg * Math.PI / 180.0;
            double clat = Math.Cos(lat);
            return new Double3(
                clat * Math.Cos(lon),
                Math.Sin(lat),
                clat * Math.Sin(lon));
        }

        private static Double3 Normalize(in Double3 v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (len <= 1e-300)
                return new Double3(0, 0, 1);
            return new Double3(v.X / len, v.Y / len, v.Z / len);
        }

        // 流式控制

        public void SetStreamingEnabled(bool enabled)
        {
            if (StreamingEnabled == enabled)
                return;

            StreamingEnabled = enabled;
            if (!enabled)
                Streamer.Clear();
        }

        // 添加渲染兴趣点
        public void AddRenderInterest(double worldX, double worldY, double worldZ, double radiusMeters, int maxLod)
        {
            RefreshCenter();
            Double3 local = new Double3(worldX, worldY, worldZ) - _center;
            _manualRenderInterest.Add(new TerrainInterest(local, radiusMeters, maxLod));
        }

        // 添加物理兴趣点
        public void AddPhysicsInterest(double worldX, double worldY, double worldZ, double radiusMeters, int maxLod)
        {
            RefreshCenter();
            Double3 local = new Double3(worldX, worldY, worldZ) - _center;
            _manualPhysicsInterest.Add(new TerrainInterest(local, radiusMeters, maxLod));
        }

        public void ClearManualRenderInterests() => _manualRenderInterest.Clear();
        public void ClearManualPhysicsInterests() => _manualPhysicsInterest.Clear();

        // 使指定区域产物失效
        public void InvalidateRegion(double worldX, double worldY, double worldZ, double radiusMeters, bool render, bool physics)
        {
            RefreshCenter();
            Double3 local = new Double3(worldX, worldY, worldZ) - _center;
            Streamer.InvalidateRegion(local, radiusMeters, render, physics);
        }

        /// <summary>
        /// 每帧推进地形
        /// </summary>
        public void Tick()
        {
            if (!StreamingEnabled)
                return;

            RefreshCenter();

            RefreshCameraInterests();

            Streamer.Tick();
        }

        private void RefreshCenter()
        {
            if (_centerDirty || StreamingEnabled)
            {
                _center = Scene.GetPosition(SceneId, ObjectId);
                _centerDirty = false;
            }
        }

        private void RefreshCameraInterests()
        {
            var cameras = new List<(Double3 WorldPos, int MaxLod, double Priority, Double3 Forward, double FovRadians, double Aspect)>();

            foreach (SceneCameraQueueItem item in Scene.GetCameraQueue(SceneId))
            {
                Double3 pos = Scene.GetPosition(SceneId, item.ObjectId);
                Double3 forward = Scene.GetForward(SceneId, item.ObjectId);
                double fovRadians = item.Settings.ProjectionType == 1
                    ? 0.0
                    : item.Settings.FovOrSize * Math.PI / 180.0;
                cameras.Add((pos, Profile.RenderMaxLevel, 1.0, forward, fovRadians, 16.0 / 9.0));
            }

            _cameraInterest.SetCameras(cameras);
        }

        // 诊断

        public int TileCount => Streamer.TileCount;
        public int PendingRenderCount => Streamer.PendingRenderCount;
        public int PendingPhysicsCount => Streamer.PendingPhysicsCount;
        public int InFlightCount => Streamer.InFlightCount;

        public int RenderReadyCount
        {
            get
            {
                int count = 0;
                foreach (TerrainTile tile in Streamer.EnumerateTiles())
                {
                    if (tile.Render != null && tile.Render.State == TileArtifactState.Ready)
                        count++;
                }
                return count;
            }
        }

        public int PhysicsReadyCount
        {
            get
            {
                int count = 0;
                foreach (TerrainTile tile in Streamer.EnumerateTiles())
                {
                    if (tile.Physics != null && tile.Physics.State == TileArtifactState.Ready)
                        count++;
                }
                return count;
            }
        }
    }
}
