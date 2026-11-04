using System;

namespace LimitlessSquareEngine.Engine
{
    // 渲染流与物理流的LOD参数配置
    public sealed class TerrainProfile
    {
        // 渲染流参数
        public int RenderBaseLevel { get; set; } = 6;
        public double RenderBaseRadius { get; set; } = 200_000.0;
        public int RenderMaxLevel { get; set; } = 17;
        public double RenderFinestTileSize { get; set; } = 100.0;
        public double RenderFinestDistance { get; set; } = 256.0;
        public double RenderStreamRadius { get; set; } = 500_000.0;
        public double RenderStreamRadiusScale { get; set; } = 500_000.0 / 6_371_000.0;
        public double RenderBudgetMilliseconds { get; set; } = 6.0;
        public int RenderBaseTileResolution { get; set; } = 17;
        public int RenderVoxelGridSize { get; set; } = 33;
        public int RenderVoxelShellThickness { get; set; } = 32;
        public double VoxelCoverRadius { get; set; } = 128.0;
        public int AlwaysResidentLevel { get; set; } = 2;

        // 物理流参数
        public int PhysicsBaseLevel { get; set; } = 10;
        public double PhysicsBaseRadius { get; set; } = 4_000.0;
        public int PhysicsMaxLevel { get; set; } = 16;
        public double PhysicsFinestTileSize { get; set; } = 16.0;
        public double PhysicsFinestDistance { get; set; } = 128.0;
        public double PhysicsStreamRadius { get; set; } = 8_000.0;
        public double PhysicsStreamRadiusScale { get; set; } = 8_000.0 / 6_371_000.0;
        public double PhysicsBudgetMilliseconds { get; set; } = 2.0;
        public int PhysicsVoxelGridSize { get; set; } = 17;
        public int PhysicsVoxelShellThickness { get; set; } = 16;

        // 按距离返回LOD级别
        public int LodForDistance(double baseLevel, double baseRadius, int maxLevel, double distance)
        {
            double d = Math.Max(distance, 1e-9);

            if (d <= RenderFinestDistance)
                return maxLevel;

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

        public void ApplyFinest(double planetRadius)
        {
            double renderLevel = FinestLevel(planetRadius, RenderFinestTileSize, RenderBaseLevel);
            if (renderLevel > 0.0)
                RenderMaxLevel = (int)renderLevel;

            double physicsLevel = FinestLevel(planetRadius, PhysicsFinestTileSize, PhysicsBaseLevel);
            if (physicsLevel > 0.0)
                PhysicsMaxLevel = (int)physicsLevel;

            RenderStreamRadius = planetRadius * RenderStreamRadiusScale;
            PhysicsStreamRadius = planetRadius * PhysicsStreamRadiusScale;
        }

        private static double FinestLevel(double planetRadius, double tileSize, int baseLevel)
        {
            if (!(planetRadius > 0.0) || !(tileSize > 0.0))
                return 0.0;

            return Math.Max(baseLevel, Math.Ceiling(Math.Log2(2.0 * planetRadius / tileSize)));
        }

        public double LevelPerAxis(double planetRadius)
        {
            if (!(planetRadius > 0.0) || !(RenderFinestTileSize > 0.0))
                return 1.0;

            double target = 2.0 * planetRadius / RenderFinestTileSize;
            double exponent = Math.Ceiling(Math.Log2(Math.Max(1.0, target)));

            return Math.Pow(2.0, exponent);
        }

        public int RenderFinestLevel(double planetRadius)
        {
            if (!(planetRadius > 0.0) || !(RenderFinestTileSize > 0.0))
                return 0;

            double target = 2.0 * planetRadius / RenderFinestTileSize;
            return Math.Max(0, (int)Math.Ceiling(Math.Log2(Math.Max(1.0, target))));
        }
    }
}
