using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LimitlessSquareEngine.Engine.Terrain
{
    // 6面立方体贴图数据
    public sealed class CubeMapData
    {
        private readonly Image<Rgba32>[] _faces;
        private readonly int _width;
        private readonly int _height;
        private readonly double _featherUv;

        public int FaceWidth => _width;
        public int FaceHeight => _height;

        public CubeMapData(Image<Rgba32> px, Image<Rgba32> nx, Image<Rgba32> py, Image<Rgba32> ny, Image<Rgba32> pz, Image<Rgba32> nz)
        {
            _faces = new Image<Rgba32>[6];
            _faces[0] = px ?? throw new ArgumentNullException(nameof(px));
            _faces[1] = nx ?? throw new ArgumentNullException(nameof(nx));
            _faces[2] = py ?? throw new ArgumentNullException(nameof(py));
            _faces[3] = ny ?? throw new ArgumentNullException(nameof(ny));
            _faces[4] = pz ?? throw new ArgumentNullException(nameof(pz));
            _faces[5] = nz ?? throw new ArgumentNullException(nameof(nz));

            _width = _faces[0].Width;
            _height = _faces[0].Height;

            for (int i = 1; i < 6; i++)
            {
                if (_faces[i].Width != _width || _faces[i].Height != _height)
                    throw new ArgumentException("[X] All cube map faces must have identical dimensions.");
            }

            _featherUv = _width > 0 ? 2.0 * 2.0 / _width : 0.02;
        }

        public static CubeMapData FromImages(Image<Rgba32>[] faces)
        {
            if (faces == null || faces.Length != 6)
                throw new ArgumentException("[X] Cube map requires exactly 6 face images.", nameof(faces));
            return new CubeMapData(faces[0], faces[1], faces[2], faces[3], faces[4], faces[5]);
        }

        /// <summary>
        /// 将等距圆柱全景图重采样为6面立方体贴图
        /// </summary>
        public static CubeMapData FromEquirectangular(Image<Rgba32> equirect, int faceSize)
        {
            if (faceSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(faceSize));

            var faces = new Image<Rgba32>[6];

            for (int f = 0; f < 6; f++)
            {
                var img = new Image<Rgba32>(faceSize, faceSize);
                for (int y = 0; y < faceSize; y++)
                {
                    double v = -1.0 + (y + 0.5) * (2.0 / faceSize);
                    for (int x = 0; x < faceSize; x++)
                    {
                        double u = -1.0 + (x + 0.5) * (2.0 / faceSize);
                        Double3 dir = QuadSphere.FaceDir(f, u, v);
                        Rgba32 c = SampleEquirect(equirect, dir);
                        img[x, y] = c;
                    }
                }
                faces[f] = img;
            }

            return FromImages(faces);
        }

        private static Rgba32 SampleEquirect(Image<Rgba32> src, in Double3 dir)
        {
            double lon = Math.Atan2(dir.Z, dir.X);
            double lat = Math.Asin(Math.Clamp(dir.Y, -1.0, 1.0));

            double px = (lon + Math.PI) / (2.0 * Math.PI) * src.Width;
            double py = (Math.PI * 0.5 - lat) / Math.PI * src.Height;

            int x0 = ClampPixel((int)Math.Floor(px), src.Width);
            int x1 = ClampPixel(x0 + 1, src.Width);
            int y0 = ClampPixel((int)Math.Floor(py), src.Height);
            int y1 = ClampPixel(y0 + 1, src.Height);

            double fx = px - Math.Floor(px);
            double fy = py - Math.Floor(py);

            Rgba32 c00 = src[x0, y0];
            Rgba32 c10 = src[x1, y0];
            Rgba32 c01 = src[x0, y1];
            Rgba32 c11 = src[x1, y1];

            float r = (float)(Lerp(Lerp(c00.R, c10.R, fx), Lerp(c01.R, c11.R, fx), fy));
            float g = (float)(Lerp(Lerp(c00.G, c10.G, fx), Lerp(c01.G, c11.G, fx), fy));
            float b = (float)(Lerp(Lerp(c00.B, c10.B, fx), Lerp(c01.B, c11.B, fx), fy));
            float a = (float)(Lerp(Lerp(c00.A, c10.A, fx), Lerp(c01.A, c11.A, fx), fy));

            return new Rgba32((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)Math.Clamp(a, 0, 255));
        }

        private static int ClampPixel(int v, int size) => Math.Clamp(v, 0, size - 1);
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        public Image<Rgba32> GetFace(int face)
        {
            return _faces[face];
        }

        // 采样单位方向对应的RGBA并做跨面边缘融合
        public void SampleDirection(in Double3 dir, out float r, out float g, out float b, out float a)
        {
            QuadSphere.DirToFaceUV(dir, out int face, out double u, out double v);
            SampleFaceEdgeBlend(face, u, v, out r, out g, out b, out a);
        }

        // 在面上双线性采样RGBA
        public void SampleFace(int face, double u, double v, out float r, out float g, out float b, out float a)
        {
            double px = (u * 0.5 + 0.5) * (_width - 1);
            double py = (v * 0.5 + 0.5) * (_height - 1);

            int x0 = ClampPixel((int)Math.Floor(px), _width);
            int x1 = ClampPixel(x0 + 1, _width);
            int y0 = ClampPixel((int)Math.Floor(py), _height);
            int y1 = ClampPixel(y0 + 1, _height);

            double fx = Math.Clamp(px - Math.Floor(px), 0.0, 1.0);
            double fy = Math.Clamp(py - Math.Floor(py), 0.0, 1.0);

            Image<Rgba32> faceImage = _faces[face];
            Rgba32 c00 = faceImage[x0, y0];
            Rgba32 c10 = faceImage[x1, y0];
            Rgba32 c01 = faceImage[x0, y1];
            Rgba32 c11 = faceImage[x1, y1];

            r = (float)(Lerp(Lerp(c00.R, c10.R, fx), Lerp(c01.R, c11.R, fx), fy) / 255.0);
            g = (float)(Lerp(Lerp(c00.G, c10.G, fx), Lerp(c01.G, c11.G, fx), fy) / 255.0);
            b = (float)(Lerp(Lerp(c00.B, c10.B, fx), Lerp(c01.B, c11.B, fx), fy) / 255.0);
            a = (float)(Lerp(Lerp(c00.A, c10.A, fx), Lerp(c01.A, c11.A, fx), fy) / 255.0);
        }

        // 跨面边缘融合采样
        public void SampleFaceEdgeBlend(int face, double u, double v, out float r, out float g, out float b, out float a)
        {
            double edgeDist = 1.0 - Math.Max(Math.Abs(u), Math.Abs(v));
            double weight = Math.Clamp(edgeDist / _featherUv, 0.0, 1.0);

            SampleFace(face, u, v, out r, out g, out b, out a);

            if (weight >= 1.0)
                return;

            Double3 dir = QuadSphere.FaceDir(face, u, v);
            QuadSphere.DirToFaceUV(dir, out int otherFace, out double ou, out double ov);
            if (otherFace == face)
                return;

            SampleFace(otherFace, ou, ov, out float or, out float og, out float ob, out float oa);

            r = (float)(r * weight + or * (1.0 - weight));
            g = (float)(g * weight + og * (1.0 - weight));
            b = (float)(b * weight + ob * (1.0 - weight));
            a = (float)(a * weight + oa * (1.0 - weight));
        }
    }
}
