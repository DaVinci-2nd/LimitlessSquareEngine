using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
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
            var accent = Color.Parse("#225588");

            var theme = new FluentTheme();

            theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources
            {
                Accent = accent
            };

            theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources
            {
                Accent = accent
            };

            Styles.Add(theme);

            Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(accent);
            Resources["TextControlSelectionHighlightColor"] = new SolidColorBrush(accent);

            Resources["TreeViewItemBorderBrushSelected"] = new SolidColorBrush(accent);
            Resources["TreeViewItemBorderBrushSelectedPointerOver"] = new SolidColorBrush(accent);
            Resources["TreeViewItemBorderBrushSelectedPressed"] = new SolidColorBrush(accent);

            Resources["TreeViewItemBackgroundSelected"] = new SolidColorBrush(Color.Parse("#113355"));
            Resources["TreeViewItemBackgroundSelectedPointerOver"] = new SolidColorBrush(accent);
            Resources["TreeViewItemBackgroundSelectedPressed"] = new SolidColorBrush(Color.Parse("#336699"));
        }
    }
}
