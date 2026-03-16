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
        public static Double3 operator /(Double3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

        public static Double3 Multiply(Double3 a, Double3 b)
            => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

        public static Double3 Divide(Double3 a, Double3 b)
        {
            const double eps = 1e-12;
            if (Math.Abs(b.X) <= eps || Math.Abs(b.Y) <= eps || Math.Abs(b.Z) <= eps)
                throw new InvalidOperationException("[X] Cannot divide Double3 by zero scale component.");

            return new Double3(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
        }

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

        public DQuaternion Inverse()
        {
            double lenSq = X * X + Y * Y + Z * Z + W * W;
            if (lenSq <= 1e-24) return Identity;
            return new DQuaternion(-X / lenSq, -Y / lenSq, -Z / lenSq, W / lenSq);
        }

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
            var r = this * p * Inverse();
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
        public bool IsMainCamera { get; set; } = false;
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
            _runtimeScenes[sceneId] = BuildRuntimeScene(scene);
            MarkAllDirty(sceneId);
            Console.WriteLine($"[i] Loaded scene: {scene.SceneId} ({scene.Objects.Count} objects)");
            return scene;
        }

        private static SceneRuntimeData BuildRuntimeScene(SceneData scene)
        {
            var runtime = new SceneRuntimeData
            {
                Scene = scene
            };

            foreach (var obj in scene.Objects)
            {
                SceneTransform tr = obj.Transform ?? new SceneTransform();

                runtime.Nodes[obj.Id] = new SceneRuntimeNode
                {
                    Source = obj,
                    LocalPosition = tr.LocalPosition,
                    LocalRotation = DQuaternion.FromEulerDegrees(tr.LocalRotation),
                    LocalScale = tr.LocalScale,
                    World = new SceneWorldState(Double3.Zero, DQuaternion.Identity, Double3.One),
                    Dirty = true
                };
            }

            foreach (var node in runtime.Nodes.Values)
            {
                string? parentId = node.Source.Transform?.ParentId;
                if (string.IsNullOrWhiteSpace(parentId))
                    continue;

                if (!runtime.Nodes.TryGetValue(parentId, out var parent))
                    throw new InvalidDataException($"[X] Parent '{parentId}' not found for object '{node.Source.Id}'.");

                node.Parent = parent;
                parent.Children.Add(node);
            }

            void SetDepth(SceneRuntimeNode node, int depth)
            {
                node.Depth = depth;
                foreach (var child in node.Children)
                    SetDepth(child, depth + 1);
            }

            foreach (var node in runtime.Nodes.Values.Where(n => n.Parent == null))
                SetDepth(node, 0);

            return runtime;
        }

        private static void MarkSubtreeDirty(SceneRuntimeData runtime, SceneRuntimeNode root)
        {
            var stack = new Stack<SceneRuntimeNode>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                node.Dirty = true;
                runtime.DirtyNodes.Add(node.Source.Id);

                if (string.Equals(node.Source.Type, "Camera", StringComparison.Ordinal))
                    runtime.CameraCacheDirty = true;

                foreach (var child in node.Children)
                    stack.Push(child);
            }
        }

        private static void MarkAllDirty(string sceneId)
        {
            if (!_runtimeScenes.TryGetValue(sceneId, out var runtime))
                return;

            foreach (var node in runtime.Nodes.Values)
            {
                node.Dirty = true;
                runtime.DirtyNodes.Add(node.Source.Id);
            }

            runtime.CameraCacheDirty = true;
        }

        private static void RecalculateWorld(SceneRuntimeNode node)
        {
            if (!node.Dirty)
                return;

            if (node.Parent == null)
            {
                node.World = new SceneWorldState(
                    node.LocalPosition,
                    node.LocalRotation,
                    node.LocalScale
                );
            }
            else
            {
                RecalculateWorld(node.Parent);

                SceneWorldState parent = node.Parent.World;

                Double3 scaledLocalPos = Double3.Multiply(node.LocalPosition, parent.Scale);
                Double3 rotatedLocalPos = parent.Rotation.Rotate(scaledLocalPos);

                Double3 worldPos = parent.Position + rotatedLocalPos;
                DQuaternion worldRot = (parent.Rotation * node.LocalRotation).Normalized();
                Double3 worldScale = Double3.Multiply(parent.Scale, node.LocalScale);

                node.World = new SceneWorldState(worldPos, worldRot, worldScale);
            }

            node.Dirty = false;
        }

        private static readonly ConcurrentDictionary<string, SceneRuntimeData> _runtimeScenes = new();
        private static Graphics? _boundGraphics;

        public static void BindGraphics(Graphics graphics)
        {
            _boundGraphics = graphics;
        }

        internal sealed class SceneRuntimeNode
        {
            public SceneObject Source { get; init; } = null!;
            public SceneRuntimeNode? Parent { get; set; }
            public List<SceneRuntimeNode> Children { get; } = new();

            public int Depth { get; set; }

            public Double3 LocalPosition;
            public DQuaternion LocalRotation;
            public Double3 LocalScale;

            public SceneWorldState World;

            public bool Dirty = true;
        }

        internal sealed class SceneRuntimeData
        {
            public SceneData Scene { get; init; } = null!;
            public Dictionary<string, SceneRuntimeNode> Nodes { get; } = new(StringComparer.Ordinal);
            public HashSet<string> DirtyNodes { get; } = new(StringComparer.Ordinal);
            public bool CameraCacheDirty { get; set; } = true;
        }

        private static bool TryGetNode(string? sceneId, string? objectId, out SceneRuntimeData runtime, out SceneRuntimeNode node)
        {
            runtime = null!;
            node = null!;

            sceneId = sceneId?.Trim();
            objectId = objectId?.Trim();

            if (string.IsNullOrWhiteSpace(sceneId))
            {
                Console.WriteLine("[!] Scene transform skipped: sceneId is null or empty.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(objectId))
            {
                Console.WriteLine($"[!] Scene transform skipped: objectId is null or empty. scene='{sceneId}'");
                return false;
            }

            if (!_runtimeScenes.TryGetValue(sceneId, out runtime!))
            {
                Console.WriteLine($"[!] Scene transform skipped: scene '{sceneId}' is not loaded.");
                return false;
            }

            if (!runtime.Nodes.TryGetValue(objectId, out node!))
            {
                Console.WriteLine($"[!] Scene transform skipped: object '{objectId}' not found in scene '{sceneId}'.");
                return false;
            }

            return true;
        }

        private static bool TryValidateScaleNoThrow(Double3 scale, string sceneId, string objectId)
        {
            const double eps = 1e-12;

            if (Math.Abs(scale.X) <= eps || Math.Abs(scale.Y) <= eps || Math.Abs(scale.Z) <= eps)
            {
                Console.WriteLine($"[!] Scene transform skipped: scale contains zero component. scene='{sceneId}', object='{objectId}', scale={scale}");
                return false;
            }

            return true;
        }

        private static void ValidateScale(Double3 scale)
        {
            const double eps = 1e-12;
            if (Math.Abs(scale.X) <= eps || Math.Abs(scale.Y) <= eps || Math.Abs(scale.Z) <= eps)
                throw new InvalidOperationException("[X] Scale component cannot be zero.");
        }

        public static void SetLocalPosition(string sceneId, string objectId, Double3 value)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            node.LocalPosition = value;

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalPosition = value;

            MarkSubtreeDirty(runtime, node);
        }

        public static void SetPosition(string sceneId, string objectId, Double3 value)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            Double3 localValue;

            if (node.Parent == null)
            {
                localValue = value;
            }
            else
            {
                RecalculateWorld(node.Parent);
                SceneWorldState parent = node.Parent.World;

                Double3 deltaWorld = value - parent.Position;
                Double3 unrotated = parent.Rotation.Inverse().Rotate(deltaWorld);

                try
                {
                    localValue = Double3.Divide(unrotated, parent.Scale);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Scene transform skipped: failed to convert world position to local. scene='{sceneId}', object='{objectId}', reason={ex.Message}");
                    return;
                }
            }

            node.LocalPosition = localValue;

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalPosition = localValue;

            MarkSubtreeDirty(runtime, node);
        }

        public static void AlterLocalPosition(string sceneId, string objectId, Double3 delta)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            Double3 deltaInParentSpace = node.LocalRotation.Rotate(delta);
            node.LocalPosition += deltaInParentSpace;

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalPosition = node.LocalPosition;

            MarkSubtreeDirty(runtime, node);
        }

        public static void AlterPosition(string sceneId, string objectId, Double3 delta)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return;

            RecalculateWorld(node);
            SetPosition(sceneId, objectId, node.World.Position + delta);
        }

        public static void SetLocalRotation(string sceneId, string objectId, Double3 eulerDegrees)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            node.LocalRotation = DQuaternion.FromEulerDegrees(eulerDegrees);

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalRotation = eulerDegrees;

            MarkSubtreeDirty(runtime, node);
        }

        public static void SetRotation(string sceneId, string objectId, Double3 eulerDegrees)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            DQuaternion targetWorld = DQuaternion.FromEulerDegrees(eulerDegrees);
            DQuaternion localRotation;

            if (node.Parent == null)
            {
                localRotation = targetWorld;
            }
            else
            {
                RecalculateWorld(node.Parent);
                DQuaternion parentWorld = node.Parent.World.Rotation;
                localRotation = (parentWorld.Inverse() * targetWorld).Normalized();
            }

            node.LocalRotation = localRotation;

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalRotation = eulerDegrees;

            MarkSubtreeDirty(runtime, node);
        }

        public static void AlterLocalRotate(string sceneId, string objectId, Double3 deltaEulerDegrees)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            DQuaternion delta = DQuaternion.FromEulerDegrees(deltaEulerDegrees);
            node.LocalRotation = (node.LocalRotation * delta).Normalized();

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalRotation += deltaEulerDegrees;

            MarkSubtreeDirty(runtime, node);
        }

        public static void AlterRotate(string sceneId, string objectId, Double3 deltaEulerDegrees)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            RecalculateWorld(node);

            DQuaternion delta = DQuaternion.FromEulerDegrees(deltaEulerDegrees);
            DQuaternion newWorld = (delta * node.World.Rotation).Normalized();

            if (node.Parent == null)
            {
                node.LocalRotation = newWorld;
            }
            else
            {
                RecalculateWorld(node.Parent);
                node.LocalRotation = (node.Parent.World.Rotation.Inverse() * newWorld).Normalized();
            }

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalRotation += deltaEulerDegrees;

            MarkSubtreeDirty(runtime, node);
        }

        public static void SetLocalScale(string sceneId, string objectId, Double3 value)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            if (!TryValidateScaleNoThrow(value, sceneId, objectId))
                return;

            node.LocalScale = value;

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalScale = value;

            MarkSubtreeDirty(runtime, node);
        }

        public static void SetScale(string sceneId, string objectId, Double3 value)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            if (!TryValidateScaleNoThrow(value, sceneId, objectId))
                return;

            Double3 localScale;

            if (node.Parent == null)
            {
                localScale = value;
            }
            else
            {
                RecalculateWorld(node.Parent);

                try
                {
                    localScale = Double3.Divide(value, node.Parent.World.Scale);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Scene transform skipped: failed to convert world scale to local. scene='{sceneId}', object='{objectId}', reason={ex.Message}");
                    return;
                }

                if (!TryValidateScaleNoThrow(localScale, sceneId, objectId))
                    return;
            }

            node.LocalScale = localScale;

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalScale = localScale;

            MarkSubtreeDirty(runtime, node);
        }

        public static void AlterLocalScale(string sceneId, string objectId, Double3 delta)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            Double3 next = node.LocalScale + delta;
            if (!TryValidateScaleNoThrow(next, sceneId, objectId))
                return;

            node.LocalScale = next;

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();
            node.Source.Transform.LocalScale = next;

            MarkSubtreeDirty(runtime, node);
        }

        public static void AlterScale(string sceneId, string objectId, Double3 delta)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return;

            RecalculateWorld(node);
            SetScale(sceneId, objectId, node.World.Scale + delta);
        }

        public static void FlushDirtyToRenderer()
        {
            if (_boundGraphics == null)
                return;

            foreach (var pair in _runtimeScenes)
            {
                string sceneId = pair.Key;
                SceneRuntimeData runtime = pair.Value;

                if (runtime.DirtyNodes.Count == 0 && !runtime.CameraCacheDirty)
                    continue;

                var dirtyNodes = runtime.DirtyNodes
                    .Select(id => runtime.Nodes[id])
                    .OrderBy(n => n.Depth)
                    .ToList();

                foreach (var node in dirtyNodes)
                {
                    RecalculateWorld(node);

                    _boundGraphics.UpsertSceneObject(new Graphics.SceneRenderObjectSnapshot
                    {
                        SceneId = sceneId,
                        ObjectId = node.Source.Id,
                        Type = node.Source.Type,
                        Active = node.Source.Active,
                        Visible = node.Source.Visible,
                        Mesh = node.Source.Mesh,
                        Material = node.Source.Material,
                        RenderTag = node.Source.RenderTag,
                        WorldPosition = node.World.Position,
                        WorldRotation = node.World.Rotation,
                        WorldScale = node.World.Scale
                    });
                }

                if (runtime.CameraCacheDirty)
                {
                    RebuildCameraQueue(sceneId);

                    var cameraSnapshots = new List<Graphics.SceneRenderCameraSnapshot>();

                    foreach (var item in GetCameraQueue(sceneId))
                    {
                        if (!runtime.Nodes.TryGetValue(item.ObjectId, out var node))
                            continue;

                        RecalculateWorld(node);

                        cameraSnapshots.Add(new Graphics.SceneRenderCameraSnapshot
                        {
                            SceneId = sceneId,
                            ObjectId = item.ObjectId,
                            Settings = item.Settings,
                            SubmissionOrder = item.SubmissionOrder,
                            World = node.World,
                            Active = node.Source.Active,
                            Visible = node.Source.Visible
                        });
                    }

                    _boundGraphics.ReplaceSceneCameras(sceneId, cameraSnapshots);
                }

                runtime.DirtyNodes.Clear();
                runtime.CameraCacheDirty = false;
            }
        }

        public static void UnloadScene(string sceneId)
        {
            _loadedScenes.TryRemove(sceneId, out _);
            _cameraQueues.TryRemove(sceneId, out _);
            _runtimeScenes.TryRemove(sceneId, out _);
            _boundGraphics?.RemoveSceneCache(sceneId);
        }

        public static void ClearAllScenes()
        {
            foreach (string sceneId in _loadedScenes.Keys.ToArray())
                _boundGraphics?.RemoveSceneCache(sceneId);

            _loadedScenes.Clear();
            _cameraQueues.Clear();
            _runtimeScenes.Clear();
        }

        public static void RemoveScene(string sceneId)
        {
            _loadedScenes.TryRemove(sceneId, out _);
            _cameraQueues.TryRemove(sceneId, out _);
            _runtimeScenes.TryRemove(sceneId, out _);
            _boundGraphics?.RemoveSceneCache(sceneId);
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
    {
        Console.WriteLine($"[!] Camera queue rebuild skipped: scene '{sceneId}' is not loaded.");
        return;
    }

    var queue = new List<SceneCameraQueueItem>();
    int order = 0;

    foreach (var obj in scene.Objects)
    {
        if (!obj.Active)
            continue;

        if (!string.Equals(obj.Type, "Camera", StringComparison.Ordinal))
            continue;

        try
        {
            CameraRenderSettings settings = ParseCameraSettings(obj.Data, obj.Id);

            queue.Add(new SceneCameraQueueItem
            {
                SceneId = scene.SceneId,
                ObjectId = obj.Id,
                Settings = settings,
                SubmissionOrder = order++
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Camera '{obj.Id}' skipped while rebuilding queue: {ex.Message}");
        }
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

                    case "isMainCamera":
                        if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.isMainCamera must be true or false.");
                        settings.IsMainCamera = prop.Value.GetBoolean();
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
