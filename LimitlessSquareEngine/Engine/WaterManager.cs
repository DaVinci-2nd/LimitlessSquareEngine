using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LimitlessSquareEngine.Engine
{
    public static class WaterManager
    {
        private static readonly ConcurrentDictionary<string, Water> _waters = new(StringComparer.Ordinal);

        private static string BuildKey(string sceneId, string objectId) => sceneId + "::" + objectId;

        public static Water? GetOrCreate(string sceneId, string objectId)
        {
            if (string.IsNullOrWhiteSpace(sceneId) || string.IsNullOrWhiteSpace(objectId))
                return null;

            string key = BuildKey(sceneId, objectId);
            if (_waters.TryGetValue(key, out Water? existing))
                return existing;

            SceneData? scene = Scene.GetLoadedScenes().FirstOrDefault(s => s.SceneId == sceneId);
            SceneObject? obj = scene?.Objects.FirstOrDefault(o => o.Id == objectId);
            if (obj == null)
            {
                Console.WriteLine($"[!] Water object '{objectId}' not found in scene '{sceneId}'.");
                return null;
            }

            if (!string.Equals(obj.Type, "Water", StringComparison.Ordinal))
            {
                Console.WriteLine($"[!] Object '{objectId}' is not a Water (type='{obj.Type}').");
                return null;
            }

            var water = new Water(sceneId, objectId);

            if (!string.IsNullOrWhiteSpace(obj.Data))
            {
                try
                {
                    ApplyConfig(water, obj.Data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Failed to parse water config for '{objectId}': {ex.Message}");
                }
            }

            _waters[key] = water;
            return water;
        }

        public static Water? TryGet(string sceneId, string objectId)
        {
            return _waters.TryGetValue(BuildKey(sceneId, objectId), out Water? water) ? water : null;
        }

        public static void Remove(string sceneId, string objectId)
        {
            if (_waters.TryRemove(BuildKey(sceneId, objectId), out Water? water))
                water.Streamer.Clear();
        }

        public static void TickAll()
        {
            foreach (Water water in _waters.Values)
                water.Tick();
        }

        public static void ClearAll()
        {
            foreach (Water water in _waters.Values)
                water.Streamer.Clear();
            _waters.Clear();
        }

        public static int Count => _waters.Count;

        public static IReadOnlyCollection<Water> GetAll()
        {
            return _waters.Values.ToArray();
        }

        private static void ApplyConfig(Water water, string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            if (TryGetString(root, "mode", out string mode))
            {
                water.SetMode(mode.ToLowerInvariant() switch
                {
                    "lake" => WaterMode.Lake,
                    "river" => WaterMode.River,
                    _ => WaterMode.Ocean
                });
            }

            if (TryGetString(root, "terrainObjectId", out string terrainObjectId))
                water.SetTerrainObjectId(terrainObjectId);

            if (TryGetNumber(root, "radius", out double radius) && radius > 0.0)
                water.SetRadius(radius);

            if (TryGetNumber(root, "seaLevel", out double seaLevel))
                water.SetSeaLevel(seaLevel);

            if (root.TryGetProperty("profile", out JsonElement profile) && profile.ValueKind == JsonValueKind.Object)
            {
                int baseLevel = TryGetNumber(profile, "baseLevel", out double bl) ? (int)bl : water.Profile.RenderBaseLevel;
                double baseRadius = TryGetNumber(profile, "baseRadius", out double br) ? br : water.Profile.RenderBaseRadius;
                int maxLevel = TryGetNumber(profile, "maxLevel", out double ml) ? (int)ml : water.Profile.RenderMaxLevel;
                int tileResolution = TryGetNumber(profile, "tileResolution", out double tr) ? (int)tr : water.Profile.RenderBaseTileResolution;

                water.ConfigureProfile(baseLevel, baseRadius, maxLevel, tileResolution);
            }

            if (root.TryGetProperty("streaming", out JsonElement streaming) && streaming.ValueKind == JsonValueKind.Object)
            {
                double scale = TryGetNumber(streaming, "radiusScale", out double rs) ? rs : water.StreamRadiusScale;
                double margin = TryGetNumber(streaming, "radiusMargin", out double rm) ? rm : water.StreamRadiusMargin;

                water.ConfigureStreaming(scale, margin);
            }

            if (root.TryGetProperty("material", out JsonElement material) && material.ValueKind == JsonValueKind.Object)
                ApplyMaterialConfig(water, material);
        }

        private static void ApplyMaterialConfig(Water water, JsonElement material)
        {
            if (TryGetNumberArray(material, "color", 4, out double[] color))
                water.SetColor(color[0], color[1], color[2], color[3]);

            if (TryGetNumberArray(material, "specularColor", 3, out double[] specularColor))
                water.SetSpecularColor(specularColor[0], specularColor[1], specularColor[2]);

            if (TryGetNumber(material, "specularIntensity", out double specularIntensity))
                water.SetSpecularIntensity(specularIntensity);

            if (TryGetNumber(material, "specularRange", out double specularRange))
                water.SetSpecularRange(specularRange);

            if (TryGetNumber(material, "ambientStrength", out double ambientStrength))
                water.SetAmbientStrength(ambientStrength);

            if (TryGetNumber(material, "smoothness", out double smoothness))
                water.SetSmoothness(smoothness);

            if (TryGetNumber(material, "reflectionIntensity", out double reflectionIntensity))
                water.SetReflectionIntensity(reflectionIntensity);

            if (TryGetNumber(material, "fresnelPower", out double fresnelPower))
                water.SetFresnelPower(fresnelPower);

            if (TryGetNumber(material, "fresnelStrength", out double fresnelStrength))
                water.SetFresnelStrength(fresnelStrength);

            if (TryGetNumber(material, "receiveShadow", out double receiveShadow))
                water.SetReceiveShadow((int)receiveShadow);

            if (TryGetNumber(material, "castShadow", out double castShadow))
                water.SetCastShadow((int)castShadow);
        }

        private static bool TryGetNumber(JsonElement obj, string name, out double value)
        {
            value = 0;
            return obj.TryGetProperty(name, out JsonElement el) &&
                   el.ValueKind == JsonValueKind.Number &&
                   el.TryGetDouble(out value);
        }

        private static bool TryGetString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (!obj.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.String)
                return false;

            value = el.GetString() ?? "";
            return true;
        }

        private static bool TryGetNumberArray(JsonElement obj, string name, int length, out double[] values)
        {
            values = Array.Empty<double>();
            if (!obj.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
                return false;

            var list = new List<double>();
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number)
                    return false;

                list.Add(item.GetDouble());
            }

            if (list.Count != length)
                return false;

            values = list.ToArray();
            return true;
        }
    }
}
