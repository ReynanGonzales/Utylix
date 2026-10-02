using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IdmClone;

/// <summary>
/// Utylix's own message box (in the app's colours, light or dark like Windows), used everywhere instead of the grey Windows one.
/// Same calls as MessageBox.Show; with <see cref="Ask"/> the buttons can say what they do ("Save", "Don't save").
/// </summary>
public static class UMessage
{
    public static MessageBoxResult Show(string text) => Show(null, text, "Utylix");
    public static MessageBoxResult Show(string text, string caption) => Show(null, text, caption);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons) => Show(null, text, caption, buttons);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image) => Show(null, text, caption, buttons, image);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult) => Show(null, text, caption, buttons, image, defaultResult);
    public static MessageBoxResult Show(Window? owner, string text) => Show(owner, text, "Utylix");

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var list = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel) },
            MessageBoxButton.YesNo => new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No) },
            MessageBoxButton.YesNoCancel => new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel) },
            _ => new[] { ("OK", MessageBoxResult.OK) },
        };
        var cancel = buttons switch { MessageBoxButton.OK => MessageBoxResult.OK, MessageBoxButton.YesNo => MessageBoxResult.No, _ => MessageBoxResult.Cancel };
        return Ask(owner, text, caption, image, defaultResult == MessageBoxResult.None ? list[0].Item2 : defaultResult, cancel, list);
    }

    /// <summary>A question with buttons that say what they do. The first button is the main (blue) one; Esc answers <paramref name="onEscape"/>.</summary>
    public static MessageBoxResult Ask(Window? owner, string text, string caption, MessageBoxImage image, MessageBoxResult byDefault, MessageBoxResult onEscape, params (string Label, MessageBoxResult Result)[] buttons)
    {
        var app = Application.Current;
        Brush Res(string key, Color fallback) => app?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
        var bg = Res("BgBrush", Color.FromRgb(0x12, 0x15, 0x1C));
        var fg = Res("TextBrush", Color.FromRgb(0xE6, 0xE9, 0xF0));
        owner ??= app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

        var result = onEscape;
        var w = new Window
        {
            Title = caption, Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = bg, Foreground = fg, FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5, Topmost = owner == null,
        };
        if (owner != null && owner.IsVisible) w.Owner = owner;
        WindowTheme.DarkTitleBar(w);
        try { w.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico")); } catch (Exception e) when (e is System.IO.IOException or UriFormatException) { }

        // a coloured round sign: ? question, ! warning, x error, i information
        var (sign, colour) = image switch
        {
            MessageBoxImage.Question => ("?", Color.FromRgb(0x3B, 0x82, 0xF6)),
            MessageBoxImage.Warning => ("!", Color.FromRgb(0xF5, 0x9E, 0x0B)),
            MessageBoxImage.Error => ("×", Color.FromRgb(0xE5, 0x48, 0x4D)),
            MessageBoxImage.Information => ("i", Color.FromRgb(0x3B, 0x82, 0xF6)),
            _ => ("", Colors.Transparent),
        };
        var body = new DockPanel { Margin = new Thickness(22, 20, 22, 6) };
        if (sign.Length > 0)
        {
            var badge = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(colour), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = sign, Foreground = Brushes.White, FontSize = 19, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) },
            };
            DockPanel.SetDock(badge, Dock.Left);
            body.Children.Add(badge);
        }
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, LineHeight = 20 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(message, "UMessageText");
        body.Children.Add(message);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 14, 22, 18) };
        Button? focus = null;
        for (int i = 0; i < buttons.Length; i++)
        {
            var (label, value) = buttons[i];
            var b = new Button { Content = label, MinWidth = 96, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0) };
            if (app?.TryFindResource(value == byDefault ? "DialogPrimary" : "DialogButton") is Style st) b.Style = st;
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, "UMessage" + value);
            b.Click += (_, _) => { result = value; w.Close(); };
            if (value == byDefault) { b.IsDefault = true; focus = b; }
            row.Children.Add(b);
        }
        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(row);
        w.Content = root;
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { result = onEscape; w.Close(); e.Handled = true; } };
        w.Loaded += (_, _) => focus?.Focus();
        w.ShowDialog();
        return result;
    }
}
