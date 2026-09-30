using System;
using System.Windows;
using System.Windows.Media;

namespace IdmClone;

/// <summary>
/// Draws the whole file as one bar and fills in what each connection has already fetched, so you can
/// see the parallel connections working on different parts of the file.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = Register<string>(nameof(Data), "");
    public static readonly DependencyProperty TotalProperty = Register<long>(nameof(Total), 0L);
    public static readonly DependencyProperty CompleteProperty = Register<bool>(nameof(Complete), false);
    public static readonly DependencyProperty FillProperty = Register<Brush?>(nameof(Fill), null);

    private static DependencyProperty Register<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(SegmentBar),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>"start:end:pos;start:end:pos;..." - one entry per connection.</summary>
    public string Data { get => (string)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public long Total { get => (long)GetValue(TotalProperty); set => SetValue(TotalProperty, value); }
    public bool Complete { get => (bool)GetValue(CompleteProperty); set => SetValue(CompleteProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var track = (Brush)Application.Current.Resources["TrackBrush"];
        var fill = Fill ?? (Brush)Application.Current.Resources["AccentBrush"];
        var divider = (Brush)Application.Current.Resources["CardBrush"];

        var area = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(track, null, area, 4, 4);
        dc.PushClip(new RectangleGeometry(area, 4, 4));

        if (Complete)
        {
            dc.DrawRectangle(fill, null, area);
        }
        else if (Total > 0 && !string.IsNullOrEmpty(Data))
        {
            var pen = new Pen(divider, 1.5);
            foreach (var part in Data.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split(':');
                if (f.Length != 3 || !long.TryParse(f[0], out long start) || !long.TryParse(f[2], out long pos)) continue;
                double x0 = Math.Clamp(start / (double)Total, 0, 1) * w;
                double x1 = Math.Clamp(pos / (double)Total, 0, 1) * w;
                if (x1 > x0) dc.DrawRectangle(fill, null, new Rect(x0, 0, x1 - x0, h));
                if (x0 > 1) dc.DrawLine(pen, new Point(x0, 0), new Point(x0, h));   // where one connection's part begins
            }
        }
        dc.Pop();
    }
}
