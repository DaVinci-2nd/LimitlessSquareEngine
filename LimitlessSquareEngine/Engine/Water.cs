using System;
using System.Collections.Generic;
using System.Text.Json;
using MoonSharp.Interpreter;

namespace LimitlessSquareEngine.Engine
{
    public enum WaterMode
    {
        Ocean = 0,
        Lake = 1,
        River = 2
    }

    [MoonSharpUserData]
    public sealed class Water
    {
        public string SceneId { get; }
        public string ObjectId { get; }

        public WaterMode Mode { get; private set; } = WaterMode.Ocean;
        public double Radius { get; private set; } = 6371000.0;
        public double SeaLevel { get; private set; } = 0.0;
        public double SurfaceRadius => Radius + SeaLevel;

        public readonly TerrainProfile Profile = new();
        public readonly TerrainStreamer Streamer = new();

        public bool StreamingEnabled { get; private set; }

        public double StreamRadiusScale { get; private set; } = 1.15;
        public double StreamRadiusMargin { get; private set; } = 2000.0;

        public string TerrainObjectId { get; private set; } = "";

        private double[] _color = { 0.03, 0.16, 0.28, 1.0 };
        private double[] _specularColor = { 1.0, 1.0, 1.0 };
        private double _specularIntensity = 2.0;
        private double _specularRange = 0.05;
        private double _ambientStrength = 1.0;
        private double _smoothness = 0.95;
        private double _reflectionIntensity = 1.0;
        private double _fresnelPower = 5.0;
        private double _fresnelStrength = 1.0;
        private int _receiveShadow = 1;
        private int _castShadow = 0;

        private readonly ManualInterestSource _cameraInterest = new();
        private Double3 _center = Double3.Zero;
        private bool _centerDirty = true;

        private readonly WaterMeshBuilder _meshBuilder;
        private string _waterMaterialKey = "";

        public string WaterMaterialKey => _waterMaterialKey;

        public Water(string sceneId, string objectId)
        {
            SceneId = sceneId ?? throw new ArgumentNullException(nameof(sceneId));
            ObjectId = objectId ?? throw new ArgumentNullException(nameof(objectId));

            Streamer.Profile = Profile;
            Streamer.PlanetRadius = SurfaceRadius;
            Streamer.RenderInterestSources.Add(_cameraInterest);

            _meshBuilder = new WaterMeshBuilder(this);
            Streamer.RenderBuilder = _meshBuilder.Build;
            Streamer.RenderCommitter = CommitRender;
            Streamer.RenderUnloader = UnloadRender;
        }

        public void SetMode(WaterMode mode)
        {
            Mode = mode;
        }

        public void SetTerrainObjectId(string? terrainObjectId)
        {
            TerrainObjectId = terrainObjectId ?? "";
        }

        public void SetRadius(double radius)
        {
            if (radius <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(radius));

            Radius = radius;
            Streamer.PlanetRadius = SurfaceRadius;
            _centerDirty = true;
            Streamer.Clear();
        }

        public void SetSeaLevel(double seaLevel)
        {
            SeaLevel = seaLevel;
            Streamer.PlanetRadius = SurfaceRadius;
            _centerDirty = true;
            Streamer.Clear();
        }

        public void ConfigureProfile(int baseLevel, double baseRadius, int maxLevel, int tileResolution)
        {
            if (baseLevel >= 0)
                Profile.RenderBaseLevel = baseLevel;

            if (baseRadius > 0.0)
                Profile.RenderBaseRadius = baseRadius;

            if (maxLevel > 0)
                Profile.RenderMaxLevel = maxLevel;

            if (tileResolution >= 2)
                Profile.RenderBaseTileResolution = tileResolution;

            Streamer.Clear();
        }

        public void ConfigureStreaming(double radiusScale, double radiusMargin)
        {
            if (radiusScale > 0.0)
                StreamRadiusScale = radiusScale;

            if (radiusMargin >= 0.0)
                StreamRadiusMargin = radiusMargin;
        }

        public void SetColor(double r, double g, double b, double a)
        {
            _color = new[] { r, g, b, a };
        }

        public void SetSpecularColor(double r, double g, double b)
        {
            _specularColor = new[] { r, g, b };
        }

        public void SetSpecularIntensity(double intensity)
        {
            _specularIntensity = intensity;
        }

        public void SetSpecularRange(double specularRange)
        {
            _specularRange = specularRange;
        }

        public void SetAmbientStrength(double strength)
        {
            _ambientStrength = strength;
        }

        public void SetSmoothness(double smoothness)
        {
            _smoothness = smoothness;
        }

        public void SetReflectionIntensity(double intensity)
        {
            _reflectionIntensity = intensity;
        }

        public void SetFresnelPower(double power)
        {
            _fresnelPower = power;
        }

        public void SetFresnelStrength(double strength)
        {
            _fresnelStrength = strength;
        }

        public void SetReceiveShadow(int receiveShadow)
        {
            _receiveShadow = receiveShadow;
        }

        public void SetCastShadow(int castShadow)
        {
            _castShadow = castShadow;
        }

