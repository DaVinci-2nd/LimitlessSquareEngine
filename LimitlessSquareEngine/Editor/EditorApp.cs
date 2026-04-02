using Avalonia;
using Avalonia.Themes.Fluent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine.Editor
{
    public sealed class EditorApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }
    }
}
