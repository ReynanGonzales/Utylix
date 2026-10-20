using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Edit the whole paragraph": the lines of a paragraph already in the PDF are taken together whatever their looks are (a bold first word or a number in front of
/// the text is fine: the number stays where it is), shown as one running text, and when the text is changed the words are laid out again to the same width and
/// line spacing. Bold, italic and colour stay with the words they were on; new words take the look of the words next to them. Saved by taking the old pieces
/// out and writing the new ones like the old ones (same font, size, colour).
/// </summary>
public sealed partial class PdfWindow
{
    /// <summary>One line of text of the page: its pieces left to right; a number or bullet in front ("5.5.1", "•") is kept apart as its label.</summary>
    private sealed class ParaLine
    {
        public readonly List<PdfTextRun> Runs;
        public readonly List<PdfTextRun> Labels = new();
        public readonly List<PdfTextRun> Body = new();
        public readonly double Baseline;
        public readonly double Size;
        public ParaLine(List<PdfTextRun> runs)
        {
            Runs = runs.OrderBy(r => r.Box.Left).ToList();
            var widest = Runs.OrderByDescending(r => r.Box.Width).First();
            Baseline = widest.Baseline.Y;
            // a number / bullet, then a clear gap, then the words
            int skip = 0;
            if (Runs.Count > 1 && IsLabel(Runs[0].Text) && Runs[1].Box.Left - Runs[0].Box.Right >= 0.6 * Math.Max(4, Runs[0].Size)) { Labels.Add(Runs[0]); skip = 1; }
            Body.AddRange(Runs.Skip(skip));
            Size = (Body.Count > 0 ? Body : Runs).OrderByDescending(r => r.Box.Width).First().Size;
        }
        public bool HasLabel => Labels.Count > 0;
        public double BodyLeft => Body.Count > 0 ? Body[0].Box.Left : Runs[0].Box.Left;
        public double Right => Runs.Max(r => r.Box.Right);
        public double BodyRight => Body.Count > 0 ? Body.Max(r => r.Box.Right) : Right;
    }

    private static readonly Regex NumberLabel = new(@"^[\(\[]?([0-9]{1,3}|[A-Za-z])([.\-][0-9]{1,3})*[.)\]:]?$", RegexOptions.Compiled);
    private static bool IsLabel(string text)
    {
        string t = text.Replace(" ", "").Replace(" ", "");
        return t.Length is > 0 and <= 12 && (NumberLabel.IsMatch(t) || (t.Length == 1 && "•·▪■□●○◦‣⁃-–—*".IndexOf(t[0]) >= 0));
    }

    /// <param name="Align">how the lines sit between Left and Right: 0 left (ragged), 1 centred, 2 right, 3 justified (every line but the last of a paragraph reaches both edges)</param>
    private sealed record Paragraph(List<ParaLine> Lines, double Left, double Right, double Advance, int Align = 0)
    {
        public double FirstStart => Lines[0].Body[0].Baseline.X;
        public double FirstBaseline => Lines[0].Baseline;
        public IEnumerable<PdfTextRun> Pieces => Lines.SelectMany(l => l.Body);
    }

    private static bool SameLook(PdfTextRun a, PdfTextRun b) =>
        string.Equals(a.Family, b.Family, StringComparison.OrdinalIgnoreCase) && Math.Abs(a.Size - b.Size) < 0.3 && a.Bold == b.Bold && a.Italic == b.Italic
        && Math.Abs(a.Color.R - b.Color.R) + Math.Abs(a.Color.G - b.Color.G) + Math.Abs(a.Color.B - b.Color.B) < 60;

