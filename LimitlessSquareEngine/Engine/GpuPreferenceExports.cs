using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine.Engine
{
    internal class GpuPreferenceExports
    {
        [UnmanagedCallersOnly(EntryPoint = "NvOptimusEnablement")]
        public static uint NvOptimusEnablement()
        {
            return 1;
        }

        [UnmanagedCallersOnly(EntryPoint = "AmdPowerXpressRequestHighPerformance")]
        public static int AmdPowerXpressRequestHighPerformance()
        {
            return 1;
        }
    }
}
