using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Numerics;

namespace LimitlessSquareEngine.Engine
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

        public Double3 ToEulerDegrees()
        {
            double ysqr = Y * Y;

            double t0 = +2.0 * (W * X + Y * Z);
            double t1 = +1.0 - 2.0 * (X * X + ysqr);
            double rollX = Math.Atan2(t0, t1);

            double t2 = +2.0 * (W * Y - Z * X);
            t2 = t2 > 1.0 ? 1.0 : t2;
            t2 = t2 < -1.0 ? -1.0 : t2;
            double pitchY = Math.Asin(t2);

            double t3 = +2.0 * (W * Z + X * Y);
            double t4 = +1.0 - 2.0 * (ysqr + Z * Z);
            double yawZ = Math.Atan2(t3, t4);

            const double radToDeg = 180.0 / Math.PI;
            return new Double3(rollX * radToDeg, pitchY * radToDeg, yawZ * radToDeg);
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

    internal sealed class CameraRenderPlaneOffsetSettings
    {
        public double X { get; set; } = 0.0;
        public double Y { get; set; } = 0.0;
    }

    internal sealed class CameraScalarPostProcessSettings
    {
        public bool Enabled { get; set; } = false;
        public double Value { get; set; } = 1.0;
    }

    internal sealed class CameraHuePostProcessSettings
    {
        public bool Enabled { get; set; } = false;
        public double Degrees { get; set; } = 0.0;
    }

    internal sealed class CameraTemperaturePostProcessSettings
    {
        public bool Enabled { get; set; } = false;
        public double Value { get; set; } = 0.0;
    }

    internal sealed class CameraBloomPostProcessSettings
    {
        public bool Enabled { get; set; } = false;
        public double Threshold { get; set; } = 1.0;
        public double SoftKnee { get; set; } = 0.5;
        public double Intensity { get; set; } = 0.5;
        public int Iterations { get; set; } = 16;
        public int Downsample { get; set; } = 2;
        public double Range { get; set; } = 100.0;
    }

    internal sealed class CameraSmaaPostProcessSettings
    {
        public bool Enabled { get; set; } = true;
    }

    internal sealed class CameraPostProcessSettings
    {
        public bool Enabled { get; set; } = false;

        public CameraScalarPostProcessSettings Brightness { get; set; } = new();
        public CameraScalarPostProcessSettings Contrast { get; set; } = new();
        public CameraScalarPostProcessSettings Saturation { get; set; } = new();
        public CameraHuePostProcessSettings Hue { get; set; } = new();
        public CameraTemperaturePostProcessSettings Temperature { get; set; } = new();
        public CameraBloomPostProcessSettings Bloom { get; set; } = new();
        public CameraSmaaPostProcessSettings Smaa { get; set; } = new();
    }

    /// <summary>
    /// 相机参数
    /// </summary>
    internal sealed class CameraRenderSettings
    {
        public int RenderMode { get; set; } = 0;
        public double FovOrSize { get; set; } = 75.0;
        public CameraRenderPlaneOffsetSettings RenderPlaneOffset { get; set; } = new();
        public double NearClip { get; set; } = 0.01;
        public double FarClip { get; set; } = 1000.0;
        public int ProjectionType { get; set; } = 0;
        public bool IsMainCamera { get; set; } = false;
        public CameraPostProcessSettings PostProcess { get; set; } = new();
    }

    /// <summary>
    /// 灯光参数
    /// </summary>
    internal sealed class LightRenderSettings
    {
        // 0 = Point
        public int LightMode { get; set; } = 0;

        public Double3 Color { get; set; } = new Double3(1.0, 1.0, 1.0);
        public double Intensity { get; set; } = 1.0;
        public double Range { get; set; } = 1.0;
        public double AttenuationCurve { get; set; } = 0.5;

        public bool CastShadow { get; set; } = false;
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

    public class PhysicsBody
    {
        public bool Enabled { get; set; } = true;

        // Static / Dynamic / Kinematic
        public string MotionType { get; set; } = "Static";

        // Box / Sphere / Capsule / Mesh
        public string ShapeType { get; set; } = "Box";

        // Box
        public Double3 Size { get; set; } = Double3.One;

        // Sphere / Capsule
        public double Radius { get; set; } = 0.5;

        // Capsule
        public double Length { get; set; } = 1.0;

        // Dynamic
        public double Mass { get; set; } = 1.0;

        public double Friction { get; set; } = 0.2;
        public double Restitution { get; set; } = 0.0;

        public bool UseGravity { get; set; } = true;
        public bool EnableSpeculativeContacts { get; set; } = false;

        public double LinearDamping { get; set; } = 0.002;
        public double AngularDamping { get; set; } = 0.005;
    }

    public sealed class PhysicsRaycastHit
    {
        public string SceneId { get; set; } = "";
        public string ObjectId { get; set; } = "";
        public Double3 Point { get; set; } = Double3.Zero;
        public Double3 Normal { get; set; } = Double3.Zero;
        public double Distance { get; set; }
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
        public PhysicsBody? Physics { get; set; }
        public List<string>? Materials { get; set; }
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

        private const int StaticRenderStillFrameThreshold = 1;

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

            if (!Program.EnsureSceneRegistered(sceneId, out string filePath))
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

            SceneRuntimeData runtime = BuildRuntimeScene(scene);
            _runtimeScenes[sceneId] = runtime;

            Physics.RegisterScene(sceneId, runtime);

            MarkAllDirty(sceneId);
            Console.WriteLine($"[i] Loaded scene: {scene.SceneId} ({scene.Objects.Count} objects)");
            return scene;
        }

        internal static int GetObjectCount(string sceneId)
        {
            if (!_loadedScenes.TryGetValue(sceneId, out SceneData? scene))
                return 0;
            return scene.Objects.Count;
        }

        internal static string[] GetObjectIds(string sceneId)
        {
            if (!_loadedScenes.TryGetValue(sceneId, out SceneData? scene))
                return Array.Empty<string>();
            return scene.Objects.Select(o => o.Id).ToArray();
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
                node.StillFramesSinceLastTransform = 0;
                node.TransformMutationVersion++;
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
        private static Audio? _boundAudio;

        public static void BindGraphics(Graphics graphics)
        {
            _boundGraphics = graphics;
            Physics.SetMeshColliderTriangleResolver(graphics.TryGetMeshColliderTriangles);
        }

        public static void BindAudio(Audio audio)
        {
            _boundAudio = audio;
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
            public int StillFramesSinceLastTransform = 0;
            public int TransformMutationVersion = 0;
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

        private static bool ShouldRouteTransformToPhysics(string sceneId, SceneRuntimeNode node)
        {
            PhysicsBody? physics = node.Source.Physics;
            return physics != null &&
                   physics.Enabled &&
                   Physics.HasBody(sceneId, node.Source.Id);
        }

        private static Double3 LocalPositionToWorld(SceneRuntimeNode node, Double3 localPosition)
        {
            if (node.Parent == null)
                return localPosition;

            RecalculateWorld(node.Parent);
            SceneWorldState parent = node.Parent.World;

            Double3 scaledLocalPos = Double3.Multiply(localPosition, parent.Scale);
            Double3 rotatedLocalPos = parent.Rotation.Rotate(scaledLocalPos);

            return parent.Position + rotatedLocalPos;
        }

        private static DQuaternion LocalRotationToWorld(SceneRuntimeNode node, DQuaternion localRotation)
        {
            if (node.Parent == null)
                return localRotation;

            RecalculateWorld(node.Parent);
            return (node.Parent.World.Rotation * localRotation).Normalized();
        }

        public static void SetLocalPosition(string sceneId, string objectId, Double3 value)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            if (ShouldRouteTransformToPhysics(sceneId, node))
            {
                Double3 worldValue = LocalPositionToWorld(node, value);
                Physics.TrySetBodyWorldPosition(sceneId, objectId, worldValue);
                return;
            }

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

            if (ShouldRouteTransformToPhysics(sceneId, node))
            {
                Physics.TrySetBodyWorldPosition(sceneId, objectId, value);
                return;
            }

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

            if (ShouldRouteTransformToPhysics(sceneId, node))
            {
                Double3 deltaInParentSpace = node.LocalRotation.Rotate(delta);
                SetLocalPosition(sceneId, objectId, node.LocalPosition + deltaInParentSpace);
                return;
            }

            Double3 deltaInParentSpaceOriginal = node.LocalRotation.Rotate(delta);
            node.LocalPosition += deltaInParentSpaceOriginal;

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

            if (ShouldRouteTransformToPhysics(sceneId, node))
            {
                Physics.TrySetBodyWorldPosition(sceneId, objectId, node.World.Position + delta);
                return;
            }

            SetPosition(sceneId, objectId, node.World.Position + delta);
        }

        public static void SetLocalRotation(string sceneId, string objectId, Double3 eulerDegrees)
        {
            if (!TryGetNode(sceneId, objectId, out var runtime, out var node))
                return;

            if (ShouldRouteTransformToPhysics(sceneId, node))
            {
                DQuaternion localRotation = DQuaternion.FromEulerDegrees(eulerDegrees);
                DQuaternion worldRotation = LocalRotationToWorld(node, localRotation);
                Physics.TrySetBodyWorldRotation(sceneId, objectId, worldRotation);
                return;
            }

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

            if (ShouldRouteTransformToPhysics(sceneId, node))
            {
                DQuaternion targetWorld = DQuaternion.FromEulerDegrees(eulerDegrees);
                Physics.TrySetBodyWorldRotation(sceneId, objectId, targetWorld);
                return;
            }

            DQuaternion targetWorldOriginal = DQuaternion.FromEulerDegrees(eulerDegrees);
            DQuaternion localRotation;

            if (node.Parent == null)
            {
                localRotation = targetWorldOriginal;
            }
            else
            {
                RecalculateWorld(node.Parent);
                DQuaternion parentWorld = node.Parent.World.Rotation;
                localRotation = (parentWorld.Inverse() * targetWorldOriginal).Normalized();
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

            if (ShouldRouteTransformToPhysics(sceneId, node))
            {
                DQuaternion delta = DQuaternion.FromEulerDegrees(deltaEulerDegrees);
                DQuaternion newLocal = (node.LocalRotation * delta).Normalized();
                DQuaternion newWorld = LocalRotationToWorld(node, newLocal);
                Physics.TrySetBodyWorldRotation(sceneId, objectId, newWorld);
                return;
            }

            DQuaternion deltaOriginal = DQuaternion.FromEulerDegrees(deltaEulerDegrees);
            node.LocalRotation = (node.LocalRotation * deltaOriginal).Normalized();

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

            if (ShouldRouteTransformToPhysics(sceneId, node))
            {
                DQuaternion delta = DQuaternion.FromEulerDegrees(deltaEulerDegrees);
                DQuaternion newWorld = (delta * node.World.Rotation).Normalized();
                Physics.TrySetBodyWorldRotation(sceneId, objectId, newWorld);
                return;
            }

            DQuaternion deltaOriginal = DQuaternion.FromEulerDegrees(deltaEulerDegrees);
            DQuaternion newWorldOriginal = (deltaOriginal * node.World.Rotation).Normalized();

            if (node.Parent == null)
            {
                node.LocalRotation = newWorldOriginal;
            }
            else
            {
                RecalculateWorld(node.Parent);
                node.LocalRotation = (node.Parent.World.Rotation.Inverse() * newWorldOriginal).Normalized();
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

        public static Double3 GetLocalPosition(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return Double3.Zero;

            return node.LocalPosition;
        }

        public static Double3 GetPosition(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return Double3.Zero;

            RecalculateWorld(node);
            return node.World.Position;
        }

        public static Double3 GetLocalRotation(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return Double3.Zero;

            return node.LocalRotation.ToEulerDegrees();
        }

        public static Double3 GetRotation(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return Double3.Zero;

            RecalculateWorld(node);
            return node.World.Rotation.ToEulerDegrees();
        }

        public static Double3 GetLocalScale(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return Double3.One;

            return node.LocalScale;
        }

        public static Double3 GetScale(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return Double3.One;

            RecalculateWorld(node);
            return node.World.Scale;
        }

        public static Double3 WorldDeltaToLocalDelta(string sceneId, string objectId, Double3 worldDelta)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return worldDelta;

            if (node.Parent == null)
                return worldDelta;

            RecalculateWorld(node.Parent);
            SceneWorldState parent = node.Parent.World;

            DQuaternion invRot = parent.Rotation.Inverse();
            Double3 rotated = invRot.Rotate(worldDelta);

            return new Double3(
                parent.Scale.X != 0.0 ? rotated.X / parent.Scale.X : 0.0,
                parent.Scale.Y != 0.0 ? rotated.Y / parent.Scale.Y : 0.0,
                parent.Scale.Z != 0.0 ? rotated.Z / parent.Scale.Z : 0.0);
        }

        private static readonly Double3 RightBasis = new Double3(1.0, 0.0, 0.0);
        private static readonly Double3 UpBasis = new Double3(0.0, 1.0, 0.0);
        private static readonly Double3 ForwardBasis = new Double3(0.0, 0.0, 1.0);

        private static Double3 Negate(Double3 v)
        {
            return new Double3(-v.X, -v.Y, -v.Z);
        }

        public static Double3 GetLocalRight(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return RightBasis;

            return node.LocalRotation.Rotate(RightBasis);
        }

        public static Double3 GetLocalLeft(string sceneId, string objectId)
        {
            return Negate(GetLocalRight(sceneId, objectId));
        }

        public static Double3 GetLocalUp(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return UpBasis;

            return node.LocalRotation.Rotate(UpBasis);
        }

        public static Double3 GetLocalDown(string sceneId, string objectId)
        {
            return Negate(GetLocalUp(sceneId, objectId));
        }

        public static Double3 GetLocalForward(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return ForwardBasis;

            return node.LocalRotation.Rotate(ForwardBasis);
        }

        public static Double3 GetLocalBack(string sceneId, string objectId)
        {
            return Negate(GetLocalForward(sceneId, objectId));
        }

        public static Double3 GetRight(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return RightBasis;

            RecalculateWorld(node);
            return node.World.Rotation.Rotate(RightBasis);
        }

        public static Double3 GetLeft(string sceneId, string objectId)
        {
            return Negate(GetRight(sceneId, objectId));
        }

        public static Double3 GetUp(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return UpBasis;

            RecalculateWorld(node);
            return node.World.Rotation.Rotate(UpBasis);
        }

        public static Double3 GetDown(string sceneId, string objectId)
        {
            return Negate(GetUp(sceneId, objectId));
        }

        public static Double3 GetForward(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return ForwardBasis;

            RecalculateWorld(node);
            return node.World.Rotation.Rotate(ForwardBasis);
        }

        public static Double3 GetBack(string sceneId, string objectId)
        {
            return Negate(GetForward(sceneId, objectId));
        }

        public static string? GetParentId(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return null;

            return node.Parent?.Source.Id;
        }

        public static string[] GetChildIds(string sceneId, string objectId)
        {
            if (!TryGetNode(sceneId, objectId, out _, out var node))
                return Array.Empty<string>();

            return node.Children
                .Select(child => child.Source.Id)
                .ToArray();
        }

        private static bool HasNonStaticPhysics(SceneRuntimeNode node)
        {
            PhysicsBody? physics = node.Source.Physics;

            if (physics == null || !physics.Enabled)
                return false;

            return !string.Equals(physics.MotionType, "Static", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStaticRenderEligible(SceneRuntimeNode node)
        {
            if (!string.Equals(node.Source.Type, "Object", StringComparison.Ordinal))
                return false;

            if (!node.Source.Active || !node.Source.Visible)
                return false;

            if (string.IsNullOrWhiteSpace(node.Source.Mesh))
                return false;

            if (HasNonStaticPhysics(node))
                return false;

            return node.StillFramesSinceLastTransform >= StaticRenderStillFrameThreshold;
        }

        private static void UpdateStaticRenderStability(SceneRuntimeData runtime)
        {
            foreach (SceneRuntimeNode node in runtime.Nodes.Values)
            {
                if (node.Dirty || HasNonStaticPhysics(node))
                {
                    node.StillFramesSinceLastTransform = 0;
                    continue;
                }

                if (node.StillFramesSinceLastTransform < StaticRenderStillFrameThreshold)
                {
                    node.StillFramesSinceLastTransform++;

                    if (node.StillFramesSinceLastTransform == StaticRenderStillFrameThreshold)
                        runtime.DirtyNodes.Add(node.Source.Id);
                }
            }
        }

        public static void FlushDirtyToRenderer()
        {
            if (_boundGraphics == null)
                return;

            foreach (var pair in _runtimeScenes)
            {
                string sceneId = pair.Key;
                SceneRuntimeData runtime = pair.Value;

                UpdateStaticRenderStability(runtime);

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
                        Materials = node.Source.Materials,
                        RenderTag = node.Source.RenderTag,
                        WorldPosition = node.World.Position,
                        WorldRotation = node.World.Rotation,
                        WorldScale = node.World.Scale,
                        StaticRenderEligible = IsStaticRenderEligible(node),
                        TransformRevision = node.TransformMutationVersion
                    });

                    if (string.Equals(node.Source.Type, "Light", StringComparison.Ordinal))
                    {
                        try
                        {
                            LightRenderSettings settings = ParseLightSettings(node.Source.Data, node.Source.Id);

                            _boundGraphics.UpsertSceneLight(new Graphics.SceneRenderLightSnapshot
                            {
                                SceneId = sceneId,
                                ObjectId = node.Source.Id,
                                Settings = settings,
                                World = node.World,
                                Active = node.Source.Active,
                                Visible = node.Source.Visible
                            });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[!] Light '{node.Source.Id}' skipped while flushing to renderer: {ex.Message}");
                        }
                    }

                    if (_boundAudio != null)
                    {
                        if (string.Equals(node.Source.Type, "AudioListener", StringComparison.Ordinal))
                        {
                            try
                            {
                                AudioListenerSettings settings = ParseAudioListenerSettings(node.Source.Data, node.Source.Id);

                                _boundAudio.SetListener(sceneId, node.Source.Id, settings,
                                    node.World.Position, node.World.Rotation, Double3.Zero);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[!] AudioListener '{node.Source.Id}' skipped: {ex.Message}");
                            }
                        }
                        else if (string.Equals(node.Source.Type, "AudioSource", StringComparison.Ordinal))
                        {
                            try
                            {
                                AudioSourceSettings settings = ParseAudioSourceSettings(node.Source.Data, node.Source.Id);
                                _boundAudio.RegisterOrUpdateSceneAudioSource(sceneId, node.Source.Id, settings,
                                    node.World.Position, node.World.Rotation);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[!] AudioSource '{node.Source.Id}' skipped: {ex.Message}");
                            }
                        }
                    }
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
            Physics.RemoveScene(sceneId);
            _loadedScenes.TryRemove(sceneId, out _);
            _cameraQueues.TryRemove(sceneId, out _);
            _runtimeScenes.TryRemove(sceneId, out _);
            _boundGraphics?.RemoveSceneCache(sceneId);
        }

        public static void ClearAllScenes()
        {
            foreach (string sceneId in _loadedScenes.Keys.ToArray())
                _boundGraphics?.RemoveSceneCache(sceneId);

            Physics.ClearAll();

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
            //Console.WriteLine($"[i] Camera queue rebuilt for scene '{sceneId}', count={queue.Count}");
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

        internal static bool TryValidateLightDataString(string rawData, string objectId, out string reason)
        {
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(rawData))
                return true;

            try
            {
                _ = ParseLightSettings(rawData, objectId);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static Double3 ParseStrictDouble3(JsonElement element, string fieldName)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] {fieldName} must be an object with lowercase x/y/z.");

            try
            {
                return JsonSerializer.Deserialize<Double3>(element.GetRawText(), _jsonOptions);
            }
            catch (Exception)
            {
                throw new InvalidDataException($"[X] {fieldName} must be an object with lowercase x/y/z.");
            }
        }

        private static bool TryGetCameraNode(string sceneId, string objectId, out SceneRuntimeData runtime, out SceneRuntimeNode node)
        {
            if (!TryGetNode(sceneId, objectId, out runtime, out node))
                return false;

            if (!string.Equals(node.Source.Type, "Camera", StringComparison.Ordinal))
            {
                Console.WriteLine($"[!] Camera setting skipped: object '{objectId}' is not a camera. scene='{sceneId}'");
                return false;
            }

            return true;
        }

        private static CameraRenderSettings ReadCameraSettingsForMutation(SceneRuntimeNode node)
        {
            return ParseCameraSettings(node.Source.Data, node.Source.Id);
        }

        private static void CommitCameraSettings(SceneRuntimeData runtime, SceneRuntimeNode node, CameraRenderSettings settings)
        {
            if (settings.FovOrSize <= 0.0)
                throw new InvalidOperationException($"[X] Camera '{node.Source.Id}' fovOrSize must be > 0.");

            if (settings.NearClip <= 0.0)
                throw new InvalidOperationException($"[X] Camera '{node.Source.Id}' nearClip must be > 0.");

            if (settings.FarClip <= 0.0)
                throw new InvalidOperationException($"[X] Camera '{node.Source.Id}' farClip must be > 0.");

            if (settings.FarClip <= settings.NearClip)
                throw new InvalidOperationException($"[X] Camera '{node.Source.Id}' farClip must be greater than nearClip.");

            node.Source.Data = JsonSerializer.Serialize(settings, _jsonOptions);
            runtime.CameraCacheDirty = true;
        }

        public static void SetCameraRenderMode(string sceneId, string objectId, int renderMode)
        {
            if (renderMode != 0 && renderMode != 1)
                throw new ArgumentException("[X] Camera renderMode must be 0 or 1.", nameof(renderMode));

            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.RenderMode = renderMode;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraFovOrSize(string sceneId, string objectId, double fovOrSize)
        {
            if (fovOrSize <= 0.0)
                throw new ArgumentException("[X] Camera fovOrSize must be > 0.", nameof(fovOrSize));

            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.FovOrSize = fovOrSize;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraRenderPlaneOffset(string sceneId, string objectId, double x, double y)
        {
            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.RenderPlaneOffset.X = x;
            settings.RenderPlaneOffset.Y = y;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraNearClip(string sceneId, string objectId, double nearClip)
        {
            if (nearClip <= 0.0)
                throw new ArgumentException("[X] Camera nearClip must be > 0.", nameof(nearClip));

            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.NearClip = nearClip;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraFarClip(string sceneId, string objectId, double farClip)
        {
            if (farClip <= 0.0)
                throw new ArgumentException("[X] Camera farClip must be > 0.", nameof(farClip));

            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.FarClip = farClip;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraClipRange(string sceneId, string objectId, double nearClip, double farClip)
        {
            if (nearClip <= 0.0)
                throw new ArgumentException("[X] Camera nearClip must be > 0.", nameof(nearClip));

            if (farClip <= 0.0)
                throw new ArgumentException("[X] Camera farClip must be > 0.", nameof(farClip));

            if (farClip <= nearClip)
                throw new ArgumentException("[X] Camera farClip must be greater than nearClip.");

            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.NearClip = nearClip;
            settings.FarClip = farClip;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraProjectionType(string sceneId, string objectId, int projectionType)
        {
            if (projectionType != 0 && projectionType != 1)
                throw new ArgumentException("[X] Camera projectionType must be 0 or 1.", nameof(projectionType));

            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.ProjectionType = projectionType;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraMain(string sceneId, string objectId, bool isMainCamera)
        {
            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.IsMainCamera = isMainCamera;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraPostProcessEnabled(string sceneId, string objectId, bool enabled)
        {
            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.PostProcess.Enabled = enabled;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraPostBrightness(string sceneId, string objectId, bool enabled, double value)
        {
            SetCameraScalarPostProcess(sceneId, objectId, "brightness", enabled, value);
        }

        public static void SetCameraPostContrast(string sceneId, string objectId, bool enabled, double value)
        {
            SetCameraScalarPostProcess(sceneId, objectId, "contrast", enabled, value);
        }

        public static void SetCameraPostSaturation(string sceneId, string objectId, bool enabled, double value)
        {
            SetCameraScalarPostProcess(sceneId, objectId, "saturation", enabled, value);
        }

        private static void SetCameraScalarPostProcess(string sceneId, string objectId, string propertyName, bool enabled, double value)
        {
            if (value < 0.0)
                throw new ArgumentException($"[X] Camera postProcess.{propertyName}.value must be >= 0.", nameof(value));

            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);

            CameraScalarPostProcessSettings scalar = propertyName switch
            {
                "brightness" => settings.PostProcess.Brightness,
                "contrast" => settings.PostProcess.Contrast,
                "saturation" => settings.PostProcess.Saturation,
                _ => throw new ArgumentException("[X] Unsupported scalar post process property.", nameof(propertyName))
            };

            scalar.Enabled = enabled;
            scalar.Value = value;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraPostHue(string sceneId, string objectId, bool enabled, double degrees)
        {
            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.PostProcess.Hue.Enabled = enabled;
            settings.PostProcess.Hue.Degrees = degrees;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraPostTemperature(string sceneId, string objectId, bool enabled, double value)
        {
            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.PostProcess.Temperature.Enabled = enabled;
            settings.PostProcess.Temperature.Value = value;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraBloom(string sceneId, string objectId, bool enabled, double threshold, double softKnee, double intensity, int iterations, int downsample, double range)
        {
            if (threshold < 0.0)
                throw new ArgumentException("[X] Camera bloom threshold must be >= 0.", nameof(threshold));

            if (softKnee < 0.0)
                throw new ArgumentException("[X] Camera bloom softKnee must be >= 0.", nameof(softKnee));

            if (intensity < 0.0)
                throw new ArgumentException("[X] Camera bloom intensity must be >= 0.", nameof(intensity));

            if (iterations < 1)
                throw new ArgumentException("[X] Camera bloom iterations must be >= 1.", nameof(iterations));

            if (downsample < 1)
                throw new ArgumentException("[X] Camera bloom downsample must be >= 1.", nameof(downsample));

            if (range < 0.0)
                throw new ArgumentException("[X] Camera bloom range must be >= 0.", nameof(range));

            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.PostProcess.Bloom.Enabled = enabled;
            settings.PostProcess.Bloom.Threshold = threshold;
            settings.PostProcess.Bloom.SoftKnee = softKnee;
            settings.PostProcess.Bloom.Intensity = intensity;
            settings.PostProcess.Bloom.Iterations = iterations;
            settings.PostProcess.Bloom.Downsample = downsample;
            settings.PostProcess.Bloom.Range = range;
            CommitCameraSettings(runtime, node, settings);
        }

        public static void SetCameraSmaa(string sceneId, string objectId, bool enabled)
        {
            if (!TryGetCameraNode(sceneId, objectId, out var runtime, out var node))
                return;

            CameraRenderSettings settings = ReadCameraSettingsForMutation(node);
            settings.PostProcess.Smaa.Enabled = enabled;
            CommitCameraSettings(runtime, node, settings);
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
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out int renderMode) || renderMode != 0 && renderMode != 1)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.renderMode must be 0 or 1.");
                        settings.RenderMode = renderMode;
                        break;

                    case "fovOrSize":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double fovOrSize) || fovOrSize <= 0.0)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.fovOrSize must be > 0.");
                        settings.FovOrSize = fovOrSize;
                        break;

                    case "renderPlaneOffset":
                        ParseCameraRenderPlaneOffset(prop.Value, settings.RenderPlaneOffset, objectId);
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
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out int projectionType) || projectionType != 0 && projectionType != 1)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.projectionType must be 0 or 1.");
                        settings.ProjectionType = projectionType;
                        break;

                    case "isMainCamera":
                        if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.isMainCamera must be true or false.");
                        settings.IsMainCamera = prop.Value.GetBoolean();
                        break;

                    case "postProcess":
                        ParseCameraPostProcessSettings(prop.Value, settings.PostProcess, objectId);
                        break;

                    default:
                        break;
                }
            }

            if (settings.FarClip <= settings.NearClip)
                throw new InvalidDataException($"[X] Camera '{objectId}' data.farClip must be greater than data.nearClip.");

            return settings;
        }

        private static void ParseCameraRenderPlaneOffset(
            JsonElement element,
            CameraRenderPlaneOffsetSettings settings,
            string objectId)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] Camera '{objectId}' data.renderPlaneOffset must be an object.");

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "x":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double x))
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.renderPlaneOffset.x must be a number.");
                        settings.X = x;
                        break;

                    case "y":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double y))
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.renderPlaneOffset.y must be a number.");
                        settings.Y = y;
                        break;

                    default:
                        throw new InvalidDataException($"[X] Camera '{objectId}' data.renderPlaneOffset contains unknown or wrong-cased property '{prop.Name}'.");
                }
            }
        }

        private static void ParseCameraPostProcessSettings(
            JsonElement element,
            CameraPostProcessSettings settings,
            string objectId)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess must be an object.");

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "enabled":
                        settings.Enabled = ReadStrictBoolean(prop.Value, $"Camera '{objectId}' data.postProcess.enabled");
                        break;

                    case "brightness":
                        ParseCameraScalarPostProcessSettings(prop.Value, settings.Brightness, objectId, "brightness", allowNegative: false);
                        break;

                    case "contrast":
                        ParseCameraScalarPostProcessSettings(prop.Value, settings.Contrast, objectId, "contrast", allowNegative: false);
                        break;

                    case "saturation":
                        ParseCameraScalarPostProcessSettings(prop.Value, settings.Saturation, objectId, "saturation", allowNegative: false);
                        break;

                    case "hue":
                        ParseCameraHuePostProcessSettings(prop.Value, settings.Hue, objectId);
                        break;

                    case "temperature":
                        ParseCameraTemperaturePostProcessSettings(prop.Value, settings.Temperature, objectId);
                        break;

                    case "bloom":
                        ParseCameraBloomPostProcessSettings(prop.Value, settings.Bloom, objectId);
                        break;

                    case "smaa":
                        ParseCameraSmaaPostProcessSettings(prop.Value, settings.Smaa, objectId);
                        break;

                    default:
                        throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess contains unknown or wrong-cased property '{prop.Name}'.");
                }
            }
        }

        private static void ParseCameraScalarPostProcessSettings(
            JsonElement element,
            CameraScalarPostProcessSettings settings,
            string objectId,
            string propertyName,
            bool allowNegative)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.{propertyName} must be an object.");

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "enabled":
                        settings.Enabled = ReadStrictBoolean(prop.Value, $"Camera '{objectId}' data.postProcess.{propertyName}.enabled");
                        break;

                    case "value":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double value))
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.{propertyName}.value must be a number.");

                        if (!allowNegative && value < 0.0)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.{propertyName}.value must be >= 0.");

                        settings.Value = value;
                        break;

                    default:
                        throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.{propertyName} contains unknown or wrong-cased property '{prop.Name}'.");
                }
            }
        }

        private static void ParseCameraHuePostProcessSettings(
            JsonElement element,
            CameraHuePostProcessSettings settings,
            string objectId)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.hue must be an object.");

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "enabled":
                        settings.Enabled = ReadStrictBoolean(prop.Value, $"Camera '{objectId}' data.postProcess.hue.enabled");
                        break;

                    case "degrees":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double degrees))
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.hue.degrees must be a number.");
                        settings.Degrees = degrees;
                        break;

                    default:
                        throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.hue contains unknown or wrong-cased property '{prop.Name}'.");
                }
            }
        }

        private static void ParseCameraTemperaturePostProcessSettings(
            JsonElement element,
            CameraTemperaturePostProcessSettings settings,
            string objectId)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.temperature must be an object.");

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "enabled":
                        settings.Enabled = ReadStrictBoolean(prop.Value, $"Camera '{objectId}' data.postProcess.temperature.enabled");
                        break;

                    case "value":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double value))
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.temperature.value must be a number.");
                        settings.Value = value;
                        break;

                    default:
                        throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.temperature contains unknown or wrong-cased property '{prop.Name}'.");
                }
            }
        }

        private static void ParseCameraBloomPostProcessSettings(
            JsonElement element,
            CameraBloomPostProcessSettings settings,
            string objectId)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.bloom must be an object.");

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "enabled":
                        settings.Enabled = ReadStrictBoolean(prop.Value, $"Camera '{objectId}' data.postProcess.bloom.enabled");
                        break;

                    case "threshold":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double threshold) || threshold < 0.0)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.bloom.threshold must be >= 0.");
                        settings.Threshold = threshold;
                        break;

                    case "softKnee":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double softKnee) || softKnee < 0.0)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.bloom.softKnee must be >= 0.");
                        settings.SoftKnee = softKnee;
                        break;

                    case "intensity":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double intensity) || intensity < 0.0)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.bloom.intensity must be >= 0.");
                        settings.Intensity = intensity;
                        break;

                    case "iterations":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out int iterations) || iterations < 1)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.bloom.iterations must be >= 1.");
                        settings.Iterations = iterations;
                        break;

                    case "downsample":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out int downsample) || downsample < 1)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.bloom.downsample must be >= 1.");
                        settings.Downsample = downsample;
                        break;

                    case "range":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double range) || range < 0.0)
                            throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.bloom.range must be >= 0.");
                        settings.Range = range;
                        break;

                    default:
                        throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.bloom contains unknown or wrong-cased property '{prop.Name}'.");
                }
            }
        }

        private static void ParseCameraSmaaPostProcessSettings(
            JsonElement element,
            CameraSmaaPostProcessSettings settings,
            string objectId)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.smaa must be an object.");

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "enabled":
                        settings.Enabled = ReadStrictBoolean(prop.Value, $"Camera '{objectId}' data.postProcess.smaa.enabled");
                        break;

                    default:
                        throw new InvalidDataException($"[X] Camera '{objectId}' data.postProcess.smaa contains unknown or wrong-cased property '{prop.Name}'.");
                }
            }
        }


        private static bool ReadStrictBoolean(JsonElement element, string fieldName)
        {
            if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
                throw new InvalidDataException($"[X] {fieldName} must be true or false.");

            return element.GetBoolean();
        }

        private static LightRenderSettings ParseLightSettings(string? rawData, string objectId)
        {
            var settings = new LightRenderSettings();

            if (string.IsNullOrWhiteSpace(rawData))
                return settings;

            using JsonDocument doc = JsonDocument.Parse(rawData);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] Light '{objectId}' data must be a JSON object string.");

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "lightMode":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetInt32(out int lightMode) ||
                            lightMode != 0 && lightMode != 3)
                        {
                            throw new InvalidDataException(
                                $"[X] Light '{objectId}' data.lightMode must be 0 (Point) or 3 (Directional).");
                        }
                        settings.LightMode = lightMode;
                        break;

                    case "color":
                        {
                            Double3 color = ParseStrictDouble3(prop.Value, $"Light '{objectId}' data.color");

                            if (color.X < 0.0 || color.X > 1.0 ||
                                color.Y < 0.0 || color.Y > 1.0 ||
                                color.Z < 0.0 || color.Z > 1.0)
                            {
                                throw new InvalidDataException($"[X] Light '{objectId}' data.color components must be between 0 and 1.");
                            }

                            settings.Color = color;
                            break;
                        }

                    case "intensity":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double intensity) ||
                            intensity < 0.0)
                        {
                            throw new InvalidDataException($"[X] Light '{objectId}' data.intensity must be >= 0.");
                        }
                        settings.Intensity = intensity;
                        break;

                    case "range":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double range) ||
                            range <= 0.0)
                        {
                            throw new InvalidDataException($"[X] Light '{objectId}' data.range must be > 0.");
                        }
                        settings.Range = range;
                        break;

                    case "attenuationCurve":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double attenuationCurve) ||
                            attenuationCurve < 0.0 ||
                            attenuationCurve > 1.0)
                        {
                            throw new InvalidDataException($"[X] Light '{objectId}' data.attenuationCurve must be between 0 and 1.");
                        }
                        settings.AttenuationCurve = attenuationCurve;
                        break;

                    case "castShadow":
                        if (prop.Value.ValueKind != JsonValueKind.True &&
                            prop.Value.ValueKind != JsonValueKind.False)
                        {
                            throw new InvalidDataException($"[X] Light '{objectId}' data.castShadow must be true or false.");
                        }
                        settings.CastShadow = prop.Value.GetBoolean();
                        break;

                    default:
                        throw new InvalidDataException($"[X] Light '{objectId}' data contains unknown or wrong-cased property '{prop.Name}'.");
                }
            }

            return settings;
        }

        private static AudioListenerSettings ParseAudioListenerSettings(string? rawData, string objectId)
        {
            var settings = new AudioListenerSettings();

            if (string.IsNullOrWhiteSpace(rawData))
                return settings;

            using JsonDocument doc = JsonDocument.Parse(rawData);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] AudioListener '{objectId}' data must be a JSON object string.");

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "OutputMode":
                    case "outputMode":
                        if (prop.Value.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException($"[X] AudioListener '{objectId}' data.OutputMode must be a string.");
                        string mode = prop.Value.GetString() ?? "";
                        if (mode != "Direct" && mode != "AudioSource")
                            throw new InvalidDataException($"[X] AudioListener '{objectId}' data.OutputMode must be 'Direct' or 'AudioSource'.");
                        settings.OutputMode = mode;
                        break;

                    case "TargetSourceId":
                    case "targetSourceId":
                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            settings.TargetSourceId = null;
                        else if (prop.Value.ValueKind == JsonValueKind.String)
                            settings.TargetSourceId = prop.Value.GetString();
                        else
                            throw new InvalidDataException($"[X] AudioListener '{objectId}' data.TargetSourceId must be a string or null.");
                        break;

                    case "Mute":
                    case "mute":
                        if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
                            throw new InvalidDataException($"[X] AudioListener '{objectId}' data.Mute must be true or false.");
                        settings.Mute = prop.Value.GetBoolean();
                        break;

                    default:
                        throw new InvalidDataException($"[X] AudioListener '{objectId}' data contains unknown property '{prop.Name}'.");
                }
            }

            return settings;
        }

        private static AudioSourceSettings ParseAudioSourceSettings(string? rawData, string objectId)
        {
            var settings = new AudioSourceSettings();

            if (string.IsNullOrWhiteSpace(rawData))
                return settings;

            using JsonDocument doc = JsonDocument.Parse(rawData);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"[X] AudioSource '{objectId}' data must be a JSON object string.");

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "ClipId":
                    case "clipId":
                        if (prop.Value.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.ClipId must be a string.");
                        settings.ClipId = prop.Value.GetString() ?? "";
                        break;

                    case "Volume":
                    case "volume":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double volume) || volume < 0.0)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.Volume must be >= 0.");
                        settings.Volume = volume;
                        break;

                    case "MinDistance":
                    case "minDistance":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double minDist) || minDist <= 0.0)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.MinDistance must be > 0.");
                        settings.MinDistance = minDist;
                        break;

                    case "MaxDistance":
                    case "maxDistance":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double maxDist) || maxDist <= 0.0)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.MaxDistance must be > 0.");
                        settings.MaxDistance = maxDist;
                        break;

                    case "RolloffFactor":
                    case "rolloffFactor":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double rolloff) || rolloff < 0.0)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.RolloffFactor must be >= 0.");
                        settings.RolloffFactor = rolloff;
                        break;

                    case "AttenuationModel":
                    case "attenuationModel":
                        if (prop.Value.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.AttenuationModel must be a string.");
                        settings.AttenuationModel = prop.Value.GetString() ?? "InverseDistanceClamped";
                        break;

                    case "SpatialBlend":
                    case "spatialBlend":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double spatialBlend) || spatialBlend < 0.0 || spatialBlend > 1.0)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.SpatialBlend must be between 0 and 1.");
                        settings.SpatialBlend = spatialBlend;
                        break;

                    case "Loop":
                    case "loop":
                        if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.Loop must be true or false.");
                        settings.Loop = prop.Value.GetBoolean();
                        break;

                    case "PlayOnAwake":
                    case "playOnAwake":
                        if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.PlayOnAwake must be true or false.");
                        settings.PlayOnAwake = prop.Value.GetBoolean();
                        break;

                    case "DopplerFactor":
                    case "dopplerFactor":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double doppler) || doppler < 0.0)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.DopplerFactor must be >= 0.");
                        settings.DopplerFactor = doppler;
                        break;

                    case "ReferenceDbLevel":
                    case "referenceDbLevel":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double refDb))
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.ReferenceDbLevel must be a number.");
                        settings.ReferenceDbLevel = refDb;
                        break;

                    case "CullDbThreshold":
                    case "cullDbThreshold":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double cullDb))
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.CullDbThreshold must be a number.");
                        settings.CullDbThreshold = cullDb;
                        break;

                    case "SpeedOfSound":
                    case "speedOfSound":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double sos) || sos <= 0.0)
                            throw new InvalidDataException($"[X] AudioSource '{objectId}' data.SpeedOfSound must be > 0.");
                        settings.SpeedOfSound = sos;
                        break;

                    default:
                        throw new InvalidDataException($"[X] AudioSource '{objectId}' data contains unknown property '{prop.Name}'.");
                }
            }

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
