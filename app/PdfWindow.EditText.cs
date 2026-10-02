using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Edit text": change the words already in the PDF, a line (or a piece of one) at a time. On screen the old words are covered with the
/// page's own colour and the new ones shown in the same font, size and colour; saving changes the text in the PDF itself.
/// </summary>
public sealed partial class PdfWindow
{
    private sealed class RunEditItem : EditItem
    {
        public PdfTextRun Run = null!;
        public string NewText = "";
        public Color Cover = Colors.White;

        public static FontFamily Family(PdfTextRun run)
        {
            if (Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, run.Family, StringComparison.OrdinalIgnoreCase))) return new FontFamily(run.Family);
            string l = run.Family.ToLowerInvariant();
            if (l.Contains("courier") || l.Contains("mono") || l.Contains("consol")) return new FontFamily("Courier New");
            if ((l.Contains("times") || l.Contains("serif") || l.Contains("roman") || l.Contains("georgia") || l.Contains("garamond") || l.Contains("cambria")) && !l.Contains("sans")) return new FontFamily("Times New Roman");
            return new FontFamily("Arial");
        }

        public double Top => Run.Baseline.Y - Family(Run).Baseline * Run.Size;
        public override Rect Bounds => Run.Box;
        public override bool Hit(Point p) => Inflate(Run.Box, 1).Contains(p);
        public override EditItem Clone() => (RunEditItem)MemberwiseClone();
        public override FrameworkElement Build()
        {
            var canvas = new Canvas();
            var cover = new Rectangle { Width = Run.Box.Width + 1.2, Height = Run.Box.Height + 1.2, Fill = new SolidColorBrush(Cover) };
            Canvas.SetLeft(cover, Run.Box.X - 0.6); Canvas.SetTop(cover, Run.Box.Y - 0.6);
            canvas.Children.Add(cover);
            var text = new TextBlock
            {
                Text = NewText, FontFamily = Family(Run), FontSize = Run.Size, Foreground = new SolidColorBrush(Run.Color.A == 0 ? Colors.Black : Run.Color),
                FontWeight = Run.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = Run.Italic ? FontStyles.Italic : FontStyles.Normal,
            };
            Canvas.SetLeft(text, Run.Baseline.X); Canvas.SetTop(text, Top);
            canvas.Children.Add(text);
            return canvas;
        }
        public override IEnumerable<PdfMark> Marks() { if (NewText != Run.Text) yield return new PdfReplaceTextMark(Page, Run.Index, Run.Text, NewText); }
        public override void MoveBy(Vector d) { }             // (it stays where the text is)
        public override void ResizeTo(Rect r) { }
    }

    private TextBox? _runBox;
    private RunEditItem? _runTyping;
    private (int Page, Rect Box)? _hoverRun;

    private List<PdfTextRun> Runs(int page)
    {
        try { return _pdf?.GetTextRuns(page) ?? new List<PdfTextRun>(); }
        catch (Exception e) when (e is ObjectDisposedException or System.IO.IOException) { return new List<PdfTextRun>(); }
    }

    /// <summary>Clicked with "Edit text": the line under the pointer opens for typing.</summary>
    private readonly TextBlock _toolHint = new() { Visibility = Visibility.Collapsed };

    private void EditTextAt(PageView pv, Point p, bool quiet = false)
    {
        var existing = _items.OfType<RunEditItem>().LastOrDefault(i => i.Page == pv.Index && i.Hit(p));
        if (existing != null) { EditRun(existing, isNew: false); return; }
        var run = Runs(pv.Index).LastOrDefault(r => { var b = r.Box; b.Inflate(1, 1); return b.Contains(p); });
        if (run == null)
        {
            if (quiet) return;
            Toast(Text(pv.Index)?.Text.Length > 0 ? "Click on a line of text to change it (turned text can't be changed)" : "This page has no text to change: it's probably a scanned picture (use Text and White-out instead)");
            return;
        }
        EditRun(new RunEditItem { Page = pv.Index, Run = run, NewText = run.Text, Cover = CoverColour(pv, run.Box) }, isNew: true);
    }

    /// <summary>The colour of the page just around a box (usually white; a coloured table cell stays coloured).</summary>
    private static Color CoverColour(PageView pv, Rect box)
    {
        if (pv.Picture.Source is not BitmapSource bmp || pv.Overlay.Width <= 0) return Colors.White;
        double kx = bmp.PixelWidth / pv.Overlay.Width, ky = bmp.PixelHeight / pv.Overlay.Height;
        var samples = new List<Color>();
        var px = new byte[4];
        foreach (var (x, y) in new[] { (box.Left - 1.5, box.Top + box.Height / 2), (box.Right + 1.5, box.Top + box.Height / 2), (box.Left + box.Width / 2, box.Top - 1.2), (box.Left + box.Width / 2, box.Bottom + 1.2),
                                       (box.Left - 1.5, box.Top), (box.Right + 1.5, box.Bottom), (box.Left + box.Width / 4, box.Top - 1.2), (box.Right - box.Width / 4, box.Bottom + 1.2) })
        {
            int ix = (int)(x * kx), iy = (int)(y * ky);
            if (ix < 0 || iy < 0 || ix >= bmp.PixelWidth || iy >= bmp.PixelHeight) continue;
            try { bmp.CopyPixels(new Int32Rect(ix, iy, 1, 1), px, 4, 0); } catch (ArgumentException) { continue; }
            samples.Add(Color.FromRgb(px[2], px[1], px[0]));
        }
        if (samples.Count == 0) return Colors.White;
        // the most common colour (text that touches the edge doesn't win)
        return samples.GroupBy(c => (c.R / 12, c.G / 12, c.B / 12)).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(c => c.R + c.G + c.B)).First().First();
    }

    private void EditRun(RunEditItem item, bool isNew)
    {
        CloseTextBox(commit: true);
        Snapshot();
        if (isNew) _items.Add(item);
        _runTyping = item;
        _selected = null;
        var run = item.Run;
        var box = new TextBox
        {
            Text = item.NewText, AcceptsReturn = false, Padding = new Thickness(0), BorderThickness = new Thickness(0), MinWidth = run.Box.Width + 4,
            FontFamily = RunEditItem.Family(run), FontSize = run.Size, FontWeight = run.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = run.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(run.Color.A == 0 ? Colors.Black : run.Color), Background = new SolidColorBrush(item.Cover),
            CaretBrush = new SolidColorBrush(Colors.Black),
            Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'>" +
                "<Border Background='{TemplateBinding Background}' BorderBrush='#CC2F6BEA' BorderThickness='0.8'><ScrollViewer x:Name='PART_ContentHost' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='0' Padding='0' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden' /></Border></ControlTemplate>"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "PdfRunBox");
        Canvas.SetLeft(box, run.Baseline.X - 2); Canvas.SetTop(box, item.Top);
        box.TextChanged += (_, _) => { if (_runTyping != null) { _runTyping.NewText = box.Text; _dirty = true; UpdateTitle(); } };
        box.LostKeyboardFocus += (_, ev) =>
        {
            if (ev.NewFocus is DependencyObject nf && IsInside(nf, _editBar)) return;
            Dispatcher.BeginInvoke(() => { if (_runBox == box && !box.IsKeyboardFocusWithin) CloseRunBox(); });
        };
        _runBox = box;
        RenderItems(item.Page);
        UpdateEditButtons();
        Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Finishes typing a changed line; when nothing changed, it is as if it was never opened.</summary>
    private void CloseRunBox()
    {
        if (_runBox == null || _runTyping == null) { _runBox = null; _runTyping = null; return; }
        var item = _runTyping;
        item.NewText = _runBox.Text;
        _runBox = null; _runTyping = null;
        if (item.NewText == item.Run.Text)
        {
            // unchanged: undo the opening (and drop the item)
            if (_undo.Count > 0) { var before = _undo.Pop(); _items.Clear(); _items.AddRange(before); }
            _dirty = _undo.Count > 0;
            UpdateTitle();
        }
        RenderItems(item.Page);
        UpdateEditButtons(); UpdateProperties();
        _scroll.Focus();
    }

    /// <summary>With "Edit text", the line under the pointer gets a frame (and the I-beam).</summary>
    private void HoverRun(PageView pv, Point p)
    {
        var run = Runs(pv.Index).LastOrDefault(r => { var b = r.Box; b.Inflate(1, 1); return b.Contains(p); });
        (int, Rect)? now = run == null ? null : (pv.Index, run.Box);
        pv.Overlay.Cursor = run != null ? Cursors.IBeam : Cursors.Arrow;
        if (now == _hoverRun) return;
        int? old = _hoverRun?.Page;
        _hoverRun = now;
        if (old is int o && o != pv.Index) RenderItems(o);
        RenderItems(pv.Index);
    }

    /// <summary>Drawn last by RenderItems: the frame of the line under the pointer, and the line being typed.</summary>
    private void RenderRunExtras(int page, Canvas overlay)
    {
        if (_fieldEditor != null && _fieldEditing?.Page == page && _fieldEditor.Parent == null) overlay.Children.Add(_fieldEditor);
        if (_runBox != null && _runTyping?.Page == page) overlay.Children.Add(_runBox);
        if (_editing && _tool == EditTool.EditText && _runBox == null && _hoverRun is { } h && h.Page == page)
        {
            double k = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX);
            var frame = new Rectangle { Width = h.Box.Width + 4 * k, Height = h.Box.Height + 4 * k, Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA)), StrokeThickness = 1 * k, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false };
            Canvas.SetLeft(frame, h.Box.X - 2 * k); Canvas.SetTop(frame, h.Box.Y - 2 * k);
            overlay.Children.Add(frame);
        }
    }
}
