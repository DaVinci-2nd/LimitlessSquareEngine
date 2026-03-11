using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Numerics;

namespace LimitlessSquareEngine
{
    public struct Double3
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public static Double3 Zero => new(0.0, 0.0, 0.0);
        public static Double3 One => new(1.0, 1.0, 1.0);

        public Double3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Double3 operator *(Double3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

        public static Double3 Multiply(Double3 a, Double3 b)
            => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

        public override string ToString() => $"({X}, {Y}, {Z})";
    }

    internal readonly struct DQuaternion
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;
        public readonly double W;

        public static DQuaternion Identity => new(0.0, 0.0, 0.0, 1.0);

        public DQuaternion(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public DQuaternion Conjugate() => new(-X, -Y, -Z, W);

        public DQuaternion Normalized()
        {
            double len = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            if (len <= 0.0) return Identity;
            return new DQuaternion(X / len, Y / len, Z / len, W / len);
        }

        public static DQuaternion operator *(DQuaternion a, DQuaternion b)
        {
            return new DQuaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z
            );
        }

        public static DQuaternion CreateAxisAngle(Double3 axis, double radians)
        {
            double half = radians * 0.5;
            double s = Math.Sin(half);
            return new DQuaternion(axis.X * s, axis.Y * s, axis.Z * s, Math.Cos(half)).Normalized();
        }

        // 场景欧拉角 X -> Y -> Z
        public static DQuaternion FromEulerDegrees(Double3 eulerDegrees)
        {
            double rx = eulerDegrees.X * Math.PI / 180.0;
            double ry = eulerDegrees.Y * Math.PI / 180.0;
            double rz = eulerDegrees.Z * Math.PI / 180.0;

            var qx = CreateAxisAngle(new Double3(1.0, 0.0, 0.0), rx);
            var qy = CreateAxisAngle(new Double3(0.0, 1.0, 0.0), ry);
            var qz = CreateAxisAngle(new Double3(0.0, 0.0, 1.0), rz);

            return (qx * qy * qz).Normalized();
        }

        public Double3 Rotate(Double3 v)
        {
            var p = new DQuaternion(v.X, v.Y, v.Z, 0.0);
            var r = this * p * Conjugate();
            return new Double3(r.X, r.Y, r.Z);
        }

