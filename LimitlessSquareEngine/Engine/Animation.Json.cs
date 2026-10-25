using System;
using System.IO;
using System.Text.Json;

namespace LimitlessSquareEngine.Engine
{
    internal static class AnimationJson
    {
        private static readonly JsonSerializerOptions _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public static bool TryLoadClip(string filePath, out AnimationClipData? data, out string reason)
        {
            data = null;
            reason = string.Empty;

            try
            {
                string json = File.ReadAllText(filePath);
                using JsonDocument doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    reason = "Root is not a JSON object.";
                    return false;
                }

                if (!TryReadAssetType(doc.RootElement, out string assetType) ||
                    !string.Equals(assetType, "AnimationClip", StringComparison.Ordinal))
                {
                    reason = "'assetType' must be 'AnimationClip'.";
                    return false;
                }

                data = JsonSerializer.Deserialize<AnimationClipData>(json, _options);

                if (data == null)
                {
                    reason = "Deserialized result is null.";
                    return false;
                }

                if (data.Channels.Count == 0)
                {
                    reason = "Clip has no channels.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                data = null;
                return false;
            }
        }

        public static bool TryLoadController(string filePath, out AnimatorControllerData? data, out string reason)
        {
            data = null;
            reason = string.Empty;

            try
            {
                string json = File.ReadAllText(filePath);
                using JsonDocument doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    reason = "Root is not a JSON object.";
                    return false;
                }

                if (!TryReadAssetType(doc.RootElement, out string assetType) ||
                    !string.Equals(assetType, "AnimatorController", StringComparison.Ordinal))
                {
                    reason = "'assetType' must be 'AnimatorController'.";
                    return false;
                }

                data = JsonSerializer.Deserialize<AnimatorControllerData>(json, _options);

                if (data == null)
                {
                    reason = "Deserialized result is null.";
                    return false;
                }

                if (data.Layers.Count == 0)
                {
                    reason = "Controller has no layers.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                data = null;
                return false;
            }
        }

        static bool TryReadAssetType(JsonElement root, out string assetType)
        {
            assetType = string.Empty;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "assetType", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (property.Value.ValueKind != JsonValueKind.String)
                    return false;

                assetType = property.Value.GetString()?.Trim() ?? string.Empty;
                return true;
            }

            return false;
        }
    }
}
