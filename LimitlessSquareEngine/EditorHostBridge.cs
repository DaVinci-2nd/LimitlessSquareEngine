using LimitlessSquareEngine.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
        private static Func<int, int, RenderedMeshRaycastHit>? _raycastRenderedMeshAtPixel;
        private static Action<string, string, bool, float, float, float, float>? _setSceneObjectContour;
        private static Action<string>? _clearSceneContours;
        private static Action<bool>? _setRuntimePaused;
        private static Func<bool>? _getRuntimePaused;
        private static Action<string, string, Double3>? _setSceneObjectLocalScale;
        private static Action? _stepRuntimeFrame;
        private static Action<string>? _setGameStartupFolder;
        private static Func<string>? _getGameStartupFolder;
        private static Action<Double3, int, int, bool>? _setGizmoState;
        private static Func<string, string, Double3>? _getSceneObjectWorldPosition;
        private static Func<string, string, Double3, Double3>? _worldDeltaToLocalDelta;
        private static Func<Matrix4x4>? _getCameraView;
        private static Func<Matrix4x4>? _getCameraProjection;
        private static Action<int>? _setGizmoHover;
        private static Action<bool, int>? _setGizmoDrag;
        private static Func<string, LuaSyntaxError[]>? _checkLuaSyntax;
        private static Func<LuaApiMetadata[]>? _getLuaApiMetadata;

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
            Action<string, string, Double3>? setSceneObjectLocalScale,
            Func<EditorRenderedFrame?> consumeLatestFrame,
            Func<int, int, RenderedMeshRaycastHit> raycastRenderedMeshAtPixel,
            Action<string, string, bool, float, float, float, float> setSceneObjectContour,
            Action<string> clearSceneContours,
            Action<bool> setRuntimePaused,
            Func<bool> getRuntimePaused,
            Action stepRuntimeFrame,
            Action<string> setGameStartupFolder,
            Func<string> getGameStartupFolder,
            Action<Double3, int, int, bool> setGizmoState,
            Func<string, string, Double3> getSceneObjectWorldPosition,
            Func<string, string, Double3, Double3> worldDeltaToLocalDelta,
            Func<Matrix4x4> getCameraView,
            Func<Matrix4x4> getCameraProjection,
            Action<int> setGizmoHover,
            Action<bool, int> setGizmoDrag,
            Func<string, LuaSyntaxError[]> checkLuaSyntax,
            Func<LuaApiMetadata[]> getLuaApiMetadata)
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
            _setSceneObjectLocalScale = setSceneObjectLocalScale;
            _consumeLatestFrame = consumeLatestFrame;
            _raycastRenderedMeshAtPixel = raycastRenderedMeshAtPixel;
            _setSceneObjectContour = setSceneObjectContour;
            _clearSceneContours = clearSceneContours;
            _setRuntimePaused = setRuntimePaused;
            _getRuntimePaused = getRuntimePaused;
            _stepRuntimeFrame = stepRuntimeFrame;
            _setGameStartupFolder = setGameStartupFolder;
            _getGameStartupFolder = getGameStartupFolder;
            _setGizmoState = setGizmoState;
            _getSceneObjectWorldPosition = getSceneObjectWorldPosition;
            _worldDeltaToLocalDelta = worldDeltaToLocalDelta;
            _getCameraView = getCameraView;
            _getCameraProjection = getCameraProjection;
            _setGizmoHover = setGizmoHover;
            _setGizmoDrag = setGizmoDrag;
            _checkLuaSyntax = checkLuaSyntax;
            _getLuaApiMetadata = getLuaApiMetadata;
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
            _setSceneObjectLocalScale = null;
            _consumeLatestFrame = null;
            _raycastRenderedMeshAtPixel = null;
            _setSceneObjectContour = null;
            _clearSceneContours = null;
            _setRuntimePaused = null;
            _getRuntimePaused = null;
            _stepRuntimeFrame = null;
            _setGameStartupFolder = null;
            _getGameStartupFolder = null;
            _setGizmoState = null;
            _getSceneObjectWorldPosition = null;
            _worldDeltaToLocalDelta = null;
            _getCameraView = null;
            _getCameraProjection = null;
            _setGizmoHover = null;
            _setGizmoDrag = null;
            _checkLuaSyntax = null;
            _getLuaApiMetadata = null;
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

        public static void SetSceneObjectLocalScale(string sceneId, string objectId, Double3 value)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("Scene ID cannot be null or empty.", nameof(sceneId));

            if (string.IsNullOrWhiteSpace(objectId))
                throw new ArgumentException("Object ID cannot be null or empty.", nameof(objectId));

            if (_setSceneObjectLocalScale == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setSceneObjectLocalScale(sceneId, objectId, value);
        }

        public static EditorRenderedFrame? ConsumeLatestFrame()
        {
            if (_consumeLatestFrame == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _consumeLatestFrame();
        }

        public static RenderedMeshRaycastHit RaycastRenderedMeshAtPixel(int screenX, int screenY)
        {
            if (_raycastRenderedMeshAtPixel == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _raycastRenderedMeshAtPixel(screenX, screenY);
        }

        public static void SetSceneObjectContour(
            string sceneId,
            string objectId,
            bool enabled,
            float r,
            float g,
            float b,
            float thicknessPixels)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("Scene ID cannot be null or empty.", nameof(sceneId));

            if (string.IsNullOrWhiteSpace(objectId))
                throw new ArgumentException("Object ID cannot be null or empty.", nameof(objectId));

            if (_setSceneObjectContour == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setSceneObjectContour(sceneId, objectId, enabled, r, g, b, thicknessPixels);
        }

        public static void ClearSceneContours(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("Scene ID cannot be null or empty.", nameof(sceneId));

            if (_clearSceneContours == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _clearSceneContours(sceneId);
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

        public static void SetGizmoState(Double3 worldPos, int mode, int hoveredAxis, bool visible)
        {
            if (_setGizmoState == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setGizmoState(worldPos, mode, hoveredAxis, visible);
        }

        public static Double3 GetSceneObjectWorldPosition(string sceneId, string objectId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("Scene ID cannot be null or empty.", nameof(sceneId));

            if (string.IsNullOrWhiteSpace(objectId))
                throw new ArgumentException("Object ID cannot be null or empty.", nameof(objectId));

            if (_getSceneObjectWorldPosition == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _getSceneObjectWorldPosition(sceneId, objectId);
        }

        public static Double3 WorldDeltaToLocalDelta(string sceneId, string objectId, Double3 worldDelta)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                throw new ArgumentException("Scene ID cannot be null or empty.", nameof(sceneId));

            if (string.IsNullOrWhiteSpace(objectId))
                throw new ArgumentException("Object ID cannot be null or empty.", nameof(objectId));

            if (_worldDeltaToLocalDelta == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _worldDeltaToLocalDelta(sceneId, objectId, worldDelta);
        }

        public static Matrix4x4 GetCameraView()
        {
            if (_getCameraView == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _getCameraView();
        }

        public static Matrix4x4 GetCameraProjection()
        {
            if (_getCameraProjection == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _getCameraProjection();
        }

        public static void SetGizmoHover(int hoveredAxis)
        {
            if (_setGizmoHover == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setGizmoHover(hoveredAxis);
        }

        public static void SetGizmoDrag(bool dragging, int activeAxis)
        {
            if (_setGizmoDrag == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            _setGizmoDrag(dragging, activeAxis);
        }

        public static LuaSyntaxError[] CheckLuaSyntax(string sourceCode)
        {
            if (_checkLuaSyntax == null)
                return Array.Empty<LuaSyntaxError>();

            return _checkLuaSyntax(sourceCode);
        }

        public static LuaApiMetadata[] GetLuaApiMetadata()
        {
            if (_getLuaApiMetadata == null)
                return Array.Empty<LuaApiMetadata>();

            return _getLuaApiMetadata();
        }

        public readonly struct LuaApiMetadata
        {
            public string Name { get; init; }
            public string Signature { get; init; }
            public string Description { get; init; }
            public string Category { get; init; }
        }

        public readonly struct LuaSyntaxError
        {
            public int Line { get; init; }
            public int Column { get; init; }
            public string Message { get; init; }
            public bool IsPrematureStreamTermination { get; init; }
        }
    }
}
