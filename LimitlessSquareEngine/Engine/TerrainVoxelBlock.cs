using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    // 球面体素块键
    public readonly struct VoxelBlockKey : IEquatable<VoxelBlockKey>
    {
        public readonly int Face;
        public readonly int Level;
        public readonly int LX;
        public readonly int LY;
        public readonly int Shell;

        public VoxelBlockKey(int face, int level, int lx, int ly, int shell)
        {
            Face = face;
            Level = level;
            LX = lx;
            LY = ly;
            Shell = shell;
        }

        public bool Equals(VoxelBlockKey other)
        {
            return Face == other.Face && Level == other.Level && LX == other.LX &&
                LY == other.LY && Shell == other.Shell;
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
                hash = hash * 31 + Face;
                hash = hash * 31 + Level;
                hash = hash * 31 + LX;
                hash = hash * 31 + LY;
                hash = hash * 31 + Shell;
                return hash;
            }
        }

        public override string ToString()
        {
            return $"V({Face},{Level},{LX},{LY},{Shell})";
        }
    }

    // 单个体素块的符号距离场
    public sealed class VoxelBlock
    {
        public static int CellSize = 16;
        public static int SampleSize = 19;
        public const int SampleOrigin = -1;
        public static double VoxelSize = 1.0;
        public static double ShellHeight => CellSize * VoxelSize;

        public static void Configure(int resolution, double planetRadius, int level)
        {
            CellSize = Math.Max(2, resolution - 1);
            SampleSize = CellSize + 3;

            double tileSize = 2.0 * planetRadius / Math.Max(1.0, QuadSphere.TilesPerAxis(level));
            VoxelSize = tileSize / CellSize;
        }

        private const int HeightGridSize = 17;
        private const int HeightGridStep = 4;

        public readonly VoxelBlockKey Key;
        public readonly double U0;
        public readonly double V0;
        public readonly double U1;
        public readonly double V1;
        public readonly double Radius0;
        public readonly Double3 Origin;
        public float[] Sdf = Array.Empty<float>();

        public TileArtifactState State = TileArtifactState.None;
        public double LastAccessTime;

        public VoxelBlock(VoxelBlockKey key)
        {
            Key = key;

            QuadSphere.GetTileRange(key.Level, key.LX, key.LY, out U0, out V0, out U1, out V1);

            Radius0 = key.Shell * ShellHeight;

            Double3 centerDir = QuadSphere.FaceDir(key.Face, (U0 + U1) * 0.5, (V0 + V1) * 0.5);
            Origin = new Double3(
                centerDir.X * Radius0,
                centerDir.Y * Radius0,
                centerDir.Z * Radius0);
        }

        public Double3 BlockDir(int i, int j)
        {
            double u = U0 + (double)i / CellSize * (U1 - U0);
            double v = V0 + (double)j / CellSize * (V1 - V0);
            return QuadSphere.FaceDir(Key.Face, u, v);
        }

        public void BlockUV(in Double3 pos, out double fu, out double fv)
        {
            QuadSphere.GetFaceAxes(Key.Face, out Double3 normal, out Double3 right, out Double3 up);

            double dn = pos.X * normal.X + pos.Y * normal.Y + pos.Z * normal.Z;

            if (Math.Abs(dn) <= 1e-15)
                dn = 1e-15;

            double u = (pos.X * right.X + pos.Y * right.Y + pos.Z * right.Z) / dn;
            double v = (pos.X * up.X + pos.Y * up.Y + pos.Z * up.Z) / dn;

            fu = (u - U0) / Math.Max(1e-300, U1 - U0);
            fv = (v - V0) / Math.Max(1e-300, V1 - V0);
        }

        public Double3 BlockPoint(int i, int j, int k)
        {
            Double3 dir = BlockDir(i, j);
            double r = Radius0 + k * VoxelSize;

            return new Double3(dir.X * r, dir.Y * r, dir.Z * r);
        }

        public void Sample(IHeightSource source, double planetRadius, IReadOnlyList<VoxelEditBrush> brushes)
        {
            if (Sdf.Length != SampleSize * SampleSize * SampleSize)
                Sdf = new float[SampleSize * SampleSize * SampleSize];

            var dirs = new Double3[SampleSize * SampleSize];

            for (int j = 0; j < SampleSize; j++)
            {
                for (int i = 0; i < SampleSize; i++)
                    dirs[j * SampleSize + i] = BlockDir(i + SampleOrigin, j + SampleOrigin);
            }

            var heights = new float[HeightGridSize * HeightGridSize];

            for (int gj = 0; gj < HeightGridSize; gj++)
            {
                for (int gi = 0; gi < HeightGridSize; gi++)
                {
                    int si = Math.Clamp(gi * HeightGridStep, 0, SampleSize - 1);
                    int sj = Math.Clamp(gj * HeightGridStep, 0, SampleSize - 1);
                    heights[gj * HeightGridSize + gi] =
                        (float)source.SampleDirection(dirs[sj * SampleSize + si]);
                }
            }

            for (int k = 0; k < SampleSize; k++)
            {
                double r = Radius0 + (k + SampleOrigin) * VoxelSize;

                for (int j = 0; j < SampleSize; j++)
                {
                    for (int i = 0; i < SampleSize; i++)
                    {
                        Double3 dir = dirs[j * SampleSize + i];
                        var p = new Double3(dir.X * r, dir.Y * r, dir.Z * r);
                        double sdf = r - (planetRadius + SampleHeightGrid(heights, i, j));

                        for (int b = 0; b < brushes.Count; b++)
                            sdf -= brushes[b].SampleDelta(p);

                        Sdf[(k * SampleSize + j) * SampleSize + i] = (float)sdf;
                    }
                }
            }
        }

        private static double SampleHeightGrid(float[] heights, int i, int j)
        {
            double gx = i / (double)HeightGridStep;
            double gy = j / (double)HeightGridStep;

            int x0 = Math.Clamp((int)gx, 0, HeightGridSize - 1);
            int y0 = Math.Clamp((int)gy, 0, HeightGridSize - 1);
            int x1 = Math.Min(x0 + 1, HeightGridSize - 1);
            int y1 = Math.Min(y0 + 1, HeightGridSize - 1);

            double fx = Math.Clamp(gx - x0, 0.0, 1.0);
            double fy = Math.Clamp(gy - y0, 0.0, 1.0);

            double c00 = heights[y0 * HeightGridSize + x0];
            double c10 = heights[y0 * HeightGridSize + x1];
            double c01 = heights[y1 * HeightGridSize + x0];
            double c11 = heights[y1 * HeightGridSize + x1];

            double x0Value = c00 + (c10 - c00) * fx;
            double x1Value = c01 + (c11 - c01) * fx;

            return x0Value + (x1Value - x0Value) * fy;
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