        public Quaternion ToSingle()
        {
            return Quaternion.Normalize(new Quaternion((float)X, (float)Y, (float)Z, (float)W));
        }
    }

    internal readonly struct SceneWorldState
    {
        public readonly Double3 Position;
        public readonly DQuaternion Rotation;
        public readonly Double3 Scale;

        public SceneWorldState(Double3 position, DQuaternion rotation, Double3 scale)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }
    }

    /// <summary>
    /// 相机参数
    /// </summary>
    internal sealed class CameraRenderSettings
    {
        public int RenderMode { get; set; } = 0;
        public double FovOrSize { get; set; } = 90.0;
        public double NearClip { get; set; } = 0.01;
        public double FarClip { get; set; } = 1000.0;
        public int ProjectionType { get; set; } = 0;
    }

    internal sealed class SceneCameraQueueItem
    {
        public string SceneId { get; init; } = "";
        public string ObjectId { get; init; } = "";
        public CameraRenderSettings Settings { get; init; } = new();
        public int SubmissionOrder { get; init; }
    }

    public class SceneTransform
    {
        public string? ParentId { get; set; }
        public Double3 LocalPosition { get; set; } = Double3.Zero;
        public Double3 LocalRotation { get; set; } = Double3.Zero;
        public Double3 LocalScale { get; set; } = Double3.One;
    }

    public class SceneObject
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public bool Active { get; set; } = true;
        public SceneTransform Transform { get; set; } = new();
        public string Type { get; set; } = "Object";
        public string? Controller { get; set; }
        public string Data { get; set; } = "";
        public string? Mesh { get; set; }
        public bool Visible { get; set; } = true;
        public string RenderTag { get; set; } = "";
        public string? Material { get; set; }
    }

    public class SceneData
    {
        public string SceneId { get; set; } = "";
        public List<SceneObject> Objects { get; set; } = new();
    }

    internal class Scene
    {
        private static readonly ConcurrentDictionary<string, SceneData> _loadedScenes = new();
        private static readonly ConcurrentDictionary<string, List<SceneCameraQueueItem>> _cameraQueues = new();

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new Double3JsonConverter() }
        };

        public static SceneData LoadScene(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("[X] Scene ID cannot be null or empty.", nameof(sceneId));

            if (_loadedScenes.TryGetValue(sceneId, out SceneData? cached))
                return cached;

            if (!Program._sceneFileRegistry.TryGetValue(sceneId, out string? filePath))
                throw new FileNotFoundException($"[X] Scene ID '{sceneId}' not registered in scene registry.");

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"[X] Scene file not found at registered path: {filePath}");

            string json = File.ReadAllText(filePath);
            SceneData? scene = JsonSerializer.Deserialize<SceneData>(json, _jsonOptions);
            if (scene == null)
                throw new InvalidDataException($"[X] Failed to deserialize scene from {filePath}");

            if (scene.SceneId != sceneId)
                throw new InvalidDataException($"[X] Scene ID mismatch: file contains '{scene.SceneId}', expected '{sceneId}'.");

            _loadedScenes[sceneId] = scene;
            RebuildCameraQueue(sceneId);

            Console.WriteLine($"[i] Loaded scene: {scene.SceneId} ({scene.Objects.Count} objects)");
            return scene;
        }

        public static void UnloadScene(string sceneId)
        {
            _loadedScenes.TryRemove(sceneId, out _);
            _cameraQueues.TryRemove(sceneId, out _);
        }

        public static void ClearAllScenes()
        {
            _loadedScenes.Clear();
            _cameraQueues.Clear();
        }

        public static void RemoveScene(string sceneId)
        {
            _loadedScenes.TryRemove(sceneId, out _);
            _cameraQueues.TryRemove(sceneId, out _);
        }

        public static IReadOnlyCollection<SceneData> GetLoadedScenes()
        {
            return _loadedScenes.Values.ToArray();
        }

        public static IReadOnlyList<SceneCameraQueueItem> GetCameraQueue(string sceneId)
        {
            if (_cameraQueues.TryGetValue(sceneId, out var list))
                return list;
            return Array.Empty<SceneCameraQueueItem>();
        }

        public static void RebuildCameraQueue(string sceneId)
        {
            if (!_loadedScenes.TryGetValue(sceneId, out var scene))
                throw new KeyNotFoundException($"[X] Scene '{sceneId}' is not loaded.");

            var queue = new List<SceneCameraQueueItem>();
            int order = 0;

            foreach (var obj in scene.Objects)
            {
                if (!obj.Active)
                    continue;

                if (!string.Equals(obj.Type, "Camera", StringComparison.Ordinal))
                    continue;

                CameraRenderSettings settings = ParseCameraSettings(obj.Data, obj.Id);

                queue.Add(new SceneCameraQueueItem
                {
                    SceneId = scene.SceneId,
                    ObjectId = obj.Id,
                    Settings = settings,
                    SubmissionOrder = order++
                });
            }

            _cameraQueues[sceneId] = queue;
            Console.WriteLine($"[i] Camera queue rebuilt for scene '{sceneId}', count={queue.Count}");
        }

        public static Dictionary<string, SceneWorldState> BuildWorldStates(SceneData scene)
        {
            var objectMap = scene.Objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
            var cache = new Dictionary<string, SceneWorldState>(StringComparer.Ordinal);

            SceneWorldState Resolve(string objectId)
            {
                if (cache.TryGetValue(objectId, out var cached))
                    return cached;

                SceneObject obj = objectMap[objectId];
                SceneTransform tr = obj.Transform ?? new SceneTransform();

                Double3 localPos = tr.LocalPosition;
                Double3 localScale = tr.LocalScale;
                DQuaternion localRot = DQuaternion.FromEulerDegrees(tr.LocalRotation);

                SceneWorldState result;

                if (string.IsNullOrWhiteSpace(tr.ParentId))
                {
                    result = new SceneWorldState(localPos, localRot, localScale);
                }
                else
                {
                    SceneWorldState parent = Resolve(tr.ParentId);

                    Double3 scaledLocalPos = Double3.Multiply(localPos, parent.Scale);
                    Double3 rotatedLocalPos = parent.Rotation.Rotate(scaledLocalPos);

                    Double3 worldPos = parent.Position + rotatedLocalPos;
                    DQuaternion worldRot = (parent.Rotation * localRot).Normalized();
                    Double3 worldScale = Double3.Multiply(parent.Scale, localScale);

                    result = new SceneWorldState(worldPos, worldRot, worldScale);
                }

                cache[objectId] = result;
                return result;
            }

            foreach (var obj in scene.Objects)
                Resolve(obj.Id);

            return cache;
        }

        internal static bool TryValidateCameraDataString(string rawData, string objectId, out string reason)
        {
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(rawData))
                return true;

            try
            {
                _ = ParseCameraSettings(rawData, objectId);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static CameraRenderSettings ParseCameraSettings(string? rawData, string objectId)
        {
            var settings = new CameraRenderSettings();

            if (string.IsNullOrWhiteSpace(rawData))
                return settings;

            using JsonDocument doc = JsonDocument.Parse(rawData);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] Camera '{objectId}' data must be a JSON object string.");

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "renderMode":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out int renderMode) || (renderMode != 0 && renderMode != 1))
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.renderMode must be 0 or 1.");
                        settings.RenderMode = renderMode;
                        break;

                    case "fovOrSize":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double fovOrSize) || fovOrSize <= 0.0)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.fovOrSize must be > 0.");
                        settings.FovOrSize = fovOrSize;
                        break;

                    case "nearClip":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double nearClip) || nearClip <= 0.0)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.nearClip must be > 0.");
                        settings.NearClip = nearClip;
                        break;

                    case "farClip":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double farClip) || farClip <= 0.0)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.farClip must be > 0.");
                        settings.FarClip = farClip;
                        break;

                    case "projectionType":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out int projectionType) || (projectionType != 0 && projectionType != 1))
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.projectionType must be 0 or 1.");
                        settings.ProjectionType = projectionType;
                        break;

                    default:
                        throw new InvalidDataException($"[X] Camera '{objectId}' data contains unknown or wrong-cased property '{prop.Name}'.");
                }
            }

            if (settings.FarClip <= settings.NearClip)
                throw new InvalidDataException($"[X] Camera '{objectId}' data.farClip must be greater than data.nearClip.");

            return settings;
        }

        private class Double3JsonConverter : JsonConverter<Double3>
        {
            public override Double3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException();

                double x = 0, y = 0, z = 0;

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        break;

                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string? prop = reader.GetString();
                        reader.Read();

                        switch (prop)
                        {
                            case "x":
                                x = reader.GetDouble();
                                break;
                            case "y":
                                y = reader.GetDouble();
                                break;
                            case "z":
                                z = reader.GetDouble();
                                break;
                            default:
                                throw new JsonException($"Unknown or wrong-cased Double3 property '{prop}'.");
                        }
                    }
                }

                return new Double3(x, y, z);
            }

            public override void Write(Utf8JsonWriter writer, Double3 value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                writer.WriteNumber("x", value.X);
                writer.WriteNumber("y", value.Y);
                writer.WriteNumber("z", value.Z);
                writer.WriteEndObject();
            }
        }
    }
}
