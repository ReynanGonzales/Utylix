using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace IdmClone;

/// <summary>
/// The music player's "small player": the card itself is the window (no title bar, rounded, see-through around it). Press anywhere on it that isn't a
/// button or a bar and drag to move it. The controls are the same ones the full player uses; this only holds them.
/// </summary>
internal sealed class MusicCardWindow : Window
{
    private readonly Grid _host = new();
    private readonly Button _close;
    private readonly Action _openFull, _closeAll;
    private bool _reallyClosing;

    public MusicCardWindow(KeyEventHandler onKey, Action<DragEventArgs> onDrop, Action openFull, Action closeAll, bool onTop)
    {
        _openFull = openFull; _closeAll = closeAll;
        Title = "Utylix Music";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; ShowInTaskbar = true; Topmost = onTop; AllowDrop = true;
        UseLayoutRounding = true; FontFamily = new FontFamily("Segoe UI");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/music.ico")); } catch (Exception e) when (e is System.IO.IOException or UriFormatException) { }
        WindowTheme.OwnTaskbarButton(this, "Utylix.Music");

        // a small round x in the corner, there when the pointer is over the window
        _close = new Button
        {
            Content = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0), Opacity = 0, Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(200, 20, 22, 30)), Focusable = false, ToolTip = "Close the player",
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='11'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_close, "MusicCardClose");
        _close.Click += (_, _) => _closeAll();
        Content = _host;
        MouseEnter += (_, _) => _close.Opacity = 0.95;
        MouseLeave += (_, _) => _close.Opacity = 0;

        PreviewKeyDown += onKey;
        Drop += (_, e) => onDrop(e);
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        PreviewMouseLeftButtonDown += OnDown;
        Closing += (_, e) => { if (!_reallyClosing) { e.Cancel = true; _closeAll(); } };        // the x / Alt+F4 close the whole player
    }

    public void SetCard(FrameworkElement card)
    {
        _host.Children.Clear();
        card.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Opacity = 0.5, Color = Colors.Black };
        _host.Children.Add(card);
        _host.Children.Add(_close);
        Panel.SetZIndex(_close, 10);
    }

    public void CloseForReal() { _reallyClosing = true; Close(); }

    private static bool OnControl(DependencyObject? source)
    {
        for (var d = source; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase or RangeBase or Thumb or TextBoxBase or ScrollBar or WaveSeek) return true;
        return false;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (OnControl(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2) { _openFull(); e.Handled = true; return; }              // double-click: back to the full player
        try { DragMove(); } catch (InvalidOperationException) { /* the button was let go already */ }
    }
}