    /// <summary>The text objects of a page that were already changed (by a line or paragraph edit): their old place is empty.</summary>
    private HashSet<int> EditedRunIndices(int page)
    {
        var set = new HashSet<int>();
        foreach (var i in _items)
        {
            if (i.Page != page) continue;
            if (i is RunEditItem r) foreach (var x in r.Run.Parts?.Select(p => p.Index) ?? new[] { r.Run.Index }) set.Add(x);
            else if (i is ParagraphItem pi) foreach (var s in pi.Sources) foreach (var x in s.Parts?.Select(p => p.Index) ?? new[] { s.Index }) set.Add(x);
        }
        return set;
    }

    /// <summary>
    /// Where text on a page was changed or laid out again in this session (the old places and the new ones): the page's own text positions are not true there any more,
    /// so no selection or search highlight is drawn over them and a click there does not select a word that is no longer there.
    /// </summary>
    private List<Rect> ChangedTextAreas(int page)
    {
        var areas = new List<Rect>();
        foreach (var i in _items)
        {
            if (i.Page != page) continue;
            if (i is RunEditItem r) { areas.Add(Grow(r.Run.Box, 2)); areas.Add(Grow(r.Bounds, 2)); }
            else if (i is ParagraphItem pi) { foreach (var h in pi.Hide) areas.Add(Grow(h.Box, 2)); areas.Add(Grow(pi.Bounds, 2)); }
        }
        return areas;
    }

    /// <summary>The lines of the page, whatever their looks: pieces on one baseline side by side (a gap of three letters' width or more starts another line: columns).</summary>
    private List<ParaLine> PageLines(int page)
    {
        var edited = EditedRunIndices(page);
        var runs = Runs(page).Where(r => !edited.Contains(r.Index) && r.Text.Trim().Length > 0).OrderBy(r => r.Baseline.Y).ThenBy(r => r.Box.Left).ToList();
        var rows = new List<List<PdfTextRun>>();
        foreach (var r in runs)
        {
            var row = rows.FirstOrDefault(x => Math.Abs(x[0].Baseline.Y - r.Baseline.Y) <= 0.3 * Math.Max(4, r.Size));
            if (row == null) rows.Add(new List<PdfTextRun> { r }); else row.Add(r);
        }
        var lines = new List<ParaLine>();
        foreach (var row in rows)
        {
            var sorted = row.OrderBy(r => r.Box.Left).ToList();
            var piece = new List<PdfTextRun>();
            foreach (var r in sorted)
            {
                if (piece.Count > 0 && r.Box.Left - piece[^1].Box.Right > 3 * Math.Max(4, piece[^1].Size)) { lines.Add(new ParaLine(piece)); piece = new List<PdfTextRun>(); }
                piece.Add(r);
            }
            if (piece.Count > 0) lines.Add(new ParaLine(piece));
        }
        return lines.OrderBy(l => l.Baseline).ThenBy(l => l.Runs[0].Box.Left).ToList();
    }

