using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IdmClone.Vid;

/// <summary>The tool chosen in the tools column: selection (V), razor (C), hand (H).</summary>
public enum VidTool { Select, Razor, Hand, Ripple, Rolling, Slip, TrackForward }

/// <summary>
/// The timeline: ruler, track headers and the clips, drawn by hand. Click a clip to choose it, drag it to move it (also to another track), drag its
/// ends to trim, click the ruler to place the playhead, Ctrl + wheel to zoom. Everything that changes the project goes through <see cref="VidSession"/>.
/// </summary>
public sealed class VidTimeline : FrameworkElement
{
    public const double HeaderWidth = 150;
    public const double RulerHeight = 28;
    private const double VideoRow = 58, AudioRow = 44, Gap = 3, EdgeGrab = 7;

    private readonly VidSession _session;
    private readonly VidMediaCache _cache;

    private double _pps = 70;               // pixels per second
    private double _offset;                 // seconds at the left edge of the clip area

    private enum Drag { None, Playhead, Move, TrimStart, TrimEnd, Pan, Marquee, RippleStart, RippleEnd, Roll, SlipClip }
    private Drag _drag;
    private Point _downPoint;
    private double _downTime;
    private Clip? _dragClip;
    private List<Clip> _dragClips = new();
    private string? _editBefore;
    private double _moveDt; private int _moveRows;
    private bool _moved;
    private double _dropTime = -1; private int _dropRow = -1;
    private double _panOffset;
    private double _razorX = -1;
    private Rect? _marquee;
    private Point _marqueeStart;
    private double _ghostTime = -1;
    private Clip? _rollLeft;
    private double _slipDelta;
    private HashSet<string> _marqueeBase = new();

    public VidTool Tool { get; set; } = VidTool.Select;
    public bool SnapEnabled { get; set; } = true;


    public VidTimeline(VidSession session, VidMediaCache cache)
    {
        _session = session;
        _cache = cache;
        Focusable = true;
        ClipToBounds = true;
        AllowDrop = true;
        _session.Changed += InvalidateVisual;
        _session.SelectionChanged += InvalidateVisual;
        _cache.Updated += InvalidateVisual;
        SnapsToDevicePixels = true;
    }

    // ---------- view ----------
    public double PixelsPerSecond
    {
        get => _pps;
        set { _pps = Math.Clamp(value, 2, 1200); ViewChanged?.Invoke(); InvalidateVisual(); }
    }

    public double Offset
    {
        get => _offset;
        set { _offset = Math.Max(0, value); ViewChanged?.Invoke(); InvalidateVisual(); }
    }

    public double VisibleSeconds => Math.Max(0.1, (ActualWidth - HeaderWidth) / _pps);

    public event Action? ViewChanged;
    /// <summary>The ruler was pressed / dragged: move the playhead there. The second value is true on release.</summary>
    public event Action<double, bool>? PlayheadDragged;
    public event Action<Clip?, double>? ContextMenuRequested;
    public event Action<string, double, int>? MediaDropped;
    public event Action<string[], double>? FilesDropped;
    public event Action<Clip>? ClipActivated;
    public event Action? Interacted;
    /// <summary>Right-click on the ruler: where (time).</summary>
    public event Action<double>? RulerContextRequested;
    /// <summary>Right-click on a track's header.</summary>
    public event Action<Track>? TrackMenuRequested;
    /// <summary>A marker on the ruler was double-clicked.</summary>
    public event Action<Marker>? MarkerActivated;

    public void ZoomToFit()
    {
        double d = Math.Max(5, _session.Project.Duration * 1.05);
        PixelsPerSecond = Math.Max(2, (ActualWidth - HeaderWidth - 24) / d);
        Offset = 0;
    }

    public void ZoomAround(double factor, double timeAnchor, double anchorX)
    {
        double newPps = Math.Clamp(_pps * factor, 2, 1200);
        _pps = newPps;
        _offset = Math.Max(0, timeAnchor - (anchorX - HeaderWidth) / newPps);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void EnsureVisible(double t)
    {
        double w = VisibleSeconds;
        if (t < _offset) Offset = Math.Max(0, t - w * 0.1);
        else if (t > _offset + w * 0.92) Offset = t - w * 0.2;
    }

    private double TimeToX(double t) => HeaderWidth + (t - _offset) * _pps;
    private double XToTime(double x) => _offset + (x - HeaderWidth) / _pps;

    // ---------- rows ----------
    /// <summary>Top layer first (like an editing program): the last video track, ..., the first one, then the audio tracks.</summary>
    private List<Track> Rows()
    {
        var p = _session.Project;
        var rows = p.Tracks.Where(t => t.Kind == TrackKind.Video).Reverse().ToList();
        rows.AddRange(p.Tracks.Where(t => t.Kind == TrackKind.Audio));
        return rows;
    }

    private static double RowHeight(Track t) => t.Kind == TrackKind.Video ? VideoRow : AudioRow;

    private double RowTop(List<Track> rows, int index)
    {
        double y = RulerHeight + 4;
        for (int i = 0; i < index; i++) y += RowHeight(rows[i]) + Gap;
        return y;
    }

    private int RowAt(double y, List<Track> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            double top = RowTop(rows, i);
            if (y >= top && y < top + RowHeight(rows[i])) return i;
        }
        return -1;
    }

