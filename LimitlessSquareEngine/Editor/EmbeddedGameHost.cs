using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;

namespace LimitlessSquareEngine.Editor
{
    public sealed class EmbeddedGameHost : Border
    {
        private readonly Image _image;
        private WriteableBitmap? _bitmap;
        private PixelSize _lastPixelSize;

        public event Action<PixelSize>? RenderSurfaceResized;

        public EmbeddedGameHost()
        {
            ClipToBounds = true;
            Background = Brushes.Black;

            _image = new Image
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };

            Child = _image;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size result = base.ArrangeOverride(finalSize);
            PublishSize();
            return result;
        }

        private void PublishSize()
        {
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            int width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
            int height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
            PixelSize size = new PixelSize(width, height);

            if (_lastPixelSize == size)
                return;

            _lastPixelSize = size;
            RenderSurfaceResized?.Invoke(size);
        }

        public void PresentFrame(EditorRenderedFrame frame)
        {
            if (frame == null || frame.Width <= 0 || frame.Height <= 0 || frame.PixelsRgba == null || frame.PixelsRgba.Length == 0)
                return;

            EnsureBitmap(frame.Width, frame.Height);

            if (_bitmap == null)
                return;

            using ILockedFramebuffer locked = _bitmap.Lock();

            int srcStride = frame.Width * 4;
            IntPtr dstBase = locked.Address;

            for (int row = 0; row < frame.Height; row++)
            {
                int srcRow = frame.Height - 1 - row;
                int srcOffset = srcRow * srcStride;
                IntPtr dstRowPtr = IntPtr.Add(dstBase, row * locked.RowBytes);
                System.Runtime.InteropServices.Marshal.Copy(frame.PixelsRgba, srcOffset, dstRowPtr, srcStride);
            }

            _image.Source = _bitmap;
        }

        private void EnsureBitmap(int width, int height)
        {
            if (_bitmap != null &&
                _bitmap.PixelSize.Width == width &&
                _bitmap.PixelSize.Height == height)
            {
                return;
            }

            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Rgba8888,
                AlphaFormat.Unpremul);

            _image.Source = _bitmap;
        }
    }
}