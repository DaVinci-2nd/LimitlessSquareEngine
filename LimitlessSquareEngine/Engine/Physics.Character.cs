using System;
using System.Collections.Generic;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

namespace LimitlessSquareEngine.Engine
{
    internal sealed class CharacterControllerRuntime
    {
        public PhysicsBody Config = null!;

        public RigidBody Body = null!;

        public Double3 TargetVelocity;

        public Double3 AppliedVelocity;

        public bool Grounded;

        public bool Blocked;

        public double KnockbackRemaining;

        public DQuaternion LockedOrientation = DQuaternion.Identity;
    }

    internal partial class Physics
    {
        private static CharacterControllerRuntime BuildCharacterRuntime(PhysicsBody config, RigidBody body)
        {
            CharacterControllerRuntime runtime = new CharacterControllerRuntime
            {
                Config = config,
                Body = body
            };

            runtime.AppliedVelocity = ToDouble3(body.Velocity);
            runtime.LockedOrientation = ToDQuaternion(body.Orientation);

            return runtime;
        }

        private static void ClearCharacterControllers(ScenePhysicsRuntime physics)
        {
            physics.Characters.Clear();
        }

        private sealed class CharacterBroadPhaseFilter : IBroadPhaseFilter
        {
            private readonly ScenePhysicsRuntime _physics;

            public CharacterBroadPhaseFilter(ScenePhysicsRuntime physics)
            {
                _physics = physics;
            }

            public bool Filter(IDynamicTreeProxy proxyA, IDynamicTreeProxy proxyB)
            {
                RigidBody? bodyA = (proxyA as RigidBodyShape)?.RigidBody;
                RigidBody? bodyB = (proxyB as RigidBodyShape)?.RigidBody;

                if (bodyA == null || bodyB == null)
                    return true;

                if (!(bodyA.Tag is string tagA) || !(bodyB.Tag is string tagB))
                    return true;

                return !ShouldIgnore(tagA, tagB) && !ShouldIgnore(tagB, tagA);
            }

            private bool ShouldIgnore(string ownerTag, string otherTag)
            {
                if (!_physics.ColliderIgnores.TryGetValue(ownerTag, out List<string>? patterns))
                    return false;

                foreach (string pattern in patterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern))
                        continue;

                    if (pattern.EndsWith("*", StringComparison.Ordinal))
                    {
                        if (otherTag.StartsWith(pattern[..^1], StringComparison.Ordinal))
                            return true;

                        continue;
                    }

                    if (string.Equals(pattern, otherTag, StringComparison.Ordinal))
                        return true;
                }

                return false;
            }
        }

        private static bool TryGetCharacter(
            string? sceneId,
            string? objectId,
            out CharacterControllerRuntime runtime)
        {
            runtime = null!;

            if (!TryGetScenePhysicsRuntime(sceneId, out ScenePhysicsRuntime physics))
                return false;

            objectId = objectId?.Trim();

            if (string.IsNullOrWhiteSpace(objectId))
            {
                Console.WriteLine($"[!] Character skipped: objectId is null or empty. scene='{sceneId}'");
                return false;
            }

            if (!physics.Characters.TryGetValue(objectId, out runtime!))
            {
                Console.WriteLine($"[!] Character skipped: object '{objectId}' is not a character controller in scene '{sceneId}'.");
                return false;
            }

            return true;
        }

        public static void SetCharacterMove(string sceneId, string objectId, Double3 direction, double speed)
        {
            if (!TryGetCharacter(sceneId, objectId, out CharacterControllerRuntime runtime))
                return;

            if (!TryNormalizeDirection(direction, out Double3 normalizedDirection))
            {
                runtime.TargetVelocity = Double3.Zero;
                return;
            }

            runtime.TargetVelocity = normalizedDirection * speed;
        }

        public static void SetCharacterVelocity(string sceneId, string objectId, Double3 velocity)
        {
            if (!TryGetCharacter(sceneId, objectId, out CharacterControllerRuntime runtime))
                return;

            runtime.TargetVelocity = velocity;
        }

        public static Double3 GetCharacterVelocity(string sceneId, string objectId)
        {
            if (!TryGetCharacter(sceneId, objectId, out CharacterControllerRuntime runtime))
                return Double3.Zero;

            return ToDouble3(runtime.Body.Velocity);
        }

        public static bool GetCharacterGrounded(string sceneId, string objectId)
        {
            if (!TryGetCharacter(sceneId, objectId, out CharacterControllerRuntime runtime))
                return false;

            return runtime.Grounded;
        }

        public static bool GetCharacterBlocked(string sceneId, string objectId)
        {
            if (!TryGetCharacter(sceneId, objectId, out CharacterControllerRuntime runtime))
                return false;

            return runtime.Blocked;
        }
    }
}
