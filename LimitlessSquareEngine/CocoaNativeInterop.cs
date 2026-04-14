using System;
using System.Runtime.InteropServices;

namespace LimitlessSquareEngine
{
    public static class CocoaNativeInterop
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect
        {
            public double X;
            public double Y;
            public double Width;
            public double Height;

            public NSRect(double x, double y, double width, double height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }
        }

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void_NSRect(IntPtr receiver, IntPtr selector, NSRect rect);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern NSRect objc_msgSend_NSRect(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr objc_getClass(string name);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_NSRect_IntPtr(IntPtr receiver, IntPtr selector, NSRect rect);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern bool objc_msgSend_bool_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);


        public static nint GetContentView(nint nsWindow)
        {
            if (nsWindow == 0)
                return 0;

            IntPtr selector = sel_registerName("contentView");
            return objc_msgSend_IntPtr(nsWindow, selector);
        }

        public static void AddSubview(nint parentView, nint childView)
        {
            if (parentView == 0 || childView == 0)
                return;

            IntPtr selector = sel_registerName("addSubview:");
            objc_msgSend_IntPtr_IntPtr(parentView, selector, childView);
        }

        public static void RemoveFromSuperview(nint view)
        {
            if (view == 0)
                return;

            IntPtr selector = sel_registerName("removeFromSuperview");
            objc_msgSend_void(view, selector);
        }

        public static void SetViewFrame(nint view, double x, double y, double width, double height)
        {
            if (view == 0)
                return;

            IntPtr selector = sel_registerName("setFrame:");
            objc_msgSend_void_NSRect(view, selector, new NSRect(x, y, width, height));
        }

        public static double GetViewHeight(nint view)
        {
            if (view == 0)
                return 0.0;

            IntPtr boundsSelector = sel_registerName("bounds");
            NSRect rect = objc_msgSend_NSRect(view, boundsSelector);
            return rect.Height;
        }

        public static nint CreateView(double x, double y, double width, double height)
        {
            IntPtr nsViewClass = objc_getClass("NSView");
            if (nsViewClass == IntPtr.Zero)
                return 0;

            IntPtr allocSel = sel_registerName("alloc");
            IntPtr initSel = sel_registerName("initWithFrame:");

            IntPtr viewAlloc = objc_msgSend_IntPtr(nsViewClass, allocSel);
            if (viewAlloc == IntPtr.Zero)
                return 0;

            return objc_msgSend_NSRect_IntPtr(viewAlloc, initSel, new NSRect(x, y, width, height));
        }

        public static void MakeWindowFirstResponder(nint nsWindow, nint responder)
        {
            if (nsWindow == 0 || responder == 0)
                return;

            IntPtr selector = sel_registerName("makeFirstResponder:");
            objc_msgSend_bool_IntPtr(nsWindow, selector, responder);
        }
    }
}