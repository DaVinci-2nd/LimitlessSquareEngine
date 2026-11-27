using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace LimitlessSquareEngine.Editor
{
    public sealed partial class EditorMainWindow
    {
        private const int AssetPreviewIconSize = 128;
        private const int AssetPreviewMaxCachedBitmaps = 512;

        private readonly Dictionary<string, WriteableBitmap> _assetPreviewBitmapCache = new(StringComparer.Ordinal);
        private readonly Queue<string> _assetPreviewCacheOrder = new();
        private readonly HashSet<string> _assetPreviewPendingKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _assetPreviewFailedKeys = new(StringComparer.Ordinal);
        private string? _assetPreviewRequestedAssetRoot;

        private string EnqueueAssetPreview(string path, EditorHostBridge.AssetPreviewKind kind, int size, out WriteableBitmap? cached)
        {
            cached = null;

            string fullPath = Path.GetFullPath(path);
            long lastWriteUtcTicks = 0;

            try
            {
                lastWriteUtcTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
            }
            catch
            {
            }

            string requestKey = $"{(int)kind}|{fullPath}|{lastWriteUtcTicks}|{size}";
            string staleKeyPrefix = $"{(int)kind}|{fullPath}|";

            foreach (KeyValuePair<string, WriteableBitmap> pair in _assetPreviewBitmapCache.ToList())
            {
                if (pair.Key == requestKey || !pair.Key.StartsWith(staleKeyPrefix, StringComparison.Ordinal))
                    continue;

                ApplyAssetPreviewToVisibleIcons(pair.Key, null);
                pair.Value.Dispose();
                _assetPreviewBitmapCache.Remove(pair.Key);
            }

            foreach (string failedKey in _assetPreviewFailedKeys.ToList())
            {
                if (failedKey.StartsWith(staleKeyPrefix, StringComparison.Ordinal))
                    _assetPreviewFailedKeys.Remove(failedKey);
            }

            if (_assetPreviewBitmapCache.TryGetValue(requestKey, out WriteableBitmap? hit))
            {
                cached = hit;
                return requestKey;
            }

            if (_assetPreviewFailedKeys.Contains(requestKey) || _assetPreviewPendingKeys.Contains(requestKey))
                return requestKey;

            try
            {
                string assetRootPath = ResolveSceneAssetRoot(fullPath);

                if (!string.Equals(_assetPreviewRequestedAssetRoot, assetRootPath, StringComparison.OrdinalIgnoreCase))
                {
                    EditorHostBridge.SetAssetRootAndReloadAssets(assetRootPath);
                    _assetPreviewRequestedAssetRoot = assetRootPath;
                }

                EditorHostBridge.QueueAssetPreview(CreateAssetPreviewRequest(fullPath, kind, size, requestKey));
                _assetPreviewPendingKeys.Add(requestKey);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                _assetPreviewFailedKeys.Add(requestKey);
            }

            return requestKey;
        }

        private EditorHostBridge.AssetPreviewRequest CreateAssetPreviewRequest(
            string fullPath,
            EditorHostBridge.AssetPreviewKind kind,
            int size,
            string requestKey)
        {
            string assetRootPath = ResolveSceneAssetRoot(fullPath);
            string assetRelativeKey = Path.GetRelativePath(assetRootPath, fullPath).Replace('\\', '/');

            switch (kind)
            {
                case EditorHostBridge.AssetPreviewKind.Material:
                    {
                        string materialKey = assetRelativeKey.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                            ? assetRelativeKey[..^5]
                            : assetRelativeKey;

                        return new EditorHostBridge.AssetPreviewRequest
                        {
                            RequestKey = requestKey,
                            Kind = kind,
                            MaterialKey = materialKey,
                            Width = size,
                            Height = size
                        };
                    }

                case EditorHostBridge.AssetPreviewKind.Avatar:
                    return new EditorHostBridge.AssetPreviewRequest
                    {
                        RequestKey = requestKey,
                        Kind = kind,
                        AvatarMeshKey = assetRelativeKey,
                        Width = size,
                        Height = size
                    };

                case EditorHostBridge.AssetPreviewKind.AnimationClip:
                    {
                        List<string> vrmKeys = CollectAssetKeys("*.vrm", false)
                            .Where(k => string.Equals(Path.GetExtension(k), ".vrm", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        string clipDirectory = Path.GetDirectoryName(assetRelativeKey) ?? string.Empty;
                        string clipAvatarMeshKey =
                            vrmKeys.FirstOrDefault(k => string.Equals(Path.GetDirectoryName(k), clipDirectory, StringComparison.OrdinalIgnoreCase))
                            ?? vrmKeys.FirstOrDefault()
                            ?? string.Empty;

                        return new EditorHostBridge.AssetPreviewRequest
                        {
                            RequestKey = requestKey,
                            Kind = kind,
                            AssetFilePath = fullPath,
                            AvatarMeshKey = clipAvatarMeshKey,
                            Width = size,
                            Height = size
                        };
                    }

                default:
                    return new EditorHostBridge.AssetPreviewRequest
                    {
                        RequestKey = requestKey,
                        Kind = kind,
                        AssetFilePath = fullPath,
                        Width = size,
                        Height = size
                    };
            }
        }

        public void PresentAssetPreviews()
        {
            List<EditorHostBridge.AssetPreviewResult> results = EditorHostBridge.ConsumeAssetPreviewResults();

            if (results.Count == 0)
                return;

            foreach (EditorHostBridge.AssetPreviewResult result in results)
            {
                _assetPreviewPendingKeys.Remove(result.RequestKey);

                if (result.Frame == null || result.Frame.Width <= 0 || result.Frame.Height <= 0)
                {
                    _assetPreviewFailedKeys.Add(result.RequestKey);
                    continue;
                }

                WriteableBitmap bitmap = new WriteableBitmap(
                    new PixelSize(result.Frame.Width, result.Frame.Height),
                    new Vector(96, 96),
                    PixelFormat.Rgba8888,
                    AlphaFormat.Unpremul);

                using (ILockedFramebuffer locked = bitmap.Lock())
                {
                    int srcStride = result.Frame.Width * 4;
                    IntPtr dstBase = locked.Address;

                    for (int row = 0; row < result.Frame.Height; row++)
                    {
                        int srcRow = result.Frame.Height - 1 - row;
                        int srcOffset = srcRow * srcStride;
                        IntPtr dstRowPtr = IntPtr.Add(dstBase, row * locked.RowBytes);
                        Marshal.Copy(result.Frame.PixelsRgba, srcOffset, dstRowPtr, srcStride);
                    }
                }

                _assetPreviewBitmapCache[result.RequestKey] = bitmap;
                _assetPreviewCacheOrder.Enqueue(result.RequestKey);

                while (_assetPreviewBitmapCache.Count > AssetPreviewMaxCachedBitmaps)
                {
                    if (_assetPreviewCacheOrder.Count == 0)
                        break;

                    string staleKey = _assetPreviewCacheOrder.Dequeue();

                    if (!_assetPreviewBitmapCache.TryGetValue(staleKey, out WriteableBitmap? stale))
                        continue;

                    ApplyAssetPreviewToVisibleIcons(staleKey, null);
                    stale.Dispose();
                    _assetPreviewBitmapCache.Remove(staleKey);
                }

                ApplyAssetPreviewToVisibleIcons(result.RequestKey, bitmap);
            }
        }

        private void ApplyAssetPreviewToVisibleIcons(string requestKey, WriteableBitmap? bitmap)
        {
            if (string.IsNullOrWhiteSpace(requestKey) || _resourceExplorerPanel == null)
                return;

            foreach (Control child in _resourceExplorerPanel.Children)
            {
                if (child is Border border &&
                    border.Tag is ResourceItemState state &&
                    string.Equals(state.Icon.PreviewRequestKey, requestKey, StringComparison.Ordinal))
                {
                    state.Icon.PreviewImage = bitmap;
                }
            }
        }

        private void DisposeAssetPreviewCache()
        {
            foreach (WriteableBitmap bitmap in _assetPreviewBitmapCache.Values)
                bitmap.Dispose();

            _assetPreviewBitmapCache.Clear();
            _assetPreviewCacheOrder.Clear();
            _assetPreviewPendingKeys.Clear();
            _assetPreviewFailedKeys.Clear();
            _assetPreviewRequestedAssetRoot = null;
        }
    }
}
