using System;
using System.Collections.Generic;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.Unmanaged;

namespace LimitlessSquareEngine.Engine
{
    internal sealed class AvatarProxyRuntime
    {
        public int BoneIndex;

        public RigidBody Body = null!;
    }

    internal sealed class AvatarPhysicsRuntime
    {
        public string AvatarId = "";

        public Avatar Avatar = null!;

        public Scene.SceneRuntimeNode Node = null!;

        public int[] BoneOrder = Array.Empty<int>();

        public DQuaternion[] RestModelRotations = Array.Empty<DQuaternion>();

        public Double3[] RestModelPositions = Array.Empty<Double3>();

        public DQuaternion[] ModelRotations = Array.Empty<DQuaternion>();

        public Double3[] ModelPositions = Array.Empty<Double3>();

        public DQuaternion[] SolvedRotations = Array.Empty<DQuaternion>();

        public int[] SolvedIndices = Array.Empty<int>();

        public int SolvedCount;

        public List<AvatarProxyRuntime> Proxies { get; } = new();

        public Dictionary<JHandle<RigidBodyData>, AvatarProxyRuntime> ProxyByBody { get; } = new();

        public List<SpringBoneChainRuntime> Chains { get; } = new();

        public Dictionary<int, Double3> PendingImpulses { get; } = new();

        public Double3 PrevWorldPosition;

        public bool HasWorldPosition;

        public Double3 WorldDeltaModel;
    }

    internal partial class Physics
    {
        private static void DiscoverAvatars(ScenePhysicsRuntime physics)
        {
            if (physics.AvatarsDiscoveryComplete &&
                physics.AvatarsDiscoveryNodeCount == physics.Runtime.Nodes.Count)
                return;

            bool complete = true;

            foreach (Scene.SceneRuntimeNode node in physics.Runtime.Nodes.Values)
            {
                if (!string.Equals(node.Source.Type, "Avatar", StringComparison.Ordinal))
                    continue;

                string avatarId = Scene.ParseAvatarId(node.Source);

                if (string.IsNullOrWhiteSpace(avatarId))
                    continue;

                if (physics.Avatars.ContainsKey(avatarId))
                    continue;

                if (!AvatarRegistry.TryGet(avatarId, out Avatar? avatar) || avatar == null)
                {
                    complete = false;
                    continue;
                }

                physics.Avatars[avatarId] = CreateAvatarPhysicsRuntime(physics, node, avatar);
            }

            physics.AvatarsDiscoveryComplete = complete;
            physics.AvatarsDiscoveryNodeCount = physics.Runtime.Nodes.Count;
        }

        private static AvatarPhysicsRuntime CreateAvatarPhysicsRuntime(
            ScenePhysicsRuntime physics,
            Scene.SceneRuntimeNode node,
            Avatar avatar)
        {
            int boneCount = avatar.Skeleton.Bones.Count;

            AvatarPhysicsRuntime runtime = new AvatarPhysicsRuntime
            {
                AvatarId = Scene.ParseAvatarId(node.Source),
                Avatar = avatar,
                Node = node,
                BoneOrder = BuildBoneOrder(avatar.Skeleton),
                RestModelRotations = new DQuaternion[boneCount],
                RestModelPositions = new Double3[boneCount],
                ModelRotations = new DQuaternion[boneCount],
                ModelPositions = new Double3[boneCount],
                SolvedRotations = new DQuaternion[boneCount],
                SolvedIndices = new int[boneCount]
            };

            ResolveModelTransforms(runtime, true);

            Array.Copy(runtime.RestModelRotations, runtime.ModelRotations, boneCount);
            Array.Copy(runtime.RestModelPositions, runtime.ModelPositions, boneCount);

            runtime.PrevWorldPosition = ResolveWorld(node).Position;
            runtime.HasWorldPosition = true;

            BuildSpringChains(runtime);
            BuildAvatarCollisionBodies(physics, runtime);

            Console.WriteLine(
                $"[i] Avatar physics '{runtime.AvatarId}': {runtime.Chains.Count} spring chains "
                + $"{runtime.Proxies.Count} collision bodies");

            return runtime;
        }

        private static void BuildAvatarCollisionBodies(ScenePhysicsRuntime physics, AvatarPhysicsRuntime runtime)
        {
            Avatar avatar = runtime.Avatar;
            int boneCount = avatar.Skeleton.Bones.Count;

            if (boneCount == 0)
                return;

            SceneWorldState nodeWorld = ResolveWorld(runtime.Node);

            foreach (AvatarSpringCollider collider in avatar.SpringBone.Colliders)
            {
                if (collider.BoneIndex < 0 || collider.BoneIndex >= boneCount)
                    continue;

                if (!(collider.Radius > 0.0))
                    continue;

                RigidBody body = physics.World.CreateRigidBody();
                body.Tag = runtime.AvatarId + "::" + avatar.Skeleton.Bones[collider.BoneIndex].Name;
                body.MotionType = MotionType.Kinematic;
                body.AffectedByGravity = false;

                if (string.Equals(collider.Shape, "capsule", StringComparison.OrdinalIgnoreCase))
                {
                    double length = LengthOf(collider.Tail - collider.Offset);
                    body.AddShape(new CapsuleShape(collider.Radius, Math.Max(0.0, length)));
                }
                else
                {
                    body.AddShape(new SphereShape(collider.Radius));
                }

                body.Position = ToJVector(nodeWorld.Position);
                body.Orientation = ToJQuaternion(nodeWorld.Rotation);

                AvatarProxyRuntime proxy = new AvatarProxyRuntime
                {
                    BoneIndex = collider.BoneIndex,
                    Body = body
                };

                runtime.Proxies.Add(proxy);
                runtime.ProxyByBody[body.Handle] = proxy;
            }
        }

