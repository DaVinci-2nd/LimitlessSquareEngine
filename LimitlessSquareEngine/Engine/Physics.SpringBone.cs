using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    internal sealed class SpringBoneJointRuntime
    {
        public int BoneIndex;

        public double HitRadius;

        public double Stiffness;

        public double GravityPower;

        public Double3 GravityDir = new Double3(0.0, -1.0, 0.0);

        public double DragForce = 0.4;

        public Double3 BoneAxis = Double3.Zero;

        public double BoneLength;

        public DQuaternion InitialLocalRotation = DQuaternion.Identity;

        public DQuaternion IntermediateRotation = DQuaternion.Identity;

        public Double3 PrevTail;

        public Double3 CurrentTail;
    }

    internal sealed class SpringBoneColliderRuntime
    {
        public int BoneIndex;

        public bool IsCapsule;

        public Double3 Offset;

        public Double3 Tail;

        public double Radius;
    }

    internal sealed class SpringBoneChainRuntime
    {
        public string Name = "";

        public int CenterBoneIndex = -1;

        public List<SpringBoneJointRuntime> Joints { get; } = new();

        public List<SpringBoneColliderRuntime> Colliders { get; } = new();
    }

    internal partial class Physics
    {
        private static int[] BuildBoneOrder(AvatarSkeleton skeleton)
        {
            int count = skeleton.Bones.Count;
            int[] order = new int[count];
            bool[] computed = new bool[count];
            int written = 0;

            while (written < count)
            {
                bool progress = false;

                for (int i = 0; i < count; i++)
                {
                    if (computed[i])
                        continue;

                    int parentIndex = skeleton.Bones[i].ParentIndex;

                    if (parentIndex >= 0 && parentIndex < count && !computed[parentIndex])
                        continue;

                    computed[i] = true;
                    order[written++] = i;
                    progress = true;
                }

                if (!progress)
                    break;
            }

            for (int i = 0; i < count; i++)
            {
                if (computed[i])
                    continue;

                computed[i] = true;
                order[written++] = i;
            }

            return order;
        }

        private static void ResolveModelTransforms(AvatarPhysicsRuntime runtime, bool useRest)
        {
            AvatarSkeleton skeleton = runtime.Avatar.Skeleton;
            int[] order = runtime.BoneOrder;
            DQuaternion[] rotations = useRest ? runtime.RestModelRotations : runtime.ModelRotations;
            Double3[] positions = useRest ? runtime.RestModelPositions : runtime.ModelPositions;

            for (int i = 0; i < order.Length; i++)
            {
                int boneIndex = order[i];
                AvatarBone bone = skeleton.Bones[boneIndex];

                DQuaternion localRotation = useRest
                    ? bone.RestRotation
                    : bone.LocalRotationQuaternion ?? DQuaternion.FromEulerDegrees(bone.LocalRotation);

                Double3 localPosition = useRest ? bone.RestPosition : bone.LocalPosition;

                int parentIndex = bone.ParentIndex;

                if (parentIndex < 0 || parentIndex >= skeleton.Bones.Count)
                {
                    rotations[boneIndex] = localRotation;
                    positions[boneIndex] = localPosition;
                    continue;
                }

                rotations[boneIndex] = (rotations[parentIndex] * localRotation).Normalized();
                positions[boneIndex] = positions[parentIndex] + rotations[parentIndex].Rotate(localPosition);
            }
        }

        private static void BuildSpringChains(AvatarPhysicsRuntime runtime)
        {
            AvatarSpringBone springBone = runtime.Avatar.SpringBone;
            AvatarSkeleton skeleton = runtime.Avatar.Skeleton;
            int boneCount = skeleton.Bones.Count;

            foreach (AvatarSpringChain chainData in springBone.Chains)
            {
                if (chainData == null || chainData.Joints.Count < 2)
                    continue;

                bool valid = true;

                foreach (AvatarSpringJoint jointData in chainData.Joints)
                {
                    if (jointData.BoneIndex < 0 || jointData.BoneIndex >= boneCount)
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                    continue;

                SpringBoneChainRuntime chain = new SpringBoneChainRuntime
                {
                    Name = chainData.Name,
                    CenterBoneIndex = chainData.CenterBoneIndex
                };

                for (int i = 0; i < chainData.Joints.Count - 1; i++)
                {
                    int boneIndex = chainData.Joints[i].BoneIndex;
                    int nextBoneIndex = chainData.Joints[i + 1].BoneIndex;

                    Double3 headPosition = runtime.RestModelPositions[boneIndex];
                    Double3 tailPosition = runtime.RestModelPositions[nextBoneIndex];

                    Double3 offset = tailPosition - headPosition;
                    double boneLength = LengthOf(offset);

                    if (boneLength <= 1e-9)
                        continue;

                    SpringBoneJointRuntime joint = new SpringBoneJointRuntime
                    {
                        BoneIndex = boneIndex,
                        HitRadius = chainData.Joints[i].HitRadius,
                        Stiffness = chainData.Joints[i].Stiffness,
                        GravityPower = chainData.Joints[i].GravityPower,
                        GravityDir = chainData.Joints[i].GravityDir,
                        DragForce = chainData.Joints[i].DragForce,
                        BoneAxis = runtime.RestModelRotations[boneIndex].Inverse().Rotate(offset / boneLength),
                        BoneLength = boneLength,
                        InitialLocalRotation = skeleton.Bones[boneIndex].RestRotation,
                        IntermediateRotation = (runtime.RestModelRotations[boneIndex].Inverse()
                            * runtime.RestModelRotations[nextBoneIndex]).Normalized(),
                        PrevTail = tailPosition,
                        CurrentTail = tailPosition
                    };

                    chain.Joints.Add(joint);
                }

                if (chain.Joints.Count == 0)
                    continue;

                foreach (int groupIndex in chainData.ColliderGroups)
                {
                    if (groupIndex < 0 || groupIndex >= springBone.ColliderGroups.Count)
                        continue;

                    AvatarSpringColliderGroup group = springBone.ColliderGroups[groupIndex];

                    foreach (int colliderIndex in group.Colliders)
                    {
                        if (colliderIndex < 0 || colliderIndex >= springBone.Colliders.Count)
                            continue;

                        AvatarSpringCollider colliderData = springBone.Colliders[colliderIndex];

                        if (colliderData.BoneIndex < 0 || colliderData.BoneIndex >= boneCount)
                            continue;

                        chain.Colliders.Add(new SpringBoneColliderRuntime
                        {
                            BoneIndex = colliderData.BoneIndex,
                            IsCapsule = string.Equals(colliderData.Shape, "capsule", StringComparison.OrdinalIgnoreCase),
                            Offset = colliderData.Offset,
                            Tail = colliderData.Tail,
                            Radius = colliderData.Radius
                        });
                    }
                }

                runtime.Chains.Add(chain);
            }
        }

        private static void StepSpringBoneChain(
            AvatarPhysicsRuntime runtime,
            SpringBoneChainRuntime chain,
            double deltaTime,
            DQuaternion avatarWorldRotation)
        {
            DQuaternion centerRotation = DQuaternion.Identity;
            Double3 centerPosition = Double3.Zero;

            if (chain.CenterBoneIndex >= 0 && chain.CenterBoneIndex < runtime.ModelRotations.Length)
            {
                centerRotation = runtime.ModelRotations[chain.CenterBoneIndex];
                centerPosition = runtime.ModelPositions[chain.CenterBoneIndex];
            }

            DQuaternion centerInverse = centerRotation.Inverse();
            DQuaternion avatarWorldInverse = avatarWorldRotation.Inverse();

            for (int i = 0; i < chain.Joints.Count; i++)
            {
                SpringBoneJointRuntime joint = chain.Joints[i];
                int boneIndex = joint.BoneIndex;

                Double3 headModel = runtime.ModelPositions[boneIndex];
                Double3 headEval = centerInverse.Rotate(headModel - centerPosition);

                int parentIndex = runtime.Avatar.Skeleton.Bones[boneIndex].ParentIndex;

                DQuaternion parentModelRotation = parentIndex >= 0 && parentIndex < runtime.ModelRotations.Length
                    ? runtime.ModelRotations[parentIndex]
                    : DQuaternion.Identity;

                DQuaternion parentEvalRotation = (centerInverse * parentModelRotation).Normalized();

                Double3 inertia = (joint.CurrentTail - joint.PrevTail) * (1.0 - joint.DragForce)
                    - runtime.WorldDeltaModel * joint.DragForce;

                Double3 stiffness = parentEvalRotation.Rotate(
                    joint.InitialLocalRotation.Rotate(joint.BoneAxis)) * (deltaTime * joint.Stiffness);

                Double3 gravityLocal = NormalizeVector(joint.GravityDir);
                Double3 gravityEval = centerInverse.Rotate(avatarWorldInverse.Rotate(gravityLocal));

                Double3 external = gravityEval * (deltaTime * joint.GravityPower);

                if (runtime.PendingImpulses.TryGetValue(boneIndex, out Double3 pendingImpulse))
                    external += pendingImpulse * deltaTime;

                Double3 nextTail = joint.CurrentTail + inertia + stiffness + external;

                nextTail = ConstrainSpringBoneLength(headEval, nextTail, joint.BoneLength);

                foreach (SpringBoneColliderRuntime collider in chain.Colliders)
                {
                    nextTail = ResolveSpringBoneCollision(
                        runtime,
                        collider,
                        centerInverse,
                        centerPosition,
                        headEval,
                        nextTail,
                        joint.BoneLength,
                        joint.HitRadius);
                }

                Double3 tailModel = centerPosition + centerRotation.Rotate(nextTail);

                DQuaternion localFrame = (parentModelRotation * joint.InitialLocalRotation).Normalized();
                Double3 localDirection = localFrame.Inverse().Rotate(tailModel - headModel);

                DQuaternion delta = FromToQuaternion(joint.BoneAxis, localDirection);
                DQuaternion newLocalRotation = (joint.InitialLocalRotation * delta).Normalized();

                joint.PrevTail = joint.CurrentTail;
                joint.CurrentTail = nextTail;

                runtime.ModelRotations[boneIndex] = (parentModelRotation * newLocalRotation).Normalized();

                if (i + 1 < chain.Joints.Count)
                {
                    int nextBoneIndex = chain.Joints[i + 1].BoneIndex;

                    runtime.ModelPositions[nextBoneIndex] = headModel
                        + runtime.ModelRotations[boneIndex].Rotate(joint.BoneAxis) * joint.BoneLength;

                    runtime.ModelRotations[nextBoneIndex] = (runtime.ModelRotations[boneIndex]
                        * joint.IntermediateRotation).Normalized();
                }

                RecordSolvedRotation(runtime, boneIndex, newLocalRotation);
            }

            runtime.PendingImpulses.Clear();
        }

        private static Double3 ResolveSpringBoneCollision(
            AvatarPhysicsRuntime runtime,
            SpringBoneColliderRuntime collider,
            DQuaternion centerInverse,
            Double3 centerPosition,
            Double3 headEval,
            Double3 tailEval,
            double boneLength,
            double jointRadius)
        {
            DQuaternion colliderRotation = runtime.ModelRotations[collider.BoneIndex];
            Double3 colliderPosition = runtime.ModelPositions[collider.BoneIndex];

            Double3 headModel = colliderPosition + colliderRotation.Rotate(collider.Offset);
            Double3 headPoint = centerInverse.Rotate(headModel - centerPosition);

            double radiusSum = collider.Radius + jointRadius;

            if (!collider.IsCapsule)
                return PushOutOfSphere(headEval, tailEval, headPoint, radiusSum, boneLength);

            Double3 tailModel = colliderPosition + colliderRotation.Rotate(collider.Tail);
            Double3 tailPoint = centerInverse.Rotate(tailModel - centerPosition);

            Double3 offsetToTail = tailPoint - headPoint;
            double offsetToTailLengthSq = offsetToTail.X * offsetToTail.X
                + offsetToTail.Y * offsetToTail.Y
                + offsetToTail.Z * offsetToTail.Z;

            if (offsetToTailLengthSq <= 1e-12)
                return PushOutOfSphere(headEval, tailEval, headPoint, radiusSum, boneLength);

            Double3 delta = tailEval - headPoint;
            double dot = offsetToTail.X * delta.X + offsetToTail.Y * delta.Y + offsetToTail.Z * delta.Z;

            if (dot > offsetToTailLengthSq)
            {
                delta -= offsetToTail;
            }
            else if (dot >= 0.0)
            {
                delta -= offsetToTail * (dot / offsetToTailLengthSq);
            }

            double distance = LengthOf(delta) - radiusSum;

            if (distance >= 0.0)
                return tailEval;

            Double3 direction = NormalizeVector(delta);

            if (LengthOf(direction) <= 1e-9)
                return tailEval;

            return ConstrainSpringBoneLength(headEval, tailEval - direction * distance, boneLength);
        }

        private static Double3 PushOutOfSphere(
            Double3 headEval,
            Double3 tailEval,
            Double3 sphereCenter,
            double radius,
            double boneLength)
        {
            Double3 delta = tailEval - sphereCenter;
            double distance = LengthOf(delta) - radius;

            if (distance >= 0.0)
                return tailEval;

            Double3 direction = NormalizeVector(delta);

            if (LengthOf(direction) <= 1e-9)
                return tailEval;

            return ConstrainSpringBoneLength(headEval, tailEval - direction * distance, boneLength);
        }

        private static Double3 ConstrainSpringBoneLength(Double3 head, Double3 tail, double boneLength)
        {
            Double3 offset = tail - head;
            double offsetLength = LengthOf(offset);

            if (offsetLength <= 1e-9)
                return head;

            return head + offset / offsetLength * boneLength;
        }

        private static DQuaternion FromToQuaternion(Double3 from, Double3 to)
        {
            Double3 a = NormalizeVector(from);
            Double3 b = NormalizeVector(to);

            if (LengthOf(a) <= 1e-9 || LengthOf(b) <= 1e-9)
                return DQuaternion.Identity;

            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z;

            if (dot >= 0.999999)
                return DQuaternion.Identity;

            if (dot <= -0.999999)
            {
                Double3 axis = new Double3(1.0, 0.0, 0.0);

                double absX = Math.Abs(a.X);
                double absY = Math.Abs(a.Y);
                double absZ = Math.Abs(a.Z);

                if (absY <= absX && absY <= absZ)
                    axis = new Double3(0.0, 1.0, 0.0);
                else if (absZ <= absX)
                    axis = new Double3(0.0, 0.0, 1.0);

                Double3 perpendicular = NormalizeVector(CrossVector(a, axis));

                if (LengthOf(perpendicular) <= 1e-9)
                    return DQuaternion.Identity;

                return DQuaternion.CreateAxisAngle(perpendicular, Math.PI);
            }

            Double3 cross = CrossVector(a, b);

            return new DQuaternion(cross.X, cross.Y, cross.Z, 1.0 + dot).Normalized();
        }

        private static void RecordSolvedRotation(AvatarPhysicsRuntime runtime, int boneIndex, DQuaternion rotation)
        {
            runtime.SolvedIndices[runtime.SolvedCount] = boneIndex;
            runtime.SolvedRotations[boneIndex] = rotation;
            runtime.SolvedCount++;
        }

        private static void WriteSolvedSpringBoneRotations(AvatarPhysicsRuntime runtime)
        {
            AvatarSkeleton skeleton = runtime.Avatar.Skeleton;

            for (int i = 0; i < runtime.SolvedCount; i++)
            {
                int boneIndex = runtime.SolvedIndices[i];

                if (boneIndex < 0 || boneIndex >= skeleton.Bones.Count)
                    continue;

                AvatarBone bone = skeleton.Bones[boneIndex];
                DQuaternion rotation = runtime.SolvedRotations[boneIndex];

                bone.LocalRotationQuaternion = rotation;
                bone.LocalRotation = rotation.ToEulerDegrees();
            }
        }

        private static double LengthOf(Double3 value)
        {
            return Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        }

        private static Double3 NormalizeVector(Double3 value)
        {
            double length = LengthOf(value);

            if (length <= 1e-12)
                return Double3.Zero;

            return value / length;
        }

        private static Double3 CrossVector(Double3 a, Double3 b)
        {
            return new Double3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }
    }
}
