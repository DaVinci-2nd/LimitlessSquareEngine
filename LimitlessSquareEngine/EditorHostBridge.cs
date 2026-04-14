using LimitlessSquareEngine.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine
{
    public static class EditorHostBridge
    {
        private static Func<EditorHostBootstrapInfo>? _bootstrapInfoProvider;
        private static Action<bool>? _setRenderWindowVisible;
        private static Action<int, int>? _setRenderWindowSize;
        private static Action? _requestRenderWindowClose;
        private static Func<bool>? _isRenderWindowAlive;
        private static Action? _runRenderFrame;
        private static Action<string>? _reloadSceneById;
        private static Action<string>? _removeSceneById;
        private static Action<string>? _setAssetRootAndReloadAssets;
        private static Action<string, string, Double3>? _setSceneObjectLocalPosition;
        private static Action<string, string, Double3>? _setSceneObjectLocalRotation;
        private static Func<EditorRenderedFrame?>? _consumeLatestFrame;
        private static Action<bool>? _setRuntimePaused;
        private static Func<bool>? _getRuntimePaused;
        private static Action? _stepRuntimeFrame;
        private static Action<string>? _setGameStartupFolder;
        private static Func<string>? _getGameStartupFolder;

        internal static void Bind(
            Func<EditorHostBootstrapInfo> bootstrapInfoProvider,
            Action<bool> setRenderWindowVisible,
            Action<int, int> setRenderWindowSize,
            Action requestRenderWindowClose,
            Func<bool> isRenderWindowAlive,
            Action runRenderFrame,
            Action<string> reloadSceneById,
            Action<string> removeSceneById,
            Action<string> setAssetRootAndReloadAssets,
            Action<string, string, Double3> setSceneObjectLocalPosition,
            Action<string, string, Double3> setSceneObjectLocalRotation,
            Func<EditorRenderedFrame?> consumeLatestFrame,
            Action<bool> setRuntimePaused,
            Func<bool> getRuntimePaused,
            Action stepRuntimeFrame,
            Action<string> setGameStartupFolder,
            Func<string> getGameStartupFolder)
        {
            _bootstrapInfoProvider = bootstrapInfoProvider;
            _setRenderWindowVisible = setRenderWindowVisible;
            _setRenderWindowSize = setRenderWindowSize;
            _requestRenderWindowClose = requestRenderWindowClose;
            _isRenderWindowAlive = isRenderWindowAlive;
            _runRenderFrame = runRenderFrame;
            _reloadSceneById = reloadSceneById;
            _removeSceneById = removeSceneById;
            _setAssetRootAndReloadAssets = setAssetRootAndReloadAssets;
            _setSceneObjectLocalPosition = setSceneObjectLocalPosition;
            _setSceneObjectLocalRotation = setSceneObjectLocalRotation;
            _consumeLatestFrame = consumeLatestFrame;
            _setRuntimePaused = setRuntimePaused;
            _getRuntimePaused = getRuntimePaused;
            _stepRuntimeFrame = stepRuntimeFrame;
            _setGameStartupFolder = setGameStartupFolder;
            _getGameStartupFolder = getGameStartupFolder;
        }

        internal static void Unbind()
        {
            _bootstrapInfoProvider = null;
            _setRenderWindowVisible = null;
            _setRenderWindowSize = null;
            _requestRenderWindowClose = null;
            _isRenderWindowAlive = null;
            _runRenderFrame = null;
            _reloadSceneById = null;
            _removeSceneById = null;
            _setAssetRootAndReloadAssets = null;
            _setSceneObjectLocalPosition = null;
            _setSceneObjectLocalRotation = null;
            _consumeLatestFrame = null;
            _setRuntimePaused = null;
            _getRuntimePaused = null;
            _stepRuntimeFrame = null;
            _setGameStartupFolder = null;
            _getGameStartupFolder = null;
        }

        public static void SetSceneObjectLocalPosition(string sceneId, string objectId, Double3 value)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("Scene ID cannot be null or empty.", nameof(sceneId));

            if (string.IsNullOrWhiteSpace(objectId))
                throw new ArgumentException("Object ID cannot be null or empty.", nameof(objectId));

            if (_setSceneObjectLocalPosition == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setSceneObjectLocalPosition(sceneId, objectId, value);
        }

        public static void SetSceneObjectLocalRotation(string sceneId, string objectId, Double3 value)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("Scene ID cannot be null or empty.", nameof(sceneId));

            if (string.IsNullOrWhiteSpace(objectId))
                throw new ArgumentException("Object ID cannot be null or empty.", nameof(objectId));

            if (_setSceneObjectLocalRotation == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setSceneObjectLocalRotation(sceneId, objectId, value);
        }

        public static EditorRenderedFrame? ConsumeLatestFrame()
        {
            if (_consumeLatestFrame == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _consumeLatestFrame();
        }

        public static void SetAssetRootAndReloadAssets(string assetRootPath)
        {
            if (string.IsNullOrWhiteSpace(assetRootPath))
                throw new ArgumentException("Asset root path cannot be null or empty.", nameof(assetRootPath));

            if (_setAssetRootAndReloadAssets == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setAssetRootAndReloadAssets(assetRootPath);
        }

        public static EditorHostBootstrapInfo GetBootstrapInfo()
        {
            if (_bootstrapInfoProvider == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _bootstrapInfoProvider();
        }

        public static void SetRuntimePaused(bool paused)
        {
            if (_setRuntimePaused == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setRuntimePaused(paused);
        }

        public static bool GetRuntimePaused()
        {
            if (_getRuntimePaused == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _getRuntimePaused();
        }

        public static void StepRuntimeFrame()
        {
            if (_stepRuntimeFrame == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _stepRuntimeFrame();
        }

        public static void SetGameStartupFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new ArgumentException("Folder path cannot be null or empty.", nameof(folderPath));

            if (_setGameStartupFolder == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setGameStartupFolder(folderPath);
        }

        public static string GetGameStartupFolder()
        {
            if (_getGameStartupFolder == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _getGameStartupFolder();
        }

        public static bool IsRenderWindowAlive
        {
            get
            {
                if (_isRenderWindowAlive == null)
                    return false;

                return _isRenderWindowAlive();
            }
        }

        public static void SetRenderWindowVisible(bool visible)
        {
            if (_setRenderWindowVisible == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setRenderWindowVisible(visible);
        }

        public static void SetRenderWindowSize(int width, int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));

            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            if (_setRenderWindowSize == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setRenderWindowSize(width, height);
        }

        public static void RequestRenderWindowClose()
        {
            if (_requestRenderWindowClose == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _requestRenderWindowClose();
        }

        public static void RunRenderFrame()
        {
            if (_runRenderFrame == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _runRenderFrame();
        }

        public static void ReloadSceneById(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("Scene ID cannot be null or empty.", nameof(sceneId));

            if (_reloadSceneById == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _reloadSceneById(sceneId);
        }

        public static void RemoveSceneById(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("Scene ID cannot be null or empty.", nameof(sceneId));

            if (_removeSceneById == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _removeSceneById(sceneId);
        }
    }
}
