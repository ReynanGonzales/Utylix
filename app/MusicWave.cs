using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IdmClone;

/// <summary>The waveform card's seek bar: bars as tall as the song is loud at that moment; click or drag to jump.</summary>
internal sealed class WaveSeek : FrameworkElement
{
    private float[]? _peaks;
    private double _progress;
    private bool _dragging;
    private double _dragProgress;

    public Brush Played { get; set; } = Brushes.Black;
    public Brush Rest { get; set; } = Brushes.LightGray;

    /// <summary>Raised with 0..1 when the person lets go after clicking or dragging.</summary>
    public event Action<double>? SeekTo;

    public float[]? Peaks { get => _peaks; set { _peaks = value; InvalidateVisual(); } }

    public double Progress
    {
        get => _progress;
        set { if (_dragging) return; if (Math.Abs(_progress - value) < 0.0005) return; _progress = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));            // (so clicks between the bars still count)
        double shown = _dragging ? _dragProgress : _progress;
        int n = _peaks?.Length ?? 70;
        double slot = w / n, bar = Math.Max(1.6, slot * 0.58);
        for (int i = 0; i < n; i++)
        {
            double level = _peaks != null ? Math.Clamp(_peaks[i], 0.04, 1) : 0.08 + 0.05 * Math.Sin(i * 0.7);        // (flat little bars until the real ones are ready)
            double bh = Math.Max(3, level * (h - 2));
            double x = i * slot + (slot - bar) / 2;
            var brush = (i + 0.5) / n <= shown ? Played : Rest;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, (h - bh) / 2, bar, bh), bar / 2, bar / 2);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true; CaptureMouse();
        _dragProgress = Fraction(e);
        InvalidateVisual();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragProgress = Fraction(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false; ReleaseMouseCapture();
        _progress = Fraction(e);
        InvalidateVisual();
        SeekTo?.Invoke(_progress);
        base.OnMouseLeftButtonUp(e);
    }

    private double Fraction(MouseEventArgs e) => ActualWidth <= 0 ? 0 : Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1);
}

/// <summary>Reads a song once to find how loud it is along its length (about 150 numbers from 0 to 1). Null for formats Windows can't decode.</summary>
internal static class WavePeaks
{
    public static float[]? Compute(string path, int bars)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var reader = new NAudio.Wave.MediaFoundationReader(path);
            var samples = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
            int channels = Math.Max(1, samples.WaveFormat.Channels);
            long frames = reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign);
            if (frames <= 0 || reader.TotalTime > TimeSpan.FromMinutes(90)) return null;
            long perBar = Math.Max(1, frames / bars);
            var sum = new double[bars]; var count = new long[bars];
            var buffer = new float[4096 * channels];
            long frame = 0;
            int read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i + channels <= read; i += channels)
                {
                    double level = 0;
                    for (int c = 0; c < channels; c++) level += Math.Abs(buffer[i + c]);
                    int bar = (int)Math.Min(bars - 1, frame / perBar);
                    sum[bar] += level / channels; count[bar]++;
                    frame++;
                }
            }
            var peaks = new float[bars];
            double max = 0;
            for (int i = 0; i < bars; i++) { peaks[i] = count[i] > 0 ? (float)(sum[i] / count[i]) : 0; max = Math.Max(max, peaks[i]); }
            if (max <= 0.0001) return null;
            for (int i = 0; i < bars; i++) peaks[i] = (float)Math.Pow(peaks[i] / max, 0.8);          // (a little lift, so quiet parts still show)
            return peaks;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
