using System.Windows;

namespace IdmClone;

internal static class ThemedExtensions
{
    /// <summary>
    /// Makes a property follow a palette brush BY NAME (like DynamicResource in XAML): when the theme or the accent changes, it changes with it.
    /// Taking the brush itself in code (<c>Foreground = (Brush)FindResource("MutedBrush")</c>) keeps the old colour after a change.
    /// </summary>
    public static T Live<T>(this T element, DependencyProperty property, string brushName) where T : FrameworkElement
    {
        element.SetResourceReference(property, brushName);
        return element;
    }
}
