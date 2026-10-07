using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Edit the whole paragraph": the lines of a paragraph already in the PDF are taken together (same font, size and colour, lines one under the other at the same spacing, a short last line ends
/// it), shown as one running text, and when the text is changed the words are laid out again to the same width and spacing. Done with the same line changes as Edit text: the first line keeps
/// its start, the second line takes the rest of the lines (written one under the other), the remaining old lines are taken out.
/// </summary>
public sealed partial class PdfWindow
{
    private sealed record Paragraph(List<PdfTextRun> Lines, double Left, double Right, double Advance);

    private static bool SameLook(PdfTextRun a, PdfTextRun b) =>
        string.Equals(a.Family, b.Family, StringComparison.OrdinalIgnoreCase) && Math.Abs(a.Size - b.Size) < 0.3 && a.Bold == b.Bold && a.Italic == b.Italic
        && Math.Abs(a.Color.R - b.Color.R) + Math.Abs(a.Color.G - b.Color.G) + Math.Abs(a.Color.B - b.Color.B) < 60;

    /// <summary>The paragraph the line belongs to (null when it is a single line on its own).</summary>
    private Paragraph? FindParagraph(int page, PdfTextRun start)
    {
        var edited = new HashSet<int>(_items.OfType<RunEditItem>().Where(i => i.Page == page).SelectMany(i => i.Run.Parts?.Select(x => x.Index) ?? new[] { i.Run.Index }));
        var runs = Runs(page).Where(r => !edited.Contains(r.Index) && SameLook(r, start) && r.Text.Trim().Length > 0).ToList();
        double em = Math.Max(4, start.Size);
        var chain = new List<PdfTextRun> { start };
        double gap = 0;
        // down
        for (var cur = start; ;)
        {
            var next = runs.Where(r => r != cur && r.Baseline.Y > cur.Baseline.Y + 0.8 * em && r.Baseline.Y <= cur.Baseline.Y + 2.3 * em
                                       && Math.Abs(r.Box.Left - start.Box.Left) <= 0.7 * em && r.Box.Right > start.Box.Left && r.Box.Left < start.Box.Right + 40 * em)
                           .OrderBy(r => r.Baseline.Y).FirstOrDefault();
            if (next == null) break;
            double g = next.Baseline.Y - cur.Baseline.Y;
            if (gap == 0) gap = g; else if (Math.Abs(g - gap) > 0.2 * gap + 0.6) break;
            chain.Add(next); cur = next;
            if (chain.Count > 80) break;
        }
        // up (the first line of a paragraph may start further in)
        for (var cur = chain[0]; ;)
        {
            var prev = runs.Where(r => r != cur && r.Baseline.Y < cur.Baseline.Y - 0.8 * em && r.Baseline.Y >= cur.Baseline.Y - 2.3 * em
                                       && (r.Box.Left - start.Box.Left) <= 3.5 * em && (r.Box.Left - start.Box.Left) >= -0.7 * em && r.Box.Right > start.Box.Left)
                           .OrderByDescending(r => r.Baseline.Y).FirstOrDefault();
            if (prev == null) break;
            double g = cur.Baseline.Y - prev.Baseline.Y;
            if (gap == 0) gap = g; else if (Math.Abs(g - gap) > 0.2 * gap + 0.6) break;
            chain.Insert(0, prev); cur = prev;
            if (chain.Count > 80) break;
        }
        if (chain.Count < 2) return null;
        double left = chain.Skip(1).DefaultIfEmpty(chain[0]).Min(r => r.Box.Left), right = chain.Max(r => r.Box.Right);
        // a line flows into the next one when the next line's first word would not have fitted on it (ragged or justified, the same test); a line that had room for it ended on purpose
        var measure = MeasureFor(start);
        double space = measure(" ");
        bool Flows(int k)
        {
            string firstWord = chain[k + 1].Text.TrimStart().Split(' ')[0];
            return right - chain[k].Box.Right < measure(firstWord) + space - 0.5;
        }
        int s = chain.IndexOf(start);
        int from = s;
        while (from > 0 && Flows(from - 1)) from--;
        int to = s;
        while (to < chain.Count - 1 && Flows(to)) to++;
        var lines = chain.GetRange(from, to - from + 1);
        if (lines.Count < 2) return null;
        return new Paragraph(lines, left, right, gap > 0 ? gap : em * 1.2);
    }