    /// <summary>The paragraph the line belongs to (null when it is a single line on its own).</summary>
    private Paragraph? FindParagraph(int page, PdfTextRun start)
    {
        var all = PageLines(page);
        var startLine = all.FirstOrDefault(l => l.Runs.Any(r => ReferenceEquals(r, start) || r.Index == start.Index));
        if (startLine == null) return null;
        double em = Math.Max(4, startLine.Size);
        double margin = startLine.BodyLeft;
        var chain = new List<ParaLine> { startLine };
        double gap = 0;

        // down: the next lines (no number of their own: that would be the next paragraph), one step under the other at the same spacing
        bool firstStep = true;
        for (var cur = startLine; ;)
        {
            double lo = firstStep ? margin - 3.5 * em : margin - 0.7 * em, hi = margin + 0.7 * em;
            var next = all.Where(l => l != cur && !l.HasLabel && l.Baseline > cur.Baseline + 0.8 * em && l.Baseline <= cur.Baseline + 2.3 * em
                                      && Math.Abs(l.Size - startLine.Size) <= 1.5 && l.BodyLeft >= lo && l.BodyLeft <= hi && l.Right > margin)
                          .OrderBy(l => l.Baseline).ThenBy(l => Math.Abs(l.BodyLeft - margin)).FirstOrDefault();
            if (next == null) break;
            double g = next.Baseline - cur.Baseline;
            if (gap == 0) gap = g; else if (Math.Abs(g - gap) > 0.2 * gap + 0.6) break;
            if (firstStep) { margin = next.BodyLeft; firstStep = false; }
            chain.Add(next); cur = next;
            if (chain.Count > 80) break;
        }
        // up: the first line may start further in, or have a number in front (then it is the first line: stop there)
        for (var cur = chain[0]; !cur.HasLabel;)
        {
            var prev = all.Where(l => l != cur && l.Baseline < cur.Baseline - 0.8 * em && l.Baseline >= cur.Baseline - 2.3 * em
                                      && Math.Abs(l.Size - startLine.Size) <= 1.5 && l.BodyLeft - margin <= 3.5 * em && l.BodyLeft - margin >= -0.7 * em && l.Right > margin)
                          .OrderByDescending(l => l.Baseline).ThenBy(l => Math.Abs(l.BodyLeft - margin)).FirstOrDefault();
            if (prev == null) break;
            double g = cur.Baseline - prev.Baseline;
            if (gap == 0) gap = g; else if (Math.Abs(g - gap) > 0.2 * gap + 0.6) break;
            chain.Insert(0, prev); cur = prev;
            if (chain.Count > 80) break;
        }
        if (chain.Count < 2) return null;
        double left = chain.Skip(1).Min(l => l.BodyLeft), right = chain.Max(l => l.BodyRight);
        // a line flows into the next one when the next line's first word would not have fitted on it (ragged or justified, the same test); a line that had room for it ended on purpose
        bool Flows(int k)
        {
            var nextFirst = chain[k + 1].Body.FirstOrDefault();
            if (nextFirst == null) return false;
            var measure = MeasureFor(nextFirst);
            string firstWord = nextFirst.Text.TrimStart().Split(' ')[0];
            return right - chain[k].BodyRight < measure(firstWord) + measure(" ") - 0.5;
        }
        int s = chain.IndexOf(startLine);
        int from = s;
        while (from > 0 && Flows(from - 1)) from--;
        int to = s;
        while (to < chain.Count - 1 && Flows(to)) to++;
        var lines = chain.GetRange(from, to - from + 1);
        if (lines.Count < 2 || lines.Any(l => l.Body.Count == 0)) return null;
        double paraLeft = lines.Count > 1 ? lines.Skip(1).Min(l => l.BodyLeft) : lines[0].BodyLeft;
        return new Paragraph(lines, paraLeft, right, gap > 0 ? gap : em * 1.2, DetectAlign(lines, paraLeft, right));
    }

    /// <summary>How the lines of a paragraph sit: justified (the lines but the last reach both edges), centred, right-aligned, or left (ragged).</summary>
    private static int DetectAlign(List<ParaLine> lines, double left, double right)
    {
        if (lines.Count < 2) return 0;
        double width = right - left;
        if (width < 30) return 0;
        double tol = Math.Max(1.2, width * 0.006);
        var full = lines.Take(lines.Count - 1).ToList();
        bool flushRight = full.All(l => Math.Abs(l.BodyRight - right) <= tol);
        bool flushLeft = lines.Skip(1).All(l => Math.Abs(l.BodyLeft - left) <= tol);
        if (flushRight && flushLeft && (full.Count >= 2 || StretchedSpaces(full[0]))) return 3;
        double leftSpread = lines.Max(l => l.BodyLeft) - lines.Min(l => l.BodyLeft);
        if (lines.Count >= 3 && leftSpread > 3 * tol)
        {
            var centres = lines.Select(l => (l.BodyLeft + l.BodyRight) / 2).ToList();
            if (centres.All(c => Math.Abs(c - centres[0]) <= tol * 1.5)) return 1;
            if (lines.All(l => Math.Abs(l.BodyRight - lines[0].BodyRight) <= tol)) return 2;
        }
        return 0;
    }

