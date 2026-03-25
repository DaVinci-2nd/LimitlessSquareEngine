using Jitter2;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine
{
    internal class Physics
    {
        private sealed class ScenePhysicsRuntime : IDisposable
        {
            public string SceneId { get; init; } = "";
            public Scene.SceneRuntimeData Runtime { get; init; } = null!;
            public World World { get; init; } = null!;
            public Dictionary<string, RigidBody> Bodies { get; } = new(StringComparer.Ordinal);
            public List<Scene.SceneRuntimeNode> OrderedPhysicsNodes { get; } = new();

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
            physics.World.ThreadModel = World.ThreadModelType.Regular;

            foreach (var node in runtime.Nodes.Values.OrderBy(n => n.Depth))
            {
                PhysicsBody? config = node.Source.Physics;
                if (config == null || !config.Enabled)
                    continue;

                RigidBody body = CreateRigidBody(physics.World, node, config);

                physics.Bodies[node.Source.Id] = body;
                physics.OrderedPhysicsNodes.Add(node);
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

        private static int _dynamicBodiesActivated = 0;

        public static void Step(double deltaTime)
        {
            if (deltaTime <= 0.0)
                return;

            foreach (var pair in _sceneWorlds)
            {
                ScenePhysicsRuntime physics = pair.Value;

                if (physics.OrderedPhysicsNodes.Count == 0)
                    continue;

                PushDirtySceneTransformsToPhysics(physics);

                foreach (Scene.SceneRuntimeNode node in physics.OrderedPhysicsNodes)
                {
                    if (!physics.Bodies.TryGetValue(node.Source.Id, out var body))
                        continue;
                }

                if (_dynamicBodiesActivated < 2)
                {
                    var testBody = physics.Bodies.Values.First(b => b.MotionType == MotionType.Dynamic);
                    foreach (var body in physics.Bodies.Values.Where(b => b.MotionType == MotionType.Dynamic))
                    {
                        body.SetActivationState(true);
                        body.Velocity = JVector.Zero;
                    }
                    _dynamicBodiesActivated++;

                    if (_dynamicBodiesActivated < 2)
                        continue;
                }

                physics.World.Step(deltaTime, true);
                PullPhysicsTransformsToScene(physics);
            }
        }

        private static RigidBody CreateRigidBody(World world, Scene.SceneRuntimeNode node, PhysicsBody config)
        {
            SceneWorldState initialWorld = ResolveWorld(node);

            RigidBody body = world.CreateRigidBody();

            body.Position = ToJVector(initialWorld.Position);
            body.Orientation = ToJQuaternion(initialWorld.Rotation);

            var shape = CreateShape(config);
            body.AddShape(shape);

            body.SetMassInertia(config.Mass);

            body.Friction = config.Friction;
            body.Restitution = config.Restitution;
            body.EnableSpeculativeContacts = config.EnableSpeculativeContacts;
            body.Damping = (config.LinearDamping, config.AngularDamping);

            switch (NormalizeMotionType(config.MotionType))
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
                    break;

                default:
                    throw new InvalidOperationException($"[X] Unsupported physics motion type '{config.MotionType}'.");
            }
            return body;
        }

        private static RigidBodyShape CreateShape(PhysicsBody config)
        {
            return NormalizeShapeType(config.ShapeType) switch
            {
                "Box" => new BoxShape(ToJVector(config.Size)),
                "Sphere" => new SphereShape(config.Radius),
                "Capsule" => new CapsuleShape(config.Radius, config.Length),
                _ => throw new InvalidOperationException($"[X] Unsupported physics shape type '{config.ShapeType}'.")
            };
        }

        private static void PushDirtySceneTransformsToPhysics(ScenePhysicsRuntime physics)
        {
            if (physics.Runtime.DirtyNodes.Count == 0)
                return;

            List<string> processedNodes = new List<string>();

            foreach (Scene.SceneRuntimeNode node in physics.OrderedPhysicsNodes)
            {
                if (!physics.Runtime.DirtyNodes.Contains(node.Source.Id))
                    continue;

                if (!physics.Bodies.TryGetValue(node.Source.Id, out var body))
                    continue;

                PhysicsBody? config = node.Source.Physics;
                if (config == null || !config.Enabled)
                    continue;

                MotionType motionType = NormalizeMotionType(config.MotionType);

                processedNodes.Add(node.Source.Id);

                if (motionType == MotionType.Dynamic)
                    continue;

                SceneWorldState worldState = ResolveWorld(node);

                body.Position = ToJVector(worldState.Position);
                body.Orientation = ToJQuaternion(worldState.Rotation);
            }
            foreach (string id in processedNodes)
            {
                physics.Runtime.DirtyNodes.Remove(id);
            }
        }


        private static void PullPhysicsTransformsToScene(ScenePhysicsRuntime physics)
        {
            foreach (Scene.SceneRuntimeNode node in physics.OrderedPhysicsNodes)
            {
                if (!physics.Bodies.TryGetValue(node.Source.Id, out var body))
                    continue;

                Double3 worldPosition = ToDouble3(body.Position);
                DQuaternion worldRotation = ToDQuaternion(body.Orientation);

                ApplyWorldPoseToNode(node, worldPosition, worldRotation);
                MarkSubtreeDirtyFromPhysics(physics.Runtime, node);
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