    protected override Size MeasureOverride(Size available)
    {
        var rows = Rows();
        double h = RowTop(rows, rows.Count) + 12;
        return new Size(double.IsInfinity(available.Width) ? 600 : available.Width, h);
    }

    // ---------- drawing ----------
    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    private static Color C(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static readonly Typeface Face = new("Segoe UI");

    private FormattedText Text(string s, double size, Brush brush, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        if (!double.IsInfinity(maxWidth)) ft.MaxTextWidth = Math.Max(1, maxWidth);
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        var bg = B("BgBrush"); var card = B("CardBrush"); var line = B("LineBrush"); var text = B("TextBrush"); var muted = B("MutedBrush");
        var accent = C("AccentBrush");
        bool dark = App.IsDarkTheme;
        dc.DrawRectangle(bg, null, new Rect(0, 0, W, H));
        var rows = Rows();

        // rows
        for (int i = 0; i < rows.Count; i++)
        {
            double top = RowTop(rows, i), h = RowHeight(rows[i]);
            dc.DrawRectangle(card, null, new Rect(HeaderWidth, top, Math.Max(0, W - HeaderWidth), h));
        }

        // ruler
        dc.DrawRectangle(card, null, new Rect(0, 0, W, RulerHeight));
        dc.DrawLine(new Pen(line, 1), new Point(0, RulerHeight), new Point(W, RulerHeight));
        DrawRuler(dc, W, muted, line);
        DrawMarkersAndWorkArea(dc, W, H, accent);

        // clips
        var selected = _session.Selected;
        var movingSet = _drag == Drag.Move && _moved ? new HashSet<Clip>(_dragClips) : null;
        for (int r = 0; r < rows.Count; r++)
        {
            double top = RowTop(rows, r), h = RowHeight(rows[r]);
            foreach (var c in rows[r].Clips)
            {
                if (movingSet != null && movingSet.Contains(c)) continue;
                double x0 = TimeToX(c.Start), x1 = TimeToX(c.End);
                if (x1 < HeaderWidth - 4 || x0 > W + 4) continue;
                DrawClip(dc, c, rows[r], new Rect(x0, top, Math.Max(2, x1 - x0), h), selected.Contains(c.Id), false, dark, accent);
            }
        }
        // clips being moved are drawn where they would land
        if (movingSet != null)
        {
            foreach (var c in _dragClips)
            {
                var home = _session.Project.TrackOf(c);
                if (home == null) continue;
                int row = rows.IndexOf(home);
                int rowShift = _moveRows;
                int target = Math.Clamp(row + rowShift, 0, rows.Count - 1);
                if (rows[target].Kind != home.Kind) target = row;
                double top = RowTop(rows, target), h = RowHeight(rows[target]);
                double start = Math.Max(0, c.Start + _moveDt);
                DrawClip(dc, c, rows[target], new Rect(TimeToX(start), top, Math.Max(2, c.Length * _pps), h), true, true, dark, accent);
            }
        }

        // drop indicator
        if (_dropTime >= 0 && _dropRow >= 0 && _dropRow < rows.Count)
        {
            double top = RowTop(rows, _dropRow), h = RowHeight(rows[_dropRow]);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, accent.R, accent.G, accent.B)), new Pen(new SolidColorBrush(accent), 1.5), new Rect(TimeToX(_dropTime), top, 90, h));
        }

        // ripple / rolling / slip feedback: the edge as it would be
        if (_ghostTime >= 0)
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)), 2), new Point(TimeToX(_ghostTime), RulerHeight), new Point(TimeToX(_ghostTime), H));
        if (_marquee is Rect mq)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)), new Pen(new SolidColorBrush(accent), 1) { DashStyle = DashStyles.Dash }, mq);

        // razor guide: where the cut would be
        if (Tool == VidTool.Razor && _razorX >= HeaderWidth)
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)), 1) { DashStyle = DashStyles.Dash }, new Point(_razorX, RulerHeight), new Point(_razorX, H));

        // playhead
        double px = TimeToX(_session.Playhead);
        if (px >= HeaderWidth - 1 && px <= W)
        {
            var red = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            dc.DrawLine(new Pen(red, 1.5), new Point(px, 0), new Point(px, H));
            var tri = new StreamGeometry();
            using (var g = tri.Open()) { g.BeginFigure(new Point(px - 6, 0), true, true); g.LineTo(new Point(px + 6, 0), true, false); g.LineTo(new Point(px, 10), true, false); }
            tri.Freeze();
            dc.DrawGeometry(red, null, tri);
        }
        // the header column covers whatever scrolled under it
        dc.DrawRectangle(bg, null, new Rect(0, RulerHeight + 1, HeaderWidth - 6, Math.Max(0, H - RulerHeight)));
        for (int i = 0; i < rows.Count; i++)
        {
            double top = RowTop(rows, i), h = RowHeight(rows[i]);
            var t = rows[i];
            dc.DrawRectangle(card, new Pen(line, 1), new Rect(0.5, top, HeaderWidth - 6, h));
            dc.DrawText(Text(t.Name, 12.5, text, HeaderWidth - (t.Kind == TrackKind.Audio ? 96 : 70)), new Point(10, top + 6));
            dc.DrawText(Text(t.Kind == TrackKind.Video ? "picture" : "sound", 10.5, muted), new Point(10, top + h - 18));
            DrawToggle(dc, ToggleRect(top, 0), t.Kind == TrackKind.Video ? "" : "", t.Muted, accent, muted);
            if (t.Kind == TrackKind.Audio) DrawSolo(dc, ToggleRect(top, 2), t.Solo, accent, muted);
            DrawToggle(dc, ToggleRect(top, 1), "", t.Locked, accent, muted);
        }
    }

    private void DrawSolo(DrawingContext dc, Rect r, bool on, Color accent, Brush muted)
    {
        if (on) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)), null, r, 5, 5);
        var ft = new FormattedText("S", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 12,
            on ? Brushes.Black : muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    /// <summary>The coloured flags of markers on the ruler, and the In / Out range (shaded over the ruler and tracks).</summary>
    private void DrawMarkersAndWorkArea(DrawingContext dc, double W, double H, Color accent)
    {
        var p = _session.Project;
        if (p.WorkIn != null || p.WorkOut != null)
        {
            double a = p.WorkIn ?? 0, b = p.WorkOut ?? p.Duration;
            double x0 = Math.Max(HeaderWidth, TimeToX(a)), x1 = Math.Min(W, TimeToX(b));
            if (x1 > x0)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(34, accent.R, accent.G, accent.B)), null, new Rect(x0, RulerHeight, x1 - x0, Math.Max(0, H - RulerHeight)));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)), null, new Rect(x0, RulerHeight - 5, x1 - x0, 5));
            }
        }
        foreach (var m in p.Markers)
        {
            double x = TimeToX(m.Time);
            if (x < HeaderWidth - 8 || x > W + 8) continue;
            Color col;
            try { col = (Color)ColorConverter.ConvertFromString(m.Color); } catch (FormatException) { col = Colors.LimeGreen; }
            var flag = new StreamGeometry();
            using (var g = flag.Open())
            {
                g.BeginFigure(new Point(x, RulerHeight), true, true);
                g.LineTo(new Point(x - 6, RulerHeight - 9), true, false);
                g.LineTo(new Point(x - 6, 3), true, false);
                g.LineTo(new Point(x + 6, 3), true, false);
                g.LineTo(new Point(x + 6, RulerHeight - 9), true, false);
            }
            flag.Freeze();
            dc.DrawGeometry(new SolidColorBrush(col), null, flag);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(120, col.R, col.G, col.B)), 1) { DashStyle = DashStyles.Dot }, new Point(x, RulerHeight), new Point(x, H));
            if (!string.IsNullOrEmpty(m.Name)) dc.DrawText(Text(m.Name, 10.5, new SolidColorBrush(col), 120), new Point(x + 8, 3));
        }
    }

    private static Rect ToggleRect(double top, int n) => new(HeaderWidth - 6 - 26 - n * 26, top + 5, 22, 22);

    private void DrawToggle(DrawingContext dc, Rect r, string glyph, bool on, Color accent, Brush muted)
    {
        if (on) dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, r, 5, 5);
        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe MDL2 Assets"), 12, on ? Brushes.White : muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    private void DrawRuler(DrawingContext dc, double W, Brush muted, Brush line)
    {
        double[] steps = { 0.04, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
        double major = steps.FirstOrDefault(s => s * _pps >= 80);
        if (major == 0) major = 3600;
        double minor = major / 5;
        double t0 = Math.Floor(_offset / minor) * minor;
        var pen = new Pen(line, 1);
        var penMajor = new Pen(muted, 1);
        for (double t = t0; TimeToX(t) < W; t += minor)
        {
            double x = Math.Round(TimeToX(t)) + 0.5;
            if (x < HeaderWidth) continue;
            bool isMajor = Math.Abs(Math.IEEERemainder(t, major)) < minor / 4;
            dc.DrawLine(isMajor ? penMajor : pen, new Point(x, RulerHeight - (isMajor ? 10 : 5)), new Point(x, RulerHeight));
            if (isMajor) dc.DrawText(Text(TimeText(t, major < 1), 11, muted), new Point(x + 4, 4));
        }
    }

    /// <summary>HH:MM:SS:FF like Premiere's timecode (frames at the project's frame rate).</summary>
    public static string Timecode(double t, double fps)
    {
        if (t < 0) t = 0;
        if (fps <= 0) fps = 30;
        long frames = (long)Math.Round(t * fps);
        int fr = (int)(frames % (long)Math.Round(fps));
        long secs = frames / (long)Math.Round(fps);
        return $"{secs / 3600:00}:{secs / 60 % 60:00}:{secs % 60:00}:{fr:00}";
    }

    public static string TimeText(double t, bool fraction = false)
    {
        if (t < 0) t = 0;
        long cs = (long)Math.Round(t * 100);                          // hundredths, rounded once so 0.995 shows as 1.00, not 0.99
        long whole = fraction ? cs / 100 : (long)Math.Floor(t + 1e-6);          // (1.9999999 from adding steps is 2)
        int h = (int)(whole / 3600), m = (int)(whole / 60) % 60, s = (int)(whole % 60);
        string head = h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        return fraction ? head + "." + (cs % 100).ToString("00") : head;
    }

    private void DrawClip(DrawingContext dc, Clip c, Track track, Rect r, bool selected, bool ghost, bool dark, Color accent)
    {
        var m = _session.Project.MediaOf(c);
        bool isAudio = track.Kind == TrackKind.Audio;
        bool isText = c.Kind == ClipKind.Text;
        Color baseColor = isText ? Color.FromRgb(0xD9, 0x68, 0x0F) : isAudio ? Color.FromRgb(0x0F, 0x9D, 0x91) : m?.Kind == MediaKind.Image ? Color.FromRgb(0x8B, 0x5C, 0xF6) : accent;
        Color fill = Mix(C("CardBrush"), baseColor, dark ? 0.55 : 0.85);
        byte alpha = (byte)(track.Muted ? 90 : ghost ? 200 : 255);
        var rect = new Rect(r.X + 0.5, r.Y + 1.5, Math.Max(2, r.Width - 1), r.Height - 3);
        var clip = new RectangleGeometry(rect, 5, 5);
        dc.PushClip(clip);
        dc.PushOpacity(alpha / 255.0);
        dc.DrawRectangle(new SolidColorBrush(fill), null, rect);

        double visible0 = Math.Max(rect.X, HeaderWidth), visible1 = Math.Min(rect.Right, ActualWidth);
        if (m != null && visible1 > visible0)
        {
            if (!isAudio && m.HasVideo)
            {
                double th = rect.Height - 2;
                var probe = _cache.Thumbnail(m, c.In);
                double aspect = probe != null && probe.Height > 0 ? probe.Width / probe.Height : 16.0 / 9;
                double tw = Math.Max(24, th * aspect);
                for (double x = rect.X + 1; x < rect.Right; x += tw)
                {
                    if (x + tw < visible0 || x > visible1) continue;
                    double srcTime = c.In + Math.Max(0, (x - rect.X) / _pps) * c.Speed;
                    var img = m.Kind == MediaKind.Image ? probe : _cache.Thumbnail(m, srcTime);
                    if (img == null) continue;
                    dc.PushClip(new RectangleGeometry(new Rect(x, rect.Y + 1, Math.Min(tw, rect.Right - x), th)));
                    dc.DrawImage(img, new Rect(x, rect.Y + 1, tw, th));
                    dc.Pop();
                }
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, rect);
            }
            else if (isAudio && m.HasAudio)
            {
                var peaks = _cache.Peaks(m);
                if (peaks != null)
                {
                    double mid = rect.Y + rect.Height / 2 + 5, amp = (rect.Height - 24) / 2;
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1);
                    double step = 2;
                    for (double x = Math.Max(rect.X, visible0); x < Math.Min(rect.Right, visible1); x += step)
                    {
                        double s0 = c.In + (x - rect.X) / _pps * c.Speed, s1 = c.In + (x + step - rect.X) / _pps * c.Speed;
                        int i0 = Math.Clamp((int)(s0 / VidMediaCache.PeakStep), 0, peaks.Length - 1), i1 = Math.Clamp((int)(s1 / VidMediaCache.PeakStep), i0, peaks.Length - 1);
                        int max = 0;
                        for (int i = i0; i <= i1; i++) if (peaks[i] > max) max = peaks[i];
                        double hgt = Math.Max(1, max / 255.0 * amp * c.Volume);
                        dc.DrawLine(pen, new Point(x + 0.5, mid - hgt), new Point(x + 0.5, mid + hgt));
                    }
                }
            }
        }
        dc.Pop(); // opacity
        // name strip
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(rect.X, rect.Y, rect.Width, 17));
        string label = isText ? "T  " + (c.Text ?? "").Split('\n')[0] : (c.Label ?? m?.Name ?? "") + (Math.Abs(c.Speed - 1) > 0.001 ? $"  {c.Speed:0.##}x" : "");
        if (rect.Width > 30) dc.DrawText(Text(label, 11, Brushes.White, rect.Width - 8), new Point(rect.X + 5, rect.Y + 1.5));
        // fades
        if (c.FadeIn > 0) dc.DrawLine(new Pen(Brushes.White, 1.2), new Point(rect.X, rect.Bottom - 2), new Point(rect.X + c.FadeIn * _pps, rect.Y + 18));
        if (c.FadeOut > 0) dc.DrawLine(new Pen(Brushes.White, 1.2), new Point(rect.Right - c.FadeOut * _pps, rect.Y + 18), new Point(rect.Right, rect.Bottom - 2));
        // the way the clip comes in
        if (c.Transition != TransitionKind.None)
        {
            double tw = Math.Max(6, c.TransitionLength * _pps);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), null, new Rect(rect.X, rect.Y + 17, Math.Min(tw, rect.Width), rect.Height - 17));
            if (rect.Width > 20) dc.DrawText(Text("\u21C6", 11, Brushes.Black), new Point(rect.X + 3, rect.Y + 19));
        }
        // effects and keyframes
        if (c.Effects.Count > 0 && rect.Width > 60)
            dc.DrawText(Text("fx", 11, new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD))), new Point(rect.Right - 18, rect.Y + 1.5));
        foreach (var list in c.Keys.Values)
            foreach (var k in list)
            {
                double kx = TimeToX(c.Start + k.T);
                if (kx < rect.X + 2 || kx > rect.Right - 2) continue;
                var d = new StreamGeometry();
                using (var g = d.Open()) { g.BeginFigure(new Point(kx, rect.Bottom - 12), true, true); g.LineTo(new Point(kx + 4, rect.Bottom - 8), true, false); g.LineTo(new Point(kx, rect.Bottom - 4), true, false); g.LineTo(new Point(kx - 4, rect.Bottom - 8), true, false); }
                d.Freeze();
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)), new Pen(Brushes.Black, 0.8), d);
            }
        dc.Pop();
        var border = selected ? new Pen(dark ? Brushes.White : new SolidColorBrush(C("TextBrush")), 2) : new Pen(new SolidColorBrush(Mix(fill, Colors.Black, 0.35)), 1);
        dc.DrawRoundedRectangle(null, border, rect, 5, 5);
        if (selected) dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(accent), 1), new Rect(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2), 6, 6);
    }

    // ---------- hit testing ----------
    private (Clip? Clip, Drag Zone, int Row) HitTest(Point p, List<Track> rows)
    {
        int row = RowAt(p.Y, rows);
        if (row < 0 || p.X < HeaderWidth) return (null, Drag.None, row);
        double t = XToTime(p.X);
        foreach (var c in rows[row].Clips)
        {
            double x0 = TimeToX(c.Start), x1 = TimeToX(c.End);
            if (p.X < x0 - 2 || p.X > x1 + 2) continue;
            double grab = Math.Min(EdgeGrab, (x1 - x0) / 3);
            if (p.X <= x0 + grab) return (c, Drag.TrimStart, row);
            if (p.X >= x1 - grab) return (c, Drag.TrimEnd, row);
            return (c, Drag.Move, row);
        }
        return (null, Drag.None, row);
    }

    // ---------- mouse ----------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Interacted?.Invoke();
        var p = e.GetPosition(this);
        var rows = Rows();
        _downPoint = p;
        _downTime = XToTime(p.X);
        _moved = false;

        if (p.Y < RulerHeight)
        {
            var hitMarker = _session.Project.Markers.FirstOrDefault(m => Math.Abs(TimeToX(m.Time) - p.X) <= 7);
            if (hitMarker != null && e.ClickCount == 2) { MarkerActivated?.Invoke(hitMarker); e.Handled = true; return; }
            _drag = Drag.Playhead;
            CaptureMouse();
            PlayheadDragged?.Invoke(Math.Max(0, XToTime(p.X)), false);
            return;
        }
        // the small switches in the track header
        if (p.X < HeaderWidth)
        {
            int hr = RowAt(p.Y, rows);
            if (hr >= 0)
            {
                double top = RowTop(rows, hr);
                var track = rows[hr];
                if (ToggleRect(top, 0).Contains(p)) { _session.Do(track.Muted ? "Unmute track" : "Mute track", () => track.Muted = !track.Muted); e.Handled = true; return; }
                if (ToggleRect(top, 1).Contains(p)) { _session.Do(track.Locked ? "Unlock track" : "Lock track", () => track.Locked = !track.Locked); e.Handled = true; return; }
                if (track.Kind == TrackKind.Audio && ToggleRect(top, 2).Contains(p)) { _session.Do(track.Solo ? "Unsolo track" : "Solo track", () => track.Solo = !track.Solo); e.Handled = true; return; }
            }
            return;
        }

        if (Tool == VidTool.Hand)
        {
            _drag = Drag.Pan; _panOffset = _offset; CaptureMouse(); e.Handled = true; return;
        }
        var (clip, zone, row) = HitTest(p, rows);
        if (Tool == VidTool.Razor)
        {
            if (clip != null && !rows[row].Locked)
            {
                double cut = SnapEnabled ? Snap(XToTime(p.X), new List<Clip>()) : XToTime(p.X);
                var targets = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    ? _session.Project.AllClips.Where(c => c.Start < cut - 0.02 && c.End > cut + 0.02).ToList()
                    : _session.WithLinked(new[] { clip }).ToList();
                _session.SplitClips(targets, cut);
            }
            e.Handled = true;
            return;
        }
        // the editing tools that work on an edge or on the inside of a clip
        if (Tool is VidTool.Ripple or VidTool.Rolling or VidTool.Slip or VidTool.TrackForward)
        {
            // a cut between two clips is grabbed from either side
            if (clip == null)
            {
                double tt = XToTime(p.X);
                var r2 = row >= 0 ? rows[row] : null;
                var near = r2?.Clips.FirstOrDefault(c => Math.Abs(TimeToX(c.End) - p.X) <= EdgeGrab);
                if (near != null && Tool != VidTool.Slip) { clip = near; zone = Drag.TrimEnd; }
            }
            if (clip == null) { e.Handled = true; return; }
            if (rows[row].Locked) { e.Handled = true; return; }
            if (Tool == VidTool.TrackForward)
            {
                var fwd = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    ? _session.Project.Tracks.SelectMany(t2 => _session.SelectForward(t2, clip.Start))
                    : _session.SelectForward(rows[row], clip.Start);
                _session.Select(fwd);
                e.Handled = true;
                return;
            }
            _dragClip = clip;
            _dragClips = new List<Clip> { clip };
            _editBefore = _session.BeginEdit();
            _slipDelta = 0;
            if (Tool == VidTool.Slip) { _session.Select(_session.WithLinked(new[] { clip })); _drag = Drag.SlipClip; }
            else if (Tool == VidTool.Rolling)
            {
                // the cut nearest to the press: the end of this clip when pressed in its right half, else the end of the clip before it
                bool rightHalf = p.X > (TimeToX(clip.Start) + TimeToX(clip.End)) / 2;
                var left = rightHalf ? clip : rows[row].Clips.FirstOrDefault(c => Math.Abs(c.End - clip.Start) < 0.002);
                if (left == null || _session.RightNeighbour(left) == null) { e.Handled = true; _editBefore = null; return; }
                _rollLeft = left;
                _drag = Drag.Roll;
                _ghostTime = left.End;
            }
            else
            {
                bool endSide = zone == Drag.TrimEnd || (zone == Drag.Move && p.X > (TimeToX(clip.Start) + TimeToX(clip.End)) / 2);
                _drag = endSide ? Drag.RippleEnd : Drag.RippleStart;
                _ghostTime = endSide ? clip.End : clip.Start;
            }
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (clip == null)
        {
            // empty space: a drag chooses everything inside the box; a plain click clears the choice and puts the playhead there
            _marqueeBase = new HashSet<string>(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? _session.Selected : Enumerable.Empty<string>());
            _marqueeStart = p;
            _drag = Drag.Marquee;
            CaptureMouse();
            return;
        }
        bool locked = rows[row].Locked;
        bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (add && _session.Selected.Contains(clip.Id)) _session.Select(_session.SelectedClips.Where(c => !_session.WithLinked(new[] { clip }).Contains(c)));
        else if (!_session.Selected.Contains(clip.Id) || add) _session.Select(_session.WithLinked(new[] { clip }), add);
        if (e.ClickCount == 2) ClipActivated?.Invoke(clip);            // (and the press still starts a drag: a quick click + drag is two clicks to Windows)
        if (locked) return;
        _dragClip = clip;
        _dragClips = _session.WithLinked(_session.SelectedClips).ToList();
        _editBefore = _session.BeginEdit();
        _drag = zone;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var rows = Rows();
        if (_drag == Drag.None)
        {
            var (clip, zone, _) = HitTest(p, rows);
            if (Tool == VidTool.Razor)
            {
                Cursor = Cursors.Cross;
                double rx = p.X >= HeaderWidth && p.Y > RulerHeight ? p.X : -1;
                if (rx != _razorX) { _razorX = rx; InvalidateVisual(); }
                return;
            }
            if (Tool == VidTool.Hand) { Cursor = Cursors.ScrollAll; return; }
            if (Tool is VidTool.Ripple or VidTool.Rolling) { Cursor = Cursors.SizeWE; return; }
            if (Tool == VidTool.Slip) { Cursor = clip != null ? Cursors.ScrollWE : Cursors.Arrow; return; }
            if (Tool == VidTool.TrackForward) { Cursor = Cursors.Arrow; return; }
            Cursor = p.Y < RulerHeight ? Cursors.Hand : zone is Drag.TrimStart or Drag.TrimEnd ? Cursors.SizeWE : clip != null ? Cursors.SizeAll : Cursors.Arrow;
            return;
        }
        double t = XToTime(p.X);
        switch (_drag)
        {
            case Drag.Playhead:
                PlayheadDragged?.Invoke(Math.Max(0, t), false);
                EdgeScroll(p.X);
                break;
            case Drag.Pan:
                Offset = Math.Max(0, _panOffset - (p.X - _downPoint.X) / _pps);
                break;
            case Drag.Marquee:
            {
                if (!_moved && (Math.Abs(p.X - _marqueeStart.X) > 4 || Math.Abs(p.Y - _marqueeStart.Y) > 4)) _moved = true;
                if (!_moved) break;
                double x0 = Math.Max(HeaderWidth, Math.Min(p.X, _marqueeStart.X)), x1 = Math.Max(p.X, _marqueeStart.X);
                double y0 = Math.Min(p.Y, _marqueeStart.Y), y1 = Math.Max(p.Y, _marqueeStart.Y);
                _marquee = new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
                var picked = new List<Clip>();
                for (int i = 0; i < rows.Count; i++)
                {
                    double top = RowTop(rows, i), h = RowHeight(rows[i]);
                    if (top + h < y0 || top > y1) continue;
                    foreach (var c in rows[i].Clips)
                        if (TimeToX(c.End) >= x0 && TimeToX(c.Start) <= x1) picked.Add(c);
                }
                var ids = new HashSet<string>(_marqueeBase);
                foreach (var c in _session.WithLinked(picked)) ids.Add(c.Id);
                _session.Select(_session.Project.AllClips.Where(c => ids.Contains(c.Id)));
                InvalidateVisual();
                EdgeScroll(p.X);
                break;
            }
            case Drag.RippleStart:
            case Drag.RippleEnd:
                if (_dragClip != null) { _ghostTime = Snap(t, _dragClips); InvalidateVisual(); }
                break;
            case Drag.Roll:
                _ghostTime = Math.Max(0, Snap(t, _dragClips));
                InvalidateVisual();
                break;
            case Drag.SlipClip:
                _slipDelta = -(p.X - _downPoint.X) / _pps;                      // the picture follows the mouse: its start in the file moves the other way
                _ghostTime = -1;
                InvalidateVisual();
                break;
            case Drag.Move:
            {
                if (!_moved && (Math.Abs(p.X - _downPoint.X) > 4 || Math.Abs(p.Y - _downPoint.Y) > 4)) _moved = true;
                if (!_moved) break;
                int downRow = RowAt(_downPoint.Y, rows);
                int nowRow = RowAt(p.Y, rows);
                int shift = nowRow >= 0 && downRow >= 0 ? nowRow - downRow : 0;
                bool allVideo = _dragClips.All(c => _session.Project.TrackOf(c)?.Kind == TrackKind.Video);
                bool allAudio = _dragClips.All(c => _session.Project.TrackOf(c)?.Kind == TrackKind.Audio);
                // clamp the row shift so every clip stays on a row of its own kind
                _moveRows = allVideo || allAudio ? shift : 0;
                int indexDelta = allVideo ? -_moveRows : allAudio ? _moveRows : 0;
                var snaps = _session.SnapPoints(_dragClips);
                var applied = _session.Move(_dragClips, t - _downTime, indexDelta, false, SnapEnabled ? snaps : null, 8 / _pps);
                _moveDt = applied.Time;
                _moveRows = allVideo ? -applied.Tracks : allAudio ? applied.Tracks : 0;
                InvalidateVisual();
                EdgeScroll(p.X);
                break;
            }
            case Drag.TrimStart:
            {
                if (_dragClip == null) break;
                double target = Snap(t, _dragClips);
                _session.TrimStart(new[] { _dragClip }, target, commit: false);
                _moved = true;
                InvalidateVisual();
                break;
            }
            case Drag.TrimEnd:
            {
                if (_dragClip == null) break;
                double target = Snap(t, _dragClips);
                _session.TrimEnd(new[] { _dragClip }, target, commit: false);
                _moved = true;
                InvalidateVisual();
                break;
            }
        }
    }

    private double Snap(double t, ICollection<Clip> exclude)
    {
        if (!SnapEnabled) return t;
        double best = 8 / _pps; double result = t;
        foreach (double s in _session.SnapPoints(exclude))
            if (Math.Abs(s - t) < best) { best = Math.Abs(s - t); result = s; }
        return result;
    }

    private void EdgeScroll(double x)
    {
        if (x > ActualWidth - 24) Offset = _offset + 0.5 / _pps * 40;
        else if (x < HeaderWidth + 24 && _offset > 0) Offset = Math.Max(0, _offset - 0.5 / _pps * 40);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var mode = _drag;
        _drag = Drag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        switch (mode)
        {
            case Drag.Playhead:
                PlayheadDragged?.Invoke(Math.Max(0, XToTime(p.X)), true);
                break;
            case Drag.Move:
                if (_moved)
                {
                    var rows = Rows();
                    bool allVideo = _dragClips.All(c => _session.Project.TrackOf(c)?.Kind == TrackKind.Video);
                    bool allAudio = _dragClips.All(c => _session.Project.TrackOf(c)?.Kind == TrackKind.Audio);
                    int downRow = RowAt(_downPoint.Y, rows), nowRow = RowAt(p.Y, rows);
                    int shift = nowRow >= 0 && downRow >= 0 ? nowRow - downRow : 0;
                    int indexDelta = allVideo ? -shift : allAudio ? shift : 0;
                    _session.Move(_dragClips, XToTime(p.X) - _downTime, indexDelta, true, SnapEnabled ? _session.SnapPoints(_dragClips) : null, 8 / _pps);
                }
                _moved = false;
                InvalidateVisual();
                break;
            case Drag.TrimStart:
            case Drag.TrimEnd:
                if (_editBefore != null) _session.CommitEdit(_editBefore, "Trim");
                break;
            case Drag.Marquee:
                if (!_moved)
                {
                    // a plain click on empty space
                    if (!(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) _session.ClearSelection();
                    PlayheadDragged?.Invoke(Math.Max(0, XToTime(p.X)), true);
                }
                _marquee = null;
                _moved = false;
                InvalidateVisual();
                break;
            case Drag.RippleEnd:
                if (_dragClip != null && _ghostTime >= 0) _session.RippleTrimEnd(_dragClip, _ghostTime);
                _ghostTime = -1;
                InvalidateVisual();
                break;
            case Drag.RippleStart:
                if (_dragClip != null && _ghostTime >= 0) _session.RippleTrimStart(_dragClip, _ghostTime - _dragClip.Start);
                _ghostTime = -1;
                InvalidateVisual();
                break;
            case Drag.Roll:
                if (_rollLeft != null && _ghostTime >= 0) _session.Roll(_rollLeft, _ghostTime);
                _rollLeft = null;
                _ghostTime = -1;
                InvalidateVisual();
                break;
            case Drag.SlipClip:
                if (_dragClip != null && Math.Abs(_slipDelta) > 1e-6) _session.Slip(new[] { _dragClip }, _slipDelta);
                _slipDelta = 0;
                InvalidateVisual();
                break;
        }
        _editBefore = null;
        _dragClip = null;
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        var rows = Rows();
        if (p.Y < RulerHeight) { RulerContextRequested?.Invoke(Math.Max(0, XToTime(p.X))); e.Handled = true; return; }
        if (p.X < HeaderWidth) { int hr = RowAt(p.Y, rows); if (hr >= 0) TrackMenuRequested?.Invoke(rows[hr]); e.Handled = true; return; }
        var (clip, _, _) = HitTest(p, rows);
        if (clip != null && !_session.Selected.Contains(clip.Id)) _session.Select(_session.WithLinked(new[] { clip }));
        ContextMenuRequested?.Invoke(clip, Math.Max(0, XToTime(p.X)));
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomAround(e.Delta > 0 ? 1.25 : 0.8, XToTime(p.X), p.X);
        }
        else
        {
            double step = VisibleSeconds * 0.15 * (e.Delta > 0 ? -1 : 1);
            Offset = Math.Max(0, _offset + step);
        }
        e.Handled = true;
    }

    // ---------- dropping files and media from the bin ----------
    protected override void OnDragOver(DragEventArgs e)
    {
        var p = e.GetPosition(this);
        var rows = Rows();
        bool ok = e.Data.GetDataPresent("UtylixVidMedia") || e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok)
        {
            _dropTime = Math.Max(0, Snap(XToTime(p.X), new List<Clip>()));
            _dropRow = Math.Max(0, RowAt(p.Y, rows));
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e) { _dropTime = -1; _dropRow = -1; InvalidateVisual(); }

    protected override void OnDrop(DragEventArgs e)
    {
        var p = e.GetPosition(this);
        var rows = Rows();
        double t = Math.Max(0, Snap(XToTime(p.X), new List<Clip>()));
        int row = RowAt(p.Y, rows);
        _dropTime = -1; _dropRow = -1;
        InvalidateVisual();
        if (e.Data.GetDataPresent("UtylixVidMedia") && e.Data.GetData("UtylixVidMedia") is string id)
        {
            int trackIndex = row >= 0 ? _session.Project.Tracks.IndexOf(rows[row]) : -1;
            MediaDropped?.Invoke(id, t, trackIndex);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files) FilesDropped?.Invoke(files, t);
        e.Handled = true;
    }

    /// <summary>The track under a point (for the window's menus), or null.</summary>
    public Track? TrackAt(Point p) { var rows = Rows(); int r = RowAt(p.Y, rows); return r >= 0 ? rows[r] : null; }
}
