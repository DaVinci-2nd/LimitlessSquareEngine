using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Runtime.InteropServices;

namespace LimitlessSquareEngine.Editor
{
    public sealed class EmbeddedGameHost : Border
    {
        private PixelSize _lastPixelSize;
        private bool _createdRaised;

        public event Action<IntPtr, string, PixelSize>? NativeControlCreated;
        public event Action<PixelSize>? HostPixelSizeChanged;
        public event Action? NativeControlDestroyed;

        public EmbeddedGameHost()
        {
            ClipToBounds = true;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size result = base.ArrangeOverride(finalSize);
            PixelSize pixelSize = GetPixelSize(finalSize);

            if (!_createdRaised)
            {
                _createdRaised = true;
                _lastPixelSize = pixelSize;
                NativeControlCreated?.Invoke(IntPtr.Zero, string.Empty, pixelSize);
                return result;
            }

            if (pixelSize != _lastPixelSize)
            {
                _lastPixelSize = pixelSize;
                HostPixelSizeChanged?.Invoke(pixelSize);
            }

            return result;
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            NativeControlDestroyed?.Invoke();
            base.OnDetachedFromVisualTree(e);
        }

        private PixelSize GetPixelSize(Size size)
        {
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            int width = Math.Max(1, (int)Math.Round(size.Width * scaling));
            int height = Math.Max(1, (int)Math.Round(size.Height * scaling));
            return new PixelSize(width, height);
        }
    }
}