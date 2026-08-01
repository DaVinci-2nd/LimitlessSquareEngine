using System;

namespace LimitlessSquareEngine.Engine.Terrain
{
    // 渲染流与物理流的LOD参数配置
    public sealed class TerrainProfile
    {
        // 渲染流参数
        public int RenderBaseLevel { get; set; } = 6;
        public double RenderBaseRadius { get; set; } = 200_000.0;
        public int RenderMaxLevel { get; set; } = 9;
        public double RenderStreamRadius { get; set; } = 500_000.0;
        public double RenderBudgetMilliseconds { get; set; } = 6.0;
        public int RenderBaseTileResolution { get; set; } = 33;
        public int RenderVoxelGridSize { get; set; } = 33;
        public int RenderVoxelShellThickness { get; set; } = 32;
        public int AlwaysResidentLevel { get; set; } = 2;

        // 物理流参数
        public int PhysicsBaseLevel { get; set; } = 10;
        public double PhysicsBaseRadius { get; set; } = 4_000.0;
        public int PhysicsMaxLevel { get; set; } = 16;
        public double PhysicsStreamRadius { get; set; } = 8_000.0;
        public double PhysicsBudgetMilliseconds { get; set; } = 2.0;
        public int PhysicsVoxelGridSize { get; set; } = 17;
        public int PhysicsVoxelShellThickness { get; set; } = 16;

        // 按距离返回LOD级别
        public int LodForDistance(double baseLevel, double baseRadius, int maxLevel, double distance)
        {
            double d = Math.Max(distance, 1e-9);
            double ratio = baseRadius / d;
            int level = (int)Math.Floor(Math.Log2(ratio) + baseLevel);
            return Math.Clamp(level, 0, maxLevel);
        }

        // 按距离返回渲染LOD级别
        public int RenderLodForDistance(double distance)
            => LodForDistance(RenderBaseLevel, RenderBaseRadius, RenderMaxLevel, distance);

        // 按距离返回物理LOD级别
        public int PhysicsLodForDistance(double distance)
            => LodForDistance(PhysicsBaseLevel, PhysicsBaseRadius, PhysicsMaxLevel, distance);

        // 返回渲染级别对应的最大距离
        public double RenderMaxDistanceForLevel(int level)
            => MaxDistanceForLevel(RenderBaseLevel, RenderBaseRadius, level);

        // 返回物理级别对应的最大距离
        public double PhysicsMaxDistanceForLevel(int level)
            => MaxDistanceForLevel(PhysicsBaseLevel, PhysicsBaseRadius, level);

        private static double MaxDistanceForLevel(double baseLevel, double baseRadius, int level)
        {
            return baseRadius / Math.Pow(2.0, level - baseLevel);
        }
    }
}
