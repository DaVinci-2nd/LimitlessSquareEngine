using System;

namespace LimitlessSquareEngine.Engine
{
    public sealed class WaterMeshBuildResult
    {
        public Double3 Origin;
        public int[] StitchLevels = new int[4];
        public float[] Vertices = Array.Empty<float>();
    }

    public sealed class WaterMeshBuilder
    {
        private const int VertexStride = 16;
        private readonly Water _water;

        public WaterMeshBuilder(Water water)
        {
            _water = water;
        }

        public object? Build(TerrainTile tile, int lod, object? buildParams)
        {
            var rb = buildParams as RenderBuildParams;
            int grid = rb?.GridSize ?? _water.Profile.RenderBaseTileResolution;
            if (grid < 2)
                grid = 2;

            int[] neighborLevels = rb != null && rb.NeighborLevels != null && rb.NeighborLevels.Length == 4
                ? rb.NeighborLevels
                : new[] { tile.Key.Level, tile.Key.Level, tile.Key.Level, tile.Key.Level };

            TileKey key = tile.Key;
            QuadSphere.GetTileRange(key.Level, key.LX, key.LY, out double u0, out double v0, out double u1, out double v1);

            double du = (u1 - u0) / (grid - 1);
            double dv = (v1 - v0) / (grid - 1);
            int count = grid * grid;

            double surfaceRadius = _water.SurfaceRadius;
            QuadSphere.GetFaceAxes(key.Face, out _, out Double3 faceRight, out _);
            Double3 rightRender = NegateZ(faceRight);

            var dirs = new Double3[count];
            var world = new Double3[count];
            var tangents = new Double3[count];

            for (int j = 0; j < grid; j++)
            {
                for (int i = 0; i < grid; i++)
                {
                    int idx = j * grid + i;
                    Double3 dir = QuadSphere.FaceDir(key.Face, u0 + i * du, v0 + j * dv);
                    Double3 dirRender = NegateZ(dir);

                    dirs[idx] = dirRender;
                    world[idx] = ScaleDir(dirRender, surfaceRadius);
                    tangents[idx] = Normalize(rightRender - dirRender * Dot(rightRender, dirRender));
                }
            }

            Double3 centerDir = QuadSphere.FaceDir(key.Face, (u0 + u1) * 0.5, (v0 + v1) * 0.5);
            Double3 origin = ScaleDir(centerDir, surfaceRadius);
            Double3 originRender = NegateZ(origin);

            int ci = grid / 2;
            int cj = grid / 2;
            int cIdx = cj * grid + ci;
            Double3 cu = world[cj * grid + Math.Min(ci + 1, grid - 1)] - world[cIdx];
            Double3 cv = world[Math.Min(cj + 1, grid - 1) * grid + ci] - world[cIdx];
            bool flipWinding = Dot(Cross(cu, cv), dirs[cIdx]) < 0.0;

            int quads = (grid - 1) * (grid - 1);
            var floats = new float[quads * 6 * VertexStride];
            int writeIndex = 0;

            void WriteTriangleVertex(int vi)
            {
                int i = vi % grid;
                int j = vi / grid;

                floats[writeIndex++] = (float)(world[vi].X - originRender.X);
                floats[writeIndex++] = (float)(world[vi].Y - originRender.Y);
                floats[writeIndex++] = (float)(world[vi].Z - originRender.Z);
                floats[writeIndex++] = 1f;
                floats[writeIndex++] = 1f;
                floats[writeIndex++] = 1f;
                floats[writeIndex++] = 1f;
                floats[writeIndex++] = (float)i / (grid - 1);
                floats[writeIndex++] = (float)j / (grid - 1);
                floats[writeIndex++] = (float)dirs[vi].X;
                floats[writeIndex++] = (float)dirs[vi].Y;
                floats[writeIndex++] = (float)dirs[vi].Z;
                floats[writeIndex++] = (float)tangents[vi].X;
                floats[writeIndex++] = (float)tangents[vi].Y;
                floats[writeIndex++] = (float)tangents[vi].Z;
                floats[writeIndex++] = 1f;
            }

            for (int j = 0; j < grid - 1; j++)
            {
                for (int i = 0; i < grid - 1; i++)
                {
                    int a = j * grid + i;
                    int b = j * grid + i + 1;
                    int c = (j + 1) * grid + i + 1;
                    int d = (j + 1) * grid + i;

                    if (flipWinding)
                    {
                        WriteTriangleVertex(a);
                        WriteTriangleVertex(c);
                        WriteTriangleVertex(b);
                        WriteTriangleVertex(a);
                        WriteTriangleVertex(d);
                        WriteTriangleVertex(c);
                    }
                    else
                    {
                        WriteTriangleVertex(a);
                        WriteTriangleVertex(b);
                        WriteTriangleVertex(c);
                        WriteTriangleVertex(a);
                        WriteTriangleVertex(c);
                        WriteTriangleVertex(d);
                    }
                }
            }

            return new WaterMeshBuildResult
            {
                Origin = origin,
                StitchLevels = neighborLevels,
                Vertices = floats
            };
        }

        private static Double3 ScaleDir(in Double3 dir, double radius)
        {
            return new Double3(dir.X * radius, dir.Y * radius, dir.Z * radius);
        }

        private static Double3 Cross(in Double3 a, in Double3 b)
        {
            return new Double3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        private static double Dot(in Double3 a, in Double3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static Double3 NegateZ(in Double3 a)
        {
            return new Double3(a.X, a.Y, -a.Z);
        }

        private static Double3 Normalize(in Double3 a)
        {
            double len = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
            if (len <= 1e-300)
                return new Double3(0, 0, 1);
            return new Double3(a.X / len, a.Y / len, a.Z / len);
        }
    }
}
