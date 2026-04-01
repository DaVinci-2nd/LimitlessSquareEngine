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

        internal static void Bind(
            Func<EditorHostBootstrapInfo> bootstrapInfoProvider,
            Action<bool> setRenderWindowVisible,
            Action<int, int> setRenderWindowSize,
            Action requestRenderWindowClose,
            Func<bool> isRenderWindowAlive,
            Action runRenderFrame)
        {
            _bootstrapInfoProvider = bootstrapInfoProvider;
            _setRenderWindowVisible = setRenderWindowVisible;
            _setRenderWindowSize = setRenderWindowSize;
            _requestRenderWindowClose = requestRenderWindowClose;
            _isRenderWindowAlive = isRenderWindowAlive;
            _runRenderFrame = runRenderFrame;
        }

        internal static void Unbind()
        {
            _bootstrapInfoProvider = null;
            _setRenderWindowVisible = null;
            _setRenderWindowSize = null;
            _requestRenderWindowClose = null;
            _isRenderWindowAlive = null;
            _runRenderFrame = null;
        }

        public static EditorHostBootstrapInfo GetBootstrapInfo()
        {
            if (_bootstrapInfoProvider == null)
                throw new InvalidOperationException("Editor host bridge is not bound.");

            return _bootstrapInfoProvider();
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
    }
}
