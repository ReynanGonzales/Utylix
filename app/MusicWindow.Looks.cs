using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;

namespace IdmClone;

/// <summary>
/// The music player's looks. Every look is made of the same controls (cover, title, artist, seek bar, times, buttons); a look lays them out and
/// dresses them in its own way, so switching is instant and the playing song never stops.
/// </summary>
public sealed partial class MusicWindow
{
    private sealed record LookSpec(string Key, string Name, string Blurb);

    private static readonly LookSpec[] LookList =
    {
        new("classic", "Classic", "Big cover, the playlist beside it"),
        new("wide", "Wide card", "One slim card; the playlist drops under it"),
        new("dark", "Dark card", "A compact dark card with shuffle and repeat"),
        new("frost", "Frosted card", "A soft grey card with a volume bar"),
        new("light", "Light card", "A light card with a big cover"),
        new("wave", "Waveform card", "A white card that draws the song's waveform"),
    };

    private string _look = "classic";
    private double _cardWidth = 340;
    private bool _vertical;                                                // the card above the playlist (the wide look) instead of beside it
    private Brush _fgBrush = Brushes.White, _accentBrush = Brushes.DodgerBlue;
    private Brush _heartBrush = Hex("#FF5A6E");
    private WaveSeek? _wave;
    private readonly Dictionary<string, float[]> _waves = new(StringComparer.OrdinalIgnoreCase);
    private int _waveToken;