        private static void StepAvatarPhysics(ScenePhysicsRuntime physics, double deltaTime)
        {
            if (physics.Avatars.Count == 0)
                return;

            foreach (AvatarPhysicsRuntime runtime in physics.Avatars.Values)
            {
                SceneWorldState nodeWorld = ResolveWorld(runtime.Node);

                Double3 worldDelta = Double3.Zero;

                if (runtime.HasWorldPosition)
                    worldDelta = nodeWorld.Position - runtime.PrevWorldPosition;

                Double3 unrotated = nodeWorld.Rotation.Inverse().Rotate(worldDelta);

                runtime.WorldDeltaModel = new Double3(unrotated.X, unrotated.Y, -unrotated.Z);
                runtime.PrevWorldPosition = nodeWorld.Position;
                runtime.HasWorldPosition = true;

                ResolveModelTransforms(runtime, false);
                SyncAvatarProxies(runtime, nodeWorld);

                if (runtime.Chains.Count == 0)
                    continue;

                runtime.SolvedCount = 0;

                foreach (SpringBoneChainRuntime chain in runtime.Chains)
                    StepSpringBoneChain(runtime, chain, deltaTime, nodeWorld.Rotation);

                WriteSolvedSpringBoneRotations(runtime);
            }
        }

        private static void SyncAvatarProxies(AvatarPhysicsRuntime runtime, SceneWorldState nodeWorld)
        {
            if (runtime.Proxies.Count == 0)
                return;

            foreach (AvatarProxyRuntime proxy in runtime.Proxies)
            {
                int boneIndex = proxy.BoneIndex;

                if (boneIndex < 0 || boneIndex >= runtime.ModelRotations.Length)
                    continue;

                Double3 modelPosition = runtime.ModelPositions[boneIndex];

                Double3 scaledModel = new Double3(
                    modelPosition.X * nodeWorld.Scale.X,
                    modelPosition.Y * nodeWorld.Scale.Y,
                    modelPosition.Z * nodeWorld.Scale.Z);

                DQuaternion worldRotation = (nodeWorld.Rotation * runtime.ModelRotations[boneIndex]).Normalized();
                Double3 worldPosition = nodeWorld.Position + nodeWorld.Rotation.Rotate(scaledModel);

                proxy.Body.Position = ToJVector(worldPosition);
                proxy.Body.Orientation = ToJQuaternion(worldRotation);
            }
        }

        private static void CollectAvatarContacts(ScenePhysicsRuntime physics)
        {
            if (physics.Avatars.Count == 0)
                return;

            bool hasProxy = false;

            foreach (AvatarPhysicsRuntime runtime in physics.Avatars.Values)
            {
                if (runtime.ProxyByBody.Count > 0)
                {
                    hasProxy = true;
                    break;
                }
            }

            if (!hasProxy)
                return;

            var contacts = physics.World.RawData.Contacts;

            for (int i = 0; i < contacts.Length; i++)
            {
                double impulse = contacts[i].Contact0.Impulse;

                if (Math.Abs(impulse) <= 1e-12)
                    continue;

                JHandle<RigidBodyData> handle1 = contacts[i].Body1;
                JHandle<RigidBodyData> handle2 = contacts[i].Body2;

                if (handle1.IsZero && handle2.IsZero)
                    continue;

                Double3 normal = ToDouble3(contacts[i].Contact0.Normal);

                if (!handle1.IsZero &&
                    TryFindAvatarProxy(physics, handle1, out AvatarPhysicsRuntime? runtime1, out AvatarProxyRuntime? proxy1))
                {
                    AccumulateAvatarImpulse(runtime1!, proxy1!.BoneIndex, normal * -impulse);
                }

                if (!handle2.IsZero &&
                    TryFindAvatarProxy(physics, handle2, out AvatarPhysicsRuntime? runtime2, out AvatarProxyRuntime? proxy2))
                {
                    AccumulateAvatarImpulse(runtime2!, proxy2!.BoneIndex, normal * impulse);
                }
            }
        }

        private static bool TryFindAvatarProxy(
            ScenePhysicsRuntime physics,
            JHandle<RigidBodyData> handle,
            out AvatarPhysicsRuntime? runtime,
            out AvatarProxyRuntime? proxy)
        {
            foreach (AvatarPhysicsRuntime candidate in physics.Avatars.Values)
            {
                if (candidate.ProxyByBody.TryGetValue(handle, out AvatarProxyRuntime? found))
                {
                    runtime = candidate;
                    proxy = found;
                    return true;
                }
            }

            runtime = null;
            proxy = null;
            return false;
        }

        private static void AccumulateAvatarImpulse(
            AvatarPhysicsRuntime runtime,
            int boneIndex,
            Double3 impulse)
        {
            if (runtime.PendingImpulses.TryGetValue(boneIndex, out Double3 existing))
                runtime.PendingImpulses[boneIndex] = existing + impulse;
            else
                runtime.PendingImpulses[boneIndex] = impulse;
        }

        private static void RemoveAvatarPhysics(ScenePhysicsRuntime physics, AvatarPhysicsRuntime runtime)
        {
            foreach (AvatarProxyRuntime proxy in runtime.Proxies)
                physics.World.Remove(proxy.Body);

            runtime.Proxies.Clear();
            runtime.ProxyByBody.Clear();
            runtime.Chains.Clear();
            runtime.PendingImpulses.Clear();
        }

        private static void ClearAvatarPhysics(ScenePhysicsRuntime physics)
        {
            foreach (AvatarPhysicsRuntime runtime in physics.Avatars.Values)
                RemoveAvatarPhysics(physics, runtime);

            physics.Avatars.Clear();
        }
    }
}
