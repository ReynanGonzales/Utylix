using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The Comments tab of the side panel: every comment in the PDF (sticky notes, and the comments written on highlighted / underlined / struck-out words) in one list, with its page and the words
/// it is about. Click one to go there (a ring shows the place), double-click to read or change it, and save or copy the whole list as a summary.
/// </summary>
public sealed partial class PdfWindow
{
    private RadioButton _commentsTab = null!;
    private readonly ListBox _commentList = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _commentHint = new() { Foreground = Soft, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 6) };
    private readonly List<CommentRow> _comments = new();
    private int _commentsGeneration;

    private sealed record CommentRow(int Page, string Kind, string Quote, string Text, PdfNote? Saved);

    private static string KindName(int subtype) => subtype switch
    {
        Pdfium.AnnotText => "Note", Pdfium.AnnotHighlight => "Highlight", Pdfium.AnnotUnderline => "Underline", Pdfium.AnnotStrikeOut => "Strike out", _ => "Comment",
    };

    /// <summary>The panel of the Comments tab (built once by <see cref="SidePanel"/>).</summary>
    private UIElement CommentsPanel()
    {
        Button Small(string text, string tip, string id, Action click)
        {
            var b = new Button { Content = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12 }, Template = BarButtonTemplate(), Height = 28, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 4, 4), Focusable = false, ToolTip = tip };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => click();
            return b;
        }
        var buttons = new WrapPanel { Margin = new Thickness(8, 6, 8, 0) };
        buttons.Children.Add(Small("Refresh", "Read the comments again", "PdfCommentsRefresh", () => _ = RefreshCommentsAsync()));
        buttons.Children.Add(Small("Save a summary…", "Save every comment with its page and words as a text file", "PdfCommentsSave", SaveCommentSummary));
        buttons.Children.Add(Small("Copy", "Copy the summary of all comments", "PdfCommentsCopy", CopyCommentSummary));
        _commentList.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Setter Property='Margin' Value='6,2' /><Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ListBoxItem'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='6' Padding='8,6' Background='#22FFFFFF'><ContentPresenter /></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#33FFFFFF' /></Trigger>" +
            "<Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='#5B8DEF' /></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        ScrollViewer.SetHorizontalScrollBarVisibility(_commentList, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_commentList, "PdfComments");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_commentHint, "PdfCommentsHint");
        _commentList.SelectionChanged += (_, _) => { if (_commentList.SelectedItem is ListBoxItem { Tag: CommentRow row }) ShowComment(row); };
        _commentList.MouseDoubleClick += (_, _) => { if (_commentList.SelectedItem is ListBoxItem { Tag: CommentRow { Saved: not null } row }) OpenSavedNote(row.Page, row.Saved!); };
        var panel = new DockPanel { Visibility = Visibility.Collapsed };
        DockPanel.SetDock(buttons, Dock.Top); DockPanel.SetDock(_commentHint, Dock.Top);
        panel.Children.Add(buttons); panel.Children.Add(_commentHint); panel.Children.Add(_commentList);
        return panel;
    }

    /// <summary>Reads every page's comments (saved ones from the document, then the ones added in this session that are not saved yet).</summary>
    private async Task RefreshCommentsAsync()
    {
        var pdf = _pdf;
        int generation = ++_commentsGeneration;
        _commentList.Items.Clear(); _comments.Clear();
        if (pdf == null) { _commentHint.Text = ""; return; }
        _commentHint.Text = "Reading the comments…";
        var rows = new List<CommentRow>();
        try
        {
            await Task.Run(() =>
            {
                for (int page = 0; page < pdf.PageCount; page++)
                {
                    if (generation != _commentsGeneration) return;
                    var t = pdf.GetText(page);
                    foreach (var note in t.Notes.Where(n => n.Subtype is Pdfium.AnnotText or Pdfium.AnnotHighlight or Pdfium.AnnotUnderline or Pdfium.AnnotStrikeOut or 3 or 4 or 5 or 6 or 7 or 8 or 11 or 13))
                        rows.Add(new CommentRow(page, KindName(note.Subtype), note.Subtype == Pdfium.AnnotText ? "" : Quote(t, note.Box), note.Text, note));
                }
            });
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { _commentHint.Text = "Couldn't read the comments: " + e.Message; return; }
        if (generation != _commentsGeneration || pdf != _pdf) return;
        foreach (var row in rows) Add(row);
        // comments written in this session and not saved yet
        foreach (var n in _items.OfType<NoteItem>().Where(n => n.Text.Trim().Length > 0)) Add(new CommentRow(n.Page, "Note (not saved yet)", "", n.Text.Trim(), null));
        foreach (var m in _items.OfType<TextMarkupItem>().Where(m => m.Comment.Trim().Length > 0)) Add(new CommentRow(m.Page, KindName(m.Subtype) + " (not saved yet)", Quote(Text(m.Page), m.Bounds), m.Comment.Trim(), null));
        _commentHint.Text = _comments.Count == 0 ? "No comments in this PDF yet. Add a note, or select words and right-click > Add a comment." : $"{_comments.Count} comment{(_comments.Count == 1 ? "" : "s")}. Click to go there, double-click to read or change.";

        void Add(CommentRow row)
        {
            _comments.Add(row);
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = $"Page {row.Page + 1}  ·  {row.Kind}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Soft });
            if (row.Quote.Length > 0) stack.Children.Add(new TextBlock { Text = "“" + row.Quote + "”", FontSize = 11.5, FontStyle = FontStyles.Italic, Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = row.Text, FontSize = 12.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), MaxHeight = 80, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = row.Text });
            _commentList.Items.Add(new ListBoxItem { Content = stack, Tag = row, Foreground = Brushes.White });
        }
    }

    /// <summary>The words under a box (what a highlight is about).</summary>
    private static string Quote(PdfPageText? t, Rect box)
    {
        if (t == null || box.IsEmpty) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < t.Boxes.Length && sb.Length < 120; i++)
        {
            var b = t.Boxes[i];
            if (b.IsEmpty) continue;
            var inter = Rect.Intersect(b, box);
            if (!inter.IsEmpty && inter.Width * inter.Height > b.Width * b.Height * 0.5) sb.Append(t.Text[i]);
        }
        string s = string.Join(" ", sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 110 ? s[..110].TrimEnd() + "…" : s;
    }

    private void ShowComment(CommentRow row)
    {
        GoTo(row.Page);
        if (row.Saved == null || row.Page < 0 || row.Page >= _pages.Count) return;
        var overlay = _pages[row.Page].Overlay;
        double k = 1 / Math.Max(0.01, _pages[row.Page].OverlayScale.ScaleX);
        var box = row.Saved.Box; box.Inflate(4 * k, 4 * k);
        var ring = new Rectangle { Width = box.Width, Height = box.Height, Stroke = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)), StrokeThickness = 2 * k, RadiusX = 4 * k, RadiusY = 4 * k, IsHitTestVisible = false };
        Canvas.SetLeft(ring, box.X); Canvas.SetTop(ring, box.Y);
        overlay.Children.Add(ring);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        timer.Tick += (_, _) => { timer.Stop(); overlay.Children.Remove(ring); };
        timer.Start();
    }

    private string CommentSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Comments in {System.IO.Path.GetFileName(_path)} ({_comments.Count})");
        sb.AppendLine();
        foreach (var c in _comments)
        {
            sb.AppendLine($"Page {c.Page + 1} - {c.Kind}");
            if (c.Quote.Length > 0) sb.AppendLine("  \"" + c.Quote + "\"");
            sb.AppendLine("  " + c.Text.Replace("\n", "\n  "));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void SaveCommentSummary()
    {
        if (_path == null) return;
        if (_comments.Count == 0) { Toast("There are no comments to save"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save the summary of the comments", Filter = "Text|*.txt", FileName = System.IO.Path.GetFileNameWithoutExtension(_path) + " (comments).txt", InitialDirectory = System.IO.Path.GetDirectoryName(_path) };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, CommentSummary(), Encoding.UTF8); Toast("Summary saved"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast("Couldn't save it: " + e.Message); }
    }

    private void CopyCommentSummary()
    {
        if (_comments.Count == 0) { Toast("There are no comments to copy"); return; }
        try { Clipboard.SetText(CommentSummary()); Toast("Summary of the comments copied"); }
        catch (System.Runtime.InteropServices.ExternalException) { Toast("The clipboard is busy, try again"); }
    }
}
