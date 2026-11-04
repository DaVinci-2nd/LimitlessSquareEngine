using System;

namespace LimitlessSquareEngine.Engine
{
    /// <summary>
    /// 整体地形高度规则接口
    /// </summary>
    public interface ITerrainHeightRule
    {
        double SampleHeight(in Double3 direction);
    }

    // 恒定零高度规则
    public sealed class ZeroHeightRule : ITerrainHeightRule
    {
        public double SampleHeight(in Double3 direction)
        {
            return 0.0;
        }
    }

    // 多尺度地形高度规则
    public sealed class PerlinHeightRule : ITerrainHeightRule
    {
        private readonly Noise3D _noise;
        private readonly Noise3D _detailNoise;
        private readonly Noise3D _ridgeNoise;
        private readonly double[] _frequencies;
        private readonly double[] _amplitudes;
        private readonly int _octaves;

        public PerlinHeightRule(uint seed, double[] frequencies, double[] amplitudes, int octaves = 4)
        {
            _noise = new Noise3D(seed);
            _detailNoise = new Noise3D(seed ^ 0x9E3779B9u);
            _ridgeNoise = new Noise3D(seed ^ 0x85EBCA6Bu);
            _frequencies = frequencies;
            _amplitudes = amplitudes;
            _octaves = octaves;
        }

        public double SampleHeight(in Double3 direction)
        {
            double h = 0.0;

            for (int i = 0; i < _frequencies.Length; i++)
            {
                double f = _frequencies[i];
                h += _noise.Fbm(direction.X * f, direction.Y * f, direction.Z * f, _octaves) * _amplitudes[i];
            }

            double mass = _noise.Fbm(direction.X * 3.0, direction.Y * 3.0, direction.Z * 3.0, 3);
            double mountain = Math.Clamp(mass * 1.8 + 0.35, 0.0, 1.0);
            double plain = 1.0 - mountain;

            double ridge = 0.0;
            double ridgeAmp = 1.0;
            double ridgeFreq = 64.0;

            for (int i = 0; i < 4; i++)
            {
                double n = _ridgeNoise.Fbm(
                    direction.X * ridgeFreq,
                    direction.Y * ridgeFreq,
                    direction.Z * ridgeFreq,
                    2);

                ridge += (1.0 - Math.Abs(n)) * ridgeAmp;
                ridgeAmp *= 0.5;
                ridgeFreq *= 2.0;
            }

            h += ridge * mountain * 2200.0;

            double detailFreq = 8192.0;

            for (int i = 0; i < 3; i++)
            {
                double n = _detailNoise.Fbm(
                    direction.X * detailFreq,
                    direction.Y * detailFreq,
                    direction.Z * detailFreq,
                    2);

                double steepness = mountain * 9.0 + plain * 1.0;
                h += n * steepness;
                detailFreq *= 2.0;
            }

            return h;
        }
    }

    // 自定义函数高度规则
    public sealed class DelegateHeightRule : ITerrainHeightRule
    {
        private readonly Func<Double3, double> _func;

        public DelegateHeightRule(Func<Double3, double> func)
        {
            _func = func;
        }

        public double SampleHeight(in Double3 direction)
        {
            return _func(direction);
        }
    }

    // 程序化规则采样高度源
    public sealed class RuleHeightSource : IHeightSource
    {
        private readonly ITerrainHeightRule _rule;

        public RuleHeightSource(ITerrainHeightRule rule)
        {
            _rule = rule;
        }

        public double SampleDirection(in Double3 direction)
        {
            return _rule.SampleHeight(direction);
        }
    }

    // 整体地形高度图生成器
    public static class TerrainMapGenerator
    {
        // 返回随机种子
        public static uint NextSeed()
        {
            var bytes = new byte[4];
            Random.Shared.NextBytes(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }

        // 按种子创建多层柏林规则
        public static PerlinHeightRule CreatePerlinRule(
            uint? seed,
            double[]? frequencies = null,
            double[]? amplitudes = null)
        {
            uint effectiveSeed = seed ?? NextSeed();
            double[] freqs = frequencies ?? new[] { 2.0, 8.0, 32.0, 128.0, 512.0, 2048.0 };
            double[] amps = amplitudes ?? new[] { 3000.0, 2500.0, 2000.0, 1500.0, 700.0, 300.0 };
            return new PerlinHeightRule(effectiveSeed, freqs, amps);
        }
    }
}
