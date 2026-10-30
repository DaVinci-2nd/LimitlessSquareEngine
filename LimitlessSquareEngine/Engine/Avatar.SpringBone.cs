using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    public sealed class AvatarSpringCollider
    {
        public int BoneIndex { get; set; } = -1;

        public string Shape { get; set; } = "sphere";

        public Double3 Offset { get; set; } = Double3.Zero;

        public double Radius { get; set; }

        public Double3 Tail { get; set; } = Double3.Zero;
    }

    public sealed class AvatarSpringColliderGroup
    {
        public string Name { get; set; } = "";

        public List<int> Colliders { get; } = new();
    }

    public sealed class AvatarSpringJoint
    {
        public int BoneIndex { get; set; } = -1;

        public double HitRadius { get; set; }

        public double Stiffness { get; set; } = 1.0;

        public double GravityPower { get; set; }

        public Double3 GravityDir { get; set; } = new Double3(0.0, -1.0, 0.0);

        public double DragForce { get; set; } = 0.4;
    }

    public sealed class AvatarSpringChain
    {
        public string Name { get; set; } = "";

        public List<AvatarSpringJoint> Joints { get; } = new();

        public List<int> ColliderGroups { get; } = new();

        public int CenterBoneIndex { get; set; } = -1;
    }

    public sealed class AvatarSpringBone
    {
        public List<AvatarSpringCollider> Colliders { get; } = new();

        public List<AvatarSpringColliderGroup> ColliderGroups { get; } = new();

        public List<AvatarSpringChain> Chains { get; } = new();
    }
}
