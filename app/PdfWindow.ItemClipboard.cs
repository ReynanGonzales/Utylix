using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Copy, cut, paste and duplicate of what was added on the pages (text, text boxes, check boxes, pictures, stamps, shapes, drawings, notes):
/// Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+D while editing, and Paste in the right-click menu. Inside Utylix only (the text of a copied text also goes to the Windows clipboard).
/// </summary>
public sealed partial class PdfWindow
{
    private List<EditItem> _itemClipboard = new();
    private int _pasteStep;

    private List<EditItem> ChosenItems() => _group.Count > 0 ? _group.ToList() : _selected != null ? new List<EditItem> { _selected } : new List<EditItem>();

    /// <summary>Words that are marked (highlight / underline / strike) belong to their words, and a changed line of the PDF's own text to that line: those are not copied.</summary>
    private static bool Copyable(EditItem item) => item is not (TextMarkupItem or RunEditItem or PageObjectItem or LinkPick or LinkDraft);

    // things that were already in the PDF (picked up with Select): a small PDF holding just them, and where they were
    private byte[]? _objectClip;
    private Rect _objectClipBox;
    private int _objectClipPage;

    private bool CopyItems()
    {
        if (_selected is PageObjectItem picked && _pdf != null)
        {
            try { _objectClip = _pdf.CopyPageObjects(picked.Page, picked.Indices); }
            catch (Exception e) when (e is IOException or InvalidOperationException or OutOfMemoryException or ObjectDisposedException or PdfProtectedException)
            {
                Toast("Couldn't copy it: " + e.Message);
                return false;
            }
            _objectClipBox = picked.Box; _objectClipPage = picked.Page;
            _itemClipboard = new List<EditItem>();                     // (the last copy is the one that is pasted)
            _pasteStep = 0;
            Toast("Copied: Ctrl+V pastes it");
            return true;
        }
        var items = ChosenItems().Where(Copyable).ToList();
        if (items.Count == 0) return false;
        _objectClip = null;
        _itemClipboard = items.Select(i => i.Clone()).ToList();
        _pasteStep = 0;
        if (items.Count == 1 && items[0] is TextItem { Text.Length: > 0 } text)
        {
            try { Clipboard.SetText(text.Text); } catch (Exception e) when (e is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException) { /* the clipboard is busy: the copy inside Utylix still works */ }
        }
        Toast(items.Count == 1 ? "Copied: Ctrl+V pastes it" : $"Copied {items.Count} things: Ctrl+V pastes them");
        return true;
    }

    private void CutItems()
    {
        if (!CopyItems()) return;
        DeleteSelected();
    }

    /// <summary>Pastes what was copied onto the page being looked at: a little below and right of the original on the same page, or where you right-clicked (<paramref name="at"/>).</summary>
    private bool HasCopy => _itemClipboard.Count > 0 || _objectClip != null;

    /// <summary>Pastes things copied from the PDF's own content: as one thing on the page, then chosen so it can be moved.</summary>
    private bool PastePageObjects(Point? at, int? onPage)
    {
        if (_pdf == null || _objectClip == null || _pages.Count == 0) return false;
        int page = Math.Clamp(onPage ?? _current, 0, _pages.Count - 1);
        _pasteStep++;
        var box = _objectClipBox;
        Vector shift = at is Point p ? p - box.TopLeft : _objectClipPage == page ? new Vector(14 * _pasteStep, 14 * _pasteStep) : new Vector(0, 0);
        var size = _sizes.Length > page ? _sizes[page] : new Size(595, 842);
        double right = box.Right + shift.X, bottom = box.Bottom + shift.Y;
        if (right > size.Width) shift.X -= right - size.Width;
        if (bottom > size.Height) shift.Y -= bottom - size.Height;
        if (box.X + shift.X < 0) shift.X = -box.X;
        if (box.Y + shift.Y < 0) shift.Y = -box.Y;
        var clip = _objectClip;
        bool done = PageOp(pdf => pdf.PastePageObjects(page, clip, shift), new[] { page }, "Pasted. Undo takes it away", keepView: true);
        if (!done) return false;
        var last = PageObjects(page).LastOrDefault();                       // (it is the newest thing on the page)
        if (last != null) Select(new PageObjectItem { Page = page, Indices = { last.Index }, Box = last.Box, Original = last.Box });
        return true;
    }

    private bool PasteItems(Point? at = null, int? onPage = null)
    {
        if (_itemClipboard.Count == 0 && _objectClip != null) return PastePageObjects(at, onPage);
        if (_itemClipboard.Count == 0 || _pages.Count == 0) return false;
        int page = Math.Clamp(onPage ?? _current, 0, _pages.Count - 1);
        _pasteStep++;
        var first = _itemClipboard.Select(i => i.Bounds).Where(b => !b.IsEmpty).Aggregate(Rect.Empty, (all, b) => { all.Union(b); return all; });
        Vector shift;
        if (at is Point p && !first.IsEmpty) shift = p - first.TopLeft;
        else shift = _itemClipboard[0].Page == page ? new Vector(14 * _pasteStep, 14 * _pasteStep) : new Vector(0, 0);
        var size = _sizes.Length > page ? _sizes[page] : new Size(595, 842);
        if (!first.IsEmpty)                                                  // (kept on the page)
        {
            double right = first.Right + shift.X, bottom = first.Bottom + shift.Y;
            if (right > size.Width) shift.X -= right - size.Width;
            if (bottom > size.Height) shift.Y -= bottom - size.Height;
            if (first.X + shift.X < 0) shift.X = -first.X;
            if (first.Y + shift.Y < 0) shift.Y = -first.Y;
        }
        if (!_editing) { EnterEditing(); if (!_editing) return false; }
        Snapshot();
        Select(null);
        var pasted = new List<EditItem>();
        var newGroups = new Dictionary<int, int>();                          // (a pasted group is a NEW group, not linked to the one it was copied from)
        foreach (var source in _itemClipboard)
        {
            var copy = source.Clone();
            copy.Page = page;
            if (copy.GroupId != 0)
            {
                if (!newGroups.TryGetValue(copy.GroupId, out int fresh)) newGroups[copy.GroupId] = fresh = NewGroupId() + newGroups.Count;
                copy.GroupId = fresh;
            }
            copy.MoveBy(shift);
            if (copy is FieldItem field)
            {
                if (field.Kind == PdfNewFieldKind.Radio) field.Value = NewRadioValue(field.Name);          // (another button of the same group)
                else field.Name = NewFieldName(field.Kind);
            }
            _items.Add(copy);
            pasted.Add(copy);
        }
        if (pasted.Count == 1) Select(pasted[0]);
        else { _group.AddRange(pasted); UpdateProperties(); }
        RenderItems(page);
        UpdateEditButtons();
        return true;
    }
}
