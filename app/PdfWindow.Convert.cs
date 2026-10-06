using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IdmClone;

/// <summary>The To Word / To Excel / To Pictures buttons of the edit bar (their small coloured icons; the buttons themselves are made in BuildEditBar).</summary>
public sealed partial class PdfWindow
{
    /// <summary>The small coloured square of a convert button (W = Word blue, X = Excel green).</summary>
    private static Border ConvertIcon(string glyph, Color colour, string glyphFont = "Segoe UI") => new()
    {
        Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(colour), HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock { Text = glyph, Foreground = Brushes.White, FontFamily = new FontFamily(glyphFont), FontSize = glyphFont == "Segoe UI" ? 13 : 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };
}