    /// <summary>The width of a piece of text in the line's own font (points).</summary>
    private static Func<string, double> MeasureFor(PdfTextRun run)
    {
        var typeface = new Typeface(RunEditItem.Family(run), run.Italic ? FontStyles.Italic : FontStyles.Normal, run.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        return s => s.Length == 0 ? 0 : new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, run.Size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
    }

    private static string JoinLines(IReadOnlyList<PdfTextRun> lines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            string t = line.Text.Trim();
            if (sb.Length > 0) { if (sb[^1] != '-' ) sb.Append(' '); }
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>The words laid out in lines of at most the given widths (the first line may have its own width): a new line starts where a word would stick out. A line break in the text stays one.</summary>
    private static List<string> Wrap(string text, Func<string, double> width, double firstWidth, double otherWidth)
    {
        var lines = new List<string>();
        foreach (string hard in text.Replace("\r\n", "\n").Split('\n'))
        {
            var words = hard.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { lines.Add(""); continue; }
            string current = "";
            foreach (string word in words)
            {
                string tryText = current.Length == 0 ? word : current + " " + word;
                double limit = lines.Count == 0 ? firstWidth : otherWidth;
                if (current.Length > 0 && width(tryText) > limit) { lines.Add(current); current = word; }
                else current = tryText;
            }
            lines.Add(current);
        }
        return lines;
    }

    private void EditParagraphAt(PageView pv, Point p)
    {
        var run = RunAt(pv.Index, p);
        if (run == null) { Toast("Click on a line of a paragraph (turned text can't be changed)"); return; }
        var para = FindParagraph(pv.Index, run);
        if (para == null) { EditTextAt(pv, p); Toast("That line stands on its own: change it here. A paragraph has several lines one under the other"); return; }
        CloseTextBox(commit: true);

        var first = para.Lines[0];
        var dlg = MakeDialog("Edit paragraph", 580);
        var box = new TextBox { Text = JoinLines(para.Lines), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(6), Margin = new Thickness(0, 6, 0, 0) };
        Id("PdfParaText", box);
        var ok = new Button { Content = "Change the paragraph", Style = DialogStyle("DialogPrimary"), Margin = new Thickness(0, 0, 8, 0) }; Id("PdfParaOk", ok);
        var cancel = new Button { Content = "Cancel", Style = DialogStyle("DialogButton"), IsCancel = true };
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; dlg.Close(); };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = $"{para.Lines.Count} lines, on page {pv.Index + 1}", FontWeight = FontWeights.SemiBold });
        root.Children.Add(box);
        root.Children.Add(new TextBlock { Text = "The words are laid out again to the same width and line spacing, in the same font, size and colour. Enter makes a line break of its own. A longer text grows downwards, so look at what is under it afterwards.", Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        dlg.ShowDialog();
        if (!accepted) return;
        string newText = box.Text;
        if (newText == JoinLines(para.Lines)) return;                                  // (nothing changed)

        // the same font as the line is drawn in, to measure the words
        double width = para.Right - para.Left;
        var lines = Wrap(newText, MeasureFor(first), para.Right - first.Baseline.X + 0.5, width + 0.5);

        Snapshot();
        RunEditItem Item(PdfTextRun r, string text, double? advance = null) =>
            new() { Page = pv.Index, Run = r, NewText = text, Cover = CoverColour(pv, r.Box), AdvanceOverride = advance };
        // line 1 keeps its own start (an indent stays); line 2 takes the rest of the lines (one under the other); the other old lines go
        // (the covers of the lines that go come first, so the new lines that reach down over them are drawn on top)
        for (int i = 2; i < para.Lines.Count; i++) _items.Add(Item(para.Lines[i], ""));
        _items.Add(Item(para.Lines[1], lines.Count > 1 ? string.Join("\n", lines.Skip(1)) : "", para.Advance));
        _items.Add(Item(para.Lines[0], lines.Count > 0 ? lines[0] : ""));
        _dirty = true; UpdateTitle(); UpdateEditButtons();
        RenderItems(pv.Index);
        Toast($"Paragraph laid out again: {lines.Count} line{(lines.Count == 1 ? "" : "s")} (was {para.Lines.Count}). Undo takes it back; Save writes it");
    }
}
