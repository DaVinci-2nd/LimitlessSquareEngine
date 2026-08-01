using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine.Terrain
{
    // 地形流兴趣点
    public readonly struct TerrainInterest
    {
        public readonly Double3 PlanetLocalPos;
        public readonly double Radius;
        public readonly int MaxLod;
        public readonly double Priority;
        public readonly bool HasFrustum;
        public readonly Double3 FrustumForward;
        public readonly double FrustumFovRadians;
        public readonly double FrustumAspect;

        public TerrainInterest(Double3 planetLocalPos, double radius, int maxLod, double priority = 1.0)
        {
            PlanetLocalPos = planetLocalPos;
            Radius = radius;
            MaxLod = maxLod;
            Priority = priority;
            HasFrustum = false;
            FrustumForward = Double3.Zero;
            FrustumFovRadians = 0.0;
            FrustumAspect = 1.0;
        }

        public TerrainInterest(
            Double3 planetLocalPos, double radius, int maxLod, double priority,
            in Double3 frustumForward, double frustumFovRadians, double frustumAspect)
        {
            PlanetLocalPos = planetLocalPos;
            Radius = radius;
            MaxLod = maxLod;
            Priority = priority;
            HasFrustum = true;
            FrustumForward = frustumForward;
            FrustumFovRadians = frustumFovRadians;
            FrustumAspect = frustumAspect;
        }
    }

    /// <summary>
    /// 提供地形流兴趣点的接口
    /// </summary>
    public abstract class TerrainInterestSource
    {
        public abstract IEnumerable<TerrainInterest> GetInterests();
    }

    // 手动维护的兴趣源
    public sealed class ManualInterestSource : TerrainInterestSource
    {
        private readonly List<TerrainInterest> _interests = new();

        public void Add(TerrainInterest interest)
        {
            _interests.Add(interest);
        }

        public void Add(Double3 planetLocalPos, double radius, int maxLod, double priority = 1.0)
        {
            _interests.Add(new TerrainInterest(planetLocalPos, radius, maxLod, priority));
        }

        public void Clear()
        {
            _interests.Clear();
        }

        public int Count => _interests.Count;

        public override IEnumerable<TerrainInterest> GetInterests()
        {
            return _interests;
        }
    }

    // 相机驱动的渲染兴趣源
    public sealed class CameraInterestSource : TerrainInterestSource
    {
        private readonly List<(Double3 WorldPos, int MaxLod, double Priority, Double3 Forward, double FovRadians, double Aspect)> _cameras = new();

        public void SetCameras(IEnumerable<(Double3 WorldPos, int MaxLod, double Priority, Double3 Forward, double FovRadians, double Aspect)> cameras)
        {
            _cameras.Clear();
            if (cameras == null)
                return;
            foreach (var c in cameras)
                _cameras.Add(c);
        }

        public void Clear()
        {
            _cameras.Clear();
        }

        public override IEnumerable<TerrainInterest> GetInterests()
        {
            for (int i = 0; i < _cameras.Count; i++)
            {
                var c = _cameras[i];
                yield return new TerrainInterest(c.WorldPos, double.PositiveInfinity, c.MaxLod, c.Priority,
                    c.Forward, c.FovRadians, c.Aspect);
            }
        }
    }
}
