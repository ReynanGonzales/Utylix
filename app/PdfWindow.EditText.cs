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
        /// <summary>Where the words must stop (page points, as shown): the right edge of the block the line is in, or of the text on the page. 0 = no limit. Words that would pass it go to the next line by themselves.</summary>
        public double WrapRight;
        /// <summary>The frame was dragged: the words flow to it even when they were not changed.</summary>
        public bool WrapSet;
        private string _wrapKey = "", _wrapped = "";

        /// <summary>The words as they are written: <see cref="NewText"/>, with a line break where a word would pass <see cref="WrapRight"/>.</summary>
        public string Shown
        {
            get
            {
                if (WrapRight <= 0 || Run.Turn != 0 || (NewText == Run.Text && !WrapSet)) return NewText;
                string key = $"{NewText}|{WrapRight:0.##}|{Offset.X:0.##}|{EffSize:0.##}|{EffBold}|{FontOverride}|{Run.Italic}";
                if (key == _wrapKey) return _wrapped;
                double room = WrapRight - (Run.Baseline.X + Offset.X);
                string result = NewText;
                if (room >= EffSize * 3)
                {
                    var typeface = new Typeface(EffFamily, Run.Italic && FontOverride == null ? FontStyles.Italic : FontStyles.Normal, EffBold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                    double Width(string s) => new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, EffSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
                    var lines = new List<string>();
                    foreach (var paragraph in NewText.Replace("\r\n", "\n").Split((char)10))
                    {
                        string line = "";
                        foreach (var word in paragraph.Split((char)32))
                        {
                            string trial = line.Length == 0 ? word : line + " " + word;
                            if (line.Length > 0 && Width(trial) > room) { lines.Add(line); line = word; } else line = trial;
                        }
                        lines.Add(line);
                    }
                    result = string.Join("\n", lines);
                }
                _wrapKey = key; _wrapped = result;
                return result;
            }
        }
        public Color Cover = Colors.White;
        /// <summary>The page as it looks without the old words, under where they were (a watermark or a coloured cell stays); null = the solid <see cref="Cover"/> colour is used.</summary>
        public System.Windows.Media.Imaging.BitmapSource? CoverPicture;
        /// <summary>Above 0: the line was a justified one (it reached the right edge of its paragraph): a changed text keeps both ends, the words get wider spaces. The width from where the line starts (points).</summary>
        public double JustifyTo;
        public Rect CoverArea;
        /// <summary>The colour the person chose for behind the words (null = the page's own, as it is; the old words are still hidden with <see cref="Cover"/> on screen).</summary>
        public Color? BackColor;
        public bool HasBack => BackColor != null;

        public static FontFamily Family(PdfTextRun run)
        {
            if (Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, run.Family, StringComparison.OrdinalIgnoreCase))) return new FontFamily(run.Family);
            string l = run.Family.ToLowerInvariant();
            if (l.Contains("courier") || l.Contains("mono") || l.Contains("consol")) return new FontFamily("Courier New");
            if ((l.Contains("times") || l.Contains("serif") || l.Contains("roman") || l.Contains("georgia") || l.Contains("garamond") || l.Contains("cambria")) && !l.Contains("sans")) return new FontFamily("Times New Roman");
            return new FontFamily("Arial");
        }

        // what the person changed about the look of the line (null = as the PDF has it)
        public string? FontOverride;
        public bool? BoldOverride;
        public double? SizeOverride;
        public Color? ColorOverride;
        public bool Styled => FontOverride != null || BoldOverride != null || SizeOverride != null || ColorOverride != null;
        public FontFamily EffFamily => FontOverride != null ? new FontFamily(FontOverride) : Family(Run);
        public bool EffBold => BoldOverride ?? Run.Bold;
        public double EffSize => SizeOverride ?? Run.Size;
        public Color EffColor => ColorOverride ?? (Run.Color.A == 0 ? Colors.Black : Run.Color);

        public Vector Offset;                                   // how far the person moved the line (points, as shown)
        /// <summary>One line down, as the font spaces its lines (points), or as the paragraph this line was part of spaced them.</summary>
        public double? AdvanceOverride;
        /// <summary>How the person set the line spacing of a line with several lines: a multiple of the font's own spacing (null = the font's own, or the paragraph's).</summary>
        public double? Spacing;
        public double LineAdvance => Spacing is double s ? EffFamily.LineSpacing * EffSize * s : AdvanceOverride ?? EffFamily.LineSpacing * EffSize;
        private int LineCount => Math.Max(1, Shown.Replace("\r\n", "\n").Split('\n').Length);

        public double Top => Run.Baseline.Y - EffFamily.Baseline * EffSize;
        /// <summary>How far the line is turned on the page as shown (degrees clockwise): 0, 90 (reads downward), 180 or 270 (reads upward).</summary>
        public double TurnDegrees => Run.Turn * 90;
        /// <summary>Where the line is now (moved, and taller when it has more lines).</summary>
        // (the frame of the line as it is now: as wide as the person made it when the frame was dragged, and as tall as its lines)
        private Rect Moved => new(Run.Box.X + Offset.X, Run.Box.Y + Offset.Y, WrapSet && WrapRight > 0 && Run.Turn == 0 ? Math.Max(10, WrapRight - Run.Box.X - Offset.X + 2) : Math.Max(Run.Box.Width, 4), Run.Box.Height + (LineCount - 1) * LineAdvance);
        public override Rect Bounds => Moved;
        public override bool Hit(Point p) => Inflate(Moved, 1).Contains(p);
        public override EditItem Clone() => (RunEditItem)MemberwiseClone();

        /// <summary>The area the chosen background covers: where the line is now, and as wide as the new words are.</summary>
        public Rect BackBox
        {
            get
            {
                var box = Moved;
                var lines = Shown.Replace("\r\n", "\n").Split('\n');
                double widest = 0;
                var typeface = new Typeface(EffFamily, Run.Italic && FontOverride == null ? FontStyles.Italic : FontStyles.Normal, EffBold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                foreach (var line in lines)
                    if (line.Length > 0) widest = Math.Max(widest, new FormattedText(line, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, EffSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace);
                var text = new Rect(Run.Baseline.X + Offset.X, Top + Offset.Y, widest, EffFamily.LineSpacing * EffSize + (lines.Length - 1) * LineAdvance);
                box.Union(text);
                box.Inflate(0.6, 0.6);
                return box;
            }
        }

        private bool JustifyNow => JustifyTo > 0 && !Styled && LineCount == 1 && NewText != Run.Text && NewText.Trim().Length > 0;

        /// <summary>The words of a justified line with their places (x in points as shown), or null when it can not be spread (one word, longer than the room, or far too short).</summary>
        private List<(string Word, double X)>? JustifiedWords()
        {
            if (!JustifyNow) return null;
            var words = NewText.Split(new[] { (char)32 }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return null;
            var measure = MeasureFor(Run);
            double x0 = Run.Baseline.X + Offset.X, natural = measure(string.Join(" ", words)), space = measure(" ");
            double extra = (JustifyTo - natural) / (words.Length - 1);
            if (extra < 0.01 || extra > 6 * space) return null;
            var list = new List<(string, double)>();
            for (int i = 0; i < words.Length; i++) list.Add((words[i], x0 + (i == 0 ? 0 : measure(string.Join(" ", words.Take(i)) + " ")) + i * extra));
            return list;
        }

        public override FrameworkElement Build()
        {
            var canvas = new Canvas();
            if (CoverPicture != null)
            {
                var clean = new Image { Source = CoverPicture, Width = CoverArea.Width, Height = CoverArea.Height, Stretch = Stretch.Fill };      // (hides the original where it was: the page itself without the old words)
                RenderOptions.SetBitmapScalingMode(clean, BitmapScalingMode.HighQuality);
                Canvas.SetLeft(clean, CoverArea.X); Canvas.SetTop(clean, CoverArea.Y);
                canvas.Children.Add(clean);
            }
            else
            {
                var cover = new Rectangle { Width = Run.Box.Width + 1.2, Height = Run.Box.Height + 1.2, Fill = new SolidColorBrush(Cover) };      // (hides the original where it was)
                Canvas.SetLeft(cover, Run.Box.X - 0.6); Canvas.SetTop(cover, Run.Box.Y - 0.6);
                canvas.Children.Add(cover);
            }
            if (BackColor is Color back)
            {
                var b = BackBox;
                var fill = new Rectangle { Width = b.Width, Height = b.Height, Fill = new SolidColorBrush(back) };
                Canvas.SetLeft(fill, b.X); Canvas.SetTop(fill, b.Y);
                canvas.Children.Add(fill);
            }
            if (JustifiedWords() is { } spread)
            {
                foreach (var (word, x) in spread)
                {
                    var piece = new TextBlock { Text = word, FontFamily = EffFamily, FontSize = EffSize, Foreground = new SolidColorBrush(EffColor), FontWeight = EffBold ? FontWeights.Bold : FontWeights.Normal, FontStyle = Run.Italic ? FontStyles.Italic : FontStyles.Normal };
                    Canvas.SetLeft(piece, x); Canvas.SetTop(piece, Top + Offset.Y);
                    canvas.Children.Add(piece);
                }
                return canvas;
            }
            var text = new TextBlock
            {
                Text = Shown, FontFamily = EffFamily, FontSize = EffSize, Foreground = new SolidColorBrush(EffColor),
                FontWeight = EffBold ? FontWeights.Bold : FontWeights.Normal, FontStyle = Run.Italic && FontOverride == null ? FontStyles.Italic : FontStyles.Normal,
            };
            double lift = 0;
            if ((AdvanceOverride != null || Spacing != null) && LineCount > 1)             // (lines spaced like the paragraph they came from, or as the person chose; the first line stays on its baseline)
            {
                double advance = LineAdvance;
                text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; text.LineHeight = advance;
                lift = (advance - EffFamily.LineSpacing * EffSize) / 2;
            }
            Canvas.SetLeft(text, Run.Baseline.X + Offset.X); Canvas.SetTop(text, Top + Offset.Y - lift);
            if (Run.Turn != 0) text.RenderTransform = new RotateTransform(TurnDegrees, 0, EffFamily.Baseline * EffSize + lift);          // (a line that is turned on the page: written along the same direction)
            canvas.Children.Add(text);
            return canvas;
        }
        public override IEnumerable<PdfMark> Marks()
        {
            if (Shown == Run.Text && Offset.X == 0 && Offset.Y == 0 && !Styled && !HasBack) yield break;
            Color? back = BackColor; Rect? backBox = back != null ? BackBox : null;
            if (JustifiedWords() is { } spread)
            {
                // a justified line: the old pieces go, every word is written again at its own place (the same font, size and colour)
                var pieces = Run.Parts ?? new List<PdfTextRun> { Run };
                for (int i = 0; i < pieces.Count; i++)
                    yield return i == 0 ? new PdfReplaceTextMark(Page, pieces[i].Index, pieces[i].Text, "", Backdrop: back, BackdropBox: backBox) : new PdfReplaceTextMark(Page, pieces[i].Index, pieces[i].Text, "");
                var donor = pieces[0];
                foreach (var (word, x) in spread) yield return new PdfPlaceTextMark(Page, donor.Index, donor.Text, word, x, Run.Baseline.Y + Offset.Y);
                yield break;
            }
            if (Run.Parts == null) { yield return new PdfReplaceTextMark(Page, Run.Index, Run.Text, Shown, Offset.X, Offset.Y, LineAdvance, FontOverride, BoldOverride, SizeOverride, ColorOverride, back, backBox); yield break; }
            // a line the PDF keeps in bits: unchanged words, every bit moves; changed words (or a changed look), the first bit takes them all and the others go
            bool same = Shown == Run.Text && !Styled;
            for (int i = 0; i < Run.Parts.Count; i++)
            {
                var part = Run.Parts[i];
                if (same) yield return new PdfReplaceTextMark(Page, part.Index, part.Text, part.Text, Offset.X, Offset.Y, LineAdvance, Backdrop: i == 0 ? back : null, BackdropBox: i == 0 ? backBox : null);
                else if (i == 0) yield return new PdfReplaceTextMark(Page, part.Index, part.Text, Shown, Offset.X, Offset.Y, LineAdvance, FontOverride, BoldOverride, SizeOverride, ColorOverride, back, backBox);
                else yield return new PdfReplaceTextMark(Page, part.Index, part.Text, "");
            }
        }
        public override void MoveBy(Vector d) => Offset += d;
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

    /// <summary>The piece of text under the point that is still where the PDF has it (one already changed or moved has an item of its own: its old place is empty).</summary>
    private PdfTextRun? RunAt(int page, Point p)
    {
        var edited = EditedRunIndices(page);                       // (also what a paragraph edit took out)
        return Runs(page).LastOrDefault(r => { var b = r.Box; b.Inflate(1, 1); return !edited.Contains(r.Index) && b.Contains(p); });
    }

    /// <summary>
    /// For "Edit text" and "Paragraph": the line under the pointer, or - when the click missed the letters by a little (between two lines, just above or below a line, just before its first
    /// letter or after its last) - the nearest line, as long as it is close. The room grows when the page is zoomed out, so a click is never asked to be exact to a pixel or two.
    /// </summary>
    private PdfTextRun? RunNear(int page, Point p)
    {
        var exact = RunAt(page, p);
        if (exact != null) return exact;
        double perPixel = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX);               // points on the page for one pixel on the screen
        var edited = EditedRunIndices(page);
        PdfTextRun? best = null; double bestScore = double.MaxValue;
        foreach (var r in Runs(page))
        {
            if (edited.Contains(r.Index)) continue;
            var b = r.Box;
            double tolY = Math.Max(Math.Max(b.Height * 0.6, 9 * perPixel), 8), tolX = Math.Max(b.Height * 2.0, 22 * perPixel);
            double dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;
            double dy = p.Y < b.Top ? b.Top - p.Y : p.Y > b.Bottom ? p.Y - b.Bottom : 0;
            if (dx > tolX || dy > tolY) continue;
            double score = (dx / tolX) * (dx / tolX) + (dy / tolY) * (dy / tolY);
            if (score < bestScore) { bestScore = score; best = r; }
        }
        return best;
    }

    /// <summary>Where the words of this line must stop: the right edge of the paragraph (or centred title) it belongs to, else of the text on the page (about its right margin). 0 for turned text.</summary>
    private double WrapRightFor(int page, PdfTextRun run)
    {
        if (run.Turn != 0) return 0;
        double right;
        var para = FindParagraph(page, run);
        if (para != null) right = para.Right;
        else
        {
            var rights = Runs(page).Where(r => r.Turn == 0 && r.Box.Width > 20).Select(r => r.Box.Right).OrderBy(x => x).ToList();
            right = rights.Count == 0 ? 0 : rights[Math.Min(rights.Count - 1, (int)(rights.Count * 0.95))];
        }
        return Math.Max(right, run.Box.Right + 1);
    }

    private void EditTextAt(PageView pv, Point p, bool quiet = false)
    {
        var existing = _items.OfType<RunEditItem>().LastOrDefault(i => i.Page == pv.Index && i.Hit(p));
        if (existing != null) { EditRun(existing, isNew: false); return; }
        var laidOut = _items.OfType<ParagraphItem>().LastOrDefault(i => i.Page == pv.Index && i.Hit(p));
        if (laidOut != null) { ReopenParagraph(pv, laidOut); return; }               // (a paragraph that was laid out again: change it again)
        var run = quiet ? RunAt(pv.Index, p) : RunNear(pv.Index, p);
        if (run == null)
        {
            if (quiet) return;
            Toast(Text(pv.Index)?.Text.Length > 0 ? "Click on a line of text to change it (turned text can't be changed)" : "This page has no text to change: it's probably a scanned picture (use Text and White-out instead)");
            return;
        }
        var fresh = new RunEditItem { Page = pv.Index, Run = run, NewText = run.Text, Cover = CoverColour(pv, run.Box), WrapRight = WrapRightFor(pv.Index, run) };
        AttachRunCovers(pv.Index, new[] { fresh });
        EditRun(fresh, isNew: true);
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
        ClearTextSelection();                                          // (a selected word would be drawn over the line being changed)
        Snapshot();
        if (isNew) _items.Add(item);
        _runTyping = item;
        _selected = null;
        var run = item.Run;
        var box = new TextBox
        {
            Text = item.NewText, AcceptsReturn = false, Padding = new Thickness(0), BorderThickness = new Thickness(0), MinWidth = run.Box.Width + 4,
            FontFamily = item.EffFamily, FontSize = item.EffSize, FontWeight = item.EffBold ? FontWeights.Bold : FontWeights.Normal, FontStyle = run.Italic && item.FontOverride == null ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(item.EffColor), Background = RunBoxBrush(item),
            CaretBrush = new SolidColorBrush(Colors.Black),
            Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'>" +
                "<Border Background='{TemplateBinding Background}' BorderBrush='#CC2F6BEA' BorderThickness='0.8'><ScrollViewer x:Name='PART_ContentHost' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='0' Padding='0' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden' /></Border></ControlTemplate>"),
        };
        if (item.WrapRight > 0 && run.Turn == 0)
        {
            box.TextWrapping = TextWrapping.Wrap;                       // (words that pass the right edge of the block go to the next line, as they will in the PDF)
            box.Width = Math.Max(run.Box.Width + 4, item.WrapRight - (run.Baseline.X + item.Offset.X) + 4);
        }
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "PdfRunBox");
        Canvas.SetLeft(box, run.Baseline.X + item.Offset.X - 2); Canvas.SetTop(box, item.Top + item.Offset.Y);
        if (run.Turn != 0) box.RenderTransform = new RotateTransform(item.TurnDegrees, 2, item.EffFamily.Baseline * item.EffSize);
        box.TextChanged += (_, _) => { if (_runTyping != null) { _runTyping.NewText = box.Text; _dirty = true; UpdateTitle(); } };
        // Enter finishes; Shift+Enter makes a new line (the box grows taller with it)
        box.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                int at = box.SelectionStart;
                box.SelectedText = "\n";
                box.CaretIndex = at + 1;
                ev.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, ev) =>
        {
            if (ev.NewFocus is DependencyObject nf && IsInside(nf, _editBar)) return;
            // (the list of fonts takes the keyboard for a moment: the line stays open for it)
            Dispatcher.BeginInvoke(() => { if (_runBox == box && !box.IsKeyboardFocusWithin && _fontPopup?.IsOpen != true && !_runMenuOpen) CloseRunBox(); });
        };
        _runBox = box;
        ApplySpacingToBox();
        RenderItems(item.Page);
        UpdateEditButtons(); UpdateProperties();
        Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>The font, bold, size or colour of the line being typed changed: the box and the line behind it follow.</summary>
    private void StyleRunBox()
    {
        if (_runBox == null || _runTyping == null) return;
        var item = _runTyping;
        _runBox.FontFamily = item.EffFamily; _runBox.FontSize = item.EffSize;
        _runBox.FontWeight = item.EffBold ? FontWeights.Bold : FontWeights.Normal;
        _runBox.FontStyle = item.Run.Italic && item.FontOverride == null ? FontStyles.Italic : FontStyles.Normal;
        _runBox.Foreground = new SolidColorBrush(item.EffColor);
        _runBox.Background = RunBoxBrush(item);
        ApplySpacingToBox();
        Canvas.SetTop(_runBox, item.Top + item.Offset.Y);
        if (item.Run.Turn != 0) _runBox.RenderTransform = new RotateTransform(item.TurnDegrees, 2, item.EffFamily.Baseline * item.EffSize);
        _dirty = true; UpdateTitle();
        UpdateProperties();
        var box = _runBox;
        Dispatcher.BeginInvoke(() => { if (_runBox == box) box.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Finishes typing a changed line; when nothing changed, it is as if it was never opened.</summary>
    private void CloseRunBox()
    {
        if (_runBox == null || _runTyping == null) { _runBox = null; _runTyping = null; return; }
        var item = _runTyping;
        item.NewText = _runBox.Text;
        _runBox = null; _runTyping = null;
        if (item.NewText == item.Run.Text && !item.Styled && !item.HasBack && !item.WrapSet)         // (a line that was only moved was moved before: opening it again changed nothing)
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

    // ---------- the frame of the line being typed in (like a text box in Word) ----------
    /// <summary>The frame round the line being typed in: from where it starts to the right edge it flows to, as tall as the box now is (page points).</summary>
    private Rect RunFrame()
    {
        if (_runBox == null || _runTyping == null || _runTyping.WrapRight <= 0 || _runTyping.Run.Turn != 0) return Rect.Empty;
        double left = Canvas.GetLeft(_runBox), top = Canvas.GetTop(_runBox);
        double h = _runBox.ActualHeight > 0 ? _runBox.ActualHeight : _runTyping.EffSize * 1.3;
        double right = Math.Max(_runTyping.WrapRight + 2, left + Math.Max(_runBox.ActualWidth, 20));
        return new Rect(left - 3, top - 3, right - left + 6, h + 6);
    }

    /// <summary>The squares of that frame: the middle of the right edge and the lower right corner. Both change the width.</summary>
    private bool OnRunWrapHandle(PageView pv, Point p)
    {
        var f = RunFrame();
        if (f.IsEmpty) return false;
        double k = 1 / Math.Max(0.01, pv.OverlayScale.ScaleX);
        return (p - new Point(f.Right, f.Top + f.Height / 2)).Length <= 9 * k || (p - new Point(f.Right, f.Bottom)).Length <= 9 * k;
    }

    /// <summary>The frame was dragged to <paramref name="x"/>: the words flow again between its edges (also the ones that were not typed).</summary>
    private void RunWrapTo(PageView pv, double x)
    {
        if (_runBox == null || _runTyping == null) return;
        var item = _runTyping;
        double left = Canvas.GetLeft(_runBox);
        item.WrapRight = Math.Max(x - 2, left + item.EffSize * 3 + 2);
        item.WrapSet = true;
        _runBox.MinWidth = 10;
        _runBox.TextWrapping = TextWrapping.Wrap;
        _runBox.Width = Math.Max(10, item.WrapRight - left + 4);
        _dirty = true; UpdateTitle();
        RenderItems(item.Page);
    }

    /// <summary>With "Edit text", the line under the pointer gets a frame (and the I-beam).</summary>
    private void HoverRun(PageView pv, Point p)
    {
        var existing = _items.OfType<RunEditItem>().LastOrDefault(i => i.Page == pv.Index && i.Hit(p));
        var run = existing == null ? RunNear(pv.Index, p) : null;
        (int, Rect)? now = existing != null ? (pv.Index, existing.Bounds) : run == null ? null : (pv.Index, run.Box);
        pv.Overlay.Cursor = now != null ? Cursors.IBeam : Cursors.Arrow;
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
        if (_runBox != null && _runTyping?.Page == page && RunFrame() is { IsEmpty: false } box)
        {
            // like a text box in Word: a frame round the text with squares on its right edge; dragging a square makes the text block wider or narrower and the words flow again
            double k = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX);
            var blue = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA));
            var frame = new Rectangle { Width = box.Width, Height = box.Height, Stroke = blue, StrokeThickness = 1.2 * k, IsHitTestVisible = false };
            Canvas.SetLeft(frame, box.X); Canvas.SetTop(frame, box.Y);
            overlay.Children.Add(frame);
            foreach (var (hx, hy) in new[] { (box.Right, box.Top + box.Height / 2), (box.Right, box.Bottom) })
            {
                var square = new Rectangle { Width = 9 * k, Height = 9 * k, Fill = Brushes.White, Stroke = blue, StrokeThickness = 1.5 * k, IsHitTestVisible = false };
                Canvas.SetLeft(square, hx - 4.5 * k); Canvas.SetTop(square, hy - 4.5 * k);
                overlay.Children.Add(square);
            }
        }
        if (_editing && _tool is EditTool.EditText or EditTool.Paragraph && _runBox == null && _hoverRun is { } h && h.Page == page)
        {
            double k = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX);
            // (room round the words, a little more on the right and below: the frame is also the place to click, so it is not asked to be exact)
            var frame = new Rectangle { Width = h.Box.Width + 14 * k, Height = h.Box.Height + 12 * k, Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA)), StrokeThickness = 1 * k, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false };
            Canvas.SetLeft(frame, h.Box.X - 2 * k); Canvas.SetTop(frame, h.Box.Y - 2 * k);
            overlay.Children.Add(frame);
        }
    }
}
