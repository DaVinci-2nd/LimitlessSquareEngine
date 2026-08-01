using System;

namespace LimitlessSquareEngine.Engine.Terrain
{
    public enum TileArtifactState
    {
        None = 0,
        Queued = 1,
        Building = 2,
        Ready = 3,
        Invalidated = 4
    }

    // 渲染产物
    public sealed class TerrainRenderArtifact
    {
        public int Lod;
        public TileArtifactState State = TileArtifactState.None;
        public string[] MeshIds = Array.Empty<string>();
        public string[] ObjectIds = Array.Empty<string>();
        public string[] LayerTags = Array.Empty<string>();
        public int LayerCount;
        public int[] BuiltStitchLevels = new int[4];
        public object? BuildData;

        public bool IsMesh => MeshIds.Length > 0;
    }

    // 物理产物
    public sealed class TerrainPhysicsArtifact
    {
        public int Lod;
        public TileArtifactState State = TileArtifactState.None;
        public object? ColliderData;
    }

    // 地形四叉树节点
    public sealed class TerrainTile
    {
        public readonly TileKey Key;

        public TerrainTile(TileKey key)
        {
            Key = key;
        }

        public TerrainRenderArtifact? Render;
        public TerrainPhysicsArtifact? Physics;

        public double RenderLastAccessTime;
        public double PhysicsLastAccessTime;

        public bool HasAnyArtifact => Render != null || Physics != null;

        public TerrainRenderArtifact EnsureRender()
        {
            if (Render == null)
            {
                Render = new TerrainRenderArtifact();
                RenderLastAccessTime = 0.0;
            }
            return Render;
        }

        public TerrainPhysicsArtifact EnsurePhysics()
        {
            if (Physics == null)
            {
                Physics = new TerrainPhysicsArtifact();
                PhysicsLastAccessTime = 0.0;
            }
            return Physics;
        }

        public void ClearRender()
        {
            Render = null;
        }

        public void ClearPhysics()
        {
            Physics = null;
        }
    }
}
