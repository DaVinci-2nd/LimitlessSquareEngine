using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    // 体素块网格构建结果
    public sealed class VoxelMeshResult
    {
        public Double3 Origin;
        public float[] Vertices = Array.Empty<float>();
        public int TriangleCount;

        public bool IsEmpty => Vertices.Length == 0;
    }

    // 体素块表面网格构建器
    public sealed class VoxelMesher
    {
        private const int VertexStride = 16;

        private static readonly int[] _edgeCorners =
        {
            0, 1,  1, 3,  3, 2,  2, 0,
            4, 5,  5, 7,  7, 6,  6, 4,
            0, 4,  1, 5,  2, 6,  3, 7
        };

        public VoxelMeshResult Build(VoxelBlock block, double tilingPerFace, int atlasTilesX, int atlasTilesY)
        {
            int sampleSize = VoxelBlock.SampleSize;
            int cellCount = VoxelBlock.SampleSize - 1;

            float[] sdf = block.Sdf;

            var cellVertex = new int[cellCount * cellCount * cellCount];
            Array.Fill(cellVertex, -1);

            var positions = new List<Double3>(512);
            var normals = new List<Double3>(512);

            var cornerSdf = new double[8];
            var cornerPos = new Double3[8];

            for (int k = 0; k < cellCount; k++)
            {
                for (int j = 0; j < cellCount; j++)
                {
                    for (int i = 0; i < cellCount; i++)
                    {
                        int negative = 0;

                        for (int c = 0; c < 8; c++)
                        {
                            int ci = i + (c & 1);
                            int cj = j + ((c >> 1) & 1);
                            int ck = k + ((c >> 2) & 1);
                            double value = sdf[(ck * sampleSize + cj) * sampleSize + ci];
                            cornerSdf[c] = value;

                            if (value < 0.0)
                                negative++;
                        }

                        if (negative == 0 || negative == 8)
                            continue;

                        double sumX = 0.0;
                        double sumY = 0.0;
                        double sumZ = 0.0;
                        int crossings = 0;

                        for (int e = 0; e < 12; e++)
                        {
                            int ca = _edgeCorners[e * 2];
                            int cb = _edgeCorners[e * 2 + 1];
                            double sa = cornerSdf[ca];
                            double sb = cornerSdf[cb];

                            if ((sa < 0.0) == (sb < 0.0))
                                continue;

                            double t = sa / (sa - sb);

                            Double3 pa = block.BlockPoint(
                                i + (ca & 1) + VoxelBlock.SampleOrigin,
                                j + ((ca >> 1) & 1) + VoxelBlock.SampleOrigin,
                                k + ((ca >> 2) & 1) + VoxelBlock.SampleOrigin);
                            Double3 pb = block.BlockPoint(
                                i + (cb & 1) + VoxelBlock.SampleOrigin,
                                j + ((cb >> 1) & 1) + VoxelBlock.SampleOrigin,
                                k + ((cb >> 2) & 1) + VoxelBlock.SampleOrigin);

                            sumX += pa.X + (pb.X - pa.X) * t;
                            sumY += pa.Y + (pb.Y - pa.Y) * t;
                            sumZ += pa.Z + (pb.Z - pa.Z) * t;
                            crossings++;
                        }

                        var vertex = new Double3(sumX / crossings, sumY / crossings, sumZ / crossings);

                        for (int c = 0; c < 8; c++)
                        {
                            cornerPos[c] = block.BlockPoint(
                                i + (c & 1) + VoxelBlock.SampleOrigin,
                                j + ((c >> 1) & 1) + VoxelBlock.SampleOrigin,
                                k + ((c >> 2) & 1) + VoxelBlock.SampleOrigin);
                        }

                        double gx =
                            (cornerSdf[1] + cornerSdf[3] + cornerSdf[5] + cornerSdf[7]) -
                            (cornerSdf[0] + cornerSdf[2] + cornerSdf[4] + cornerSdf[6]);
                        double gy =
                            (cornerSdf[2] + cornerSdf[3] + cornerSdf[6] + cornerSdf[7]) -
                            (cornerSdf[0] + cornerSdf[1] + cornerSdf[4] + cornerSdf[5]);
                        double gz =
                            (cornerSdf[4] + cornerSdf[5] + cornerSdf[6] + cornerSdf[7]) -
                            (cornerSdf[0] + cornerSdf[1] + cornerSdf[2] + cornerSdf[3]);

                        Double3 ti = new Double3(
                            cornerPos[1].X + cornerPos[3].X + cornerPos[5].X + cornerPos[7].X - cornerPos[0].X - cornerPos[2].X - cornerPos[4].X - cornerPos[6].X,
                            cornerPos[1].Y + cornerPos[3].Y + cornerPos[5].Y + cornerPos[7].Y - cornerPos[0].Y - cornerPos[2].Y - cornerPos[4].Y - cornerPos[6].Y,
                            cornerPos[1].Z + cornerPos[3].Z + cornerPos[5].Z + cornerPos[7].Z - cornerPos[0].Z - cornerPos[2].Z - cornerPos[4].Z - cornerPos[6].Z);
                        Double3 tj = new Double3(
                            cornerPos[2].X + cornerPos[3].X + cornerPos[6].X + cornerPos[7].X - cornerPos[0].X - cornerPos[1].X - cornerPos[4].X - cornerPos[5].X,
                            cornerPos[2].Y + cornerPos[3].Y + cornerPos[6].Y + cornerPos[7].Y - cornerPos[0].Y - cornerPos[1].Y - cornerPos[4].Y - cornerPos[5].Y,
                            cornerPos[2].Z + cornerPos[3].Z + cornerPos[6].Z + cornerPos[7].Z - cornerPos[0].Z - cornerPos[1].Z - cornerPos[4].Z - cornerPos[5].Z);
                        Double3 tk = new Double3(
                            cornerPos[4].X + cornerPos[5].X + cornerPos[6].X + cornerPos[7].X - cornerPos[0].X - cornerPos[1].X - cornerPos[2].X - cornerPos[3].X,
                            cornerPos[4].Y + cornerPos[5].Y + cornerPos[6].Y + cornerPos[7].Y - cornerPos[0].Y - cornerPos[1].Y - cornerPos[2].Y - cornerPos[3].Y,
                            cornerPos[4].Z + cornerPos[5].Z + cornerPos[6].Z + cornerPos[7].Z - cornerPos[0].Z - cornerPos[1].Z - cornerPos[2].Z - cornerPos[3].Z);

                        double tiSq = Math.Max(1e-300, ti.X * ti.X + ti.Y * ti.Y + ti.Z * ti.Z);
                        double tjSq = Math.Max(1e-300, tj.X * tj.X + tj.Y * tj.Y + tj.Z * tj.Z);
                        double tkSq = Math.Max(1e-300, tk.X * tk.X + tk.Y * tk.Y + tk.Z * tk.Z);

                        Double3 normal = Normalize(new Double3(
                            gx * ti.X / tiSq + gy * tj.X / tjSq + gz * tk.X / tkSq,
                            gx * ti.Y / tiSq + gy * tj.Y / tjSq + gz * tk.Y / tkSq,
                            gx * ti.Z / tiSq + gy * tj.Z / tjSq + gz * tk.Z / tkSq));

                        if (normal.X == 0.0 && normal.Y == 0.0 && normal.Z == 0.0)
                            normal = Normalize(vertex - block.Origin);

                        cellVertex[(k * cellCount + j) * cellCount + i] = positions.Count;
                        positions.Add(vertex);
                        normals.Add(normal);
                    }
                }
            }

            var floats = new List<float>(positions.Count * VertexStride * 2);

            for (int k = 1; k <= cellCount - 2; k++)
            {
                for (int j = 1; j <= cellCount - 2; j++)
                {
                    for (int i = 1; i <= cellCount - 2; i++)
                    {
                        double sa = sdf[(k * sampleSize + j) * sampleSize + i];
                        double sb = sdf[(k * sampleSize + j) * sampleSize + i + 1];

                        if ((sa < 0.0) == (sb < 0.0))
                            continue;

                        int v0 = cellVertex[((k - 1) * cellCount + (j - 1)) * cellCount + i];
                        int v1 = cellVertex[((k - 1) * cellCount + j) * cellCount + i];
                        int v2 = cellVertex[(k * cellCount + j) * cellCount + i];
                        int v3 = cellVertex[(k * cellCount + (j - 1)) * cellCount + i];

                        if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                            continue;

                        AddQuad(floats, block, positions, normals, v0, v1, v2, v3, sb > 0.0,
                            block.Origin, tilingPerFace, atlasTilesX, atlasTilesY);
                    }
                }
            }

            for (int k = 1; k <= cellCount - 2; k++)
            {
                for (int j = 1; j <= cellCount - 2; j++)
                {
                    for (int i = 1; i <= cellCount - 2; i++)
                    {
                        double sa = sdf[(k * sampleSize + j) * sampleSize + i];
                        double sb = sdf[(k * sampleSize + j + 1) * sampleSize + i];

                        if ((sa < 0.0) == (sb < 0.0))
                            continue;

                        int v0 = cellVertex[((k - 1) * cellCount + j) * cellCount + (i - 1)];
                        int v1 = cellVertex[((k - 1) * cellCount + j) * cellCount + i];
                        int v2 = cellVertex[(k * cellCount + j) * cellCount + i];
                        int v3 = cellVertex[(k * cellCount + j) * cellCount + (i - 1)];

                        if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                            continue;

                        AddQuad(floats, block, positions, normals, v0, v1, v2, v3, sa > 0.0,
                            block.Origin, tilingPerFace, atlasTilesX, atlasTilesY);
                    }
                }
            }

            for (int k = 1; k <= cellCount - 2; k++)
            {
                for (int j = 1; j <= cellCount - 2; j++)
                {
                    for (int i = 1; i <= cellCount - 2; i++)
                    {
                        double sa = sdf[(k * sampleSize + j) * sampleSize + i];
                        double sb = sdf[((k + 1) * sampleSize + j) * sampleSize + i];

                        if ((sa < 0.0) == (sb < 0.0))
                            continue;

                        int v0 = cellVertex[(k * cellCount + (j - 1)) * cellCount + (i - 1)];
                        int v1 = cellVertex[(k * cellCount + (j - 1)) * cellCount + i];
                        int v2 = cellVertex[(k * cellCount + j) * cellCount + i];
                        int v3 = cellVertex[(k * cellCount + j) * cellCount + (i - 1)];

                        if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0)
                            continue;

                        AddQuad(floats, block, positions, normals, v0, v1, v2, v3, sb > 0.0,
                            block.Origin, tilingPerFace, atlasTilesX, atlasTilesY);
                    }
                }
            }

            return new VoxelMeshResult
            {
                Origin = block.Origin,
                Vertices = floats.ToArray(),
                TriangleCount = floats.Count / (VertexStride * 3)
            };
        }

        private static void AddQuad(
            List<float> floats,
            VoxelBlock block,
            List<Double3> positions,
            List<Double3> normals,
            int v0, int v1, int v2, int v3,
            bool reverse,
            in Double3 origin,
            double tilingPerFace,
            int atlasTilesX,
            int atlasTilesY)
        {
            Double3 p0 = positions[v0];
            Double3 p1 = positions[v1];
            Double3 p2 = positions[v2];
            Double3 p3 = positions[v3];

            if (reverse)
            {
                WriteVertex(floats, block, p0, normals[v0], origin, tilingPerFace, atlasTilesX, atlasTilesY);
                WriteVertex(floats, block, p2, normals[v2], origin, tilingPerFace, atlasTilesX, atlasTilesY);
                WriteVertex(floats, block, p1, normals[v1], origin, tilingPerFace, atlasTilesX, atlasTilesY);

                WriteVertex(floats, block, p0, normals[v0], origin, tilingPerFace, atlasTilesX, atlasTilesY);
                WriteVertex(floats, block, p3, normals[v3], origin, tilingPerFace, atlasTilesX, atlasTilesY);
                WriteVertex(floats, block, p2, normals[v2], origin, tilingPerFace, atlasTilesX, atlasTilesY);
            }
            else
            {
                WriteVertex(floats, block, p0, normals[v0], origin, tilingPerFace, atlasTilesX, atlasTilesY);
                WriteVertex(floats, block, p1, normals[v1], origin, tilingPerFace, atlasTilesX, atlasTilesY);
                WriteVertex(floats, block, p2, normals[v2], origin, tilingPerFace, atlasTilesX, atlasTilesY);

                WriteVertex(floats, block, p0, normals[v0], origin, tilingPerFace, atlasTilesX, atlasTilesY);
                WriteVertex(floats, block, p2, normals[v2], origin, tilingPerFace, atlasTilesX, atlasTilesY);
                WriteVertex(floats, block, p3, normals[v3], origin, tilingPerFace, atlasTilesX, atlasTilesY);
            }
        }

        private static void WriteVertex(
            List<float> floats,
            VoxelBlock block,
            in Double3 pos,
            in Double3 normal,
            in Double3 origin,
            double tilingPerFace,
            int atlasTilesX,
            int atlasTilesY)
        {
            double localX = pos.X - origin.X;
            double localY = pos.Y - origin.Y;
            double localZ = -(pos.Z - origin.Z);

            block.BlockUV(pos, out double tileU, out double tileV);

            tileU *= 1.0 / Math.Max(1, atlasTilesX);
            tileV *= 1.0 / Math.Max(1, atlasTilesY);

            floats.Add((float)localX);
            floats.Add((float)localY);
            floats.Add((float)localZ);
            floats.Add(1f);
            floats.Add(1f);
            floats.Add(1f);
            floats.Add(1f);
            floats.Add((float)tileU);
            floats.Add((float)tileV);
            floats.Add((float)normal.X);
            floats.Add((float)normal.Y);
            floats.Add((float)(-normal.Z));
            floats.Add(1f);
            floats.Add(0f);
            floats.Add(0f);
            floats.Add(1f);
        }

        private static Double3 Normalize(in Double3 a)
        {
            double len = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
            if (len <= 1e-300)
                return Double3.Zero;
            return new Double3(a.X / len, a.Y / len, a.Z / len);
        }
    }
}
