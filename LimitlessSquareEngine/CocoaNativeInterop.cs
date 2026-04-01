using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine
{
    internal class CocoaNativeInterop
    {
        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

        internal static nint GetContentView(nint nsWindow)
        {
            if (nsWindow == 0)
                return 0;

            IntPtr selector = sel_registerName("contentView");
            return objc_msgSend_IntPtr(nsWindow, selector);
        }
    }
}
