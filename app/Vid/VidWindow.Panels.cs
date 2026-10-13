using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IdmClone.Vid;

/// <summary>
/// The small panels that sit beside Project and Effects, like Premiere's: Info (what is chosen), History (every step, click one to go back to it)
/// and Markers (the flags on the ruler, click one to go there).
/// </summary>
public sealed partial class VidWindow
{
    private StackPanel _infoRows = null!, _historyRows = null!, _markerRows = null!;
    private ScrollViewer _historyScroll = null!;

    private ScrollViewer PanelScroller(out StackPanel rows)
    {
        rows = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        return new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0) };
    }

    private UIElement BuildInfoPanel()
    {
        var sv = PanelScroller(out _infoRows);
        sv.IsVisibleChanged += (_, _) => { if (sv.IsVisible) RefreshInfo(); };
        _s.Changed += () => { if (sv.IsVisible) RefreshInfo(); };
        _s.SelectionChanged += () => { if (sv.IsVisible) RefreshInfo(); };
        return sv;
    }

    private UIElement BuildHistoryPanel()
    {
        _historyScroll = PanelScroller(out _historyRows);
        _historyScroll.IsVisibleChanged += (_, _) => { if (_historyScroll.IsVisible) RefreshHistory(); };
        _s.Changed += () => { if (_historyScroll.IsVisible) RefreshHistory(); };
        return _historyScroll;
    }

    private UIElement BuildMarkersPanel()
    {
        var sv = PanelScroller(out _markerRows);
        sv.IsVisibleChanged += (_, _) => { if (sv.IsVisible) RefreshMarkers(); };
        _s.Changed += () => { if (sv.IsVisible) RefreshMarkers(); };
        return sv;
    }

    private TextBlock PanelText(string text, bool muted = false, bool bold = false, double size = 12)
    {
        var tb = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        tb.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
        return tb;
    }

    private void InfoLine(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(PanelText(label, muted: true));
        var v = PanelText(value);
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        _infoRows.Children.Add(g);
    }

    private string TimeCode(double t) => VidTimeline.Timecode(t, Fps);

    private void RefreshInfo()
    {
        _infoRows.Children.Clear();
        var p = _s.Project;
        var sel = _s.SelectedClips.ToList();
        if (sel.Count == 1)
        {
            var c = sel[0];
            var track = p.TrackOf(c);
            var m = p.MediaOf(c);
            _infoRows.Children.Add(PanelText(c.Kind == ClipKind.Text ? "Title" : (c.Label ?? m?.Name ?? "Clip"), bold: true, size: 13));
            _infoRows.Children.Add(new Border { Height = 8 });
            InfoLine("Track", track?.Name ?? "");
            InfoLine("Starts", TimeCode(c.Start));
            InfoLine("Ends", TimeCode(c.End));
            InfoLine("Length", TimeCode(c.Length));
            if (c.Kind == ClipKind.Media)
            {
                InfoLine("Speed", (c.Speed * 100).ToString("0.#") + " %");
                InfoLine("From the file", TimeCode(c.In) + "  to  " + TimeCode(c.In + c.Length * c.Speed));
                if (m != null)
                {
                    if (m.HasVideo) InfoLine("Picture", $"{m.Width} × {m.Height}" + (m.Fps > 0 ? $", {m.Fps:0.##} fps" : ""));
                    InfoLine("Contains", m.Kind == MediaKind.Image ? "a picture" : m.HasVideo && m.HasAudio ? "picture and sound" : m.HasVideo ? "picture only" : "sound only");
                    InfoLine("File", m.Path);
                }
            }
            else InfoLine("Words", (c.Text ?? "").Replace("\n", "  /  "));
            if (c.Effects.Count > 0) InfoLine("Effects", string.Join(", ", c.Effects.Select(e => VidEffects.Info(e.Type)?.Name ?? e.Type)));
            if (c.Transition != TransitionKind.None) InfoLine("Transition", VidEffects.Transitions.FirstOrDefault(t => t.Kind == c.Transition).Name + $" ({c.TransitionLength:0.##} s)");
            return;
        }
        if (sel.Count > 1)
        {
            _infoRows.Children.Add(PanelText($"{sel.Count} clips chosen", bold: true, size: 13));
            _infoRows.Children.Add(new Border { Height = 8 });
            InfoLine("From", TimeCode(sel.Min(c => c.Start)));
            InfoLine("To", TimeCode(sel.Max(c => c.End)));
            InfoLine("Together", TimeCode(sel.Sum(c => c.Length)));
            return;
        }
        _infoRows.Children.Add(PanelText("Sequence", bold: true, size: 13));
        _infoRows.Children.Add(new Border { Height = 8 });
        InfoLine("Picture", $"{p.Width} × {p.Height}, {Fps:0.##} fps");
        InfoLine("Length", TimeCode(p.Duration));
        InfoLine("Clips", p.AllClips.Count().ToString());
        InfoLine("Tracks", $"{p.Tracks.Count(t => t.Kind == TrackKind.Video)} video, {p.Tracks.Count(t => t.Kind == TrackKind.Audio)} sound");
        InfoLine("Markers", p.Markers.Count.ToString());
        InfoLine("Playhead", TimeCode(_s.Playhead));
        var hint = PanelText("Choose a clip on the timeline to see its details here.", muted: true);
        hint.Margin = new Thickness(0, 10, 0, 0);
        _infoRows.Children.Add(hint);
    }

    // ---------- history ----------
    private Border HistoryRow(string text, bool current, bool future, Action? click)
    {
        var tb = PanelText(text, muted: future, bold: current);
        var b = new Border { Child = tb, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(3), Cursor = click != null ? Cursors.Hand : Cursors.Arrow, Background = Brushes.Transparent };
        if (current) b.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        if (click != null)
        {
            b.MouseEnter += (_, _) => { if (!current) b.SetResourceReference(Border.BackgroundProperty, "TrackBrush"); };
            b.MouseLeave += (_, _) => { if (!current) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (_, _) => click();
        }
        return b;
    }

    private void RefreshHistory()
    {
        _historyRows.Children.Clear();
        var labels = _s.UndoLabels;
        int redo = _s.RedoCount;
        _historyRows.Children.Add(HistoryRow("Start of this session", labels.Count == 0, false, labels.Count > 0 ? () => _s.UndoSteps(labels.Count) : null));
        for (int i = 0; i < labels.Count; i++)
        {
            int back = labels.Count - 1 - i;                     // steps to undo to be standing on this one
            _historyRows.Children.Add(HistoryRow(labels[i], i == labels.Count - 1, false, i == labels.Count - 1 ? null : () => _s.UndoSteps(back)));
        }
        if (redo > 0)
        {
            var more = PanelText(redo == 1 ? "1 step can be redone (Ctrl+Shift+Z)" : $"{redo} steps can be redone (Ctrl+Shift+Z)", muted: true, size: 11);
            more.Margin = new Thickness(8, 8, 0, 0);
            _historyRows.Children.Add(more);
        }
        _historyScroll.ScrollToBottom();
    }

    // ---------- markers ----------
    private void RefreshMarkers()
    {
        _markerRows.Children.Clear();
        var list = _s.Project.Markers.OrderBy(m => m.Time).ToList();
        if (list.Count == 0)
        {
            _markerRows.Children.Add(PanelText("No markers yet. Press M to put one at the playhead, or double-click the ruler.", muted: true));
            return;
        }
        foreach (var m in list)
        {
            var marker = m;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Color col;
            try { col = (Color)ColorConverter.ConvertFromString(marker.Color); } catch (FormatException) { col = Colors.LimeGreen; }
            var flag = new System.Windows.Shapes.Rectangle { Width = 10, Height = 14, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(col), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var time = PanelText(TimeCode(marker.Time));
            time.FontFamily = new FontFamily("Consolas");
            time.VerticalAlignment = VerticalAlignment.Center;
            var name = PanelText(string.IsNullOrWhiteSpace(marker.Name) ? "(no name)" : marker.Name, muted: string.IsNullOrWhiteSpace(marker.Name));
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(time, 1); Grid.SetColumn(name, 2);
            g.Children.Add(flag); g.Children.Add(time); g.Children.Add(name);
            g.Margin = new Thickness(0, 0, 0, 2);
            g.MouseLeftButtonUp += (_, ev) =>
            {
                if (ev.ClickCount >= 2) { RenameMarkerPrompt(marker); return; }
                SeekTo(marker.Time); _timeline.EnsureVisible(marker.Time);
            };
            g.ContextMenu = new ContextMenu { Style = (Style)FindResource("ThemedMenu") };
            g.ContextMenu.Items.Add(Item("Rename…", () => RenameMarkerPrompt(marker)));
            g.ContextMenu.Items.Add(Item("Delete", () => _s.RemoveMarker(marker)));
            _markerRows.Children.Add(g);
        }
    }
}
