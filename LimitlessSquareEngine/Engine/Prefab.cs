using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace LimitlessSquareEngine.Engine
{
    public class PrefabData
    {
        public string AssetType { get; set; } = "Prefab";
        public string PrefabId { get; set; } = "";
        public string Name { get; set; } = "";
        public string RootObjectId { get; set; } = "";
        public List<SceneObject> Objects { get; set; } = new();
    }

    internal static class Prefab
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new Scene.Double3JsonConverter() }
        };

        internal static bool TryValidatePrefabFile(string filePath, out string prefabId, out string reason)
        {
            prefabId = string.Empty;
            reason = string.Empty;

            string json = File.ReadAllText(filePath);
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                reason = "Root must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("assetType", out JsonElement assetTypeElement) ||
                assetTypeElement.ValueKind != JsonValueKind.String)
            {
                reason = "Missing or invalid 'assetType'.";
                return false;
            }

            string assetType = assetTypeElement.GetString()?.Trim() ?? string.Empty;
            if (!string.Equals(assetType, "Prefab", StringComparison.Ordinal))
            {
                reason = "'assetType' must be 'Prefab'.";
                return false;
            }

            if (!root.TryGetProperty("prefabId", out JsonElement prefabIdElement) ||
                prefabIdElement.ValueKind != JsonValueKind.String)
            {
                reason = "Missing or invalid 'prefabId'.";
                return false;
            }

            prefabId = prefabIdElement.GetString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prefabId))
            {
                reason = "'prefabId' cannot be empty.";
                return false;
            }

            if (!root.TryGetProperty("objects", out JsonElement objectsElement) ||
                objectsElement.ValueKind != JsonValueKind.Array)
            {
                reason = "Missing or invalid 'objects' array.";
                return false;
            }

            HashSet<string> objectIds = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string?> parentMap = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (JsonElement obj in objectsElement.EnumerateArray())
            {
                if (obj.ValueKind != JsonValueKind.Object)
                {
                    reason = "Every item in 'objects' must be a JSON object.";
                    return false;
                }

                if (!obj.TryGetProperty("id", out JsonElement objectIdElement) ||
                    objectIdElement.ValueKind != JsonValueKind.String)
                {
                    reason = "Every object must contain a string 'id'.";
                    return false;
                }

                string objectId = objectIdElement.GetString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(objectId))
                {
                    reason = "Object 'id' cannot be empty.";
                    return false;
                }

                if (!objectIds.Add(objectId))
                {
                    reason = $"Duplicate object id '{objectId}' in prefab '{prefabId}'.";
                    return false;
                }

                string? parentId = null;

                if (obj.TryGetProperty("transform", out JsonElement transformElement))
                {
                    if (transformElement.ValueKind != JsonValueKind.Object)
                    {
                        reason = $"Object '{objectId}' has invalid 'transform'.";
                        return false;
                    }

                    if (transformElement.TryGetProperty("parentId", out JsonElement parentIdElement))
                    {
                        if (parentIdElement.ValueKind == JsonValueKind.Null)
                        {
                            parentId = null;
                        }
                        else if (parentIdElement.ValueKind == JsonValueKind.String)
                        {
                            parentId = parentIdElement.GetString();
                            if (string.IsNullOrWhiteSpace(parentId))
                                parentId = null;
                        }
                        else
                        {
                            reason = $"Object '{objectId}' has invalid 'transform.parentId'.";
                            return false;
                        }
                    }
                }

                if (obj.TryGetProperty("physics", out JsonElement physicsElement))
                {
                    if (physicsElement.ValueKind != JsonValueKind.Object &&
                        physicsElement.ValueKind != JsonValueKind.Null)
                    {
                        reason = $"Object '{objectId}' physics must be object or null.";
                        return false;
                    }
                }

                if (obj.TryGetProperty("scripts", out JsonElement scriptsElement))
                {
                    if (scriptsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement scriptItem in scriptsElement.EnumerateArray())
                        {
                            if (scriptItem.ValueKind != JsonValueKind.Object)
                            {
                                reason = $"Object '{objectId}' scripts item must be a JSON object.";
                                return false;
                            }

                            if (!scriptItem.TryGetProperty("script", out JsonElement scriptNameElement) ||
                                scriptNameElement.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(scriptNameElement.GetString()))
                            {
                                reason = $"Object '{objectId}' scripts item must contain a non-empty 'script'.";
                                return false;
                            }
                        }
                    }
                    else if (scriptsElement.ValueKind != JsonValueKind.Null)
                    {
                        reason = $"Object '{objectId}' scripts must be array or null.";
                        return false;
                    }
                }

                parentMap[objectId] = parentId;
            }

            foreach (var pair in parentMap)
            {
                string objectId = pair.Key;
                string? parentId = pair.Value;

                if (!string.IsNullOrWhiteSpace(parentId) && !objectIds.Contains(parentId))
                {
                    reason = $"Object '{objectId}' references missing parent '{parentId}'.";
                    return false;
                }
            }

            foreach (string objectId in objectIds)
            {
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                string? current = objectId;

                while (!string.IsNullOrWhiteSpace(current))
                {
                    if (!visited.Add(current))
                    {
                        reason = $"Circular parent relationship detected at object '{objectId}'.";
                        return false;
                    }

                    if (!parentMap.TryGetValue(current, out string? nextParent))
                        break;

                    current = nextParent;
                }
            }

            return true;
        }

        internal static PrefabData? ParsePrefabFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                string json = File.ReadAllText(filePath);
                PrefabData? prefab = JsonSerializer.Deserialize<PrefabData>(json, _jsonOptions);
                if (prefab == null)
                    return null;

                if (string.IsNullOrWhiteSpace(prefab.PrefabId))
                    return null;

                return prefab;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static List<SceneObject> CollectPrefabObjects(IEnumerable<SceneObject> sourceObjects)
        {
            string json = JsonSerializer.Serialize(
                sourceObjects.Where(o => !string.Equals(o.Type, "Bone", StringComparison.Ordinal)).ToList(),
                _jsonOptions);

            List<SceneObject>? cloned = JsonSerializer.Deserialize<List<SceneObject>>(json, _jsonOptions);

            if (cloned == null || cloned.Count == 0)
                return cloned ?? new List<SceneObject>();

            Dictionary<string, string?> parentRedirect = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (SceneObject removedBone in sourceObjects.Where(
                o => string.Equals(o.Type, "Bone", StringComparison.Ordinal)))
            {
                parentRedirect[removedBone.Id] = removedBone.Transform?.ParentId;
            }

            string? ResolveParent(string? parentId)
            {
                while (parentId != null && parentRedirect.TryGetValue(parentId, out string? redirect))
                    parentId = redirect;

                return parentId;
            }

            foreach (SceneObject obj in cloned)
            {
                if (obj.Transform == null)
                    continue;

                obj.Transform.ParentId = ResolveParent(obj.Transform.ParentId);
            }

            return cloned;
        }

        internal static PrefabData? ExtractPrefabData(SceneData scene, string rootObjectId, string prefabId, string name)
        {
            if (scene == null || string.IsNullOrWhiteSpace(rootObjectId))
                return null;

            SceneObject? root = scene.Objects.FirstOrDefault(
                o => string.Equals(o.Id, rootObjectId, StringComparison.Ordinal));

            if (root == null)
                return null;

            HashSet<string> collectedIds = new HashSet<string>(StringComparer.Ordinal);
            List<SceneObject> collected = new List<SceneObject>();
            Queue<string> queue = new Queue<string>();

            collectedIds.Add(root.Id);
            collected.Add(root);
            queue.Enqueue(root.Id);

            while (queue.Count > 0)
            {
                string currentId = queue.Dequeue();

                foreach (SceneObject child in scene.Objects.Where(
                    o => string.Equals(o.Transform?.ParentId, currentId, StringComparison.Ordinal)))
                {
                    if (!collectedIds.Add(child.Id))
                        continue;

                    collected.Add(child);
                    queue.Enqueue(child.Id);
                }
            }

            PrefabData prefab = new PrefabData
            {
                PrefabId = prefabId,
                Name = name,
                RootObjectId = root.Id,
                Objects = CollectPrefabObjects(collected)
            };

            return prefab;
        }

        internal static string? Instantiate(PrefabData prefab, SceneData targetScene, string? parentId, string? nameOverride, out List<SceneObject> instances)
        {
            instances = new List<SceneObject>();

            if (prefab == null || targetScene == null || prefab.Objects.Count == 0)
                return null;

            List<SceneObject> prefabObjects = CollectPrefabObjects(prefab.Objects);

            if (prefabObjects.Count == 0)
                return null;

            HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (SceneObject existing in targetScene.Objects)
            {
                if (!string.IsNullOrWhiteSpace(existing.Id))
                    usedIds.Add(existing.Id);
            }

            Dictionary<string, string> idMap = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (SceneObject source in prefabObjects)
            {
                int suffix = 1;
                string candidate = source.Id;

                while (usedIds.Contains(candidate))
                {
                    candidate = source.Id + "_prefab" + suffix.ToString(CultureInfo.InvariantCulture);
                    suffix++;
                }

                usedIds.Add(candidate);
                idMap[source.Id] = candidate;
            }

            string? resolvedRootOldId = prefabObjects
                .FirstOrDefault(o => string.Equals(o.Id, prefab.RootObjectId, StringComparison.Ordinal))?.Id;

            if (resolvedRootOldId == null)
            {
                resolvedRootOldId = prefabObjects
                    .FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Transform?.ParentId))?.Id;
            }

            string? resolvedRootNewId =
                resolvedRootOldId != null && idMap.TryGetValue(resolvedRootOldId, out string? mappedRootId)
                    ? mappedRootId
                    : null;

            string? rootId = null;

            foreach (SceneObject source in prefabObjects)
            {
                source.Id = idMap[source.Id];

                if (source.Transform == null)
                    source.Transform = new SceneTransform();

                if (resolvedRootNewId != null &&
                    string.Equals(source.Id, resolvedRootNewId, StringComparison.Ordinal))
                {
                    source.Transform.ParentId = string.IsNullOrWhiteSpace(parentId) ? null : parentId;

                    if (!string.IsNullOrWhiteSpace(nameOverride))
                        source.Name = nameOverride;

                    rootId = source.Id;
                }
                else
                {
                    string? oldParentId = source.Transform.ParentId;

                    source.Transform.ParentId =
                        oldParentId != null && idMap.TryGetValue(oldParentId, out string? newParentId)
                            ? newParentId
                            : (string.IsNullOrWhiteSpace(parentId) ? null : parentId);
                }

                if (source.Physics != null && source.Physics.IgnoreColliders != null)
                {
                    List<string> remappedIgnoreColliders = new List<string>();

                    foreach (string ignoreId in source.Physics.IgnoreColliders)
                    {
                        if (idMap.TryGetValue(ignoreId, out string? newIgnoreId))
                        {
                            remappedIgnoreColliders.Add(newIgnoreId);
                        }
                        else
                        {
                            Console.WriteLine($"[!] Prefab ignore collider '{ignoreId}' dropped while instantiating object '{source.Id}'.");
                        }
                    }

                    source.Physics.IgnoreColliders = remappedIgnoreColliders;
                }
            }

            targetScene.Objects.AddRange(prefabObjects);
            Scene.InternSceneObjectStrings(prefabObjects);
            instances = prefabObjects;

            return rootId;
        }

        internal static bool TryGetPrefab(string prefabKey, out PrefabData prefab)
        {
            prefab = null!;

            string key = prefabKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (!Program.EnsurePrefabRegistered(key, out string filePath))
                return false;

            PrefabData? loaded = ParsePrefabFile(filePath);
            if (loaded == null)
                return false;

            prefab = loaded;

            return true;
        }
    }
}
