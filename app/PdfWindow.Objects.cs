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

    /// <summary>
    /// Before editing starts: the document's character and word spacing is turned into position adjustments that survive PDFium rewriting a page (see <see cref="PdfSpacingBake"/>),
    /// so lines that are not touched do not come out narrower or wider. Nothing visible changes; a document without such spacing is left alone.
    /// </summary>
    private void BakeSpacingForEditing()
    {
        if (_pdf == null) return;
        try
        {
            if (_pdf.BakeTextSpacing()) RefreshAfterPageChange(Array.Empty<int>(), keepView: true);      // (the document was swapped for the same one: drawn again, the view stays where it was)
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { /* the document stays as it is */ }
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

    /// <summary>Is the point on the thing? An outline with nothing inside (a revision cloud) counts only near its edge, so what lies inside it can still be picked.</summary>
    private static bool HitsObject(PdfPageObject o, Point p)
    {
        if (!Grow(o.Box, 2).Contains(p)) return false;
        if (!o.Hollow || o.Box.Width < 28 || o.Box.Height < 28) return true;
        return !Grow(o.Box, -9).Contains(p);
    }

    /// <summary>The top thing drawn at this spot, or null. Comments of the PDF (callouts, clouds, strike-outs) lie on top of the page, so they are met first.</summary>
    private PageObjectItem? PickPageObject(int page, Point p)
    {
        var hit = PageObjects(page).LastOrDefault(o => !IsBackdrop(o, page) && o.Box.Width + o.Box.Height > 1 && HitsObject(o, p));
        return hit == null ? null : new PageObjectItem { Page = page, Indices = { hit.Index }, Box = hit.Box, Original = hit.Box };
    }

    /// <summary>The chosen thing when it is exactly one callout (free text comment) of the PDF: its place in the page's comments.</summary>
    private bool TryChosenFreeText(out int annotIndex)
    {
        annotIndex = -1;
        if (!_editing || _selected is not PageObjectItem { Indices.Count: 1 } po || !PdfFile.IsAnnotation(po.Indices[0])) return false;
        var obj = PageObjects(po.Page).FirstOrDefault(o => o.Index == po.Indices[0]);
        if (obj == null || PdfFile.AnnotationSubtype(obj) != PdfFile.SubtypeFreeText) return false;
        annotIndex = po.Indices[0] - PdfFile.AnnotationBase;
        return true;
    }

    /// <summary>
    /// A callout of the PDF becomes a text box of Utylix: the comment leaves the PDF's comments and its words come back as text you can change, move, resize and delete
    /// (it is page text once saved). The look is read from the comment: size and colour.
    /// </summary>
    private void EditFreeTextComment()
    {
        if (_pdf == null || !TryChosenFreeText(out int annot) || _selected is not PageObjectItem po) return;
        int page = po.Page;
        PdfFreeText? info;
        try { info = _pdf.ReadFreeText(page, annot); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { Toast("That comment can't be read: " + e.Message); return; }
        if (info == null) { Toast("That comment can't be changed here (use Delete to remove it)"); return; }
        Select(null);
        bool done = PageOp(p => p.SetNote(page, annot, null), new[] { page }, "The comment's words are now a text box: change them, move them, or delete them. Undo puts the comment back", keepView: true);
        if (!done) return;
        var item = new TextItem
        {
            Page = page, Text = info.Text, FontSize = info.Size, Color = info.Color, Font = PdfFontKind.Sans,
            TopLeft = new Point(info.Box.X + 2, info.Box.Y + 2), BoxWidth = Math.Max(20, info.Box.Width + 14),      // (a little room: the font here is not exactly the one the comment was written in)
        };
        _items.Add(item);                                             // (no step of its own: Undo goes back to the page as it was, with the comment on it)
        _dirty = true; UpdateTitle(); UpdateEditButtons();
        Select(item);
        RenderItems(page);
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
        bool done = PageOp(p => p.SetNote(page, f.AnnotIndex, null), new[] { page }, f.Kind switch { PdfNewFieldKind.Text => "Text box", PdfNewFieldKind.Signature => "Signature box", PdfNewFieldKind.Radio => "Option", PdfNewFieldKind.Dropdown => "Drop-down list", _ => "Check box" } + " picked up: move it, resize it, copy it, delete it. Save makes it a field again", keepView: true);
        if (!done) return;
        _fieldsLifted = true;
        var x = f.Extra ?? new PdfFieldExtra();
        var item = new FieldItem
        {
            Page = page, Kind = f.Kind, Box = f.Box, Name = f.Name, Value = f.Value, FontSize = f.FontSize, Font = f.Font, Bold = f.Bold, Color = f.Color,
            Required = x.Required, Multiline = x.Multiline, MaxLength = x.MaxLength, DefaultText = x.DefaultText, Choices = x.Choices?.ToList() ?? new List<string>(), Ticked = x.Ticked,
            Format = x.Format, Calc = x.Calc,
        };
        _items.Add(item);                                                // (no step of its own: Undo goes back to the page as it was, with the field on it)
        Select(item);
        RenderItems(page);
    }
}
