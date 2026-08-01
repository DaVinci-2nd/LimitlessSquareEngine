using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine.Terrain
{
    // 立方体贴面四叉树区块键
    public readonly struct TileKey : IEquatable<TileKey>
    {
        public readonly int Face;
        public readonly int Level;
        public readonly int LX;
        public readonly int LY;

        public TileKey(int face, int level, int lx, int ly)
        {
            Face = face;
            Level = level;
            LX = lx;
            LY = ly;
        }

        public bool Equals(TileKey other)
        {
            return Face == other.Face && Level == other.Level && LX == other.LX && LY == other.LY;
        }

        public override bool Equals(object? obj)
        {
            return obj is TileKey other && Equals(other);
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
                return hash;
            }
        }

        public override string ToString()
        {
            return $"T(F{Face},L{Level},{LX},{LY})";
        }
    }

    // 立方体贴面数学
    public static class QuadSphere
    {
        private const double RadiansToDegrees = 180.0 / Math.PI;

        private readonly struct FaceAxes
        {
            public readonly Double3 N;
            public readonly Double3 R;
            public readonly Double3 U;

            public FaceAxes(Double3 n, Double3 r, Double3 u)
            {
                N = n;
                R = r;
                U = u;
            }
        }

        private static readonly FaceAxes[] Faces =
        {
            new FaceAxes(new Double3(1, 0, 0), new Double3(0, 0, -1), new Double3(0, 1, 0)),
            new FaceAxes(new Double3(-1, 0, 0), new Double3(0, 0, 1), new Double3(0, 1, 0)),
            new FaceAxes(new Double3(0, 1, 0), new Double3(1, 0, 0), new Double3(0, 0, -1)),
            new FaceAxes(new Double3(0, -1, 0), new Double3(1, 0, 0), new Double3(0, 0, 1)),
            new FaceAxes(new Double3(0, 0, 1), new Double3(1, 0, 0), new Double3(0, 1, 0)),
            new FaceAxes(new Double3(0, 0, -1), new Double3(-1, 0, 0), new Double3(0, 1, 0)),
        };

        public const int FaceCount = 6;

        public static bool IsValidFace(int face) => face >= 0 && face < FaceCount;

        public static void GetFaceAxes(int face, out Double3 normal, out Double3 right, out Double3 up)
        {
            FaceAxes f = Faces[face];
            normal = f.N;
            right = f.R;
            up = f.U;
        }

        // 返回面上参数对应的立方体点
        public static Double3 FacePoint(int face, double u, double v)
        {
            FaceAxes f = Faces[face];
            return new Double3(
                f.N.X + f.R.X * u + f.U.X * v,
                f.N.Y + f.R.Y * u + f.U.Y * v,
                f.N.Z + f.R.Z * u + f.U.Z * v);
        }

        // 返回面上参数投影到单位球上的单位方向
        public static Double3 FaceDir(int face, double u, double v)
        {
            Double3 p = FacePoint(face, u, v);
            double len = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            if (len <= 1e-300)
                return new Double3(0, 0, 1);
            return new Double3(p.X / len, p.Y / len, p.Z / len);
        }

        // 将单位方向反解为所在面及参数u和v
        public static void DirToFaceUV(in Double3 dir, out int face, out double u, out double v)
        {
            double bestDot = double.NegativeInfinity;
            face = 4;
            for (int i = 0; i < FaceCount; i++)
            {
                double dot = Math.Abs(dir.X * Faces[i].N.X + dir.Y * Faces[i].N.Y + dir.Z * Faces[i].N.Z);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    face = i;
                }
            }

            FaceAxes f = Faces[face];
            double dn = dir.X * f.N.X + dir.Y * f.N.Y + dir.Z * f.N.Z;
            if (Math.Abs(dn) <= 1e-15)
                dn = 1e-15;
            u = (dir.X * f.R.X + dir.Y * f.R.Y + dir.Z * f.R.Z) / dn;
            v = (dir.X * f.U.X + dir.Y * f.U.Y + dir.Z * f.U.Z) / dn;
        }

        public static int TileCountPerAxis(int level)
        {
            if (level <= 0)
                return 1;
            return 1 << level;
        }

        public static int TileCount(int level)
        {
            int per = TileCountPerAxis(level);
            return per * per;
        }

        // 返回指定级别区块的参数范围
        public static void GetTileRange(int level, int lx, int ly, out double u0, out double v0, out double u1, out double v1)
        {
            int per = TileCountPerAxis(level);
            if (lx < 0 || lx >= per || ly < 0 || ly >= per)
                throw new ArgumentOutOfRangeException(nameof(lx), $"Tile coords out of range at level {level}: ({lx},{ly})");

            double step = 2.0 / per;
            u0 = -1.0 + lx * step;
            v0 = -1.0 + ly * step;
            u1 = u0 + step;
            v1 = v0 + step;
        }

        public static Double3 TileCenterDir(in TileKey key)
        {
            GetTileRange(key.Level, key.LX, key.LY, out double u0, out double v0, out double u1, out double v1);
            return FaceDir(key.Face, (u0 + u1) * 0.5, (v0 + v1) * 0.5);
        }

        // 返回区块四个角中指定角的单位方向
        public static Double3 TileCornerDir(in TileKey key, int corner)
        {
            GetTileRange(key.Level, key.LX, key.LY, out double u0, out double v0, out double u1, out double v1);
            double u = corner == 0 || corner == 3 ? u0 : u1;
            double v = corner == 0 || corner == 1 ? v0 : v1;
            return FaceDir(key.Face, u, v);
        }

        // 返回区块中心到角落的角距
        public static double TileAngularRadius(in TileKey key)
        {
            Double3 center = TileCenterDir(key);
            Double3 corner = TileCornerDir(key, 0);
            return AngleBetween(center, corner);
        }

        // 返回区块中心到最远角落的角距
        public static double TileMaxAngularRadius(in TileKey key)
        {
            Double3 center = TileCenterDir(key);
            double max = 0.0;
            for (int c = 0; c < 4; c++)
            {
                double a = AngleBetween(center, TileCornerDir(key, c));
                if (a > max)
                    max = a;
            }
            return max;
        }

        public static double AngleBetween(in Double3 a, in Double3 b)
        {
            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            dot = Math.Clamp(dot, -1.0, 1.0);
            return Math.Acos(dot);
        }

        // 返回两点沿球面的弧长距离
        public static double SurfaceDistance(in Double3 a, in Double3 b, double radius)
        {
            return AngleBetween(a, b) * radius;
        }

        // 返回单位方向在指定半径处的世界坐标
        public static Double3 WorldPosition(in Double3 worldCenter, double radius, in Double3 dir)
        {
            return new Double3(
                worldCenter.X + dir.X * radius,
                worldCenter.Y + dir.Y * radius,
                worldCenter.Z + dir.Z * radius);
        }

        public static TileKey GetParent(in TileKey key)
        {
            if (key.Level <= 0)
                throw new InvalidOperationException("[X] Tile at level 0 has no parent.");
            return new TileKey(key.Face, key.Level - 1, key.LX >> 1, key.LY >> 1);
        }

        // 将区块四个子区块写入指定数组
        public static void GetChildren(in TileKey key, Span<TileKey> children)
        {
            if (children.Length < 4)
                throw new ArgumentException("children span must have length >= 4", nameof(children));

            int l = key.Level + 1;
            int lx2 = key.LX << 1;
            int ly2 = key.LY << 1;
            children[0] = new TileKey(key.Face, l, lx2, ly2);
            children[1] = new TileKey(key.Face, l, lx2 + 1, ly2);
            children[2] = new TileKey(key.Face, l, lx2 + 1, ly2 + 1);
            children[3] = new TileKey(key.Face, l, lx2, ly2 + 1);
        }

        // 返回同级别相邻区块
        public static bool TryGetEdgeNeighbor(in TileKey key, int edge, out TileKey neighbor)
        {
            neighbor = default;
            int per = TileCountPerAxis(key.Level);
            switch (edge)
            {
                case 0:
                    if (key.LX <= 0) return false;
                    neighbor = new TileKey(key.Face, key.Level, key.LX - 1, key.LY);
                    return true;
                case 1:
                    if (key.LX >= per - 1) return false;
                    neighbor = new TileKey(key.Face, key.Level, key.LX + 1, key.LY);
                    return true;
                case 2:
                    if (key.LY <= 0) return false;
                    neighbor = new TileKey(key.Face, key.Level, key.LX, key.LY - 1);
                    return true;
                case 3:
                    if (key.LY >= per - 1) return false;
                    neighbor = new TileKey(key.Face, key.Level, key.LX, key.LY + 1);
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(edge), "edge must be in [0,4)");
            }
        }

        // 返回跨面相邻区块
        public static bool TryGetCrossFaceEdgeNeighbor(in TileKey key, int edge, out TileKey neighbor)
        {
            neighbor = default;
            int per = TileCountPerAxis(key.Level);

            GetTileRange(key.Level, key.LX, key.LY, out double u0, out double v0, out double u1, out double v1);

            double u, v;
            switch (edge)
            {
                case 0:
                    if (key.LX > 0) return false;
                    u = u0; v = (v0 + v1) * 0.5;
                    break;
                case 1:
                    if (key.LX < per - 1) return false;
                    u = u1; v = (v0 + v1) * 0.5;
                    break;
                case 2:
                    if (key.LY > 0) return false;
                    v = v0; u = (u0 + u1) * 0.5;
                    break;
                case 3:
                    if (key.LY < per - 1) return false;
                    v = v1; u = (u0 + u1) * 0.5;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(edge));
            }

            Double3 mid = FaceDir(key.Face, u, v);
            DirToFaceUV(mid, out int otherFace, out double ou, out double ov);
            if (otherFace == key.Face)
                return false;

            double step = 2.0 / per;
            int olx = Math.Clamp((int)Math.Floor((ou + 1.0) * 0.5 * per), 0, per - 1);
            int oly = Math.Clamp((int)Math.Floor((ov + 1.0) * 0.5 * per), 0, per - 1);
            if (olx >= per || oly >= per)
                return false;

            neighbor = new TileKey(otherFace, key.Level, olx, oly);
            return true;
        }

        // 返回区块内局部坐标对应的单位方向
        public static Double3 TileLocalDir(in TileKey key, double localU, double localV)
        {
            GetTileRange(key.Level, key.LX, key.LY, out double u0, out double v0, out double u1, out double v1);
            double u = Math.Clamp(u0 + localU * (u1 - u0), -1.0, 1.0);
            double v = Math.Clamp(v0 + localV * (v1 - v0), -1.0, 1.0);
            return FaceDir(key.Face, u, v);
        }

        public static double Degrees(double radians) => radians * RadiansToDegrees;
    }
}
