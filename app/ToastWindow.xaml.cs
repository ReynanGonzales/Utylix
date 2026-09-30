using System;
using System.Windows;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>The small "Download this link?" popup shown in the corner when you copy a downloadable link.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly Action _accepted;

    public ToastWindow(ClipboardOffer offer, Action accepted)
    {
        InitializeComponent();
        _accepted = accepted;
        TitleText.Text = offer.Title;
        TitleText.ToolTip = offer.Url;
        SubText.Text = offer.Subtitle;

        var icon = offer.Kind == OfferKind.File ? ShellIcons.ForFile(offer.Title) : null;
        if (icon != null)
        {
            IconImage.Source = icon;
            IconImage.Visibility = Visibility.Visible;
            Tile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BgBrush");     // a real logo sits on a neutral tile
            Tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "LineBrush");
        }
        else GlyphText.Text = offer.Kind == OfferKind.Video ? "🎬" : Categories.Glyph(Categories.Of(offer.Title));

        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;               // bottom-right corner, above the taskbar
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8;
        };
        _timer.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _timer.Stop();                  // don't vanish while you're reading it
        MouseLeave += (_, _) => { _timer.Stop(); _timer.Start(); };
    }

    public new void Show()
    {
        base.Show();                                            // ShowActivated=False: it never steals the keyboard focus
        _timer.Start();
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _accepted();
    }

    private void No_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
