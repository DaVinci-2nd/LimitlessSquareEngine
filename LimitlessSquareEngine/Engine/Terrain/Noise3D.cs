using System;

namespace LimitlessSquareEngine.Engine.Terrain
{
    // 确定性三维噪声
    public sealed class Noise3D
    {
        private readonly uint _seed;
        private readonly int[] _perm;

        public Noise3D(uint seed)
        {
            _seed = seed;
            _perm = new int[512];
            BuildPermutation(seed);
        }

        private void BuildPermutation(uint seed)
        {
            int[] p = new int[256];
            for (int i = 0; i < 256; i++)
                p[i] = i;

            uint state = seed != 0 ? seed : 0x9E3779B9u;
            for (int i = 255; i > 0; i--)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                int j = (int)(state & 0xFFu);
                (p[i], p[j]) = (p[j], p[i]);
            }

            for (int i = 0; i < 512; i++)
                _perm[i] = p[i & 255];
        }

        private static double Fade(double t) => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        // 返回三维值噪声
        public double Noise(double x, double y, double z)
        {
            int xi = FloorToInt(x);
            int yi = FloorToInt(y);
            int zi = FloorToInt(z);

            double xf = x - xi;
            double yf = y - yi;
            double zf = z - zi;

            double u = Fade(xf);
            double v = Fade(yf);
            double w = Fade(zf);

            int x0 = xi & 255;
            int y0 = yi & 255;
            int z0 = zi & 255;

            double n000 = HashUnit(x0, y0, z0);
            double n100 = HashUnit(x0 + 1, y0, z0);
            double n010 = HashUnit(x0, y0 + 1, z0);
            double n110 = HashUnit(x0 + 1, y0 + 1, z0);
            double n001 = HashUnit(x0, y0, z0 + 1);
            double n101 = HashUnit(x0 + 1, y0, z0 + 1);
            double n011 = HashUnit(x0, y0 + 1, z0 + 1);
            double n111 = HashUnit(x0 + 1, y0 + 1, z0 + 1);

            double nx00 = Lerp(n000, n100, u);
            double nx10 = Lerp(n010, n110, u);
            double nx01 = Lerp(n001, n101, u);
            double nx11 = Lerp(n011, n111, u);

            double nxy0 = Lerp(nx00, nx10, v);
            double nxy1 = Lerp(nx01, nx11, v);

            return Lerp(nxy0, nxy1, w);
        }

        private static int FloorToInt(double x)
        {
            int i = (int)x;
            return x < i ? i - 1 : i;
        }

        private double HashUnit(int x, int y, int z)
        {
            int idx = _perm[(x + _perm[(y + _perm[z & 255]) & 255]) & 255];
            return (idx / 127.5) - 1.0;
        }

        // 返回分形布朗运动噪声
        public double Fbm(double x, double y, double z, int octaves = 4, double lacunarity = 2.0, double gain = 0.5)
        {
            double sum = 0.0;
            double amp = 1.0;
            double freq = 1.0;
            double norm = 0.0;

            for (int i = 0; i < octaves; i++)
            {
                sum += Noise(x * freq, y * freq, z * freq) * amp;
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }

            if (norm <= 1e-12)
                return 0.0;
            return sum / norm;
        }
    }
}
