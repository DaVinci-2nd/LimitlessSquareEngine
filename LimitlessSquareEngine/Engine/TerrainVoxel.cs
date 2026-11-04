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
        public int MaxCommitsPerTick = 8;
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

        public bool IsBlockReadyAt(in Double3 worldPos)
        {
            Double3 dir = Normalize(worldPos);
            double surface = _terrain.Height.SampleDirection(dir);
            double shellHeight = VoxelBlock.ShellHeight;
            int shell = (int)Math.Floor(surface / shellHeight);

            QuadSphere.DirToFaceUV(dir, out int face, out double u, out double v);
            int level = _terrain.Profile.RenderMaxLevel;
            int per = QuadSphere.TileCountPerAxis(level);
            int lx = Math.Clamp((int)Math.Floor((u + 1.0) * 0.5 * per), 0, per - 1);
            int ly = Math.Clamp((int)Math.Floor((v + 1.0) * 0.5 * per), 0, per - 1);

            var key = new VoxelBlockKey(face, level, lx, ly, shell);

            return _blocks.TryGetValue(key, out VoxelBlock? block) &&
                block.State == TileArtifactState.Ready;
        }

        public bool IsTileReady(int face, int level, int lx, int ly)
        {
            double shellHeight = VoxelBlock.ShellHeight;

            if (!(shellHeight > 0.0))
                return false;

            Double3 centerDir = QuadSphere.TileCenterDir(new TileKey(face, level, lx, ly));
            double surface = _terrain.Radius + _terrain.Height.SampleDirection(centerDir);
            int shellCenter = (int)Math.Floor(surface / shellHeight);

            for (int shell = shellCenter - 2; shell <= shellCenter + 2; shell++)
            {
                var key = new VoxelBlockKey(face, level, lx, ly, shell);

                if (_blocks.TryGetValue(key, out VoxelBlock? block) &&
                    block.State == TileArtifactState.Ready)
                    return true;
            }

            return false;
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
            int enqueued = 0;
            int finestLevel = _terrain.Profile.RenderMaxLevel;
            double per = QuadSphere.TilesPerAxis(finestLevel);

            if (!(per > 0.0))
                return;

            double tileSize = 2.0 * _terrain.Radius / per;
            int half = Math.Max(1, (int)Math.Ceiling(CoverRadius / tileSize) + 1);

            int bound = QuadSphere.TileCountPerAxis(finestLevel);
            var visited = new HashSet<VoxelBlockKey>();

            Double3 camDir = Normalize(_center);

            for (int face = 0; face < QuadSphere.FaceCount; face++)
            {
                QuadSphere.GetFaceAxes(face, out Double3 faceNormal, out Double3 faceRight, out Double3 faceUp);

                double dn = camDir.X * faceNormal.X + camDir.Y * faceNormal.Y + camDir.Z * faceNormal.Z;
                if (Math.Abs(dn) <= 1e-6)
                    continue;

                double camU = (camDir.X * faceRight.X + camDir.Y * faceRight.Y + camDir.Z * faceRight.Z) / dn;
                double camV = (camDir.X * faceUp.X + camDir.Y * faceUp.Y + camDir.Z * faceUp.Z) / dn;

                camU = Math.Clamp(camU, -1.0, 1.0);
                camV = Math.Clamp(camV, -1.0, 1.0);

                int cx = (int)Math.Floor((camU + 1.0) * 0.5 * per);
                int cy = (int)Math.Floor((camV + 1.0) * 0.5 * per);

                cx = Math.Clamp(cx, 0, bound - 1);
                cy = Math.Clamp(cy, 0, bound - 1);

                for (int ring = 0; ring <= half; ring++)
                {
                    for (int dy = -ring; dy <= ring; dy++)
                    {
                        for (int dx = -ring; dx <= ring; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                                continue;

                            int lx = cx + dx;
                            int ly = cy + dy;

                            if (lx < 0 || lx >= bound || ly < 0 || ly >= bound)
                                continue;

                            var node = new TileKey(face, finestLevel, lx, ly);
                            Double3 centerDir = QuadSphere.TileCenterDir(node);
                            double centerDist = DistanceToTile(_center, centerDir);

                            if (centerDist > CoverRadius)
                                continue;

                            EnqueueShells(node, centerDir, centerDist, visited, ref enqueued);
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

            int staleCount = _pending.Count;

            for (int i = 0; i < staleCount; i++)
            {
                if (!_pending.TryDequeue(out VoxelBuildJob? job))
                    break;

                if (job.Block.LastAccessTime == _time)
                {
                    _pending.Enqueue(job);
                    continue;
                }

                if (job.Block.State == TileArtifactState.Queued)
                    job.Block.State = TileArtifactState.Invalidated;
            }
        }

        private void EnqueueShells(in TileKey node, in Double3 centerDir, double centerDist,
            HashSet<VoxelBlockKey> visited, ref int enqueued)
        {
            if (centerDist > CoverRadius)
                return;

            double shellHeight = VoxelBlock.ShellHeight;
            double minSurface = double.MaxValue;
            double maxSurface = double.MinValue;

            for (int c = 0; c < 5; c++)
            {
                Double3 dir = c == 4 ? centerDir : QuadSphere.TileCornerDir(node, c);
                double h = _terrain.Radius + _terrain.Height.SampleDirection(dir);

                if (h < minSurface)
                    minSurface = h;

                if (h > maxSurface)
                    maxSurface = h;
            }

            int shellMin = (int)Math.Floor((minSurface - VoxelBlock.VoxelSize) / shellHeight);
            int shellMax = (int)Math.Floor((maxSurface + VoxelBlock.VoxelSize) / shellHeight);

            for (int shell = shellMin; shell <= shellMax; shell++)
            {
                var key = new VoxelBlockKey(node.Face, node.Level, node.LX, node.LY, shell);

                if (!visited.Add(key))
                    continue;

                if (_blocks.TryGetValue(key, out VoxelBlock? existing))
                {
                    existing.LastAccessTime = _time;

                    if (existing.State == TileArtifactState.Invalidated && enqueued < MaxBlocksPerTick)
                    {
                        existing.State = TileArtifactState.Queued;
                        _pending.Enqueue(new VoxelBuildJob { Block = existing });
                        enqueued++;
                    }

                    continue;
                }

                var block = new VoxelBlock(key);
                block.LastAccessTime = _time;
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

        private void InvalidateRegion(in Double3 center, double radius)
        {
            foreach (VoxelBlock block in _blocks.Values)
            {
                Double3 centerDir = QuadSphere.TileCenterDir(
                    new TileKey(block.Key.Face, block.Key.Level, block.Key.LX, block.Key.LY));
                double dist = DistanceToTile(center, centerDir);

                if (dist > radius + VoxelBlock.ShellHeight)
                    continue;

                if (block.State == TileArtifactState.Ready || block.State == TileArtifactState.None)
                    block.State = TileArtifactState.Invalidated;
            }
        }

        private static double DistanceToTile(in Double3 point, in Double3 dir)
        {
            double len = Math.Sqrt(point.X * point.X + point.Y * point.Y + point.Z * point.Z);
            double px = dir.X * len;
            double py = dir.Y * len;
            double pz = dir.Z * len;
            double dx = point.X - px;
            double dy = point.Y - py;
            double dz = point.Z - pz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static Double3 Normalize(in Double3 v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (len <= 1e-300)
                return new Double3(0, 0, 1);
            return new Double3(v.X / len, v.Y / len, v.Z / len);
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
                    job.Block.LastAccessTime = _time + 5.0;
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

        private string BuildVoxelMaterialJson(string atlasKey)
        {
            var parameters = new Dictionary<string, object?>
            {
                ["uColor"] = new[] { 1.0, 1.0, 1.0, 1.0 },
                ["uUseTexture"] = 0,
                ["uTexture"] = atlasKey,
                ["uTextureWrap"] = "Clamp",
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
                ["uPlanetSeaLevelRadius"] = _terrain.Radius,
                ["uTerrainHeightScale"] = 1.0,
                ["uCull"] = "front"
            };

            var root = new Dictionary<string, object?>
            {
                ["assetType"] = "Material",
                ["shader"] = "Shaders/Builtin/Terrain",
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
                Physics.RemoveVoxelCollider(_terrain.SceneId, BuildObjectId(block.Key));
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

            Physics.SetVoxelCollider(_terrain.SceneId, objectId, result.Origin, result.Vertices);

            block.State = TileArtifactState.Ready;
            block.LastAccessTime = _time;
        }

        private void UnloadBlockGraphics(VoxelBlock block, Graphics? graphics)
        {
            Physics.RemoveVoxelCollider(_terrain.SceneId, BuildObjectId(block.Key));

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
                ":v" + key.Face + ":" + key.Level + ":" + key.LX + ":" + key.LY + ":" + key.Shell;
        }

        private string BuildObjectId(VoxelBlockKey key)
        {
            return "voxel:" + _terrain.SceneId + ":" + _terrain.ObjectId +
                ":o" + key.Face + ":" + key.Level + ":" + key.LX + ":" + key.LY + ":" + key.Shell;
        }
    }
}
