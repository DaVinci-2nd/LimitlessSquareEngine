using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    // 世界轴对齐体素块键
    public readonly struct VoxelBlockKey : IEquatable<VoxelBlockKey>
    {
        public readonly int BX;
        public readonly int BY;
        public readonly int BZ;

        public VoxelBlockKey(int bx, int by, int bz)
        {
            BX = bx;
            BY = by;
            BZ = bz;
        }

        public bool Equals(VoxelBlockKey other)
        {
            return BX == other.BX && BY == other.BY && BZ == other.BZ;
        }

        public override bool Equals(object? obj)
        {
            return obj is VoxelBlockKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + BX;
                hash = hash * 31 + BY;
                hash = hash * 31 + BZ;
                return hash;
            }
        }

        public override string ToString()
        {
            return $"V({BX},{BY},{BZ})";
        }
    }

    // 单个体素块的符号距离场
    public sealed class VoxelBlock
    {
        public const int CellSize = 32;
        public const int SampleSize = 35;
        public const int SampleOrigin = -1;
        public const double VoxelSize = 1.0;

        private const int HeightGridSize = 10;
        private const int HeightGridStep = 4;

        private static readonly Noise3D _voxelNoise = new Noise3D(0x5EED3Du);

        public readonly VoxelBlockKey Key;
        public readonly Double3 Origin;
        public readonly float[] Sdf = new float[SampleSize * SampleSize * SampleSize];

        public TileArtifactState State = TileArtifactState.None;
        public double LastAccessTime;

        public VoxelBlock(VoxelBlockKey key, Double3 origin)
        {
            Key = key;
            Origin = origin;
        }

        public Double3 BlockPoint(int i, int j, int k)
        {
            return new Double3(
                Origin.X + i * VoxelSize,
                Origin.Y + j * VoxelSize,
                Origin.Z + k * VoxelSize);
        }

        public void Sample(IHeightSource source, double planetRadius, IReadOnlyList<VoxelEditBrush> brushes)
        {
            var heights = new float[HeightGridSize * HeightGridSize * HeightGridSize];

            for (int gk = 0; gk < HeightGridSize; gk++)
            {
                for (int gj = 0; gj < HeightGridSize; gj++)
                {
                    for (int gi = 0; gi < HeightGridSize; gi++)
                    {
                        Double3 p = BlockPoint(
                            gi * HeightGridStep + SampleOrigin,
                            gj * HeightGridStep + SampleOrigin,
                            gk * HeightGridStep + SampleOrigin);
                        double len = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
                        Double3 dir = len <= 1e-300
                            ? new Double3(0.0, 0.0, 1.0)
                            : new Double3(p.X / len, p.Y / len, p.Z / len);
                        heights[(gk * HeightGridSize + gj) * HeightGridSize + gi] =
                            (float)source.SampleDirection(dir);
                    }
                }
            }

            for (int k = 0; k < SampleSize; k++)
            {
                for (int j = 0; j < SampleSize; j++)
                {
                    for (int i = 0; i < SampleSize; i++)
                    {
                        Double3 p = BlockPoint(i + SampleOrigin, j + SampleOrigin, k + SampleOrigin);
                        double len = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
                        double sdf = len - (planetRadius + SampleHeightGrid(heights, i, j, k));

                        // 体素噪声测试
                        sdf -= _voxelNoise.Fbm(p.X * 0.08, p.Y * 0.08, p.Z * 0.08, 3) * 6.0;
                        sdf -= _voxelNoise.Fbm(p.X * 0.35, p.Y * 0.35, p.Z * 0.35, 2) * 1.2;

                        for (int b = 0; b < brushes.Count; b++)
                            sdf -= brushes[b].SampleDelta(p);

                        Sdf[(k * SampleSize + j) * SampleSize + i] = (float)sdf;
                    }
                }
            }
        }

        private static double SampleHeightGrid(float[] heights, int i, int j, int k)
        {
            double gx = i / (double)HeightGridStep;
            double gy = j / (double)HeightGridStep;
            double gz = k / (double)HeightGridStep;

            int x0 = Math.Clamp((int)gx, 0, HeightGridSize - 1);
            int y0 = Math.Clamp((int)gy, 0, HeightGridSize - 1);
            int z0 = Math.Clamp((int)gz, 0, HeightGridSize - 1);
            int x1 = Math.Min(x0 + 1, HeightGridSize - 1);
            int y1 = Math.Min(y0 + 1, HeightGridSize - 1);
            int z1 = Math.Min(z0 + 1, HeightGridSize - 1);

            double fx = Math.Clamp(gx - x0, 0.0, 1.0);
            double fy = Math.Clamp(gy - y0, 0.0, 1.0);
            double fz = Math.Clamp(gz - z0, 0.0, 1.0);

            double c000 = heights[(z0 * HeightGridSize + y0) * HeightGridSize + x0];
            double c100 = heights[(z0 * HeightGridSize + y0) * HeightGridSize + x1];
            double c010 = heights[(z0 * HeightGridSize + y1) * HeightGridSize + x0];
            double c110 = heights[(z0 * HeightGridSize + y1) * HeightGridSize + x1];
            double c001 = heights[(z1 * HeightGridSize + y0) * HeightGridSize + x0];
            double c101 = heights[(z1 * HeightGridSize + y0) * HeightGridSize + x1];
            double c011 = heights[(z1 * HeightGridSize + y1) * HeightGridSize + x0];
            double c111 = heights[(z1 * HeightGridSize + y1) * HeightGridSize + x1];

            double x00 = c000 + (c100 - c000) * fx;
            double x10 = c010 + (c110 - c010) * fx;
            double x01 = c001 + (c101 - c001) * fx;
            double x11 = c011 + (c111 - c011) * fx;

            double y0Value = x00 + (x10 - x00) * fy;
            double y1Value = x01 + (x11 - x01) * fy;

            return y0Value + (y1Value - y0Value) * fz;
        }
    }

    // 体素编辑笔刷
    public sealed class VoxelEditBrush
    {
        public Double3 Center { get; init; }
        public double Radius { get; init; }
        public bool Subtract { get; init; }

        public double SampleDelta(in Double3 p)
        {
            double dx = p.X - Center.X;
            double dy = p.Y - Center.Y;
            double dz = p.Z - Center.Z;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (dist >= Radius)
                return 0.0;

            double delta = Radius - dist;
            return Subtract ? -delta : delta;
        }
    }
}