        public void SetStreamingEnabled(bool enabled)
        {
            if (StreamingEnabled == enabled)
                return;

            StreamingEnabled = enabled;
            if (!enabled)
                Streamer.Clear();
        }

        public void AddRenderInterest(double worldX, double worldY, double worldZ, double radiusMeters, int maxLod)
        {
            RefreshCenter();
            Double3 local = new Double3(worldX, worldY, worldZ) - _center;
            _cameraInterest.Add(local, radiusMeters, maxLod);
        }

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
            _cameraInterest.Clear();

            double surfaceRadius = SurfaceRadius;

            foreach (SceneCameraQueueItem item in Scene.GetCameraQueue(SceneId))
            {
                Double3 worldPos = Scene.GetPosition(SceneId, item.ObjectId);
                Double3 local = worldPos - _center;
                double camDist = Length(local);

                double horizon = camDist > surfaceRadius
                    ? Math.Sqrt(camDist * camDist - surfaceRadius * surfaceRadius)
                    : 0.0;

                _cameraInterest.Add(local, horizon * StreamRadiusScale + StreamRadiusMargin, Profile.RenderMaxLevel);
            }
        }

        private void EnsureRenderResources()
        {
            Graphics? graphics = Scene.BoundGraphics;
            if (graphics == null)
                return;

            if (string.IsNullOrEmpty(_waterMaterialKey))
                _waterMaterialKey = "__water:" + SceneId + ":" + ObjectId;

            if (!Program._generatedMaterialJsonRegistry.ContainsKey(_waterMaterialKey))
                Program._generatedMaterialJsonRegistry[_waterMaterialKey] = BuildWaterMaterialJson();
        }

        private void CommitRender(TerrainTile tile, int lod, object? buildData)
        {
            if (buildData is not WaterMeshBuildResult result)
                return;

            Graphics? graphics = Scene.BoundGraphics;
            if (graphics == null)
                return;

            EnsureRenderResources();

            string meshId = BuildMeshId(tile.Key);
            string objectId = BuildObjectId(tile.Key);

            graphics.RegisterMesh(meshId, result.Vertices, Silk.NET.OpenGL.PrimitiveType.Triangles, 16);

            graphics.UpsertSceneObject(new Graphics.SceneRenderObjectSnapshot
            {
                SceneId = SceneId,
                ObjectId = objectId,
                Type = "Object",
                Active = true,
                Visible = true,
                Mesh = meshId,
                Materials = new List<string> { _waterMaterialKey },
                RenderTag = "",
                WorldPosition = _center + result.Origin,
                WorldRotation = DQuaternion.Identity,
                WorldScale = Double3.One,
                StaticRenderEligible = true,
                TransformRevision = 0
            });

            TerrainRenderArtifact artifact = tile.EnsureRender();
            artifact.Lod = lod;
            artifact.MeshIds = new[] { meshId };
            artifact.ObjectIds = new[] { objectId };
            artifact.LayerTags = new[] { "" };
            artifact.LayerCount = 1;
            artifact.BuiltStitchLevels = result.StitchLevels;
            artifact.BuildData = buildData;
        }

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

        private string BuildMeshId(TileKey key)
        {
            return "water:" + SceneId + ":" + ObjectId +
                ":L" + key.Level + ":" + key.Face + ":" + key.LX + ":" + key.LY;
        }

        private string BuildObjectId(TileKey key)
        {
            return "water:" + SceneId + ":" + ObjectId +
                ":o" + key.Face + ":" + key.Level + ":" + key.LX + ":" + key.LY;
        }

        private string BuildWaterMaterialJson()
        {
            var parameters = new Dictionary<string, object?>
            {
                ["uColor"] = _color,
                ["uUseTexture"] = 0,
                ["uUseAlphaCutoff"] = 0,
                ["uAmbientStrength"] = _ambientStrength,
                ["uSpecularIntensity"] = _specularIntensity,
                ["uSpecularRange"] = _specularRange,
                ["uSpecularColor"] = _specularColor,
                ["uSmoothness"] = _smoothness,
                ["uMetallic"] = 0.0,
                ["uReceiveShadow"] = _receiveShadow,
                ["uCastShadow"] = _castShadow,
                ["uReceiveReflection"] = 1,
                ["uReflectionSource"] = "Skybox",
                ["uReflectionIntensity"] = _reflectionIntensity,
                ["uWaterFresnelPower"] = _fresnelPower,
                ["uWaterFresnelStrength"] = _fresnelStrength,
                ["uEnableColorBanding"] = 0,
                ["uEnableOutline"] = 0,
                ["uCull"] = "both"
            };

            var root = new Dictionary<string, object?>
            {
                ["assetType"] = "Material",
                ["shader"] = "Shaders/Builtin/Water",
                ["parameters"] = parameters
            };

            return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        }

        public int TileCount => Streamer.TileCount;
        public int PendingRenderCount => Streamer.PendingRenderCount;
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

        private static double Length(in Double3 v)
        {
            return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        }
    }
}
