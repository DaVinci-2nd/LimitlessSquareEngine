using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace LimitlessSquareEngine.Engine
{
    // 近景体素地形流式层
    public sealed class TerrainVoxel
    {
        private sealed class VoxelBuildJob
        {
            public VoxelBlock Block = null!;
        }

        private sealed class VoxelCommitJob
        {
            public VoxelBlock Block = null!;
            public VoxelMeshResult Result = null!;
        }

        public double CoverRadius = 128.0;
        public double CoverExtent => VoxelBlock.CellSize * VoxelBlock.VoxelSize * 0.8660254037844386;
        public int MaxBlocksPerTick = 24;
        public int MaxCommitsPerTick = 6;
        public int MaxInFlightBuilds = 4;
        public double UnloadGraceSeconds = 1.0;

        private readonly Terrain _terrain;
        private readonly VoxelMesher _mesher = new();
        private readonly Dictionary<VoxelBlockKey, VoxelBlock> _blocks = new();
        private readonly Dictionary<VoxelBlockKey, string> _meshIds = new();
        private readonly Dictionary<VoxelBlockKey, string> _objectIds = new();
        private readonly ConcurrentQueue<VoxelBuildJob> _pending = new();
        private readonly ConcurrentQueue<VoxelCommitJob> _commits = new();
        private readonly List<VoxelBuildJob> _inFlight = new();
        private readonly object _inFlightLock = new();
        private readonly List<VoxelEditBrush> _brushes = new();
        private readonly object _brushLock = new();

        private Double3 _center = Double3.Zero;
        private double _time = 0.0;
        private string _voxelMaterialKey = "";
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public TerrainVoxel(Terrain terrain)
        {
            _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        }

        public int BlockCount => _blocks.Count;

        public int ReadyBlockCount
        {
            get
            {
                int count = 0;
                foreach (VoxelBlock block in _blocks.Values)
                {
                    if (block.State == TileArtifactState.Ready)
                        count++;
                }
                return count;
            }
        }

        public bool IsCoverageReady
        {
            get
            {
                if (_blocks.Count == 0)
                    return false;

                int ready = 0;

                foreach (VoxelBlock block in _blocks.Values)
                {
                    if (block.State == TileArtifactState.Ready)
                        ready++;
                }

                return ready * 2 >= _blocks.Count;
            }
        }

        public void AddBrush(VoxelEditBrush brush)
        {
            if (brush == null)
                return;

            lock (_brushLock)
                _brushes.Add(brush);

            InvalidateRegion(brush.Center, brush.Radius);
        }

        public void ClearBrushes()
        {
            lock (_brushLock)
                _brushes.Clear();

            foreach (VoxelBlock block in _blocks.Values)
                block.State = TileArtifactState.Invalidated;
        }

        public void Clear()
        {
            Graphics? graphics = Scene.BoundGraphics;

            foreach (VoxelBlock block in _blocks.Values)
                UnloadBlockGraphics(block, graphics);

            _blocks.Clear();
            _meshIds.Clear();
            _objectIds.Clear();

            while (_pending.TryDequeue(out _)) { }
            while (_commits.TryDequeue(out _)) { }

            lock (_inFlightLock)
                _inFlight.Clear();
        }

        public void Tick()
        {
            _time += _stopwatch.Elapsed.TotalSeconds;
            _stopwatch.Restart();

            if (!_terrain.StreamingEnabled)
                return;

            RefreshCenter();
            ComputeWindow();
            EnsureWorkers();
            DrainCommits();
        }

        private void RefreshCenter()
        {
            foreach (SceneCameraQueueItem item in Scene.GetCameraQueue(_terrain.SceneId))
            {
                _center = Scene.GetPosition(_terrain.SceneId, item.ObjectId);
                return;
            }
        }

        private void ComputeWindow()
        {
            double half = CoverRadius;
            int bxMin = (int)Math.Floor((_center.X - half) / VoxelBlock.CellSize);
            int bxMax = (int)Math.Floor((_center.X + half) / VoxelBlock.CellSize);
            int byMin = (int)Math.Floor((_center.Y - half) / VoxelBlock.CellSize);
            int byMax = (int)Math.Floor((_center.Y + half) / VoxelBlock.CellSize);
            int bzMin = (int)Math.Floor((_center.Z - half) / VoxelBlock.CellSize);
            int bzMax = (int)Math.Floor((_center.Z + half) / VoxelBlock.CellSize);

            int enqueued = 0;

            for (int bz = bzMin; bz <= bzMax; bz++)
            {
                for (int by = byMin; by <= byMax; by++)
                {
                    for (int bx = bxMin; bx <= bxMax; bx++)
                    {
                        var key = new VoxelBlockKey(bx, by, bz);

                        if (_blocks.TryGetValue(key, out VoxelBlock? existing))
                        {
                            existing.LastAccessTime = _time;

                            if (existing.State == TileArtifactState.Invalidated &&
                                enqueued < MaxBlocksPerTick)
                            {
                                existing.State = TileArtifactState.Queued;
                                _pending.Enqueue(new VoxelBuildJob { Block = existing });
                                enqueued++;
                            }

                            continue;
                        }

                        var origin = new Double3(
                            bx * VoxelBlock.CellSize * VoxelBlock.VoxelSize,
                            by * VoxelBlock.CellSize * VoxelBlock.VoxelSize,
                            bz * VoxelBlock.CellSize * VoxelBlock.VoxelSize);

                        if (!IsBlockOnSurface(origin))
                            continue;

                        var block = new VoxelBlock(key, origin);
                        _blocks[key] = block;

                        if (enqueued < MaxBlocksPerTick)
                        {
                            block.State = TileArtifactState.Queued;
                            _pending.Enqueue(new VoxelBuildJob { Block = block });
                            enqueued++;
                        }
                        else
                        {
                            block.State = TileArtifactState.Invalidated;
                        }
                    }
                }
            }

            List<VoxelBlockKey>? toRemove = null;

            foreach (var pair in _blocks)
            {
                VoxelBlock block = pair.Value;

                if (block.State == TileArtifactState.Queued || block.State == TileArtifactState.Building)
                    continue;

                if (_time - block.LastAccessTime <= UnloadGraceSeconds)
                    continue;

                toRemove ??= new List<VoxelBlockKey>();
                toRemove.Add(pair.Key);
            }

            if (toRemove != null)
            {
                Graphics? graphics = Scene.BoundGraphics;

                foreach (VoxelBlockKey key in toRemove)
                {
                    if (!_blocks.TryGetValue(key, out VoxelBlock? block))
                        continue;

                    UnloadBlockGraphics(block, graphics);
                    _blocks.Remove(key);
                }
            }
        }

        private bool IsBlockOnSurface(in Double3 origin)
        {
            double half = VoxelBlock.CellSize * VoxelBlock.VoxelSize * 0.5;
            double extent = VoxelBlock.CellSize * VoxelBlock.VoxelSize * 0.8660254037844386;
            double cx = origin.X + half;
            double cy = origin.Y + half;
            double cz = origin.Z + half;

            double dx = cx - _center.X;
            double dy = cy - _center.Y;
            double dz = cz - _center.Z;

            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) - extent >= CoverRadius)
                return false;

            double len = Math.Sqrt(cx * cx + cy * cy + cz * cz);

            if (len <= 1e-300)
                return true;

            var dir = new Double3(cx / len, cy / len, cz / len);
            double surface = _terrain.Radius + _terrain.Height.SampleDirection(dir);

            return Math.Abs(len - surface) <= extent + 1.0;
        }

        private void InvalidateRegion(in Double3 center, double radius)
        {
            double half = VoxelBlock.CellSize * VoxelBlock.VoxelSize * 0.5;
            double extent = VoxelBlock.CellSize * VoxelBlock.VoxelSize * 0.8660254037844386;

            foreach (VoxelBlock block in _blocks.Values)
            {
                double dx = block.Origin.X + half - center.X;
                double dy = block.Origin.Y + half - center.Y;
                double dz = block.Origin.Z + half - center.Z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                if (dist > radius + extent)
                    continue;

                if (block.State == TileArtifactState.Ready || block.State == TileArtifactState.None)
                    block.State = TileArtifactState.Invalidated;
            }
        }

        private void EnsureWorkers()
        {
            bool needMore;

            lock (_inFlightLock)
                needMore = _inFlight.Count < MaxInFlightBuilds && _pending.Count > 0;

            if (!needMore)
                return;

            ThreadPool.QueueUserWorkItem(WorkerLoop);
        }

        private void WorkerLoop(object? _)
        {
            while (true)
            {
                if (!_pending.TryDequeue(out VoxelBuildJob? job))
                    return;

                lock (_inFlightLock)
                {
                    if (_inFlight.Count >= MaxInFlightBuilds)
                    {
                        _pending.Enqueue(job);
                        return;
                    }

                    _inFlight.Add(job);
                }

                try
                {
                    job.Block.State = TileArtifactState.Building;

                    lock (_brushLock)
                        job.Block.Sample(_terrain.Height, _terrain.Radius, _brushes);

                    VoxelMeshResult result = _mesher.Build(
                        job.Block,
                        _terrain.TilingPerFace,
                        _terrain.AtlasTilesX,
                        _terrain.AtlasTilesY);

                    _commits.Enqueue(new VoxelCommitJob
                    {
                        Block = job.Block,
                        Result = result
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[X] Voxel build failed for {job.Block.Key}: {ex.Message}");
                    job.Block.State = TileArtifactState.None;
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

            while (processed < MaxCommitsPerTick && _commits.TryDequeue(out VoxelCommitJob? job))
            {
                processed++;

                if (!_blocks.TryGetValue(job.Block.Key, out VoxelBlock? current) ||
                    !ReferenceEquals(current, job.Block))
                {
                    continue;
                }

                CommitBlock(job.Block, job.Result);
            }
        }

        private void EnsureVoxelMaterial()
        {
            if (!string.IsNullOrEmpty(_voxelMaterialKey))
                return;

            string atlasKey = string.IsNullOrWhiteSpace(_terrain.AtlasTextureKey)
                ? "__terrain_atlas:" + _terrain.SceneId + ":" + _terrain.ObjectId
                : _terrain.AtlasTextureKey;

            _voxelMaterialKey = "__terrain_voxel:" + _terrain.SceneId + ":" + _terrain.ObjectId;

            if (!Program._generatedMaterialJsonRegistry.ContainsKey(_voxelMaterialKey))
                Program._generatedMaterialJsonRegistry[_voxelMaterialKey] = BuildVoxelMaterialJson(atlasKey);
        }

        private static string BuildVoxelMaterialJson(string atlasKey)
        {
            var parameters = new Dictionary<string, object?>
            {
                ["uColor"] = new[] { 1.0, 1.0, 1.0, 1.0 },
                ["uUseTexture"] = 1,
                ["uTexture"] = atlasKey,
                ["uTextureWrap"] = "Repeat",
                ["uUseAlphaCutoff"] = 0,
                ["uUseNormalTexture"] = 0,
                ["uAmbientStrength"] = 1.0,
                ["uSpecularIntensity"] = 0.0,
                ["uSpecularColor"] = new[] { 1.0, 1.0, 1.0 },
                ["uRimIntensity"] = 0.0,
                ["uSmoothness"] = 0.0,
                ["uMetallic"] = 0.0,
                ["uReceiveShadow"] = 1,
                ["uCastShadow"] = 1,
                ["uReceiveReflection"] = 0,
                ["uEnableColorBanding"] = 0,
                ["uEnableOutline"] = 0,
                ["uCull"] = "front"
            };

            var root = new Dictionary<string, object?>
            {
                ["assetType"] = "Material",
                ["shader"] = "Shaders/Builtin/Lit",
                ["parameters"] = parameters
            };

            return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        }

        private void CommitBlock(VoxelBlock block, VoxelMeshResult result)
        {
            Graphics? graphics = Scene.BoundGraphics;

            if (graphics == null)
            {
                block.State = TileArtifactState.None;
                return;
            }

            if (result.IsEmpty)
            {
                UnloadBlockGraphics(block, graphics);
                block.State = TileArtifactState.Ready;
                block.LastAccessTime = _time;
                return;
            }

            string meshId = BuildMeshId(block.Key);
            string objectId = BuildObjectId(block.Key);

            EnsureVoxelMaterial();

            graphics.RegisterMesh(meshId, result.Vertices, Silk.NET.OpenGL.PrimitiveType.Triangles, 16);

            graphics.UpsertSceneObject(new Graphics.SceneRenderObjectSnapshot
            {
                SceneId = _terrain.SceneId,
                ObjectId = objectId,
                Type = "Object",
                Active = true,
                Visible = true,
                Mesh = meshId,
                Materials = new List<string> { _voxelMaterialKey },
                RenderTag = "",
                WorldPosition = result.Origin,
                WorldRotation = DQuaternion.Identity,
                WorldScale = Double3.One,
                StaticRenderEligible = true,
                TransformRevision = 0
            });

            _meshIds[block.Key] = meshId;
            _objectIds[block.Key] = objectId;

            block.State = TileArtifactState.Ready;
            block.LastAccessTime = _time;
        }

        private void UnloadBlockGraphics(VoxelBlock block, Graphics? graphics)
        {
            if (graphics == null)
                return;

            if (_meshIds.TryGetValue(block.Key, out string? meshId))
            {
                graphics.RemoveMesh(meshId);
                _meshIds.Remove(block.Key);
            }

            if (_objectIds.TryGetValue(block.Key, out string? objectId))
            {
                graphics.RemoveSceneObject(_terrain.SceneId, objectId);
                _objectIds.Remove(block.Key);
            }
        }

        private string BuildMeshId(VoxelBlockKey key)
        {
            return "voxel:" + _terrain.SceneId + ":" + _terrain.ObjectId +
                ":v" + key.BX + ":" + key.BY + ":" + key.BZ;
        }

        private string BuildObjectId(VoxelBlockKey key)
        {
            return "voxel:" + _terrain.SceneId + ":" + _terrain.ObjectId +
                ":o" + key.BX + ":" + key.BY + ":" + key.BZ;
        }
    }
}
