using Jitter2;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine.Engine
{
    internal class Physics
    {
        internal delegate bool MeshColliderTriangleResolver(
            string meshId,
            out List<Graphics.MeshColliderTriangle> triangles);

        private static MeshColliderTriangleResolver? _meshColliderTriangleResolver;

        internal static void SetMeshColliderTriangleResolver(
            MeshColliderTriangleResolver resolver)
        {
            _meshColliderTriangleResolver = resolver;
        }
        private sealed class ScenePhysicsRuntime : IDisposable
        {
            public string SceneId { get; init; } = "";
            public Scene.SceneRuntimeData Runtime { get; init; } = null!;
            public World World { get; init; } = null!;
            public Dictionary<string, RigidBody> Bodies { get; } = new(StringComparer.Ordinal);
            public List<Scene.SceneRuntimeNode> OrderedPhysicsNodes { get; } = new();
            public Dictionary<string, bool> ForceWakeState { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, Double3> LastWorldScale { get; } = new(StringComparer.Ordinal);

            public double FixedDeltaAccumulator;
            public bool SkipFirstPhysicsStep = true;

            public void Dispose()
            {
                World.Dispose();
            }
        }

        private static readonly ConcurrentDictionary<string, ScenePhysicsRuntime> _sceneWorlds =
            new(StringComparer.Ordinal);

        public static void RegisterScene(string sceneId, Scene.SceneRuntimeData runtime)
        {
            RemoveScene(sceneId);

            var physics = new ScenePhysicsRuntime
            {
                SceneId = sceneId,
                Runtime = runtime,
                World = new World()
            };

            physics.World.Gravity = new JVector(0.0, -9.81, 0.0);

            foreach (var node in runtime.Nodes.Values.OrderBy(n => n.Depth))
            {
                PhysicsBody? config = node.Source.Physics;
                if (config == null || !config.Enabled)
                    continue;

                RigidBody body = CreateRigidBody(physics.World, node, config);

                physics.Bodies[node.Source.Id] = body;
                physics.OrderedPhysicsNodes.Add(node);
                physics.ForceWakeState[node.Source.Id] = false;
                physics.LastWorldScale[node.Source.Id] = ResolveWorld(node).Scale;
            }

            _sceneWorlds[sceneId] = physics;
        }

        public static void RemoveScene(string sceneId)
        {
            if (_sceneWorlds.TryRemove(sceneId, out var runtime))
                runtime.Dispose();
        }

        public static void ClearAll()
        {
            foreach (var pair in _sceneWorlds)
                pair.Value.Dispose();

            _sceneWorlds.Clear();
        }

        public static void Step(double deltaTime, float fixedDeltaTime)
        {
            foreach (var pair in _sceneWorlds)
            {
                ScenePhysicsRuntime physics = pair.Value;

                if (physics.OrderedPhysicsNodes.Count == 0)
                    continue;

                if (physics.SkipFirstPhysicsStep)
                {
                    physics.FixedDeltaAccumulator = 0.0;
                    physics.SkipFirstPhysicsStep = false;
                    continue;
                }

                physics.FixedDeltaAccumulator += deltaTime;

                double maxAccumulatedTime = fixedDeltaTime * 2.0;
                if (physics.FixedDeltaAccumulator > maxAccumulatedTime)
                    physics.FixedDeltaAccumulator = maxAccumulatedTime;

                while (physics.FixedDeltaAccumulator >= fixedDeltaTime)
                {
                    PushDirtySceneTransformsToPhysics(physics);
                    WakeBodiesUnderForce(physics);
                    physics.World.Step(fixedDeltaTime, false);
                    PullPhysicsTransformsToScene(physics);

                    physics.FixedDeltaAccumulator -= fixedDeltaTime;
                }
            }
        }

        private static RigidBody CreateRigidBody(World world, Scene.SceneRuntimeNode node, PhysicsBody config)
        {
            SceneWorldState initialWorld = ResolveWorld(node);
            MotionType motionType = NormalizeMotionType(config.MotionType);
            string shapeType = NormalizeShapeType(config.ShapeType);

            RigidBody body = world.CreateRigidBody();
            body.Tag = node.Source.Id;

            body.Position = ToJVector(initialWorld.Position);
            body.Orientation = ToJQuaternion(initialWorld.Rotation);

            AddConfiguredShapes(body, node, config, initialWorld, motionType);

            if (shapeType != "Mesh")
            {
                body.SetMassInertia(config.Mass);
            }

            body.Friction = config.Friction;
            body.Restitution = config.Restitution;
            body.EnableSpeculativeContacts = config.EnableSpeculativeContacts;
            body.Damping = (config.LinearDamping, config.AngularDamping);

            switch (motionType)
            {
                case MotionType.Static:
                    body.MotionType = MotionType.Static;
                    body.AffectedByGravity = false;
                    break;

                case MotionType.Kinematic:
                    body.MotionType = MotionType.Kinematic;
                    body.AffectedByGravity = false;
                    break;

                case MotionType.Dynamic:
                    body.MotionType = MotionType.Dynamic;
                    body.AffectedByGravity = config.UseGravity;
                    body.DeactivationThreshold = (0.001, 0.001);
                    break;

                default:
                    throw new InvalidOperationException($"[X] Unsupported physics motion type '{config.MotionType}'.");
            }

            return body;
        }

        private static void AddConfiguredShapes(
            RigidBody body,
            Scene.SceneRuntimeNode node,
            PhysicsBody config,
            SceneWorldState initialWorld,
            MotionType motionType)
        {
            string shapeType = NormalizeShapeType(config.ShapeType);

            if (shapeType != "Mesh")
            {
                body.AddShape(CreatePrimitiveShape(config));
                return;
            }

            AddMeshShapes(body, node, config, initialWorld.Scale, motionType);
        }

        private static RigidBodyShape CreatePrimitiveShape(PhysicsBody config)
        {
            return NormalizeShapeType(config.ShapeType) switch
            {
                "Box" => new BoxShape(ToJVector(config.Size)),
                "Sphere" => new SphereShape(config.Radius),
                "Capsule" => new CapsuleShape(config.Radius, config.Length),
                _ => throw new InvalidOperationException(
                    $"[X] Primitive shape builder does not support '{config.ShapeType}'.")
            };
        }

        private static void AddMeshShapes(
            RigidBody body,
            Scene.SceneRuntimeNode node,
            PhysicsBody config,
            Double3 worldScale,
            MotionType motionType)
        {
            if (motionType == MotionType.Dynamic)
            {
                throw new InvalidOperationException(
                    $"[X] Mesh collider on object '{node.Source.Id}' only supports Static or Kinematic motion.");
            }

            if (string.IsNullOrWhiteSpace(node.Source.Mesh))
            {
                throw new InvalidOperationException(
                    $"[X] Mesh collider requires SceneObject.Mesh. object='{node.Source.Id}'.");
            }

            if (_meshColliderTriangleResolver == null)
            {
                throw new InvalidOperationException(
                    "[X] Mesh collider requires Graphics to be bound before scene loading.");
            }

            if (!_meshColliderTriangleResolver(node.Source.Mesh, out List<Graphics.MeshColliderTriangle> sourceTriangles) ||
                sourceTriangles.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[X] Mesh collider source mesh '{node.Source.Mesh}' was not found or contains no triangles.");
            }

            List<JTriangle> triangles = BuildScaledMeshTriangles(sourceTriangles, worldScale);
            TriangleMesh triangleMesh = new TriangleMesh(triangles);

            foreach (TriangleShape triangleShape in TriangleShape.CreateAllShapes(triangleMesh))
            {
                body.AddShape(triangleShape, MassInertiaUpdateMode.Preserve);
            }
        }

        private static List<JTriangle> BuildScaledMeshTriangles(
            IReadOnlyList<Graphics.MeshColliderTriangle> sourceTriangles,
            Double3 scale)
        {
            var result = new List<JTriangle>(sourceTriangles.Count);

            int skippedDegenerateCount = 0;
            bool flipWinding = scale.X * scale.Y * scale.Z > 0.0;

            foreach (Graphics.MeshColliderTriangle tri in sourceTriangles)
            {
                Double3 pa = ScalePoint(tri.A, scale);
                Double3 pb = ScalePoint(tri.B, scale);
                Double3 pc = ScalePoint(tri.C, scale);

                JVector a = new JVector(pa.X, pa.Y, -pa.Z);
                JVector b = new JVector(pb.X, pb.Y, -pb.Z);
                JVector c = new JVector(pc.X, pc.Y, -pc.Z);

                if (IsDegenerateTriangle(a, b, c))
                {
                    skippedDegenerateCount++;
                    continue;
                }

                result.Add(flipWinding
                    ? new JTriangle(a, c, b)
                    : new JTriangle(a, b, c));
            }

            if (result.Count == 0)
            {
                throw new InvalidOperationException(
                    "[X] Mesh collider contains no valid triangles after degenerate triangle filtering.");
            }

            if (skippedDegenerateCount > 0)
            {
                Console.WriteLine(
                    $"[!] Mesh collider skipped {skippedDegenerateCount} degenerate triangles.");
            }

            return result;
        }

        private static bool IsDegenerateTriangle(JVector a, JVector b, JVector c)
        {
            const double pointEpsilonSq = 1e-12;
            const double areaEpsilonSq = 1e-12;

            JVector ab = b - a;
            JVector ac = c - a;
            JVector bc = c - b;

            double abLenSq = ab.LengthSquared();
            double acLenSq = ac.LengthSquared();
            double bcLenSq = bc.LengthSquared();

            if (abLenSq <= pointEpsilonSq ||
                acLenSq <= pointEpsilonSq ||
                bcLenSq <= pointEpsilonSq)
            {
                return true;
            }

            JVector cross = JVector.Cross(ab, ac);
            double crossLenSq = cross.LengthSquared();

            return crossLenSq <= areaEpsilonSq;
        }

        private static Double3 ScalePoint(Double3 point, Double3 scale)
        {
            return new Double3(
                point.X * scale.X,
                point.Y * scale.Y,
                point.Z * scale.Z);
        }

        private static void PushDirtySceneTransformsToPhysics(ScenePhysicsRuntime physics)
        {
            if (physics.Runtime.DirtyNodes.Count == 0)
                return;

            List<string> processedNodes = new List<string>();

            foreach (Scene.SceneRuntimeNode node in physics.OrderedPhysicsNodes)
            {
                string id = node.Source.Id;

                if (!physics.Runtime.DirtyNodes.Contains(id))
                    continue;

                if (!physics.Bodies.TryGetValue(id, out var body))
                    continue;

                PhysicsBody? config = node.Source.Physics;
                if (config == null || !config.Enabled)
                    continue;

                MotionType motionType = NormalizeMotionType(config.MotionType);
                processedNodes.Add(id);

                if (motionType == MotionType.Dynamic)
                    continue;

                SceneWorldState worldState = ResolveWorld(node);

                JVector newPos = ToJVector(worldState.Position);
                JQuaternion newRot = ToJQuaternion(worldState.Rotation);

                bool posChanged = !MathHelper.CloseToZero(body.Position - newPos);
                bool rotChanged = !SameRotation(body.Orientation, newRot);

                Double3 previousScale = physics.LastWorldScale.TryGetValue(id, out Double3 cachedScale)
                    ? cachedScale
                    : worldState.Scale;

                bool scaleChanged =
                    UsesWorldScale(config) &&
                    !SameScale(previousScale, worldState.Scale);

                if (!posChanged && !rotChanged && !scaleChanged)
                    continue;

                RecreateNonDynamicBody(physics, node, config, body);
            }

            foreach (string id in processedNodes)
                physics.Runtime.DirtyNodes.Remove(id);
        }

        private static void RecreateNonDynamicBody(
            ScenePhysicsRuntime physics,
            Scene.SceneRuntimeNode node,
            PhysicsBody config,
            RigidBody oldBody)
        {
            physics.World.Remove(oldBody);

            RigidBody newBody = CreateRigidBody(physics.World, node, config);

            physics.Bodies[node.Source.Id] = newBody;
            physics.ForceWakeState[node.Source.Id] = false;
            physics.LastWorldScale[node.Source.Id] = ResolveWorld(node).Scale;
        }

        private static bool SameRotation(JQuaternion a, JQuaternion b)
        {
            return
                Math.Abs(a.X - b.X) <= 1e-9 &&
                Math.Abs(a.Y - b.Y) <= 1e-9 &&
                Math.Abs(a.Z - b.Z) <= 1e-9 &&
                Math.Abs(a.W - b.W) <= 1e-9;
        }

        private static bool SameScale(Double3 a, Double3 b)
        {
            return
                Math.Abs(a.X - b.X) <= 1e-9 &&
                Math.Abs(a.Y - b.Y) <= 1e-9 &&
                Math.Abs(a.Z - b.Z) <= 1e-9;
        }

        private static bool UsesWorldScale(PhysicsBody config)
        {
            return NormalizeShapeType(config.ShapeType) == "Mesh";
        }

        internal static bool HasBody(string sceneId, string objectId)
        {
            sceneId = sceneId?.Trim() ?? string.Empty;
            objectId = objectId?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(sceneId) || string.IsNullOrWhiteSpace(objectId))
                return false;

            if (!_sceneWorlds.TryGetValue(sceneId, out var physics))
                return false;

            return physics.Bodies.ContainsKey(objectId);
        }

        private static bool TryGetScenePhysicsRuntime(string? sceneId, out ScenePhysicsRuntime physics)
        {
            physics = null!;

            sceneId = sceneId?.Trim();

            if (string.IsNullOrWhiteSpace(sceneId))
            {
                Console.WriteLine("[!] Physics skipped: sceneId is null or empty.");
                return false;
            }

            if (!_sceneWorlds.TryGetValue(sceneId, out physics!))
            {
                Console.WriteLine($"[!] Physics skipped: scene '{sceneId}' is not registered.");
                return false;
            }

            return true;
        }

        private static bool TryGetBody(
            string? sceneId,
            string? objectId,
            out ScenePhysicsRuntime physics,
            out Scene.SceneRuntimeNode node,
            out RigidBody body,
            out PhysicsBody config)
        {
            physics = null!;
            node = null!;
            body = null!;
            config = null!;

            objectId = objectId?.Trim();

            if (!TryGetScenePhysicsRuntime(sceneId, out physics))
                return false;

            if (string.IsNullOrWhiteSpace(objectId))
            {
                Console.WriteLine($"[!] Physics skipped: objectId is null or empty. scene='{sceneId}'");
                return false;
            }

            if (!physics.Runtime.Nodes.TryGetValue(objectId, out node!))
            {
                Console.WriteLine($"[!] Physics skipped: object '{objectId}' not found in scene '{sceneId}'.");
                return false;
            }

            if (!physics.Bodies.TryGetValue(objectId, out body!))
            {
                Console.WriteLine($"[!] Physics skipped: object '{objectId}' has no rigid body in scene '{sceneId}'.");
                return false;
            }

            config = node.Source.Physics!;
            if (config == null || !config.Enabled)
            {
                Console.WriteLine($"[!] Physics skipped: object '{objectId}' physics is null or disabled in scene '{sceneId}'.");
                return false;
            }

            return true;
        }

        private static void SyncBodyPoseToScene(ScenePhysicsRuntime physics, Scene.SceneRuntimeNode node, RigidBody body)
        {
            Double3 worldPosition = ToDouble3(body.Position);
            DQuaternion worldRotation = ToDQuaternion(body.Orientation);

            ApplyWorldPoseToNode(node, worldPosition, worldRotation);
            MarkSubtreeDirtyFromPhysics(physics.Runtime, node);
        }

        private static bool TryNormalizeDirection(Double3 direction, out Double3 normalized)
        {
            double lenSq =
                direction.X * direction.X +
                direction.Y * direction.Y +
                direction.Z * direction.Z;

            if (lenSq <= 1e-24)
            {
                normalized = Double3.Zero;
                return false;
            }

            double len = Math.Sqrt(lenSq);
            normalized = direction / len;
            return true;
        }

        internal static bool TrySetBodyWorldPosition(string sceneId, string objectId, Double3 worldPosition)
        {
            if (!TryGetBody(sceneId, objectId, out var physics, out var node, out var body, out _))
                return false;

            body.Position = ToJVector(worldPosition);
            SyncBodyPoseToScene(physics, node, body);
            return true;
        }

        internal static bool TrySetBodyWorldRotation(string sceneId, string objectId, DQuaternion worldRotation)
        {
            if (!TryGetBody(sceneId, objectId, out var physics, out var node, out var body, out _))
                return false;

            body.Orientation = ToJQuaternion(worldRotation.Normalized());
            SyncBodyPoseToScene(physics, node, body);
            return true;
        }

        public static Double3 GetVelocity(string sceneId, string objectId)
        {
            if (!TryGetBody(sceneId, objectId, out _, out _, out var body, out _))
                return Double3.Zero;

            return ToDouble3(body.Velocity);
        }

        public static void SetVelocity(string sceneId, string objectId, Double3 velocity)
        {
            if (!TryGetBody(sceneId, objectId, out _, out _, out var body, out _))
                return;

            body.Velocity = ToJVector(velocity);
        }

        public static Double3 GetAngularVelocity(string sceneId, string objectId)
        {
            if (!TryGetBody(sceneId, objectId, out _, out _, out var body, out _))
                return Double3.Zero;

            return ToDouble3(body.AngularVelocity);
        }

        public static void SetAngularVelocity(string sceneId, string objectId, Double3 angularVelocity)
        {
            if (!TryGetBody(sceneId, objectId, out _, out _, out var body, out _))
                return;

            body.AngularVelocity = ToJVector(angularVelocity);
        }

        public static void AddForce(string sceneId, string objectId, Double3 force)
        {
            if (!TryGetBody(sceneId, objectId, out _, out _, out var body, out _))
                return;

            body.AddForce(ToJVector(force));
        }

        public static void AddForceAtPosition(string sceneId, string objectId, Double3 force, Double3 worldPosition)
        {
            if (!TryGetBody(sceneId, objectId, out _, out _, out var body, out _))
                return;

            body.AddForce(ToJVector(force), ToJVector(worldPosition));
        }

        public static void ApplyImpulse(string sceneId, string objectId, Double3 impulse)
        {
            if (!TryGetBody(sceneId, objectId, out _, out _, out var body, out _))
                return;

            body.ApplyImpulse(ToJVector(impulse));
        }

        public static void ApplyImpulseAtPosition(string sceneId, string objectId, Double3 impulse, Double3 worldPosition)
        {
            if (!TryGetBody(sceneId, objectId, out _, out _, out var body, out _))
                return;

            body.ApplyImpulse(ToJVector(impulse), ToJVector(worldPosition));
        }

        public static void SetActivationState(string sceneId, string objectId, bool active)
        {
            if (!TryGetBody(sceneId, objectId, out _, out _, out var body, out _))
                return;

            body.SetActivationState(active);
        }

        public static PhysicsRaycastHit? Raycast(string sceneId, Double3 origin, Double3 direction, double maxDistance)
        {
            if (!TryGetScenePhysicsRuntime(sceneId, out var physics))
                return null;

            if (!(maxDistance > 0.0))
            {
                Console.WriteLine($"[!] Physics raycast skipped: maxDistance must be > 0. scene='{sceneId}'");
                return null;
            }

            if (!TryNormalizeDirection(direction, out Double3 normalizedDirection))
            {
                Console.WriteLine($"[!] Physics raycast skipped: direction is zero. scene='{sceneId}'");
                return null;
            }

            JVector rayOrigin = ToJVector(origin);
            JVector rayDirection = ToJVector(normalizedDirection);

            bool hit = physics.World.DynamicTree.RayCast(
                rayOrigin,
                rayDirection,
                null,
                null,
                out IDynamicTreeProxy? proxy,
                out JVector normal,
                out double lambda);

            if (!hit)
                return null;

            double distance = lambda;
            if (distance > maxDistance)
                return null;

            string objectId = string.Empty;

            if (proxy is RigidBodyShape shape && shape.RigidBody?.Tag is string hitObjectId)
                objectId = hitObjectId;

            Double3 point = origin + normalizedDirection * distance;

            return new PhysicsRaycastHit
            {
                SceneId = sceneId,
                ObjectId = objectId,
                Point = point,
                Normal = ToDouble3(normal),
                Distance = distance
            };
        }

        private static void WakeBodiesUnderForce(ScenePhysicsRuntime physics)
        {
            foreach (Scene.SceneRuntimeNode node in physics.OrderedPhysicsNodes)
            {
                string id = node.Source.Id;

                if (!physics.Bodies.TryGetValue(id, out var body))
                    continue;

                PhysicsBody? config = node.Source.Physics;
                if (config == null || !config.Enabled)
                    continue;

                if (NormalizeMotionType(config.MotionType) != MotionType.Dynamic)
                    continue;

                bool hasGravity =
                    config.UseGravity &&
                    body.AffectedByGravity;

                bool hasExternalForce =
                    !MathHelper.CloseToZero(body.Force);

                bool hasExternalTorque =
                    !MathHelper.CloseToZero(body.Torque);

                bool underForce = hasGravity || hasExternalForce || hasExternalTorque;

                bool wasUnderForce = physics.ForceWakeState.TryGetValue(id, out bool prev) && prev;

                if (underForce && !wasUnderForce)
                {
                    body.SetActivationState(true);
                }

                physics.ForceWakeState[id] = underForce;
            }
        }

        private static void PullPhysicsTransformsToScene(ScenePhysicsRuntime physics)
        {
            foreach (Scene.SceneRuntimeNode node in physics.OrderedPhysicsNodes)
            {
                if (!physics.Bodies.TryGetValue(node.Source.Id, out var body))
                    continue;

                PhysicsBody? config = node.Source.Physics;
                if (config == null || !config.Enabled)
                    continue;

                if (NormalizeMotionType(config.MotionType) != MotionType.Dynamic)
                    continue;

                SyncBodyPoseToScene(physics, node, body);
            }
        }

        private static void MarkSubtreeDirtyFromPhysics(Scene.SceneRuntimeData runtime, Scene.SceneRuntimeNode root)
        {
            var stack = new Stack<Scene.SceneRuntimeNode>();
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

        private static void ApplyWorldPoseToNode(Scene.SceneRuntimeNode node, Double3 worldPosition, DQuaternion worldRotation)
        {
            if (node.Parent == null)
            {
                node.LocalPosition = worldPosition;
                node.LocalRotation = worldRotation;
            }
            else
            {
                SceneWorldState parentWorld = ResolveWorld(node.Parent);

                Double3 deltaWorld = worldPosition - parentWorld.Position;
                Double3 unrotated = parentWorld.Rotation.Inverse().Rotate(deltaWorld);

                node.LocalPosition = Double3.Divide(unrotated, parentWorld.Scale);
                node.LocalRotation = (parentWorld.Rotation.Inverse() * worldRotation).Normalized();
            }

            node.World = ResolveWorld(node);
            node.Dirty = true;

            if (node.Source.Transform == null)
                node.Source.Transform = new SceneTransform();

            node.Source.Transform.LocalPosition = node.LocalPosition;
            node.Source.Transform.LocalRotation = node.LocalRotation.ToEulerDegrees();
            node.Source.Transform.LocalScale = node.LocalScale;
        }

        private static SceneWorldState ResolveWorld(Scene.SceneRuntimeNode node)
        {
            if (node.Parent == null)
            {
                return new SceneWorldState(
                    node.LocalPosition,
                    node.LocalRotation,
                    node.LocalScale
                );
            }

            SceneWorldState parent = ResolveWorld(node.Parent);

            Double3 scaledLocalPos = Double3.Multiply(node.LocalPosition, parent.Scale);
            Double3 rotatedLocalPos = parent.Rotation.Rotate(scaledLocalPos);

            Double3 worldPos = parent.Position + rotatedLocalPos;
            DQuaternion worldRot = (parent.Rotation * node.LocalRotation).Normalized();
            Double3 worldScale = Double3.Multiply(parent.Scale, node.LocalScale);

            return new SceneWorldState(worldPos, worldRot, worldScale);
        }

        private static MotionType NormalizeMotionType(string? motionType)
        {
            return motionType switch
            {
                "Static" => MotionType.Static,
                "Dynamic" => MotionType.Dynamic,
                "Kinematic" => MotionType.Kinematic,
                _ => throw new InvalidOperationException($"[X] Unsupported physics motionType '{motionType}'.")
            };
        }

        private static string NormalizeShapeType(string? shapeType)
        {
            return shapeType switch
            {
                "Box" => "Box",
                "Sphere" => "Sphere",
                "Capsule" => "Capsule",
                "Mesh" => "Mesh",
                _ => throw new InvalidOperationException($"[X] Unsupported physics shapeType '{shapeType}'.")
            };
        }

        private static JVector ToJVector(Double3 value)
        {
            return new JVector(value.X, value.Y, value.Z);
        }

        private static Double3 ToDouble3(JVector value)
        {
            return new Double3(value.X, value.Y, value.Z);
        }

        private static JQuaternion ToJQuaternion(DQuaternion value)
        {
            return new JQuaternion(value.X, value.Y, value.Z, value.W);
        }

        private static DQuaternion ToDQuaternion(JQuaternion value)
        {
            return new DQuaternion(value.X, value.Y, value.Z, value.W).Normalized();
        }
    }
}
