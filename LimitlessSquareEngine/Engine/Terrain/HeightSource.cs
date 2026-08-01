using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine.Terrain
{
    /// <summary>
    /// 按单位方向返回地形高度的接口
    /// </summary>
    public interface IHeightSource
    {
        double SampleDirection(in Double3 direction);
    }

    /// <summary>
    /// 按单位方向返回高度修改增量的接口
    /// </summary>
    public interface IHeightEditProvider
    {
        double SampleEdit(in Double3 direction);
    }

    // 恒定高度源
    public sealed class ConstantHeightSource : IHeightSource
    {
        private readonly double _height;

        public ConstantHeightSource(double height)
        {
            _height = height;
        }

        public double SampleDirection(in Double3 direction) => _height;
    }

    // 从6面高度图采样高度
    public sealed class MapHeightSource : IHeightSource
    {
        private readonly CubeMapData _map;
        private readonly double _minHeight;
        private readonly double _maxHeight;

        public MapHeightSource(CubeMapData map, double minHeight, double maxHeight)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _minHeight = minHeight;
            _maxHeight = maxHeight;
        }

        public double SampleDirection(in Double3 direction)
        {
            _map.SampleDirection(direction, out float r, out float g, out float b, out _);
            float luminance = (r + g + b) * (1f / 3f);
            return _minHeight + luminance * (_maxHeight - _minHeight);
        }
    }

    // 生成确定性噪声高度
    public sealed class NoiseHeightSource : IHeightSource
    {
        private readonly Noise3D _noise;
        private readonly double _frequency;
        private readonly int _octaves;

        public NoiseHeightSource(uint seed, double frequency, int octaves = 4)
        {
            _noise = new Noise3D(seed);
            _frequency = frequency;
            _octaves = octaves;
        }

        public double SampleDirection(in Double3 direction)
        {
            return _noise.Fbm(
                direction.X * _frequency,
                direction.Y * _frequency,
                direction.Z * _frequency,
                _octaves);
        }
    }

    // 组合多个高度源与修改层
    public sealed class CompositeHeightSource : IHeightSource
    {
        private readonly List<(IHeightSource Source, double Multiplier, double Offset)> _layers = new();
        private IHeightEditProvider? _edit;

        public CompositeHeightSource()
        {
        }

        public void AddLayer(IHeightSource source, double multiplier = 1.0, double offset = 0.0)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            _layers.Add((source, multiplier, offset));
        }

        public void ClearLayers()
        {
            _layers.Clear();
        }

        public void SetEditProvider(IHeightEditProvider? edit)
        {
            _edit = edit;
        }

        public IHeightEditProvider? EditProvider => _edit;

        public double SampleDirection(in Double3 direction)
        {
            double height = 0.0;
            for (int i = 0; i < _layers.Count; i++)
            {
                var layer = _layers[i];
                height += layer.Source.SampleDirection(direction) * layer.Multiplier + layer.Offset;
            }

            if (_edit != null)
                height += _edit.SampleEdit(direction);

            return height;
        }
    }
}
