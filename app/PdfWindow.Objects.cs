using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Things that are already in the PDF - what was added in an earlier session and saved (text, check marks, pictures, stamps, drawings), or any drawn part of the page - can be picked with the
/// Select tool: click one (or drag a box round several), then move it, resize it by the corner, or delete it (Undo takes it back). Saved text boxes and check boxes made here are picked up as
/// fields again, so they can also be moved, resized, recoloured, copied and deleted. Changes go into the open document at once (like page changes); Save writes them.
/// </summary>
public sealed partial class PdfWindow
{
    /// <summary>The choice of one or more things already drawn on a page (their places in the page's list); the frame follows the mouse, the change is made when the mouse is let go.</summary>
    private sealed class PageObjectItem : EditItem
    {
        public List<int> Indices = new();
        public Rect Box;
        public Rect Original;
        public override Rect Bounds => Box;
        public override EditItem Clone() { var c = (PageObjectItem)MemberwiseClone(); c.Indices = Indices.ToList(); return c; }
        public override FrameworkElement Build() => new Canvas();
        public override IEnumerable<PdfMark> Marks() { yield break; }
        public override void MoveBy(Vector d) => Box.Offset(d);
        public override void ResizeTo(Rect r) => Box = r;
    }

    private readonly Dictionary<int, List<PdfOwnField>> _ownFields = new();
    private bool _fieldsLifted;                      // a saved field was picked up: Save tidies the form

    private static Rect Grow(Rect r, double by) { r.Inflate(by, by); return r; }

    /// <summary>Parts of the page that are a background (most of the page): not for picking, or a click anywhere would take the whole page.</summary>
    private bool IsBackdrop(PdfPageObject o, int page)
    {
        var s = page < _sizes.Length ? _sizes[page] : new Size(595, 842);
        return o.Box.Width * o.Box.Height > 0.5 * s.Width * s.Height;
    }

    private List<PdfPageObject> PageObjects(int page)
    {
        try { return _pdf?.GetPageObjects(page) ?? new List<PdfPageObject>(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { return new List<PdfPageObject>(); }
    }

    /// <summary>The top thing drawn at this spot, or null.</summary>
    private PageObjectItem? PickPageObject(int page, Point p)
    {
        var hit = PageObjects(page).LastOrDefault(o => !IsBackdrop(o, page) && o.Box.Width + o.Box.Height > 1 && Grow(o.Box, 2).Contains(p));
        return hit == null ? null : new PageObjectItem { Page = page, Indices = { hit.Index }, Box = hit.Box, Original = hit.Box };
    }

    /// <summary>Everything drawn completely inside a box (a drag on empty paper), as one choice; null when there is nothing.</summary>
    private PageObjectItem? PickPageObjectsIn(int page, Rect area)
    {
        var inside = PageObjects(page).Where(o => !IsBackdrop(o, page) && o.Box.Width + o.Box.Height > 1 && area.Contains(o.Box)).ToList();
        if (inside.Count == 0) return null;
        var all = Rect.Empty;
        foreach (var o in inside) all.Union(o.Box);
        return new PageObjectItem { Page = page, Indices = inside.Select(o => o.Index).ToList(), Box = all, Original = all };
    }

    /// <summary>The mouse is let go after moving or resizing a picked thing: the change goes into the document.</summary>
    private void CommitPageObject(PageObjectItem po)
    {
        var from = po.Original; var to = po.Box;
        int page = po.Page;
        var indices = po.Indices.ToList();
        bool same = Math.Abs(to.X - from.X) < 0.05 && Math.Abs(to.Y - from.Y) < 0.05 && Math.Abs(to.Width - from.Width) < 0.05 && Math.Abs(to.Height - from.Height) < 0.05;
        if (same) { RenderItems(page); return; }
        bool moved = Math.Abs(to.Width - from.Width) < 0.05 && Math.Abs(to.Height - from.Height) < 0.05;
        bool done = PageOp(p => { if (moved) p.MovePageObjects(page, indices, to.TopLeft - from.TopLeft); else p.ScalePageObjects(page, indices, from, to); },
                           new[] { page }, moved ? "Moved. Undo puts it back" : "Resized. Undo puts it back", keepView: true);
        if (done) Select(new PageObjectItem { Page = page, Indices = indices, Box = to, Original = to });      // (still chosen, where it is now)
        else Select(null);
    }

    private void DeletePageObjects(PageObjectItem po)
    {
        int page = po.Page;
        var indices = po.Indices.ToList();
        Select(null);
        PageOp(p => p.RemovePageObjects(page, indices), new[] { page }, "Deleted. Undo brings it back", keepView: true);
    }

    // ---------- saved text boxes and check boxes ----------
    private List<PdfOwnField> OwnFields(int page)
    {
        if (_pdf == null || !_pdf.HasForm) return new List<PdfOwnField>();
        if (_ownFields.TryGetValue(page, out var list)) return list;
        try { list = _pdf.GetOwnFields(page); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { list = new List<PdfOwnField>(); }
        _ownFields[page] = list;
        return list;
    }

    private PdfOwnField? OwnFieldAt(int page, Point p) => OwnFields(page).LastOrDefault(f => Grow(f.Box, 2).Contains(p));

    /// <summary>Picks a saved field up: it leaves the page's fields and comes back as a text box / check box you can move, resize, copy and delete (Save makes it a field again).</summary>
    private void LiftOwnField(int page, PdfOwnField f)
    {
        if (_pdf == null) return;
        bool done = PageOp(p => p.SetNote(page, f.AnnotIndex, null), new[] { page }, f.Kind == PdfNewFieldKind.Text ? "Text box picked up: move or resize it, copy it, delete it. Save makes it a field again" : "Check box picked up: move it, copy it, delete it. Save makes it a field again", keepView: true);
        if (!done) return;
        _fieldsLifted = true;
        var item = new FieldItem { Page = page, Kind = f.Kind, Box = f.Box, Name = f.Name, FontSize = f.FontSize, Font = f.Font, Bold = f.Bold, Color = f.Color };
        _items.Add(item);                                                // (no step of its own: Undo goes back to the page as it was, with the field on it)
        Select(item);
        RenderItems(page);
    }
}