    /// <summary>Do the spaces of a line look stretched (wider than the font's own space): the sign of a justified line when there is only one full line to go by.</summary>
    private static bool StretchedSpaces(ParaLine line)
    {
        if (line.Body.Count == 0) return false;
        string text = string.Concat(line.Body.Select(r => r.Text));
        int spaces = text.Count(c => c == ' ');
        if (spaces < 3) return false;
        var look = line.Body[0];
        var measure = MeasureFor(look);
        double natural = line.Body.Sum(r => MeasureFor(r)(r.Text));
        double actual = line.BodyRight - line.BodyLeft;
        return (actual - natural) / spaces > 0.15 * measure(" ");
    }

    /// <summary>The width of a piece of text in the run's own font (points).</summary>
    private static Func<string, double> MeasureFor(PdfTextRun run)
    {
        var typeface = new Typeface(RunEditItem.Family(run), run.Italic ? FontStyles.Italic : FontStyles.Normal, run.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        return s => s.Length == 0 ? 0 : new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, run.Size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
    }

    // ---------- the paragraph as one text, a character at a time knowing which piece (look) it came from ----------
    private static (string Text, List<PdfTextRun> Looks) JoinParagraph(IEnumerable<ParaLine> lines)
    {
        var sb = new StringBuilder();
        var looks = new List<PdfTextRun>();
        // (the hyphen at the end of a line may be a soft or non-breaking hyphen: it becomes a plain one, so "non-" + "biodegradable" stays "non-biodegradable")
        void Add(string s, PdfTextRun look) { foreach (char c in s) { sb.Append((int)c is 0xAD or 0x2010 or 0x2011 ? '-' : c); looks.Add(look); } }
        PdfTextRun? previous = null;
        foreach (var line in lines)
        {
            for (int k = 0; k < line.Body.Count; k++)
            {
                var run = line.Body[k];
                string t = run.Text;
                if (sb.Length == 0) t = t.TrimStart();
                else
                {
                    bool prevSpace = sb[^1] == ' ', nextSpace = t.Length > 0 && t[0] == ' ';
                    if (prevSpace && nextSpace) t = t.TrimStart();
                    else if (!prevSpace && !nextSpace)
                    {
                        // a line ends: a space (but not after a hyphen, the word goes on); two pieces on one line: a space only when there is a gap between them
                        bool joined = k == 0 ? sb[^1] == '-' : previous != null && run.Box.Left - previous.Box.Right <= 0.12 * run.Size;
                        if (!joined) Add(" ", run);
                    }
                }
                Add(t, run);
                previous = run;
            }
        }
        while (sb.Length > 0 && sb[^1] == ' ') { sb.Length--; looks.RemoveAt(looks.Count - 1); }
        return (sb.ToString(), looks);
    }

    /// <summary>The look of every character of a changed text: what stayed keeps its look; new words take the look of the word they replace, or of the one before.</summary>
    private static List<PdfTextRun> Restyle(string oldText, List<PdfTextRun> oldLooks, string newText)
    {
        int p = 0;
        while (p < oldText.Length && p < newText.Length && oldText[p] == newText[p]) p++;
        int s = 0;
        while (s < oldText.Length - p && s < newText.Length - p && oldText[oldText.Length - 1 - s] == newText[newText.Length - 1 - s]) s++;
        var result = new List<PdfTextRun>(newText.Length);
        for (int i = 0; i < p; i++) result.Add(oldLooks[i]);
        int oldChanged = oldText.Length - s - p;
        PdfTextRun inherit = oldLooks.Count == 0 ? null! : oldChanged > 0 ? oldLooks[p] : oldLooks[Math.Clamp(p > 0 ? p - 1 : 0, 0, oldLooks.Count - 1)];
        for (int i = p; i < newText.Length - s; i++) result.Add(inherit);
        for (int i = 0; i < s; i++) result.Add(oldLooks[oldText.Length - s + i]);
        return result;
    }

    /// <summary>A piece of the laid-out paragraph: its words, the piece whose look it has, and where it starts (x on the baseline) - points as shown.</summary>
    private sealed record ParaSeg(string Text, PdfTextRun Look, double X, double Baseline);

    /// <summary>The words laid out in lines: the first starts at <paramref name="firstStart"/>, the others at <paramref name="left"/>; a new line starts where a word would stick out past <paramref name="right"/>. A line break in the text stays one.</summary>
    private static List<ParaSeg> Layout(string text, List<PdfTextRun> looks, double firstStart, double left, double right, double baseline, double advance, int align = 0)
    {
        var segs = new List<ParaSeg>();
        var measures = new Dictionary<PdfTextRun, Func<string, double>>();
        Func<string, double> M(PdfTextRun r) { if (!measures.TryGetValue(r, out var f)) measures[r] = f = MeasureFor(r); return f; }
        double line = 0;
        int pos = 0;
        int lineStart = 0;                                                       // (where the line being laid out starts in segs, and the x it starts at)
        double lineX0 = firstStart;
        // a line is finished: justified (when it is full), or centred / pushed right
        void Finish(bool full, double endX)
        {
            if (segs.Count <= lineStart) return;
            if (align == 3) { if (full) JustifyLine(segs, lineStart, right, M); }
            else if (align is 1 or 2)
            {
                double dx = align == 1 ? (left + right) / 2 - (lineX0 + endX) / 2 : right - endX;
                if (Math.Abs(dx) > 0.01) for (int k = lineStart; k < segs.Count; k++) segs[k] = segs[k] with { X = segs[k].X + dx };
            }
        }
        foreach (string hard in text.Replace("\r\n", "\n").Split('\n'))
        {
            double x = line == 0 && segs.Count == 0 ? firstStart : left;
            lineStart = segs.Count; lineX0 = x;
            double y = baseline + line * advance;
            bool any = false;
            int i = 0;
            while (i < hard.Length)
            {
                while (i < hard.Length && hard[i] == ' ') i++;
                if (i >= hard.Length) break;
                int j = i;
                while (j < hard.Length && hard[j] != ' ') j++;
                // the word, in pieces of one look each
                var pieces = new List<(string Text, PdfTextRun Look)>();
                for (int k = i; k < j;)
                {
                    var look = looks[Math.Min(pos + k, looks.Count - 1)];
                    int e = k + 1;
                    while (e < j && ReferenceEquals(looks[Math.Min(pos + e, looks.Count - 1)], look)) e++;
                    pieces.Add((hard[k..e], look));
                    k = e;
                }
                double width = pieces.Sum(pc => M(pc.Look)(pc.Text));
                var spaceLook = any && segs.Count > 0 ? segs[^1].Look : pieces[0].Look;
                double space = any ? M(spaceLook)(" ") : 0;
                if (any && x + space + width > right - 0.5)
                {
                    Finish(true, x);
                    line++; y = baseline + line * advance; x = left; any = false; space = 0;
                    lineStart = segs.Count; lineX0 = x;
                }
                if (any)
                {
                    // the space after the words before: part of the piece they are in
                    var last = segs[^1];
                    segs[^1] = last with { Text = last.Text + " " };
                    x += space;
                }
                foreach (var (t, look) in pieces)
                {
                    if (segs.Count > 0 && segs[^1].Baseline == y && SameLook(segs[^1].Look, look) && any) segs[^1] = segs[^1] with { Text = segs[^1].Text + t };
                    else segs.Add(new ParaSeg(t, look, x, y));
                    x += M(look)(t);
                    any = true;
                }
                i = j;
            }
            Finish(false, x);                                                    // (the last line of a paragraph is not justified)
            pos += hard.Length + 1;
            line++;
        }
        return segs;
    }

    /// <summary>
    /// Justifies one full line (<c>segs[start..]</c>): every word gets its own place, the spaces between the words are made wider by the same amount so the line ends exactly at
    /// <paramref name="right"/>. Pieces of one word (a bold ending, say) stay together. A line that cannot be justified (one word, or far too short) is left as it is.
    /// </summary>
    private static void JustifyLine(List<ParaSeg> segs, int start, double right, Func<PdfTextRun, Func<string, double>> M)
    {
        var line = segs.GetRange(start, segs.Count - start);
        var words = new List<(string Text, PdfTextRun Look, double X, int Gaps)>();
        int gaps = 0; bool pending = false, first = true;
        foreach (var s in line)
        {
            var measure = M(s.Look);
            string t = s.Text;
            int k = 0;
            while (k < t.Length)
            {
                if (t[k] == ' ') { pending = true; k++; continue; }
                int e = k;
                while (e < t.Length && t[e] != ' ') e++;
                if (pending && !first) gaps++;                                   // a space came before: this is another word
                pending = false; first = false;
                words.Add((t[k..e], s.Look, s.X + (k == 0 ? 0 : measure(t[..k])), gaps));
                k = e;
            }
            if (t.EndsWith(' ')) pending = true;
        }
        if (gaps == 0 || words.Count < 2) return;
        var lastWord = words[^1];
        double end = lastWord.X + M(lastWord.Look)(lastWord.Text);
        double extra = (right - end) / gaps;
        if (extra < 0.01 || extra > 6 * M(words[0].Look)(" ")) return;           // (already full, or much too short to spread)
        segs.RemoveRange(start, segs.Count - start);
        foreach (var w in words) segs.Add(new ParaSeg(w.Text, w.Look, w.X + w.Gaps * extra, line[0].Baseline));
    }

    // ---------- the item ----------
    /// <summary>A paragraph laid out again: the old pieces are hidden (and taken out when saved) and the new lines are shown (and written when saved).</summary>
    /// <summary>A place where old words were: its solid colour, and the page itself without the old words (cut to the place) when that could be drawn.</summary>
    private sealed class HideBox { public Rect Box; public Color Cover; public System.Windows.Media.Imaging.BitmapSource? Picture; public Rect Area; }

    private sealed class ParagraphItem : EditItem
    {
        public List<PdfTextRun> Sources = new();
        public List<HideBox> Hide = new();
        public int Align;
        public List<ParaSeg> Segs = new();
        public string OriginalText = "";
        public string Text = "";
        public List<PdfTextRun> Looks = new();
        public double FirstStart, Left, Right, FirstBaseline, Advance;
        public Vector Offset;

        public override Rect Bounds
        {
            get
            {
                var r = Rect.Empty;
                foreach (var h in Hide) r.Union(h.Box);
                foreach (var s in Segs)
                {
                    var f = RunEditItem.Family(s.Look);
                    r.Union(new Rect(s.X, s.Baseline - f.Baseline * s.Look.Size, Math.Max(4, MeasureFor(s.Look)(s.Text)), f.LineSpacing * s.Look.Size));
                }
                if (r.IsEmpty) return new Rect(0, 0, 4, 4);
                r.Offset(Offset);
                return r;
            }
        }
        public override bool Hit(Point p) => Inflate(Bounds, 1).Contains(p);
        public override EditItem Clone() => (ParagraphItem)MemberwiseClone();       // (a changed paragraph gets new lists: the old ones are never edited in place)
        public override FrameworkElement Build()
        {
            var canvas = new Canvas();
            foreach (var h in Hide)
            {
                if (h.Picture != null)
                {
                    var clean = new Image { Source = h.Picture, Width = h.Area.Width, Height = h.Area.Height, Stretch = Stretch.Fill };
                    RenderOptions.SetBitmapScalingMode(clean, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(clean, h.Area.X); Canvas.SetTop(clean, h.Area.Y);
                    canvas.Children.Add(clean);
                    continue;
                }
                var rect = new Rectangle { Width = h.Box.Width + 1.2, Height = h.Box.Height + 1.2, Fill = new SolidColorBrush(h.Cover) };
                Canvas.SetLeft(rect, h.Box.X - 0.6); Canvas.SetTop(rect, h.Box.Y - 0.6);
                canvas.Children.Add(rect);
            }
            foreach (var s in Segs)
            {
                var f = RunEditItem.Family(s.Look);
                var t = new TextBlock
                {
                    Text = s.Text, FontFamily = f, FontSize = s.Look.Size, Foreground = new SolidColorBrush(s.Look.Color.A == 0 ? Colors.Black : s.Look.Color),
                    FontWeight = s.Look.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = s.Look.Italic ? FontStyles.Italic : FontStyles.Normal,
                };
                Canvas.SetLeft(t, s.X + Offset.X); Canvas.SetTop(t, s.Baseline - f.Baseline * s.Look.Size + Offset.Y);
                canvas.Children.Add(t);
            }
            return canvas;
        }
        public override IEnumerable<PdfMark> Marks()
        {
            foreach (var src in Sources)
                foreach (var part in src.Parts ?? new[] { src })
                    yield return new PdfReplaceTextMark(Page, part.Index, part.Text, "");
            foreach (var s in Segs)
            {
                var donor = s.Look.Parts?[0] ?? s.Look;
                yield return new PdfPlaceTextMark(Page, donor.Index, donor.Text, s.Text, s.X + Offset.X, s.Baseline + Offset.Y);
            }
        }
        public override void MoveBy(Vector d) => Offset += d;
        public override void ResizeTo(Rect r) { }
    }

    private string? AskParagraphText(int pageNumber, int lineCount, string text, out bool accepted)
    {
        var dlg = MakeDialog("Edit paragraph", 580);
        var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(6), Margin = new Thickness(0, 6, 0, 0) };
        Id("PdfParaText", box);
        var ok = new Button { Content = "Change the paragraph", Style = DialogStyle("DialogPrimary"), Margin = new Thickness(0, 0, 8, 0) }; Id("PdfParaOk", ok);
        var cancel = new Button { Content = "Cancel", Style = DialogStyle("DialogButton"), IsCancel = true };
        bool done = false;
        ok.Click += (_, _) => { done = true; dlg.Close(); };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = $"{lineCount} lines, on page {pageNumber}", FontWeight = FontWeights.SemiBold });
        root.Children.Add(box);
        root.Children.Add(new TextBlock
        {
            Text = "The words are laid out again to the same width and line spacing. Bold, italic and colour stay with the words they were on; new words take the look of the words next to them. " +
                   "Enter makes a line break of its own. A longer text grows downwards, so look at what is under it afterwards.",
            Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        dlg.ShowDialog();
        accepted = done;
        return done ? box.Text.Replace("\r\n", "\n").Replace('\r', '\n') : null;
    }

    private PdfTextRun? _hoverParaRun;

    /// <summary>With the Paragraph tool: the dashed frame goes round the whole paragraph under the pointer (or the single line when it stands alone), before anything is clicked.</summary>
    private void HoverParagraph(PageView pv, Point p)
    {
        var laid = _items.OfType<ParagraphItem>().LastOrDefault(i => i.Page == pv.Index && i.Hit(p));
        var run = laid == null ? RunAt(pv.Index, p) : null;
        if (laid == null && run != null && ReferenceEquals(run, _hoverParaRun) && _hoverRun is { } same && same.Page == pv.Index) return;      // (still on the same line: nothing to work out again)
        _hoverParaRun = run;
        Rect? box = null;
        if (laid != null) box = laid.Bounds;
        else if (run != null)
        {
            var para = FindParagraph(pv.Index, run);
            if (para != null)
            {
                var all = Rect.Empty;
                foreach (var line in para.Lines) foreach (var r in line.Runs) all.Union(r.Box);
                box = all;
            }
            else box = run.Box;
        }
        pv.Overlay.Cursor = box != null ? Cursors.IBeam : Cursors.Arrow;
        (int, Rect)? now = box == null ? null : (pv.Index, box.Value);
        if (now == _hoverRun) return;
        int? old = _hoverRun?.Page;
        _hoverRun = now;
        if (old is int o && o != pv.Index) RenderItems(o);
        RenderItems(pv.Index);
    }

    private void EditParagraphAt(PageView pv, Point p)
    {
        // a paragraph already laid out again in this session: open it again
        var again = _items.OfType<ParagraphItem>().LastOrDefault(i => i.Page == pv.Index && i.Hit(p));
        if (again != null) { ReopenParagraph(pv, again); return; }

        var run = RunAt(pv.Index, p);
        if (run == null) { Toast("Click on a line of a paragraph (turned text can't be changed)"); return; }
        var para = FindParagraph(pv.Index, run);
        if (para == null) { EditTextAt(pv, p); Toast("That line stands on its own: change it here. A paragraph has several lines one under the other"); return; }
        CloseTextBox(commit: true);

        var (oldText, looks) = JoinParagraph(para.Lines);
        string? newText = AskParagraphText(pv.Index + 1, para.Lines.Count, oldText, out bool accepted);
        if (!accepted || newText == null || newText == oldText) return;
        var newLooks = Restyle(oldText, looks, newText);
        double firstStart = para.FirstStart;
        var segs = Layout(newText, newLooks, firstStart, para.Left, para.Right, para.FirstBaseline, para.Advance, para.Align);

        Snapshot();
        var item = new ParagraphItem
        {
            Page = pv.Index, Sources = para.Pieces.ToList(), Segs = segs, OriginalText = oldText, Text = newText, Looks = newLooks,
            FirstStart = firstStart, Left = para.Left, Right = para.Right, FirstBaseline = para.FirstBaseline, Advance = para.Advance, Align = para.Align,
        };
        foreach (var src in item.Sources) item.Hide.Add(new HideBox { Box = src.Box, Cover = CoverColour(pv, src.Box), Area = Grow(src.Box, 1.2) });
        var crops = CleanCrops(pv.Index, item.Sources.SelectMany(ObjectsOf), item.Hide.Select(h => h.Area).ToList());
        for (int k = 0; k < item.Hide.Count; k++) item.Hide[k].Picture = crops[k];
        _items.Add(item);
        _dirty = true; UpdateTitle(); UpdateEditButtons();
        ClearTextSelection();                                           // (the word that was selected for the right-click menu is gone with the old lines)
        RenderItems(pv.Index);
        int lineCount = segs.Select(s => s.Baseline).Distinct().Count();
        Toast($"Paragraph laid out again: {lineCount} line{(lineCount == 1 ? "" : "s")} (was {para.Lines.Count}). Undo takes it back; Save writes it");
    }

    private void ReopenParagraph(PageView pv, ParagraphItem item)
    {
        CloseTextBox(commit: true);
        string? newText = AskParagraphText(pv.Index + 1, item.Segs.Select(s => s.Baseline).Distinct().Count(), item.Text, out bool accepted);
        if (!accepted || newText == null || newText == item.Text) return;
        var newLooks = Restyle(item.Text, item.Looks, newText);
        var segs = Layout(newText, newLooks, item.FirstStart, item.Left, item.Right, item.FirstBaseline, item.Advance, item.Align);
        Snapshot();
        int at = _items.IndexOf(item);
        var changed = (ParagraphItem)item.Clone();
        changed.Segs = segs; changed.Text = newText; changed.Looks = newLooks;
        if (at >= 0) _items[at] = changed;
        _dirty = true; UpdateTitle(); UpdateEditButtons();
        ClearTextSelection();
        RenderItems(pv.Index);
    }
}
