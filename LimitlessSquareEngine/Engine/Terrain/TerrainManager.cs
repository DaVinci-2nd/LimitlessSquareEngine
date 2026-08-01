using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LimitlessSquareEngine.Engine.Terrain
{
    // 地形节点管理器
    public static class TerrainManager
    {
        private static readonly ConcurrentDictionary<string, Terrain> _terrains = new(StringComparer.Ordinal);

        private static string BuildKey(string sceneId, string objectId) => sceneId + "::" + objectId;

        /// <summary>
        /// 获取或创建地形节点
        /// </summary>
        public static Terrain? GetOrCreate(string sceneId, string objectId)
        {
            if (string.IsNullOrWhiteSpace(sceneId) || string.IsNullOrWhiteSpace(objectId))
                return null;

            string key = BuildKey(sceneId, objectId);
            if (_terrains.TryGetValue(key, out Terrain? existing))
                return existing;

            SceneData? scene = Scene.GetLoadedScenes().FirstOrDefault(s => s.SceneId == sceneId);
            SceneObject? obj = scene?.Objects.FirstOrDefault(o => o.Id == objectId);
            if (obj == null)
            {
                Console.WriteLine($"[!] Terrain object '{objectId}' not found in scene '{sceneId}'.");
                return null;
            }

            if (!string.Equals(obj.Type, "Terrain", StringComparison.Ordinal))
            {
                Console.WriteLine($"[!] Object '{objectId}' is not a Terrain (type='{obj.Type}').");
                return null;
            }

            var terrain = new Terrain(sceneId, objectId);

            if (!string.IsNullOrWhiteSpace(obj.Data))
            {
                try
                {
                    ApplyConfig(terrain, obj.Data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Failed to parse terrain config for '{objectId}': {ex.Message}");
                }
            }

            _terrains[key] = terrain;
            return terrain;
        }

        public static Terrain? TryGet(string sceneId, string objectId)
        {
            return _terrains.TryGetValue(BuildKey(sceneId, objectId), out Terrain? t) ? t : null;
        }

        public static void Remove(string sceneId, string objectId)
        {
            if (_terrains.TryRemove(BuildKey(sceneId, objectId), out Terrain? terrain))
                terrain.Streamer.Clear();
        }

        // 每帧推进所有地形
        public static void TickAll()
        {
            foreach (Terrain terrain in _terrains.Values)
                terrain.Tick();
        }

        public static void ClearAll()
        {
            foreach (Terrain terrain in _terrains.Values)
                terrain.Streamer.Clear();
            _terrains.Clear();
        }

        public static int Count => _terrains.Count;

        public static IReadOnlyCollection<Terrain> GetAll()
        {
            return _terrains.Values.ToArray();
        }

        private static void ApplyConfig(Terrain terrain, string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            if (TryGetNumber(root, "radius", out double radius) && radius > 0)
                terrain.SetRadius(radius);

            if (TryGetUInt32(root, "seed", out uint seed))
                terrain.SetSeed(seed);

            if (TryGetNumber(root, "mapMinHeight", out double minH))
                terrain.ConfigureMapHeightRange(minH, terrain.MapMaxHeight);

            if (TryGetNumber(root, "mapMaxHeight", out double maxH))
                terrain.ConfigureMapHeightRange(terrain.MapMinHeight, maxH);

            if (root.TryGetProperty("detailNoise", out JsonElement noise) && noise.ValueKind == JsonValueKind.Object)
            {
                bool enabled = TryGetBool(noise, "enabled", out bool e) ? e : true;
                double freq = TryGetNumber(noise, "frequency", out double f) ? f : 0.001;
                int octaves = TryGetNumber(noise, "octaves", out double o) ? (int)o : 4;
                double amp = TryGetNumber(noise, "amplitude", out double a) ? a : 50.0;
                if (enabled)
                    terrain.ConfigureDetailNoise(freq, octaves, amp);
                else
                    terrain.DisableDetailNoise();
            }

            if (root.TryGetProperty("profile", out JsonElement profile) && profile.ValueKind == JsonValueKind.Object)
                ApplyProfile(terrain.Profile, profile);

            if (root.TryGetProperty("atlas", out JsonElement atlas) && atlas.ValueKind == JsonValueKind.Object)
                ApplyAtlas(terrain, atlas);
        }

        private static void ApplyAtlas(Terrain terrain, JsonElement a)
        {
            string texture = TryGetString(a, "texture", "");
            int tilesX = TryGetNumber(a, "tilesX", out double tx) ? (int)tx : 2;
            int tilesY = TryGetNumber(a, "tilesY", out double ty) ? (int)ty : 2;
            double tiling = TryGetNumber(a, "tilingPerFace", out double tp) ? tp : 32.0;

            terrain.ConfigureAtlas(
                string.IsNullOrEmpty(texture) ? null : texture,
                tilesX,
                tilesY,
                tiling,
                null);
        }

        private static void ApplyProfile(TerrainProfile profile, JsonElement p)
        {
            if (TryGetNumber(p, "renderBaseLevel", out double rbl)) profile.RenderBaseLevel = (int)rbl;
            if (TryGetNumber(p, "renderBaseRadius", out double rbr)) profile.RenderBaseRadius = rbr;
            if (TryGetNumber(p, "renderMaxLevel", out double rml)) profile.RenderMaxLevel = (int)rml;
            if (TryGetNumber(p, "renderStreamRadius", out double rsr)) profile.RenderStreamRadius = rsr;
            if (TryGetNumber(p, "renderBudgetMilliseconds", out double rbms)) profile.RenderBudgetMilliseconds = rbms;
            if (TryGetNumber(p, "renderBaseTileResolution", out double rbtr)) profile.RenderBaseTileResolution = (int)rbtr;
            if (TryGetNumber(p, "renderVoxelGridSize", out double rvgs)) profile.RenderVoxelGridSize = (int)rvgs;
            if (TryGetNumber(p, "renderVoxelShellThickness", out double rvst)) profile.RenderVoxelShellThickness = (int)rvst;
            if (TryGetNumber(p, "alwaysResidentLevel", out double arl)) profile.AlwaysResidentLevel = (int)arl;

            if (TryGetNumber(p, "physicsBaseLevel", out double pbl)) profile.PhysicsBaseLevel = (int)pbl;
            if (TryGetNumber(p, "physicsBaseRadius", out double pbr)) profile.PhysicsBaseRadius = pbr;
            if (TryGetNumber(p, "physicsMaxLevel", out double pml)) profile.PhysicsMaxLevel = (int)pml;
            if (TryGetNumber(p, "physicsStreamRadius", out double psr)) profile.PhysicsStreamRadius = psr;
            if (TryGetNumber(p, "physicsBudgetMilliseconds", out double pbms)) profile.PhysicsBudgetMilliseconds = pbms;
            if (TryGetNumber(p, "physicsVoxelGridSize", out double pvgs)) profile.PhysicsVoxelGridSize = (int)pvgs;
            if (TryGetNumber(p, "physicsVoxelShellThickness", out double pvst)) profile.PhysicsVoxelShellThickness = (int)pvst;
        }

        private static bool TryGetNumber(JsonElement obj, string name, out double value)
        {
            value = 0;
            return obj.TryGetProperty(name, out JsonElement el) &&
                   el.ValueKind == JsonValueKind.Number &&
                   el.TryGetDouble(out value);
        }

        private static bool TryGetUInt32(JsonElement obj, string name, out uint value)
        {
            value = 0;
            return obj.TryGetProperty(name, out JsonElement el) &&
                   el.ValueKind == JsonValueKind.Number &&
                   el.TryGetUInt32(out value);
        }

        private static bool TryGetBool(JsonElement obj, string name, out bool value)
        {
            value = false;
            if (!obj.TryGetProperty(name, out JsonElement el))
                return false;

            if (el.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (el.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }

            return false;
        }

        private static string TryGetString(JsonElement obj, string name, string fallback)
        {
            return obj.TryGetProperty(name, out JsonElement el) &&
                   el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? fallback
                : fallback;
        }
    }
}