    private static SolidColorBrush Hex(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static void Detach(FrameworkElement? e)
    {
        if (e == null) return;
        switch (e.Parent)
        {
            case Panel p: p.Children.Remove(e); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
        }
    }

    private IEnumerable<FrameworkElement> SharedControls() => new FrameworkElement[]
    {
        _coverHost, _nowTitle, _nowArtist, _seek, _timeNow, _timeLen, _vol, _volIcon, _soundBtn, _shuffleBtn, _prevBtn, _play, _nextBtn, _repeatBtn, _heartBtn, _moreBtn,
    }.Concat(_wave == null ? Array.Empty<FrameworkElement>() : new FrameworkElement[] { _wave });

    // ---------- dressing the shared controls ----------
    private static void SetFg(Button b, Brush fg)
    {
        b.Foreground = fg;
        if (b.Content is TextBlock t) t.Foreground = fg;
    }

    private void Dress(Button b, double size, double glyph, Brush fg, bool light, Brush? fill = null, Brush? glyphOnFill = null)
    {
        b.Width = b.Height = size;
        b.Margin = new Thickness(3, 0, 3, 0);
        b.Background = fill ?? Brushes.Transparent;
        b.Template = fill == null ? ButtonTemplate(light) : FilledButtonTemplate();
        SetFg(b, glyphOnFill ?? fg);
        if (b.Content is TextBlock t) t.FontSize = glyph;
    }

    private static ControlTemplate FilledButtonTemplate() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='bd' Background='{TemplateBinding Background}' CornerRadius='100' MinWidth='{TemplateBinding Width}' MinHeight='{TemplateBinding Height}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.88' /></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Opacity' Value='0.7' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    private void DressText(TextBlock t, double size, FontWeight weight, Brush fg, TextAlignment align, HorizontalAlignment h = HorizontalAlignment.Stretch)
    {
        t.FontSize = size; t.FontWeight = weight; t.Foreground = fg; t.TextAlignment = align; t.HorizontalAlignment = h;
        t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.Margin = new Thickness(0); t.VerticalAlignment = VerticalAlignment.Center;
    }

    private void DressCover(double w, double h, double radius, Brush bg)
    {
        _coverHost.Width = w; _coverHost.Height = h; _coverHost.CornerRadius = new CornerRadius(radius); _coverHost.Background = bg;
        _coverHost.HorizontalAlignment = HorizontalAlignment.Center; _coverHost.VerticalAlignment = VerticalAlignment.Center; _coverHost.Margin = new Thickness(0);
        _coverFallback.FontSize = Math.Max(20, h * 0.4);
    }

    private void DressSeek(string track, string thumb, int thumbSize, int thickness, Brush fill)
    {
        _seek.Template = SliderTemplate(track, thumb, thumbSize, thickness);
        _seek.Foreground = fill;
        _seek.Margin = new Thickness(0); _seek.Width = double.NaN; _seek.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void DressTimes(Brush fg, double size)
    {
        foreach (var t in new[] { _timeNow, _timeLen }) { t.Foreground = fg; t.FontSize = size; t.Margin = new Thickness(0); t.VerticalAlignment = VerticalAlignment.Center; }
        _timeNow.HorizontalAlignment = HorizontalAlignment.Left; _timeLen.HorizontalAlignment = HorizontalAlignment.Right;
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    private static Grid TimesRow(UIElement left, UIElement right)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        g.Children.Add(left); g.Children.Add(right);
        return g;
    }

    // ---------- choosing and building a look ----------
    private void ApplyLook(string key)
    {
        if (!LookList.Any(l => l.Key == key)) key = "classic";
        _look = key; _saved.Look = key;
        foreach (var e in SharedControls()) Detach(e);
        Detach(_right);
        _root.Children.Clear(); _root.RowDefinitions.Clear(); _root.ColumnDefinitions.Clear();
        _vertical = key == "wide";
        _wave = null;

        FrameworkElement card = key switch
        {
            "wide" => BuildWide(), "dark" => BuildDark(), "frost" => BuildFrost(), "light" => BuildLight(), "wave" => BuildWave(), _ => BuildClassic(),
        };

        if (_vertical)
        {
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(card, 0); Grid.SetRow(_right, 1);
        }
        else
        {
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(card, 0); Grid.SetColumn(_right, 1);
        }
        _root.Children.Add(card); _root.Children.Add(_right);
        _right.Visibility = _compact || (_vertical && !_saved.ShowList) ? Visibility.Collapsed : Visibility.Visible;

        RefreshModes();
        UpdateHeart();
        var current = _list.FirstOrDefault(t => t.IsCurrent);
        if (current != null) UpdateNow(current);
        UpdateCoverVisibility();
        if (_wave != null && current != null) _ = LoadWaveAsync(current);
        FitWindowToLook();
    }

    /// <summary>The window is as wide as the card (and the playlist, when it shows).</summary>
    private void FitWindowToLook()
    {
        if (_compact) { MinWidth = _cardWidth + 10; Width = _cardWidth + 40; SizeToContent = SizeToContent.Height; return; }
        if (_vertical)
        {
            MinWidth = _cardWidth + 40;
            if (_saved.ShowList) { SizeToContent = SizeToContent.Manual; Width = Math.Max(Width, _cardWidth + 40); Height = Math.Clamp(Math.Max(Height, 560), 420, SystemParameters.WorkArea.Height); }
            else { Width = _cardWidth + 40; SizeToContent = SizeToContent.Height; }
            return;
        }
        SizeToContent = SizeToContent.Manual;
        MinWidth = _cardWidth + 280;
        Width = Math.Clamp(Math.Max(Width, _cardWidth + 520), _cardWidth + 280, SystemParameters.WorkArea.Width);
        Height = Math.Clamp(Math.Max(Height, 480), 420, SystemParameters.WorkArea.Height);
    }

    private void UpdateCoverVisibility()
    {
        bool none = _cover.Source == null;
        _coverFallback.Visibility = none && _look != "wide" ? Visibility.Visible : Visibility.Collapsed;
        _coverWords.Visibility = none && _look == "wide" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- classic ----------
    private FrameworkElement BuildClassic()
    {
        _cardWidth = 340;
        _fgBrush = Brushes.White; _accentBrush = Hex("#5B8DEF"); var muted = Hex("#A7AEBF");
        _heartBrush = Hex("#FF5A6E");
        DressCover(240, 240, 10, Hex("#1A1F2B"));
        _coverFallback.Foreground = Hex("#3A445E");
        DressText(_nowTitle, 20, FontWeights.SemiBold, Brushes.White, TextAlignment.Center); _nowTitle.TextWrapping = TextWrapping.Wrap; _nowTitle.TextTrimming = TextTrimming.None;
        DressText(_nowArtist, 14, FontWeights.Normal, muted, TextAlignment.Center); _nowArtist.TextWrapping = TextWrapping.Wrap; _nowArtist.TextTrimming = TextTrimming.None; _nowArtist.Margin = new Thickness(0, 4, 0, 0);
        DressSeek("#2D3446", "White", 14, 4, _accentBrush); _seek.Margin = new Thickness(0, 14, 0, 0);
        _vol.Template = SliderTemplate(); _vol.Foreground = _accentBrush; _vol.Width = 110;
        DressTimes(muted, 12);
        _volIcon.Foreground = muted;
        foreach (var b in new[] { _shuffleBtn, _prevBtn, _nextBtn, _repeatBtn, _heartBtn }) Dress(b, 44, 18, Brushes.White, false);
        Dress(_play, 44, 26, Brushes.White, false);
        Dress(_moreBtn, 36, 16, muted, false);

        var transport = Row(_shuffleBtn, _prevBtn, _play, _nextBtn, _repeatBtn); transport.Margin = new Thickness(0, 10, 0, 0);
        var volRow = Row(_volIcon, _vol, _soundBtn); volRow.Margin = new Thickness(0, 12, 0, 0);
        var extras = Row(_heartBtn, _moreBtn); extras.Margin = new Thickness(0, 4, 0, 0);
        var left = new StackPanel { Margin = new Thickness(26, 16, 26, 16), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_coverHost);
        left.Children.Add(new Border { Margin = new Thickness(0, 14, 0, 0), Child = _nowTitle });
        left.Children.Add(_nowArtist); left.Children.Add(_seek); left.Children.Add(TimesRow(_timeNow, _timeLen)); left.Children.Add(transport); left.Children.Add(volRow); left.Children.Add(extras);
        return new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = _cardWidth };
    }

    // ---------- wide card ----------
    private FrameworkElement BuildWide()
    {
        _cardWidth = 580;
        var white = Brushes.White; var grey = Hex("#9A9BA5");
        _fgBrush = white; _accentBrush = Hex("#FF5A6E"); _heartBrush = Hex("#FF5A6E");
        DressCover(104, 104, 16, Hex("#C9C9CB"));
        DressText(_nowTitle, 25, FontWeights.SemiBold, white, TextAlignment.Left);
        DressText(_nowArtist, 11.5, FontWeights.Normal, white, TextAlignment.Left);
        DressSeek("#7C7D87", "White", 9, 3, white); _seek.Margin = new Thickness(0, 10, 0, 0);
        DressTimes(grey, 11);
        Dress(_heartBtn, 38, 19, white, false); Dress(_moreBtn, 34, 15, white, false);
        Dress(_prevBtn, 36, 14, white, false); Dress(_play, 40, 22, white, false); Dress(_nextBtn, 36, 14, white, false);
        Dress(_shuffleBtn, 36, 14, white, false); Dress(_repeatBtn, 36, 14, white, false);

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_heartBtn, 1); Grid.SetColumn(_moreBtn, 2);
        titleRow.Children.Add(_nowTitle); titleRow.Children.Add(_heartBtn); titleRow.Children.Add(_moreBtn);

        var bottom = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        var transport = Row(_prevBtn, _play, _nextBtn);
        bottom.Children.Add(_timeNow); bottom.Children.Add(transport); bottom.Children.Add(_timeLen);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        info.Children.Add(titleRow); info.Children.Add(_nowArtist); info.Children.Add(_seek); info.Children.Add(bottom);
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(info, 1);
        inner.Children.Add(_coverHost); inner.Children.Add(info);
        return new Border { Background = Hex("#4B4C57"), CornerRadius = new CornerRadius(34), Padding = new Thickness(18, 16, 22, 14), Margin = new Thickness(20, 20, 20, 8), Width = _cardWidth, Child = inner, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    }

    // ---------- dark card ----------
    private FrameworkElement BuildDark()
    {
        _cardWidth = 340;
        var white = Brushes.White; var grey = Hex("#9A9A9F");
        _fgBrush = Hex("#D8D8DC"); _accentBrush = Hex("#5B8DEF"); _heartBrush = Hex("#FF5A6E");
        DressCover(54, 54, 9, Hex("#33343A"));
        DressText(_nowTitle, 17, FontWeights.SemiBold, white, TextAlignment.Left);
        DressText(_nowArtist, 10.5, FontWeights.Normal, grey, TextAlignment.Left);
        DressSeek("#4A4B52", "White", 11, 3, white);
        DressTimes(grey, 10);
        Dress(_moreBtn, 34, 16, white, false); Dress(_heartBtn, 34, 16, white, false);
        Dress(_shuffleBtn, 40, 15, _fgBrush, false); Dress(_prevBtn, 40, 17, white, false);
        Dress(_play, 58, 24, white, false, fill: Brushes.White, glyphOnFill: Hex("#111111"));
        Dress(_nextBtn, 40, 17, white, false); Dress(_repeatBtn, 40, 15, _fgBrush, false);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        titles.Children.Add(_nowTitle); titles.Children.Add(_nowArtist);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titles, 1); Grid.SetColumn(_heartBtn, 2); Grid.SetColumn(_moreBtn, 3);
        head.Children.Add(_coverHost); head.Children.Add(titles); head.Children.Add(_heartBtn); head.Children.Add(_moreBtn);
        _coverHost.HorizontalAlignment = HorizontalAlignment.Left;

        var stack = new StackPanel();
        stack.Children.Add(head);
        _seek.Margin = new Thickness(0, 26, 0, 0);
        stack.Children.Add(_seek); stack.Children.Add(TimesRow(_timeNow, _timeLen));
        var transport = Row(_shuffleBtn, _prevBtn, _play, _nextBtn, _repeatBtn); transport.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(transport);
        return Card(stack, Hex("#232326"), 20, new Thickness(20, 16, 20, 18));
    }

    // ---------- frosted card ----------
    private FrameworkElement BuildFrost()
    {
        _cardWidth = 340;
        var white = Brushes.White; var soft = Hex("#E8E8EC"); var track = Hex("#66FFFFFF");
        _fgBrush = white; _accentBrush = Hex("#FFFFFF"); _heartBrush = Hex("#FF5A6E");
        DressCover(54, 54, 10, Hex("#8A8C94"));
        DressText(_nowTitle, 17, FontWeights.SemiBold, white, TextAlignment.Left);
        DressText(_nowArtist, 10.5, FontWeights.Normal, soft, TextAlignment.Left);
        DressSeek("#66FFFFFF", "White", 9, 3, white);
        DressTimes(soft, 10);
        Dress(_heartBtn, 34, 16, white, false); Dress(_moreBtn, 34, 16, white, false);
        Dress(_prevBtn, 52, 28, Hex("#BFFFFFFF"), false); Dress(_play, 58, 32, white, false); Dress(_nextBtn, 52, 28, Hex("#BFFFFFFF"), false);
        Dress(_shuffleBtn, 34, 14, soft, false); Dress(_repeatBtn, 34, 14, soft, false);
        _vol.Template = SliderTemplate("#66FFFFFF", "White", 9, 3); _vol.Foreground = white; _vol.Width = double.NaN; _vol.HorizontalAlignment = HorizontalAlignment.Stretch;
        _volIcon.Foreground = soft; _volIcon.Margin = new Thickness(0, 0, 8, 0);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        titles.Children.Add(_nowTitle); titles.Children.Add(_nowArtist);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titles, 1); Grid.SetColumn(_heartBtn, 2); Grid.SetColumn(_moreBtn, 3);
        head.Children.Add(_coverHost); head.Children.Add(titles); head.Children.Add(_heartBtn); head.Children.Add(_moreBtn);
        _coverHost.HorizontalAlignment = HorizontalAlignment.Left;

        var stack = new StackPanel();
        stack.Children.Add(head);
        _seek.Margin = new Thickness(0, 22, 0, 0);
        stack.Children.Add(_seek); stack.Children.Add(TimesRow(_timeNow, _timeLen));
        var transport = Row(_prevBtn, _play, _nextBtn); transport.Margin = new Thickness(0, 6, 0, 4);
        stack.Children.Add(transport);
        var vol = new Grid { Margin = new Thickness(2, 6, 2, 0) };
        vol.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        vol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_vol, 1);
        vol.Children.Add(_volIcon); vol.Children.Add(_vol);
        stack.Children.Add(vol);
        var gradient = new LinearGradientBrush(Color.FromRgb(0x9B, 0x9D, 0xA6), Color.FromRgb(0x6C, 0x6E, 0x78), 70);
        gradient.Freeze();
        return Card(stack, gradient, 20, new Thickness(20, 16, 20, 16));
    }

    // ---------- light card ----------
    private FrameworkElement BuildLight()
    {
        _cardWidth = 340;
        var ink = Hex("#161616"); var grey = Hex("#4A4A4A");
        _fgBrush = ink; _accentBrush = Hex("#161616"); _heartBrush = Hex("#E8344E");
        DressCover(138, 138, 12, Hex("#141414"));
        _coverFallback.Foreground = Hex("#F2F2F2");
        DressText(_nowTitle, 17, FontWeights.Bold, ink, TextAlignment.Center);
        DressText(_nowArtist, 10.5, FontWeights.SemiBold, ink, TextAlignment.Center);
        DressSeek("#B4B4B4", "#161616", 8, 3, ink);
        DressTimes(grey, 10);
        Dress(_moreBtn, 34, 16, ink, true); Dress(_heartBtn, 34, 16, ink, true);
        Dress(_prevBtn, 52, 26, ink, true); Dress(_play, 58, 30, ink, true); Dress(_nextBtn, 52, 26, ink, true);
        Dress(_shuffleBtn, 34, 14, ink, true); Dress(_repeatBtn, 34, 14, ink, true);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_heartBtn, 2);
        top.Children.Add(_moreBtn); top.Children.Add(_heartBtn);
        _coverHost.HorizontalAlignment = HorizontalAlignment.Center;
        _coverHost.Margin = new Thickness(0, -6, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(top); stack.Children.Add(_coverHost);
        _nowTitle.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(_nowTitle); stack.Children.Add(_nowArtist);
        _seek.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(_seek); stack.Children.Add(TimesRow(_timeNow, _timeLen));
        var transport = Row(_shuffleBtn, _prevBtn, _play, _nextBtn, _repeatBtn); transport.Margin = new Thickness(0, 4, 0, 2);
        stack.Children.Add(transport);
        return Card(stack, Hex("#DCDCDE"), 20, new Thickness(20, 12, 20, 14));
    }

    // ---------- waveform card ----------
    private FrameworkElement BuildWave()
    {
        _cardWidth = 340;
        var ink = Hex("#151515"); var grey = Hex("#7A7A7E");
        _fgBrush = ink; _accentBrush = Hex("#151515"); _heartBrush = Hex("#E8344E");
        DressCover(50, 50, 9, Hex("#E4E4E6"));
        _coverFallback.Foreground = Hex("#9A9AA0");
        DressText(_nowTitle, 17, FontWeights.Bold, ink, TextAlignment.Left);
        DressText(_nowArtist, 10, FontWeights.Normal, grey, TextAlignment.Left);
        DressTimes(grey, 10);
        Dress(_moreBtn, 34, 16, ink, true); Dress(_heartBtn, 34, 16, ink, true);
        Dress(_prevBtn, 46, 22, ink, true); Dress(_play, 54, 28, ink, true); Dress(_nextBtn, 46, 22, ink, true);
        Dress(_shuffleBtn, 34, 14, ink, true); Dress(_repeatBtn, 34, 14, ink, true);

        _wave = new WaveSeek { Height = 60, Margin = new Thickness(0, 14, 0, 0), Played = ink, Rest = Hex("#C6C6CA") };
        _wave.SeekTo += fraction => { long target = (long)(fraction * _snap.Length); Post(mp => mp.Time = target); };

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        titles.Children.Add(_nowTitle); titles.Children.Add(_nowArtist);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titles, 1); Grid.SetColumn(_heartBtn, 2); Grid.SetColumn(_moreBtn, 3);
        head.Children.Add(_coverHost); head.Children.Add(titles); head.Children.Add(_heartBtn); head.Children.Add(_moreBtn);
        _coverHost.HorizontalAlignment = HorizontalAlignment.Left;

        var stack = new StackPanel();
        stack.Children.Add(head); stack.Children.Add(_wave); stack.Children.Add(TimesRow(_timeNow, _timeLen));
        var transport = Row(_shuffleBtn, _prevBtn, _play, _nextBtn, _repeatBtn); transport.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(transport);
        var card = Card(stack, Brushes.White, 20, new Thickness(20, 16, 20, 14));
        card.BorderBrush = ink; card.BorderThickness = new Thickness(1.5);
        return card;
    }

    private Border Card(UIElement content, Brush background, double radius, Thickness padding) => new()
    {
        Background = background, CornerRadius = new CornerRadius(radius), Padding = padding, Margin = new Thickness(20), Width = _cardWidth, Child = content,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    // ---------- the heart: favourites ----------
    private Track? CurrentTrack => _list.FirstOrDefault(t => t.IsCurrent);

    private void UpdateHeart()
    {
        var t = CurrentTrack;
        bool fav = t?.IsFavourite == true;
        if (_heartBtn.Content is TextBlock tb) { tb.Text = fav ? "" : ""; }
        SetFg(_heartBtn, fav ? _heartBrush : _fgBrush);
        _heartBtn.ToolTip = fav ? "A favourite: click to remove the heart (F)" : "Mark as a favourite (F)";
    }

    private void ToggleFavourite() => SetFavourite(CurrentTrack ?? (_playlist.SelectedItem as Track), null);

    private void SetFavourite(Track? track, bool? value)
    {
        if (track == null) return;
        bool on = value ?? !track.IsFavourite;
        track.IsFavourite = on;
        _saved.Favourites.RemoveAll(p => string.Equals(p, track.Path, StringComparison.OrdinalIgnoreCase));
        if (on) _saved.Favourites.Add(track.Path);
        UpdateHeart();
        Save();
    }

    /// <summary>Replaces the playlist with the songs that have a heart (the ones that are still there).</summary>
    private void PlayFavourites()
    {
        var songs = _saved.Favourites.Where(File.Exists).ToList();
        if (songs.Count == 0)
        {
            UMessage.Show(this, "No favourites yet. Press the heart (or F) while a song plays to mark it.", "Utylix Music", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Clear();
        AddAndPlay(songs, enqueue: false);
    }

    // ---------- the menus ----------
    private ContextMenu NewMenu() => new() { Style = (Style)FindResource("ThemedMenu"), PlacementTarget = this };

    private MenuItem Item(string header, Action action, bool checkedOn = false, bool check = false)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource("ThemedMenuItem"), IsCheckable = check, IsChecked = checkedOn };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Just the looks, as a menu under the "Look…" button.</summary>
    private void ShowLookMenu(UIElement? under = null)
    {
        var menu = NewMenu();
        foreach (var l in LookList)
        {
            var key = l.Key;
            menu.Items.Add(Item(l.Name + "   -   " + l.Blurb, () => { ApplyLook(key); Save(); }, checkedOn: key == _look, check: true));
        }
        menu.PlacementTarget = under ?? _right; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ShowMoreMenu()
    {
        var menu = NewMenu();
        var cur = CurrentTrack;
        menu.Items.Add(Item(cur?.IsFavourite == true ? "Remove the heart" : "Mark as favourite   (F)", ToggleFavourite));
        menu.Items.Add(Item("Edit song info…   (F2)", () => { var t = CurrentTrack ?? _playlist.SelectedItem as Track; if (t != null) EditInfo(new List<Track> { t }); }));
        menu.Items.Add(Item("Show in its folder", () => ShowInFolder(CurrentTrack ?? _playlist.SelectedItem as Track)));
        menu.Items.Add(new Separator());
        var looks = new MenuItem { Header = "Look", Style = (Style)FindResource("ThemedMenuItem") };
        foreach (var l in LookList)
        {
            var key = l.Key;
            var it = Item(l.Name + "   -   " + l.Blurb, () => { ApplyLook(key); Save(); }, checkedOn: key == _look, check: true);
            looks.Items.Add(it);
        }
        menu.Items.Add(looks);
        if (_vertical) menu.Items.Add(Item(_saved.ShowList ? "Hide the playlist" : "Show the playlist", TogglePlaylistUnderCard));
        else menu.Items.Add(Item(_compact ? "Show the playlist" : "Small player (hide the playlist)", ToggleCompact));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Shuffle   (S)", () => { _shuffle = !_shuffle; RebuildOrder(keepCurrent: true); RefreshModes(); }, _shuffle, true));
        var repeat = new MenuItem { Header = "Repeat   (R)", Style = (Style)FindResource("ThemedMenuItem") };
        repeat.Items.Add(Item("Off", () => { _repeat = Repeat.Off; RefreshModes(); }, _repeat == Repeat.Off, true));
        repeat.Items.Add(Item("All songs", () => { _repeat = Repeat.All; RefreshModes(); }, _repeat == Repeat.All, true));
        repeat.Items.Add(Item("This song", () => { _repeat = Repeat.One; RefreshModes(); }, _repeat == Repeat.One, true));
        menu.Items.Add(repeat);
        menu.Items.Add(Item("Sound…  (equalizer, speed, sleep timer)", ShowSound));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Add songs…", () => Browse(add: true)));
        menu.Items.Add(Item("Add a folder…", AddFolder));
        menu.Items.Add(Item("Play my favourites", PlayFavourites));
        menu.PlacementTarget = _moreBtn; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu PlaylistMenu()
    {
        var picked = _playlist.SelectedItems.Cast<Track>().ToList();
        var menu = NewMenu();
        menu.Items.Add(Item("Play", () => { if (_playlist.SelectedIndex >= 0) PlayTrack(_playlist.SelectedIndex); }));
        menu.Items.Add(Item(picked.All(t => t.IsFavourite) ? "Remove the heart" : "Mark as favourite", () => { bool on = !picked.All(t => t.IsFavourite); foreach (var t in picked) SetFavourite(t, on); }));
        menu.Items.Add(Item(picked.Count > 1 ? $"Edit info of {picked.Count} songs…   (F2)" : "Edit song info…   (F2)", () => EditInfo(picked)));
        menu.Items.Add(Item("Show in its folder", () => ShowInFolder(picked.FirstOrDefault())));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove from the playlist   (Delete)", () => { foreach (var t in picked.ToList()) { int i = _list.IndexOf(t); if (i >= 0) RemoveAt(i); } }));
        return menu;
    }

    private static void ShowInFolder(Track? track)
    {
        if (track == null || !File.Exists(track.Path)) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{track.Path}\""); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>The wide look: the playlist under the card, or only the card.</summary>
    private void TogglePlaylistUnderCard()
    {
        _saved.ShowList = !_saved.ShowList;
        _right.Visibility = _saved.ShowList ? Visibility.Visible : Visibility.Collapsed;
        if (!_compact) FitWindowToLook();
        Save();
    }

    // ---------- the waveform ----------
    private async System.Threading.Tasks.Task LoadWaveAsync(Track track)
    {
        if (_wave == null) return;
        int token = ++_waveToken;
        _wave.Peaks = _waves.TryGetValue(track.Path, out var known) ? known : null;
        if (known != null) return;
        string path = track.Path;
        var peaks = await System.Threading.Tasks.Task.Run(() => WavePeaks.Compute(path, 150));
        if (peaks != null) _waves[path] = peaks;
        if (token == _waveToken && _wave != null) _wave.Peaks = peaks;
    }
}
