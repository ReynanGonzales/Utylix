using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Links: the Link tool (Add tab, Alt+J) drags a box and asks where it goes - a web address or a page of the PDF; or select words and right-click > "Make the selected words a link".
/// While editing with Select or Link, the links of the PDF are outlined in blue: click one to choose it, Enter (or a double-click) changes where it goes, Delete removes it.
/// A link is put into the open document at once (like page changes), so Undo takes it back and Save writes it.
/// </summary>
public sealed partial class PdfWindow
{
    /// <summary>The box being dragged for a new link.</summary>
    private sealed class LinkDraft : EditItem
    {
        public Rect Box;
        public override Rect Bounds => Box;
        public override EditItem Clone() => (LinkDraft)MemberwiseClone();
        public override IEnumerable<PdfMark> Marks() { yield break; }
        public override void MoveBy(Vector d) => Box.Offset(d);
        public override void ResizeTo(Rect r) => Box = r;
        public override FrameworkElement Build()
        {
            var r = new Rectangle { Width = Box.Width, Height = Box.Height, Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA)), StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = new SolidColorBrush(Color.FromArgb(40, 0x2F, 0x6B, 0xEA)) };
            Canvas.SetLeft(r, Box.X); Canvas.SetTop(r, Box.Y);
            return r;
        }
    }

    /// <summary>A link of the PDF that was clicked in edit mode (not made of items: it is in the document).</summary>
    private sealed class LinkPick : EditItem
    {
        public PdfLink Link = null!;
        public override Rect Bounds => Link.Box;
        public override EditItem Clone() => (LinkPick)MemberwiseClone();
        public override FrameworkElement Build() => new Canvas();
        public override IEnumerable<PdfMark> Marks() { yield break; }
        public override void MoveBy(Vector d) { }
        public override void ResizeTo(Rect r) { }
    }

    private readonly HashSet<int> _outlined = new();                         // the pages whose link outlines were drawn since the tool / the pages last changed

    /// <summary>The tool or the pages changed: the outlines are drawn again for the pages in view.</summary>
    private void OutlinesChanged()
    {
        _outlined.Clear();
        _renderTimer.Stop(); _renderTimer.Start();
    }

    private PdfLink? AnnotLinkAt(int page, Point p) => Text(page)?.Links.LastOrDefault(l => l.Annotation && Grow(l.Box, 1).Contains(p));

    /// <summary>The outlines of the links of a page, drawn on its editing layer while Select or Link is the tool.</summary>
    private void DrawLinkOutlines(int page, Canvas overlay)
    {
        if (!_editing || _tool is not (EditTool.Select or EditTool.Link) || Text(page) is not { } text) return;
        var blue = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA));
        foreach (var link in text.Links.Where(l => l.Annotation))
        {
            var outline = new Rectangle { Width = link.Box.Width, Height = link.Box.Height, Stroke = blue, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 }, Fill = new SolidColorBrush(Color.FromArgb(22, 0x2F, 0x6B, 0xEA)), IsHitTestVisible = false };
            Canvas.SetLeft(outline, link.Box.X); Canvas.SetTop(outline, link.Box.Y);
            overlay.Children.Add(outline);
        }
    }

    /// <summary>Where should the link go? Null = cancelled; Uri set = a web address, else Page (0-based).</summary>
    private (string? Uri, int Page)? AskLink(string? uri, int page)
    {
        var dlg = new Window
        {
            Title = "Link", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("BgBrush"), Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        WindowTheme.DarkTitleBar(dlg);
        var chip = (Style)Application.Current.FindResource("ChipButton");
        var web = new RadioButton { Content = "A web address", Style = chip, GroupName = "linkkind", IsChecked = page < 0 };
        var inside = new RadioButton { Content = "A page of this PDF", Style = chip, GroupName = "linkkind", IsChecked = page >= 0 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(web, "PdfLinkWeb");
        System.Windows.Automation.AutomationProperties.SetAutomationId(inside, "PdfLinkPage");
        var address = new TextBox { Text = uri ?? "https://", Margin = new Thickness(0, 12, 0, 0) };
        var pageBox = new TextBox { Text = page >= 0 ? (page + 1).ToString() : (_current + 1).ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(address, "PdfLinkAddress");
        System.Windows.Automation.AutomationProperties.SetAutomationId(pageBox, "PdfLinkPageNumber");
        void Sync() { address.Visibility = web.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; pageBox.Visibility = web.IsChecked == true ? Visibility.Collapsed : Visibility.Visible; }
        web.Checked += (_, _) => Sync(); inside.Checked += (_, _) => Sync();
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "PdfLinkOk");
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; dlg.Close(); };
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        kinds.Children.Add(web); kinds.Children.Add(inside);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Where does the link go?", FontWeight = FontWeights.SemiBold });
        root.Children.Add(kinds);
        root.Children.Add(address); root.Children.Add(pageBox);
        root.Children.Add(new TextBlock { Text = "Clicking it opens the address, or jumps to the page, in any PDF reader.", Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        root.Children.Add(buttons);
        dlg.Content = root;
        Sync();
        dlg.Loaded += (_, _) => { if (web.IsChecked == true) { address.Focus(); address.SelectAll(); } else { pageBox.Focus(); pageBox.SelectAll(); } };
        dlg.ShowDialog();
        if (!accepted) return null;
        if (web.IsChecked == true)
        {
            string a = address.Text.Trim();
            if (a.Length == 0 || a == "https://") return null;
            if (!a.Contains("://") && !a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) a = a.Contains('@') && !a.Contains('/') ? "mailto:" + a : "https://" + a;
            return (a, -1);
        }
        if (!int.TryParse(pageBox.Text.Trim(), out int n) || _pdf == null) return null;
        return (null, Math.Clamp(n, 1, _pdf.PageCount) - 1);
    }

    private void AddLinkOn(int page, IReadOnlyList<Rect> boxes)
    {
        if (_pdf == null || boxes.Count == 0) return;
        if (AskLink(null, -1) is not { } target) return;
        PageOp(p => p.AddLinks(page, boxes, target.Uri, target.Page), new[] { page },
               target.Uri != null ? "Link made: it opens " + target.Uri + ". Undo takes it back until you save" : $"Link made: it goes to page {target.Page + 1}. Undo takes it back until you save", keepView: true);
    }

    /// <summary>The mouse is let go after dragging the box of a new link.</summary>
    private void FinishLink(LinkDraft draft)
    {
        var box = new Rect(draft.Box.TopLeft, draft.Box.BottomRight);
        RenderItems(draft.Page);
        if (box.Width < 8 || box.Height < 8) { Toast("Drag a box where the link should be"); return; }
        AddLinkOn(draft.Page, new[] { box });
    }

    /// <summary>Selected words become a link (one box for each line of them).</summary>
    private void LinkSelection()
    {
        if (!HasSelection || Text(_selPage) is not { } t) return;
        var (s, e) = SelectionRange;
        int page = _selPage;
        var rects = t.LineBoxes(s, e).Where(r => !r.IsEmpty).ToList();
        ClearTextSelection();
        AddLinkOn(page, rects);
    }

    private void EditLink(LinkPick pick)
    {
        if (_pdf == null) return;
        if (AskLink(pick.Link.Uri, pick.Link.Annotation && pick.Link.Uri == null ? pick.Link.Page : -1) is not { } target) return;
        int page = pick.Page; var box = pick.Link.Box;
        Select(null);
        PageOp(p => { p.RemoveLink(page, box); p.AddLinks(page, new[] { box }, target.Uri, target.Page); }, new[] { page },
               target.Uri != null ? "Link changed: it opens " + target.Uri : $"Link changed: it goes to page {target.Page + 1}", keepView: true);
    }

    private void DeleteLink(LinkPick pick)
    {
        if (_pdf == null) return;
        int page = pick.Page; var box = pick.Link.Box;
        Select(null);
        PageOp(p => p.RemoveLink(page, box), new[] { page }, "Link removed. Undo takes it back until you save", keepView: true);
    }
}
