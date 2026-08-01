using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LimitlessSquareEngine.Engine.Terrain
{
    // 后台构建委托
    public delegate object? TerrainBuildDelegate(TerrainTile tile, int lod, object? buildParams);

    // 主线程提交委托
    public delegate void TerrainCommitDelegate(TerrainTile tile, int lod, object? buildData);

    // 主线程卸载委托
    public delegate void TerrainUnloadDelegate(TerrainTile tile);

    internal sealed class BuildJob
    {
        public TerrainTile Tile = null!;
        public TileArtifactKind Kind;
        public double Priority;
        public long Sequence;
        public object? BuildParams;
    }

    internal enum TileArtifactKind
    {
        Render = 0,
        Physics = 1
    }

    internal sealed class CommitJob
    {
        public TerrainTile Tile = null!;
        public TileArtifactKind Kind;
        public int Lod;
        public object? BuildData;
    }

    // 地形双流调度器
    public sealed class TerrainStreamer
    {
        public TerrainProfile Profile = new();

        public readonly List<TerrainInterestSource> RenderInterestSources = new();
        public readonly List<TerrainInterestSource> PhysicsInterestSources = new();

        public double PlanetRadius = 6371000.0;

        public double UnloadGraceSeconds = 1.0;

        public TerrainBuildDelegate? RenderBuilder;
        public TerrainCommitDelegate? RenderCommitter;
        public TerrainUnloadDelegate? RenderUnloader;

        public TerrainBuildDelegate? PhysicsBuilder;
        public TerrainCommitDelegate? PhysicsCommitter;
        public TerrainUnloadDelegate? PhysicsUnloader;

        private readonly Dictionary<TileKey, TerrainTile> _tiles = new();
        private readonly ConcurrentQueue<BuildJob> _pendingRender = new();
        private readonly ConcurrentQueue<BuildJob> _pendingPhysics = new();
        private readonly ConcurrentQueue<CommitJob> _commitQueue = new();

        private readonly List<BuildJob> _inFlight = new();
        private readonly object _inFlightLock = new();
        private int _maxInFlight = 8;

        public int MaxRenderJobsPerTick = 16;
        public int MaxPhysicsJobsPerTick = 8;

        public int MaxCommitsPerTick = 6;

        public int MaxRequiredProcessedPerTick = 2000;
        private const int MaxRequiredTilesPerTick = 50000;

        private long _jobSequence = 0;
        private double _time = 0.0;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public int TileCount => _tiles.Count;
        public int PendingRenderCount => _pendingRender.Count;
        public int PendingPhysicsCount => _pendingPhysics.Count;
        public int InFlightCount => _inFlight.Count;

        public IEnumerable<TerrainTile> EnumerateTiles()
        {
            return _tiles.Values;
        }

        /// <summary>
        /// 每帧推进调度
        /// </summary>
        public void Tick()
        {
            _time += _stopwatch.Elapsed.TotalSeconds;
            _stopwatch.Restart();

            ComputeTargetsAndEnqueue();

            EnsureWorkers();

            DrainCommits();
        }

        private void ComputeTargetsAndEnqueue()
        {
            var requiredRender = new HashSet<TileKey>();
            var requiredPhysics = new HashSet<TileKey>();

            // 渲染流
            foreach (TerrainInterestSource source in RenderInterestSources)
            {
                foreach (TerrainInterest interest in source.GetInterests())
                {
                    CollectRequiredTiles(interest, requiredRender, isPhysics: false);
                }
            }

            // 物理流
            foreach (TerrainInterestSource source in PhysicsInterestSources)
            {
                foreach (TerrainInterest interest in source.GetInterests())
                {
                    CollectRequiredTiles(interest, requiredPhysics, isPhysics: true);
                }
            }

            // 处理所需区块并卸载多余区块
            int enqueuedRender = 0;
            int enqueuedPhysics = 0;
            int processedRender = 0;

            foreach (TileKey key in requiredRender)
            {
                if (processedRender >= MaxRequiredProcessedPerTick)
                    break;
                processedRender++;

                if (!_tiles.TryGetValue(key, out TerrainTile? tile))
                {
                    tile = new TerrainTile(key);
                    _tiles[key] = tile;
                }

                TerrainRenderArtifact artifact = tile.EnsureRender();
                tile.RenderLastAccessTime = _time;

                int[] neighborLevels = ComputeNeighborLevels(requiredRender, key);
                if (artifact.State == TileArtifactState.Ready && HasStitchChanged(artifact, neighborLevels))
                    artifact.State = TileArtifactState.Invalidated;

                if (artifact.State != TileArtifactState.Ready)
                {
                    if (enqueuedRender < MaxRenderJobsPerTick)
                    {
                        EnqueueRenderBuild(tile, neighborLevels);
                        enqueuedRender++;
                    }
                }
            }

            foreach (TileKey key in requiredPhysics)
            {
                if (!_tiles.TryGetValue(key, out TerrainTile? tile))
                {
                    tile = new TerrainTile(key);
                    _tiles[key] = tile;
                }

                TerrainPhysicsArtifact artifact = tile.EnsurePhysics();
                tile.PhysicsLastAccessTime = _time;
                if (artifact.State != TileArtifactState.Ready)
                {
                    if (enqueuedPhysics < MaxPhysicsJobsPerTick)
                    {
                        EnqueuePhysicsBuild(tile);
                        enqueuedPhysics++;
                    }
                }
            }

            // 卸载
            List<TileKey>? toRemove = null;
            foreach (var pair in _tiles)
            {
                TerrainTile tile = pair.Value;

                bool needRender = tile.Render != null && requiredRender.Contains(pair.Key);
                bool needPhysics = tile.Physics != null && requiredPhysics.Contains(pair.Key);

                if (tile.Render != null && !needRender &&
                    CanUnload(tile.Render.State, tile.RenderLastAccessTime))
                {
                    RenderUnloader?.Invoke(tile);
                    tile.ClearRender();
                }

                if (tile.Physics != null && !needPhysics &&
                    CanUnload(tile.Physics.State, tile.PhysicsLastAccessTime))
                {
                    PhysicsUnloader?.Invoke(tile);
                    tile.ClearPhysics();
                }

                if (!tile.HasAnyArtifact)
                {
                    toRemove ??= new List<TileKey>();
                    toRemove.Add(pair.Key);
                }
            }

            if (toRemove != null)
            {
                foreach (TileKey key in toRemove)
                    _tiles.Remove(key);
            }
        }

        private bool CanUnload(TileArtifactState state, double lastAccessTime)
        {
            if (state == TileArtifactState.Queued || state == TileArtifactState.Building)
                return false;
            return _time - lastAccessTime > UnloadGraceSeconds;
        }

        // 收集兴趣点所需的区块
        private void CollectRequiredTiles(TerrainInterest interest, HashSet<TileKey> required, bool isPhysics)
        {
            Double3 interestPos = interest.PlanetLocalPos;
            int maxLevel = Math.Min(
                interest.MaxLod,
                isPhysics ? Profile.PhysicsMaxLevel : Profile.RenderMaxLevel);

            Double3 camDir = Normalize(interestPos);
            double camDist = Length(interestPos);
            double horizonCos = camDist > PlanetRadius ? PlanetRadius / camDist : -1.0;

            var stack = new Stack<TileKey>(64);
            for (int face = 0; face < QuadSphere.FaceCount; face++)
                stack.Push(new TileKey(face, 0, 0, 0));

            var childBuffer = new TileKey[4];

            // 区块最近处需更细时细分全部子块
            while (stack.Count > 0)
            {
                if (required.Count >= MaxRequiredTilesPerTick)
                    break;

                TileKey node = stack.Pop();
                if (required.Contains(node))
                    continue;

                Double3 centerDir = QuadSphere.TileCenterDir(node);
                double centerDist = DistanceToTile(interestPos, centerDir);
                double nodeAngle = QuadSphere.TileMaxAngularRadius(node);
                double nodeRadius = nodeAngle * PlanetRadius;
                double dMin = Math.Max(0.0, centerDist - nodeRadius);

                // 行星背侧剔除
                if (horizonCos > -0.5)
                {
                    double dotC = centerDir.X * camDir.X + centerDir.Y * camDir.Y + centerDir.Z * camDir.Z;
                    double angleFromCam = Math.Acos(Math.Clamp(dotC, -1.0, 1.0));
                    double horizonAngle = Math.Acos(Math.Clamp(horizonCos, -1.0, 1.0));
                    if (angleFromCam - nodeAngle > horizonAngle)
                        continue;
                }

                // 区块整个位于兴趣区域外则剔除
                if (!double.IsPositiveInfinity(interest.Radius) && dMin > interest.Radius + nodeRadius)
                    continue;

                int targetLod = isPhysics
                    ? Profile.PhysicsLodForDistance(dMin)
                    : Profile.RenderLodForDistance(dMin);
                if (targetLod > maxLevel)
                    targetLod = maxLevel;

                if (node.Level >= maxLevel || node.Level >= targetLod)
                {
                    required.Add(node);
                    continue;
                }

                QuadSphere.GetChildren(node, childBuffer);
                for (int i = 0; i < 4; i++)
                    stack.Push(childBuffer[i]);
            }
        }

        private double DistanceToTile(in Double3 interestPos, in Double3 dir)
        {
            double px = dir.X * PlanetRadius;
            double py = dir.Y * PlanetRadius;
            double pz = dir.Z * PlanetRadius;
            double dx = interestPos.X - px;
            double dy = interestPos.Y - py;
            double dz = interestPos.Z - pz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static Double3 Normalize(in Double3 v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (len <= 1e-300)
                return new Double3(0, 0, 1);
            return new Double3(v.X / len, v.Y / len, v.Z / len);
        }

        private static double Length(in Double3 v)
        {
            return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        }

        private void EnqueueRenderBuild(TerrainTile tile, int[] neighborLevels)
        {
            TerrainRenderArtifact artifact = tile.EnsureRender();
            if (artifact.State == TileArtifactState.Queued || artifact.State == TileArtifactState.Building)
                return;

            artifact.State = TileArtifactState.Queued;
            var job = new BuildJob
            {
                Tile = tile,
                Kind = TileArtifactKind.Render,
                Priority = 1.0,
                Sequence = Interlocked.Increment(ref _jobSequence),
                BuildParams = new RenderBuildParams
                {
                    GridSize = Profile.RenderBaseTileResolution,
                    VoxelGridSize = Profile.RenderVoxelGridSize,
                    ShellThickness = Profile.RenderVoxelShellThickness,
                    NeighborLevels = (int[])neighborLevels.Clone()
                }
            };
            _pendingRender.Enqueue(job);
        }

        private int[] ComputeNeighborLevels(HashSet<TileKey> required, TileKey key)
        {
            var levels = new int[4];
            for (int e = 0; e < 4; e++)
                levels[e] = FindNeighborLevel(required, key, e);
            return levels;
        }

        private int FindNeighborLevel(HashSet<TileKey> required, TileKey key, int edge)
        {
            if (!QuadSphere.TryGetEdgeNeighbor(key, edge, out TileKey neighbor))
                return key.Level;

            if (required.Contains(neighbor))
                return neighbor.Level;

            TileKey ancestor = neighbor;
            while (ancestor.Level > 0)
            {
                ancestor = QuadSphere.GetParent(ancestor);
                if (required.Contains(ancestor))
                    return ancestor.Level;
            }

            return key.Level;
        }

        private bool HasStitchChanged(TerrainRenderArtifact artifact, int[] levels)
        {
            if (artifact.BuiltStitchLevels.Length != 4)
                return true;

            for (int e = 0; e < 4; e++)
            {
                if (artifact.BuiltStitchLevels[e] != levels[e])
                    return true;
            }

            return false;
        }

        private void EnqueuePhysicsBuild(TerrainTile tile)
        {
            TerrainPhysicsArtifact artifact = tile.EnsurePhysics();
            if (artifact.State == TileArtifactState.Queued || artifact.State == TileArtifactState.Building)
                return;

            artifact.State = TileArtifactState.Queued;
            var job = new BuildJob
            {
                Tile = tile,
                Kind = TileArtifactKind.Physics,
                Priority = 1.0,
                Sequence = Interlocked.Increment(ref _jobSequence),
                BuildParams = new PhysicsBuildParams
                {
                    GridSize = Profile.PhysicsVoxelGridSize,
                    ShellThickness = Profile.PhysicsVoxelShellThickness
                }
            };
            _pendingPhysics.Enqueue(job);
        }

        private void EnsureWorkers()
        {
            bool needMore;
            lock (_inFlightLock)
                needMore = _inFlight.Count < _maxInFlight && (_pendingRender.Count > 0 || _pendingPhysics.Count > 0);

            if (!needMore)
                return;

            ThreadPool.QueueUserWorkItem(WorkerLoop);
        }

        private void WorkerLoop(object? _)
        {
            while (true)
            {
                BuildJob? job = null;
                if (_pendingRender.TryDequeue(out BuildJob? rj))
                    job = rj;
                else if (_pendingPhysics.TryDequeue(out BuildJob? pj))
                    job = pj;

                if (job == null)
                    return;

                lock (_inFlightLock)
                {
                    if (_inFlight.Count >= _maxInFlight)
                    {
                        if (job.Kind == TileArtifactKind.Render)
                            _pendingRender.Enqueue(job);
                        else
                            _pendingPhysics.Enqueue(job);
                        return;
                    }
                    _inFlight.Add(job);
                }

                try
                {
                    object? buildData = job.Kind switch
                    {
                        TileArtifactKind.Render => RenderBuilder?.Invoke(job.Tile, job.Tile.Key.Level, job.BuildParams),
                        _ => PhysicsBuilder?.Invoke(job.Tile, job.Tile.Key.Level, job.BuildParams)
                    };

                    _commitQueue.Enqueue(new CommitJob
                    {
                        Tile = job.Tile,
                        Kind = job.Kind,
                        Lod = job.Tile.Key.Level,
                        BuildData = buildData
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[X] Terrain build failed for {job.Tile.Key} ({job.Kind}): {ex.Message}");
                }
                finally
                {
                    lock (_inFlightLock)
                        _inFlight.Remove(job);
                }
            }
        }

        private void DrainCommits()
        {
            int processed = 0;
            while (processed < MaxCommitsPerTick && _commitQueue.TryDequeue(out CommitJob? job))
            {
                processed++;

                if (!_tiles.TryGetValue(job.Tile.Key, out TerrainTile? current) || !ReferenceEquals(current, job.Tile))
                    continue;

                switch (job.Kind)
                {
                    case TileArtifactKind.Render:
                        {
                            TerrainRenderArtifact artifact = current.EnsureRender();
                            artifact.Lod = job.Lod;
                            RenderCommitter?.Invoke(current, job.Lod, job.BuildData);
                            artifact.State = TileArtifactState.Ready;
                            current.RenderLastAccessTime = _time;
                            break;
                        }
                    case TileArtifactKind.Physics:
                        {
                            TerrainPhysicsArtifact artifact = current.EnsurePhysics();
                            artifact.Lod = job.Lod;
                            PhysicsCommitter?.Invoke(current, job.Lod, job.BuildData);
                            artifact.State = TileArtifactState.Ready;
                            current.PhysicsLastAccessTime = _time;
                            break;
                        }
                }
            }
        }

        /// <summary>
        /// 使指定区域的产物失效
        /// </summary>
        public void InvalidateRegion(in Double3 planetLocalCenter, double radiusMeters, bool render, bool physics)
        {
            foreach (var pair in _tiles)
            {
                TerrainTile tile = pair.Value;
                Double3 centerDir = QuadSphere.TileCenterDir(pair.Key);
                double dist = DistanceToTile(planetLocalCenter, centerDir);
                double nodeRadius = QuadSphere.TileAngularRadius(pair.Key) * PlanetRadius;
                if (dist > radiusMeters + nodeRadius)
                    continue;

                if (render && tile.Render != null && tile.Render.State == TileArtifactState.Ready)
                    tile.Render.State = TileArtifactState.Invalidated;
                if (physics && tile.Physics != null && tile.Physics.State == TileArtifactState.Ready)
                    tile.Physics.State = TileArtifactState.Invalidated;
            }
        }

        public void Clear()
        {
            foreach (TerrainTile tile in _tiles.Values)
            {
                if (tile.Render != null)
                {
                    RenderUnloader?.Invoke(tile);
                    tile.ClearRender();
                }
                if (tile.Physics != null)
                {
                    PhysicsUnloader?.Invoke(tile);
                    tile.ClearPhysics();
                }
            }
            _tiles.Clear();
            while (_pendingRender.TryDequeue(out _)) { }
            while (_pendingPhysics.TryDequeue(out _)) { }
            while (_commitQueue.TryDequeue(out _)) { }
        }
    }

    // 渲染构建参数
    public sealed class RenderBuildParams
    {
        public int GridSize;
        public int VoxelGridSize;
        public int ShellThickness;
        public int[] NeighborLevels = new int[4];
    }

    // 物理构建参数
    public sealed class PhysicsBuildParams
    {
        public int GridSize;
        public int ShellThickness;
    }
}
