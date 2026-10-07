using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Find and replace across the whole PDF (Tools &gt; TEXT). It works on the lines of text the PDF holds (the same pieces "Edit text" changes), so every replaced line is a normal "Edit text" change:
/// shown at once, undone with Undo, written by Save with the line's own font, size and colour. A phrase that the PDF has split over two lines is not found (the dialog says so).
/// </summary>
public sealed partial class PdfWindow
{
    private static Regex? ReplacePattern(string find, bool matchCase, bool wholeWords)
    {
        if (find.Length == 0) return null;
        string p = Regex.Escape(find);
        if (wholeWords) p = @"(?<!\w)" + p + @"(?!\w)";
        return new Regex(p, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    private void FindAndReplace()
    {
        if (_pdf == null || _path == null) return;
        if (!_editing) { EnterEditing(); if (!_editing) return; }
        CloseTextBox(commit: true);
        var pdf = _pdf;
        int pages = pdf.PageCount;
        var dlg = MakeDialog("Find and replace", 520);
        var find = Field("", "PdfReplaceFind"); var with = Field("", "PdfReplaceWith");
        if (HasSelection && SelectedWords() is { Length: > 0 and < 120 } picked && !picked.Contains('\n')) find.Text = picked;
        var matchCase = new CheckBox { Content = "Match upper / lower case", Margin = new Thickness(0, 10, 0, 0) }; Id("PdfReplaceCase", matchCase);
        var whole = new CheckBox { Content = "Whole words only", Margin = new Thickness(0, 6, 0, 0) }; Id("PdfReplaceWhole", whole);
        var count = new TextBlock { Text = "Reading the text of the pages…", Opacity = 0.75, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap }; Id("PdfReplaceCount", count);
        var go = new Button { Content = "Replace all", Style = DialogStyle("DialogPrimary"), IsDefault = true, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) }; Id("PdfReplaceGo", go);
        var close = new Button { Content = "Close", Style = DialogStyle("DialogButton"), IsCancel = true };
        TextBlock Label(string t, double top = 10) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 0) };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Label("Find", 0)); root.Children.Add(find);
        root.Children.Add(Label("Replace with")); root.Children.Add(with);
        root.Children.Add(matchCase); root.Children.Add(whole); root.Children.Add(count);
        root.Children.Add(new TextBlock
        {
            Text = "Each changed line keeps the font, size and colour the PDF gave it. A longer word makes the line longer, so look at the pages afterwards. A phrase that the PDF has split over two lines, and text in scanned pages (use OCR first), is not found.",
            Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(go); buttons.Children.Add(close);
        root.Children.Add(buttons);
        dlg.Content = root;

        List<List<PdfTextRun>>? all = null;                    // the text of every page, read once on a worker
        int hitLines = 0, hitPages = 0, hits = 0;
        void Recount()
        {
            hitLines = hitPages = hits = 0;
            var rx = ReplacePattern(find.Text, matchCase.IsChecked == true, whole.IsChecked == true);
            if (all == null) return;
            if (rx == null) { count.Text = "Type what to look for."; go.IsEnabled = false; return; }
            try
            {
                for (int p = 0; p < all.Count; p++)
                {
                    int onPage = 0;
                    foreach (var run in all[p]) { int n = rx.Matches(CurrentText(p, run)).Count; if (n > 0) { onPage++; hits += n; } }
                    hitLines += onPage; if (onPage > 0) hitPages++;
                }
            }
            catch (RegexMatchTimeoutException) { count.Text = "That took too long to search."; go.IsEnabled = false; return; }
            count.Text = hits == 0 ? "Nothing like that in the text of this PDF." : $"{hits} match{(hits == 1 ? "" : "es")} in {hitLines} line{(hitLines == 1 ? "" : "s")} on {hitPages} page{(hitPages == 1 ? "" : "s")}.";
            go.IsEnabled = hits > 0;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); Recount(); };
        void Soon() { timer.Stop(); timer.Start(); }
        find.TextChanged += (_, _) => Soon();
        matchCase.Checked += (_, _) => Recount(); matchCase.Unchecked += (_, _) => Recount();
        whole.Checked += (_, _) => Recount(); whole.Unchecked += (_, _) => Recount();

        bool replaced = false;
        go.Click += (_, _) =>
        {
            var rx = ReplacePattern(find.Text, matchCase.IsChecked == true, whole.IsChecked == true);
            if (rx == null || all == null) return;
            string newWord = with.Text;
            var changed = new List<int>(); int done = 0;
            Snapshot();
            for (int p = 0; p < all.Count; p++)
            {
                foreach (var run in all[p])
                {
                    string text = CurrentText(p, run);
                    if (!rx.IsMatch(text)) continue;
                    string result = rx.Replace(text, _ => newWord);
                    if (result == text) continue;
                    var item = ItemFor(p, run);
                    if (item == null)
                    {
                        Color cover = p < _pages.Count ? CoverColour(_pages[p], run.Box) : Colors.White;
                        item = new RunEditItem { Page = p, Run = run, NewText = run.Text, Cover = cover };
                        _items.Add(item);
                    }
                    item.NewText = result; done++;
                    if (!changed.Contains(p)) changed.Add(p);
                }
            }
            replaced = done > 0;
            if (!replaced) { if (_undo.Count > 0) { _items.Clear(); _items.AddRange(_undo.Pop()); } return; }
            _dirty = true; UpdateTitle(); UpdateEditButtons();
            foreach (int p in changed) RenderItems(p);
            dlg.Close();
            Toast($"Changed {done} line{(done == 1 ? "" : "s")} on {changed.Count} page{(changed.Count == 1 ? "" : "s")}. Undo takes it back; Save writes it");
        };

        dlg.Loaded += (_, _) =>
        {
            find.Focus(); find.SelectAll();
            _ = Task.Run(() =>
            {
                var list = new List<List<PdfTextRun>>();
                try { for (int p = 0; p < pages; p++) list.Add(pdf.GetTextRuns(p)); }
                catch (Exception e) when (e is ObjectDisposedException or System.IO.IOException) { }
                return list;
            }).ContinueWith(t => dlg.Dispatcher.BeginInvoke(() => { all = t.Result; Recount(); if (all.Count == 0 || all.All(l => l.Count == 0)) count.Text = "This PDF has no text to search (a scanned PDF: use Tools > Make scanned pages searchable first)."; }));
        };
        dlg.ShowDialog();
        timer.Stop();
        if (replaced) { _scroll.Focus(); }
    }

    /// <summary>The line's text as it is now: a change already made with Edit text counts.</summary>
    private string CurrentText(int page, PdfTextRun run) => ItemFor(page, run)?.NewText ?? run.Text;

    private RunEditItem? ItemFor(int page, PdfTextRun run) =>
        _items.OfType<RunEditItem>().FirstOrDefault(i => i.Page == page && (i.Run.Index == run.Index || (i.Run.Parts != null && run.Parts != null && i.Run.Parts[0].Index == run.Parts[0].Index)));
}
