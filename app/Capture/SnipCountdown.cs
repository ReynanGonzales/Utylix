using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IdmClone;

/// <summary>
/// A small count-down at the top of the screen while a snip waits for its timer ("Snipping in 3…"). It never takes the keyboard from what the
/// person is arranging; a click on it cancels the snip. It is closed before the screen is captured, so it is never in the picture.
/// </summary>
internal sealed class SnipCountdown : Window
{
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 15, FontWeight = FontWeights.SemiBold };

    public SnipCountdown(Action cancel)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; Focusable = false;
        var sub = new TextBlock { Text = "click here to cancel", Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAE, 0xBF)), FontFamily = new FontFamily("Segoe UI"), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center };
        var stack = new StackPanel();
        stack.Children.Add(_text); stack.Children.Add(sub);
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        Content = new Border
        {
            Child = stack, Background = new SolidColorBrush(Color.FromArgb(235, 20, 24, 34)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)), BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(22, 10, 22, 10), Cursor = Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45 },
            Margin = new Thickness(14),
        };
        MouseLeftButtonDown += (_, _) => cancel();
        Left = -10000; Top = 0;
        SourceInitialized += (_, _) => { };
    }

    public void ShowAt(int secondsLeft)
    {
        _text.Text = $"Snipping in {secondsLeft}…";
        if (!IsVisible) Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 10;
    }
}
