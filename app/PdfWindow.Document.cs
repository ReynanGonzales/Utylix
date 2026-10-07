using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The document itself (Tools > DOCUMENT): Properties (title, author, subject, keywords), Page labels (i, ii, iii ... then 1, 2, 3; prefixes like "A-") and Attachments (files carried inside the PDF).
/// Each change goes into the open document at once (Undo takes it back until you save).
/// </summary>
public sealed partial class PdfWindow
{
    private Window MakeDialog(string title, double width)
    {
        var dlg = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("BgBrush"), Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        WindowTheme.DarkTitleBar(dlg);
        return dlg;
    }

    private static string Id(string id, DependencyObject o) { AutomationProperties.SetAutomationId(o, id); return id; }
    private static Style DialogStyle(string name) => (Style)Application.Current.FindResource(name);

    private static TextBox Field(string text, string id, double top = 4)
    {
        var box = new TextBox { Text = text, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, top, 0, 0) };
        Id(id, box);
        return box;
    }

    /// <summary>"D:20261007083015+08'00'" -> "2026-10-07 08:30".</summary>
    private static string NiceDate(string pdfDate)
    {
        var m = Regex.Match(pdfDate, @"D:(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?");
        if (!m.Success) return pdfDate;
        string G(int i, string fallback) => m.Groups[i].Success ? m.Groups[i].Value : fallback;
        return $"{G(1, "")}-{G(2, "01")}-{G(3, "01")} {G(4, "00")}:{G(5, "00")}";
    }

    // ---------- properties ----------
    private void EditProperties()
    {
        if (_pdf == null || _path == null) return;
        PdfProperties props;
        try { props = _pdf.GetProperties(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { Toast("Couldn't read the properties: " + e.Message); return; }
        var dlg = MakeDialog("Document properties", 480);
        var title = Field(props.Title, "PdfPropTitle"); var author = Field(props.Author, "PdfPropAuthor"); var subject = Field(props.Subject, "PdfPropSubject"); var keywords = Field(props.Keywords, "PdfPropKeywords");
        var ok = new Button { Content = "OK", Style = DialogStyle("DialogPrimary"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = DialogStyle("DialogButton"), IsCancel = true };
        Id("PdfPropOk", ok);
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; dlg.Close(); };
        var root = new StackPanel { Margin = new Thickness(20) };
        TextBlock Label(string t, double top = 10) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 0) };
        root.Children.Add(Label("Title", 0)); root.Children.Add(title);
        root.Children.Add(Label("Author")); root.Children.Add(author);
        root.Children.Add(Label("Subject")); root.Children.Add(subject);
        root.Children.Add(Label("Keywords")); root.Children.Add(keywords);
        var facts = new List<string>();
        if (props.Creator.Length > 0) facts.Add("Made with: " + props.Creator);
        if (props.Producer.Length > 0) facts.Add("Written by: " + props.Producer);
        if (props.Created.Length > 0) facts.Add("Created: " + NiceDate(props.Created));
        if (props.Modified.Length > 0) facts.Add("Changed: " + NiceDate(props.Modified));
        facts.Add($"{_pdf.PageCount} page{(_pdf.PageCount == 1 ? "" : "s")}, {PdfReduceWindow.Bytes(_pdf.Length)}");
        root.Children.Add(new TextBlock { Text = string.Join("\n", facts), Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = "The title, author, subject and keywords are what Explorer and other programs show for this file.", Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.Loaded += (_, _) => { title.Focus(); title.SelectAll(); };
        dlg.ShowDialog();
        if (!accepted) return;
        string t0 = title.Text.Trim(), a0 = author.Text.Trim(), s0 = subject.Text.Trim(), k0 = keywords.Text.Trim();
        if (t0 == props.Title && a0 == props.Author && s0 == props.Subject && k0 == props.Keywords) return;
        PageOp(p => p.SetProperties(t0, a0, s0, k0), new[] { _current }, "Properties changed. Undo takes them back until you save", keepView: true);
    }

    // ---------- page labels ----------
    private sealed class LabelRow
    {
        public TextBox From = null!, Prefix = null!, Start = null!;
        public ComboBox Style = null!;
        public FrameworkElement Visual = null!;
    }

    private static readonly (string Text, PdfLabelStyle Style)[] LabelStyles =
    {
        ("1, 2, 3", PdfLabelStyle.Decimal), ("i, ii, iii", PdfLabelStyle.LowerRoman), ("I, II, III", PdfLabelStyle.UpperRoman),
        ("a, b, c", PdfLabelStyle.LowerAlpha), ("A, B, C", PdfLabelStyle.UpperAlpha), ("no number", PdfLabelStyle.None),
    };

    private static string Roman(int n, bool upper)
    {
        var parts = new[] { (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"), (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i") };
        var sb = new System.Text.StringBuilder();
        foreach (var (value, text) in parts) while (n >= value) { sb.Append(text); n -= value; }
        return upper ? sb.ToString().ToUpperInvariant() : sb.ToString();
    }

    private static string Alpha(int n, bool upper)                // 1 = a ... 26 = z, 27 = aa
    {
        int letter = (n - 1) % 26, times = (n - 1) / 26 + 1;
        string s = new string((char)((upper ? 'A' : 'a') + letter), times);
        return s;
    }

    /// <summary>What a page is called with these ranges (the same rules readers use).</summary>
    private static string LabelOf(IReadOnlyList<PdfLabelRange> ranges, int page)
    {
        PdfLabelRange? range = null;
        foreach (var r in ranges.OrderBy(r => r.From)) if (r.From - 1 <= page) range = r;
        if (range == null) return (page + 1).ToString();
        int n = range.Start + (page - (range.From - 1));
        string body = range.Style switch
        {
            PdfLabelStyle.None => "",
            PdfLabelStyle.UpperRoman => Roman(n, true), PdfLabelStyle.LowerRoman => Roman(n, false),
            PdfLabelStyle.UpperAlpha => Alpha(n, true), PdfLabelStyle.LowerAlpha => Alpha(n, false),
            _ => n.ToString(),
        };
        return range.Prefix + body;
    }

    private void EditPageLabels()
    {
        if (_pdf == null || _path == null) return;
        int pages = _pdf.PageCount;
        List<PdfLabelRange> existing;
        try { existing = _pdf.GetPageLabelRanges(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or PdfProtectedException) { Toast("Couldn't read the page numbering: " + e.Message); return; }
        var dlg = MakeDialog("Page labels", 600);
        var rows = new List<LabelRow>();
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var preview = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        Id("PdfLabelPreview", preview);

        List<PdfLabelRange> Current()
        {
            var result = new List<PdfLabelRange>();
            foreach (var r in rows)
            {
                if (!int.TryParse(r.From.Text.Trim(), out int from)) continue;
                int.TryParse(r.Start.Text.Trim(), out int start);
                result.Add(new PdfLabelRange(Math.Clamp(from, 1, pages), LabelStyles[Math.Max(0, r.Style.SelectedIndex)].Style, r.Prefix.Text, Math.Max(1, start == 0 ? 1 : start)));
            }
            return result;
        }
        void Refresh()
        {
            var ranges = Current();
            var shown = Enumerable.Range(0, Math.Min(pages, 12)).Select(i => $"{i + 1} → {(LabelOf(ranges, i) is { Length: > 0 } l ? l : "(none)")}");
            preview.Text = ranges.Count == 0 ? "No special numbering: the pages are 1, 2, 3 ..." : "Pages: " + string.Join(",   ", shown) + (pages > 12 ? " ..." : "");
        }
        void AddRow(PdfLabelRange? r)
        {
            var row = new LabelRow();
            row.From = new TextBox { Text = (r?.From ?? 1).ToString(), Width = 60, Padding = new Thickness(6, 4, 6, 4) };
            row.Style = new ComboBox { Width = 130, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
            foreach (var s in LabelStyles) row.Style.Items.Add(s.Text);
            row.Style.SelectedIndex = Math.Max(0, Array.FindIndex(LabelStyles, s => s.Style == (r?.Style ?? PdfLabelStyle.Decimal)));
            row.Prefix = new TextBox { Text = r?.Prefix ?? "", Width = 100, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
            row.Start = new TextBox { Text = (r?.Start ?? 1).ToString(), Width = 56, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
            int n = rows.Count + 1;
            Id("PdfLabelFrom" + n, row.From); Id("PdfLabelStyle" + n, row.Style); Id("PdfLabelPrefix" + n, row.Prefix); Id("PdfLabelStart" + n, row.Start);
            var remove = new Button { Content = "×", Style = DialogStyle("DialogButton"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "Take this line away" };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (UIElement e in new UIElement[] { row.From, row.Style, row.Prefix, row.Start, remove }) panel.Children.Add(e);
            row.Visual = panel;
            row.From.TextChanged += (_, _) => Refresh(); row.Prefix.TextChanged += (_, _) => Refresh(); row.Start.TextChanged += (_, _) => Refresh(); row.Style.SelectionChanged += (_, _) => Refresh();
            remove.Click += (_, _) => { rows.Remove(row); list.Children.Remove(panel); Refresh(); };
            rows.Add(row); list.Children.Add(panel);
        }
        foreach (var r in existing) AddRow(r);
        if (rows.Count == 0) AddRow(null);

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Opacity = 0.7 };
        foreach (var (text, width, gap) in new[] { ("From page", 60.0, 0.0), ("Numbered as", 130.0, 8.0), ("Prefix", 100.0, 8.0), ("Starts at", 56.0, 8.0) })
            head.Children.Add(new TextBlock { Text = text, Width = width + gap, FontSize = 12, Padding = new Thickness(gap, 0, 0, 0) });
        var add = new Button { Content = "+ Add a line", Style = DialogStyle("DialogButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        Id("PdfLabelAdd", add);
        add.Click += (_, _) => { AddRow(new PdfLabelRange(Math.Min(pages, (Current().LastOrDefault()?.From ?? 1) + 1), PdfLabelStyle.Decimal, "", 1)); Refresh(); };
        var ok = new Button { Content = "OK", Style = DialogStyle("DialogPrimary"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var plain = new Button { Content = "No special numbering", Style = DialogStyle("DialogButton"), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = DialogStyle("DialogButton"), IsCancel = true };
        Id("PdfLabelOk", ok); Id("PdfLabelNone", plain);
        bool accepted = false, cleared = false;
        ok.Click += (_, _) => { accepted = true; dlg.Close(); };
        plain.Click += (_, _) => { accepted = true; cleared = true; dlg.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(plain); buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Number the pages the way a book does: i, ii, iii for the front, then 1, 2, 3, or with a prefix like A-1. This is the number readers show in their page box; it does not change what is printed on the pages.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        root.Children.Add(new TextBlock { Text = "A line applies from its page to the next line.", Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        list.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(head); root.Children.Add(list); root.Children.Add(add); root.Children.Add(preview); root.Children.Add(buttons);
        dlg.Content = root;
        Refresh();
        dlg.ShowDialog();
        if (!accepted) return;
        var ranges = cleared ? new List<PdfLabelRange>() : Current();
        PageOp(p => p.SetPageLabels(ranges), new[] { _current }, ranges.Count == 0 ? "Page numbering back to 1, 2, 3. Undo takes it back until you save" : "Page labels set. Undo takes them back until you save", keepView: true);
    }

    // ---------- personal details ----------
    private void FindPersonalDetails()
    {
        if (_pdf == null || _path == null) return;
        var dlg = new PdfPersonalDialog(this, _pdf, page => GoTo(page));
        if (dlg.ShowDialog() != true || dlg.Chosen.Count == 0) return;
        var areas = new List<(int Page, Rect Box)>();
        foreach (var hit in dlg.Chosen)
            if (Text(hit.Page) is { } t) foreach (var r in t.LineBoxes(hit.Start, hit.End)) if (!r.IsEmpty) areas.Add((hit.Page, r));
        if (areas.Count == 0) { Toast("Those matches have no place on the page to black out"); return; }
        RedactAreas(areas, dlg.Chosen.Count);
    }

    // ---------- attachments ----------
    private void EditAttachments()
    {
        if (_pdf == null || _path == null) return;
        var dlg = MakeDialog("Attached files", 560);
        var list = new ListBox { Height = 180, Background = Brushes.Transparent, Foreground = dlg.Foreground, BorderThickness = new Thickness(1) };
        list.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
        Id("PdfAttachList", list);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 20, Opacity = 0.85 };
        Id("PdfAttachStatus", status);
        var addBtn = new Button { Content = "Attach a file…", Style = DialogStyle("DialogButton"), Margin = new Thickness(0, 0, 8, 0) };
        var saveBtn = new Button { Content = "Save the chosen…", Style = DialogStyle("DialogButton"), Margin = new Thickness(0, 0, 8, 0) };
        var removeBtn = new Button { Content = "Remove the chosen", Style = DialogStyle("DialogButton"), Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close", Style = DialogStyle("DialogPrimary"), IsCancel = true, IsDefault = true };
        Id("PdfAttachAdd", addBtn); Id("PdfAttachSave", saveBtn); Id("PdfAttachRemove", removeBtn);
        List<PdfAttachment> items = new();
        void Reload()
        {
            try { items = _pdf!.GetAttachments(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { items = new(); }
            list.Items.Clear();
            foreach (var a in items) list.Items.Add(a.Name + "    (" + PdfReduceWindow.Bytes(a.Size) + ")");
            saveBtn.IsEnabled = removeBtn.IsEnabled = false;
            if (items.Count == 0) status.Text = "No files are attached to this PDF.";
        }
        list.SelectionChanged += (_, _) => saveBtn.IsEnabled = removeBtn.IsEnabled = list.SelectedIndex >= 0;
        addBtn.Click += (_, _) =>
        {
            var open = new Microsoft.Win32.OpenFileDialog { Title = "File to attach to the PDF", CheckFileExists = true };
            if (open.ShowDialog(dlg) != true) return;
            byte[] data; string name = Path.GetFileName(open.FileName);
            try { data = File.ReadAllBytes(open.FileName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "Couldn't read that file: " + e.Message; return; }
            if (data.Length > 100 * 1024 * 1024) { status.Text = "That file is bigger than 100 MB: too big to carry inside a PDF."; return; }
            if (PageOp(p => p.AddAttachment(name, data), new[] { _current }, $"\"{name}\" attached. Undo takes it back until you save", keepView: true)) { Reload(); status.Text = $"Attached {name} ({PdfReduceWindow.Bytes(data.Length)}). Save the PDF to keep it."; }
        };
        saveBtn.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= items.Count) return;
            var save = new Microsoft.Win32.SaveFileDialog { Title = "Save the attached file", FileName = items[i].Name };
            if (save.ShowDialog(dlg) != true) return;
            try { File.WriteAllBytes(save.FileName, _pdf!.GetAttachmentData(i)); status.Text = "Saved " + Path.GetFileName(save.FileName) + "."; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "Couldn't save it: " + e.Message; }
        };
        removeBtn.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= items.Count) return;
            string name = items[i].Name;
            if (PageOp(p => p.RemoveAttachment(i), new[] { _current }, $"\"{name}\" removed from the PDF. Undo takes it back until you save", keepView: true)) { Reload(); status.Text = $"Removed {name}."; }
        };
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(addBtn); buttons.Children.Add(saveBtn); buttons.Children.Add(removeBtn);
        var closeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        closeRow.Children.Add(close);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Files carried inside the PDF (a contract with its annexes, a spreadsheet behind a report). Most readers show them in a paperclip panel.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(list); root.Children.Add(buttons); root.Children.Add(status); root.Children.Add(closeRow);
        dlg.Content = root;
        Reload();
        dlg.ShowDialog();
    }
}
