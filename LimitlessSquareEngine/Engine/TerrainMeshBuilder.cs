using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    // 单层网格数据
    public sealed class TerrainLayerMesh
    {
        public string Tag = "";
        public float[] Vertices = Array.Empty<float>();
    }

    // 区块网格构建结果
    public sealed class TerrainMeshBuildResult
    {
        public Double3 Origin;
        public int[] StitchLevels = new int[4];
        public readonly List<TerrainLayerMesh> Layers = new();
    }

    // 高度壳网格构建器
    public sealed class TerrainMeshBuilder
    {
        private const int VertexStride = 16;
        private readonly Terrain _terrain;

        public TerrainMeshBuilder(Terrain terrain)
        {
            _terrain = terrain;
        }

        public object? Build(TerrainTile tile, int lod, object? buildParams)
        {
            var rb = buildParams as RenderBuildParams;
            int grid = rb?.GridSize ?? 33;
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

            var dirs = new Double3[count];
            var world = new Double3[count];

            double radius = _terrain.Radius;
            IHeightSource heightSource = _terrain.Height;

            for (int j = 0; j < grid; j++)
            {
                for (int i = 0; i < grid; i++)
                {
                    int idx = j * grid + i;
                    Double3 dir = QuadSphere.FaceDir(key.Face, u0 + i * du, v0 + j * dv);
                    double h = heightSource.SampleDirection(dir);
                    dirs[idx] = dir;
                    world[idx] = ScaleDir(dir, radius + h);
                }
            }

            Double3 centerDir = QuadSphere.FaceDir(key.Face, (u0 + u1) * 0.5, (v0 + v1) * 0.5);
            Double3 origin = ScaleDir(centerDir, radius + heightSource.SampleDirection(centerDir));

            SnapStitchedEdges(world, grid, key, neighborLevels, u0, v0, u1, v1, radius);

            var dirsRender = new Double3[count];
            var worldRender = new Double3[count];
            for (int i = 0; i < count; i++)
            {
                dirsRender[i] = NegateZ(dirs[i]);
                worldRender[i] = NegateZ(world[i]);
            }
            Double3 originRender = NegateZ(origin);

            var tiles = new int[count];
            for (int i = 0; i < count; i++)
                tiles[i] = _terrain.GetMaterialTile(world[i], dirs[i]);

            int tilesX = Math.Max(1, _terrain.AtlasTilesX);
            int tilesY = Math.Max(1, _terrain.AtlasTilesY);
            double tilingPerFace = Math.Max(1.0, _terrain.TilingPerFace);

            var normals = new Double3[count];
            var tangents = new Double3[count];
            var bitSigns = new double[count];

            for (int j = 0; j < grid; j++)
            {
                for (int i = 0; i < grid; i++)
                {
                    int idx = j * grid + i;
                    double u = u0 + i * du;
                    double v = v0 + j * dv;
                    double um = u - du;
                    double up = u + du;
                    double vm = v - dv;
                    double vp = v + dv;

                    Double3 pm = NegateZ(ScaleDir(QuadSphere.FaceDir(key.Face, um, v), radius + HeightAt(key.Face, um, v)));
                    Double3 pp = NegateZ(ScaleDir(QuadSphere.FaceDir(key.Face, up, v), radius + HeightAt(key.Face, up, v)));
                    Double3 qm = NegateZ(ScaleDir(QuadSphere.FaceDir(key.Face, u, vm), radius + HeightAt(key.Face, u, vm)));
                    Double3 qp = NegateZ(ScaleDir(QuadSphere.FaceDir(key.Face, u, vp), radius + HeightAt(key.Face, u, vp)));

                    Double3 dU = pp - pm;
                    Double3 dV = qp - qm;

                    Double3 n = Cross(dU, dV);
                    if (Dot(n, dirsRender[idx]) < 0.0)
                        n = Negate(n);
                    normals[idx] = Normalize(n);

                    Double3 t = Normalize(dU);
                    tangents[idx] = t;
                    bitSigns[idx] = Dot(Cross(normals[idx], t), Normalize(dV)) >= 0.0 ? 1.0 : -1.0;
                }
            }

            int ci = grid / 2;
            int cj = grid / 2;
            int cIdx = cj * grid + ci;
            Double3 cu = worldRender[cj * grid + Math.Min(ci + 1, grid - 1)] - worldRender[cIdx];
            Double3 cv = worldRender[Math.Min(cj + 1, grid - 1) * grid + ci] - worldRender[cIdx];
            bool flipWinding = Dot(Cross(cu, cv), dirsRender[cIdx]) < 0.0;

            var triGroups = new Dictionary<int, List<int>>();

            for (int j = 0; j < grid - 1; j++)
            {
                for (int i = 0; i < grid - 1; i++)
                {
                    int a = j * grid + i;
                    int b = j * grid + i + 1;
                    int c = (j + 1) * grid + i + 1;
                    int d = (j + 1) * grid + i;

                    AddTriangle(triGroups, tiles, flipWinding ? new[] { a, c, b } : new[] { a, b, c });
                    AddTriangle(triGroups, tiles, flipWinding ? new[] { a, d, c } : new[] { a, c, d });
                }
            }

            var result = new TerrainMeshBuildResult
            {
                Origin = origin,
                StitchLevels = neighborLevels
            };

            var tileIndexList = new List<int>(triGroups.Keys);
            tileIndexList.Sort();

            foreach (int tileIdx in tileIndexList)
            {
                List<int> triangles = triGroups[tileIdx];
                var floats = new List<float>(triangles.Count * VertexStride);

                int tx = tileIdx % tilesX;
                int ty = tileIdx / tilesX;
                double baseU = (double)tx / tilesX;
                double baseV = (double)ty / tilesY;
                double sizeU = 1.0 / tilesX;
                double sizeV = 1.0 / tilesY;

                for (int t = 0; t < triangles.Count; t += 3)
                {
                    WriteTriangleVertex(floats, triangles[t], grid, tilingPerFace, baseU, baseV, sizeU, sizeV, worldRender, originRender, normals, tangents, bitSigns);
                    WriteTriangleVertex(floats, triangles[t + 1], grid, tilingPerFace, baseU, baseV, sizeU, sizeV, worldRender, originRender, normals, tangents, bitSigns);
                    WriteTriangleVertex(floats, triangles[t + 2], grid, tilingPerFace, baseU, baseV, sizeU, sizeV, worldRender, originRender, normals, tangents, bitSigns);
                }

                result.Layers.Add(new TerrainLayerMesh
                {
                    Tag = _terrain.GetMaterialTag(tileIdx),
                    Vertices = floats.ToArray()
                });
            }

            return result;
        }

        private static void WriteTriangleVertex(
            List<float> floats, int vi, int grid, double tilingPerFace,
            double baseU, double baseV, double sizeU, double sizeV,
            Double3[] worldRender, in Double3 originRender, Double3[] normals, Double3[] tangents, double[] bitSigns)
        {
            double tileU = (double)(vi % grid) / (grid - 1) * tilingPerFace;
            double tileV = (double)(vi / grid) / (grid - 1) * tilingPerFace;
            double fu = tileU - Math.Floor(tileU);
            double fv = tileV - Math.Floor(tileV);

            WriteVertex(
                floats,
                worldRender[vi] - originRender,
                normals[vi],
                tangents[vi],
                bitSigns[vi],
                baseU + fu * sizeU,
                baseV + fv * sizeV);
        }

        private void SnapStitchedEdges(
            Double3[] world, int grid, in TileKey key, int[] neighborLevels,
            double u0, double v0, double u1, double v1, double radius)
        {
            double du = (u1 - u0) / (grid - 1);
            double dv = (v1 - v0) / (grid - 1);

            for (int e = 0; e < 4; e++)
            {
                if (neighborLevels[e] >= key.Level)
                    continue;

                double stepCoarse = 2.0 / Math.Pow(2.0, neighborLevels[e]) / (grid - 1);

                int coarseFace = key.Face;

                if (!QuadSphere.TryGetEdgeNeighbor(key, e, out _))
                {
                    Double3 mid = e switch
                    {
                        0 => QuadSphere.FaceDir(key.Face, u0, (v0 + v1) * 0.5),
                        1 => QuadSphere.FaceDir(key.Face, u1, (v0 + v1) * 0.5),
                        2 => QuadSphere.FaceDir(key.Face, (u0 + u1) * 0.5, v0),
                        _ => QuadSphere.FaceDir(key.Face, (u0 + u1) * 0.5, v1)
                    };

                    double bestDot = -1.0;

                    for (int f = 0; f < QuadSphere.FaceCount; f++)
                    {
                        if (f == key.Face)
                            continue;

                        QuadSphere.GetFaceAxes(f, out Double3 fn, out _, out _);
                        double dot = Math.Abs(mid.X * fn.X + mid.Y * fn.Y + mid.Z * fn.Z);

                        if (dot > bestDot)
                        {
                            bestDot = dot;
                            coarseFace = f;
                        }
                    }
                }

                QuadSphere.GetFaceAxes(coarseFace, out Double3 cn, out Double3 cr, out Double3 cup);

                if (e == 0 || e == 1)
                {
                    int col = e == 0 ? 0 : grid - 1;
                    double u = e == 0 ? u0 : u1;
                    for (int j = 0; j < grid; j++)
                    {
                        double v = v0 + j * dv;
                        Double3 dir = QuadSphere.FaceDir(key.Face, u, v);
                        double h;

                        if (coarseFace == key.Face)
                        {
                            h = SampleCoarseSurfaceHeight(key.Face, u, v, stepCoarse, radius);
                        }
                        else
                        {
                            double dn = dir.X * cn.X + dir.Y * cn.Y + dir.Z * cn.Z;
                            double nu = (dir.X * cr.X + dir.Y * cr.Y + dir.Z * cr.Z) / dn;
                            double nv = (dir.X * cup.X + dir.Y * cup.Y + dir.Z * cup.Z) / dn;
                            h = SampleCoarseSurfaceHeight(coarseFace, nu, nv, stepCoarse, radius);
                        }

                        world[j * grid + col] = ScaleDir(dir, radius + h);
                    }
                }
                else
                {
                    int row = e == 2 ? 0 : grid - 1;
                    double v = e == 2 ? v0 : v1;
                    for (int i = 0; i < grid; i++)
                    {
                        double u = u0 + i * du;
                        Double3 dir = QuadSphere.FaceDir(key.Face, u, v);
                        double h;

                        if (coarseFace == key.Face)
                        {
                            h = SampleCoarseSurfaceHeight(key.Face, u, v, stepCoarse, radius);
                        }
                        else
                        {
                            double dn = dir.X * cn.X + dir.Y * cn.Y + dir.Z * cn.Z;
                            double nu = (dir.X * cr.X + dir.Y * cr.Y + dir.Z * cr.Z) / dn;
                            double nv = (dir.X * cup.X + dir.Y * cup.Y + dir.Z * cup.Z) / dn;
                            h = SampleCoarseSurfaceHeight(coarseFace, nu, nv, stepCoarse, radius);
                        }

                        world[row * grid + i] = ScaleDir(dir, radius + h);
                    }
                }
            }
        }

        private double SampleCoarseSurfaceHeight(int face, double u, double v, double stepCoarse, double radius)
        {
            double ua = Math.Max(-1.0, -1.0 + Math.Floor((u + 1.0) / stepCoarse) * stepCoarse);
            double va = Math.Max(-1.0, -1.0 + Math.Floor((v + 1.0) / stepCoarse) * stepCoarse);
            double ub = Math.Min(1.0, ua + stepCoarse);
            double vb = Math.Min(1.0, va + stepCoarse);

            Double3 h00 = ScaleDir(QuadSphere.FaceDir(face, ua, va), radius + HeightAt(face, ua, va));
            Double3 h10 = ScaleDir(QuadSphere.FaceDir(face, ub, va), radius + HeightAt(face, ub, va));
            Double3 h11 = ScaleDir(QuadSphere.FaceDir(face, ub, vb), radius + HeightAt(face, ub, vb));
            Double3 h01 = ScaleDir(QuadSphere.FaceDir(face, ua, vb), radius + HeightAt(face, ua, vb));

            Double3 dir = QuadSphere.FaceDir(face, u, v);

            double fu = Math.Clamp((u - ua) / stepCoarse, 0.0, 1.0);
            double fv = Math.Clamp((v - va) / stepCoarse, 0.0, 1.0);

            Double3 second = fu >= fv ? h10 : h01;
            Double3 third = h11;

            Double3 normal = Cross(second - h00, third - h00);
            double denominator = Dot(dir, normal);

            if (Math.Abs(denominator) <= 1e-12)
                return HeightAt(face, u, v);

            return Dot(h00, normal) / denominator - radius;
        }

        private double HeightAt(int face, double u, double v)
        {
            Double3 dir = QuadSphere.FaceDir(face, u, v);
            return _terrain.Height.SampleDirection(dir);
        }

        private static void AddTriangle(Dictionary<int, List<int>> groups, int[] tiles, int[] tri)
        {
            int tile = tiles[tri[0]];
            if (!groups.TryGetValue(tile, out List<int>? list))
            {
                list = new List<int>();
                groups[tile] = list;
            }
            list.Add(tri[0]);
            list.Add(tri[1]);
            list.Add(tri[2]);
        }

        private static void WriteVertex(
            List<float> floats,
            in Double3 pos,
            in Double3 normal,
            in Double3 tangent,
            double bitSign,
            double u,
            double v)
        {
            floats.Add((float)pos.X);
            floats.Add((float)pos.Y);
            floats.Add((float)pos.Z);
            floats.Add(1f);
            floats.Add(1f);
            floats.Add(1f);
            floats.Add(1f);
            floats.Add((float)u);
            floats.Add((float)v);
            floats.Add((float)normal.X);
            floats.Add((float)normal.Y);
            floats.Add((float)normal.Z);
            floats.Add((float)tangent.X);
            floats.Add((float)tangent.Y);
            floats.Add((float)tangent.Z);
            floats.Add((float)bitSign);
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

        private static Double3 Negate(in Double3 a)
        {
            return new Double3(-a.X, -a.Y, -a.Z);
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
