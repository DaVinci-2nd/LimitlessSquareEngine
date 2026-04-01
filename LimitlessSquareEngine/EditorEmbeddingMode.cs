using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine
{
    public enum EditorEmbeddingMode
    {
        ForeignChildWindow = 0,
        CocoaViewHost = 1,
        NestedWaylandCompositor = 2,
        Unsupported = 3
    }
}
