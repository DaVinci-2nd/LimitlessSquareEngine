using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LimitlessSquareEngine.Editor
{
    internal static class EditorRuntimeState
    {
        internal static EditorHostBootstrapInfo? BootstrapInfo;
        internal static CancellationTokenSource? CancellationTokenSource;
    }
}
