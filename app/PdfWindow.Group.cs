using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Group / Ungroup (Select tool; right-click, Ctrl+Shift+G / Ctrl+Shift+U): things chosen together are linked, so a click on any one of them chooses them all and they move,
/// copy and delete together. A group of things added in this session is kept until you save (saving flattens them into the page). Things that are already saved in the PDF
/// (a box round them with Select) are grouped by making them ONE thing on the page, which stays grouped in the file; that can't be taken apart again.
/// </summary>
public sealed partial class PdfWindow
{
    private int NewGroupId() => (_items.Count == 0 ? 0 : _items.Max(i => i.GroupId)) + 1;

    /// <summary>The chosen things that can be linked: what was added now (not the frames of picked page content, links or the box being dragged).</summary>
    private List<EditItem> GroupableChosen() => ChosenItems().Where(i => _items.Contains(i) && i is not (LinkDraft or LinkPick or PageObjectItem)).ToList();

    private bool CanGroup => _editing && (_selected is PageObjectItem { Indices.Count: > 1 } || GroupableChosen().Count >= 2);

    private bool CanUngroup => _editing && ChosenItems().Any(i => i.GroupId != 0);

    /// <summary>Everything linked to the given things (the whole of every group they belong to).</summary>
    private List<EditItem> WithGroupMates(IEnumerable<EditItem> items)
    {
        var list = items.ToList();
        var ids = new HashSet<int>(list.Where(i => i.GroupId != 0).Select(i => i.GroupId));
        if (ids.Count == 0) return list;
        foreach (var mate in _items.Where(i => ids.Contains(i.GroupId) && !list.Contains(i))) list.Add(mate);
        return list;
    }

    /// <summary>Chooses an item; one that is in a group chooses the whole group (so the mouse can drag them all).</summary>
    private void ChooseItem(EditItem item)
    {
        if (item.GroupId == 0) { Select(item); return; }
        var mates = WithGroupMates(new[] { item });
        Select(null);
        _group.AddRange(mates);
        foreach (int page in mates.Select(i => i.Page).Distinct().ToList()) RenderItems(page);
        UpdateEditButtons(); UpdateProperties();
    }

    /// <summary>
    /// Shift + click (or Ctrl + click) with Select adds the thing under the pointer to what is chosen, or takes it out again if it is chosen already (a grouped thing brings its whole group).
    /// True when the click was used. Works for things added now, and for things already drawn on the page (they have to be on the same page).
    /// </summary>
    private bool ExtendChoice(PageView pv, Point p)
    {
        if (_selected is LinkPick) return false;
        var item = ItemAt(pv.Index, p);
        if (item != null)
        {
            if (_selected is PageObjectItem) { Toast("Things already in the PDF and things you added can't be chosen together"); return true; }
            var chosen = ChosenItems().Where(i => _items.Contains(i)).ToList();
            var mates = WithGroupMates(new[] { item });
            if (chosen.Contains(item)) chosen.RemoveAll(mates.Contains);
            else foreach (var m in mates) if (!chosen.Contains(m)) chosen.Add(m);
            var pages = chosen.Concat(mates).Select(i => i.Page).Distinct().ToList();
            Select(null);
            if (chosen.Count == 1) Select(chosen[0]);
            else if (chosen.Count > 1) _group.AddRange(chosen);
            foreach (int page in pages) RenderItems(page);
            UpdateEditButtons(); UpdateProperties();
            return true;
        }
        if (_pdf == null || _group.Count > 0) return false;
        var objects = PageObjects(pv.Index);
        var hit = objects.LastOrDefault(o => !IsBackdrop(o, pv.Index) && o.Box.Width + o.Box.Height > 1 && Grow(o.Box, 2).Contains(p));
        if (hit == null) return false;                                          // (nothing there: the click works as it always did)
        List<int> indices = _selected is PageObjectItem po && po.Page == pv.Index ? po.Indices.ToList() : new List<int>();
        if (!indices.Remove(hit.Index)) indices.Add(hit.Index);
        if (indices.Count == 0) { Select(null); return true; }
        var box = Rect.Empty;
        foreach (var o in objects.Where(o => indices.Contains(o.Index))) box.Union(o.Box);
        Select(new PageObjectItem { Page = pv.Index, Indices = indices, Box = box, Original = box });
        return true;
    }

    /// <summary>The pointer is inside the frame round the chosen group (between its things).</summary>
    private bool InGroupFrame(int page, Point p)
    {
        var frame = Rect.Empty;
        foreach (var g in _group.Where(i => i.Page == page && !i.Bounds.IsEmpty)) frame.Union(g.Bounds);
        return !frame.IsEmpty && Grow(frame, 3).Contains(p);
    }

    /// <summary>
    /// Things already in the PDF are chosen and the pointer is on one of them (or between them, inside their frame): a drag moves the whole choice.
    /// (Without this a click on one of two chosen things picked only that one.) False when the pointer is on some other thing: that click works as always.
    /// </summary>
    private bool SavedChoiceUnder(int page, Point p, PageObjectItem choice)
    {
        if (choice.Page != page || !Grow(choice.Box, 3).Contains(p)) return false;
        var under = PageObjects(page).LastOrDefault(o => !IsBackdrop(o, page) && o.Box.Width + o.Box.Height > 1 && Grow(o.Box, 2).Contains(p));
        return under == null || choice.Indices.Contains(under.Index);
    }

    private void GroupChosen()
    {
        if (_selected is PageObjectItem po)
        {
            if (po.Indices.Count > 1) GroupPageObjects(po);
            else Toast("That is one thing already. Drag a box round several things to group them");
            return;
        }
        var chosen = WithGroupMates(GroupableChosen());
        if (GroupableChosen().Count < 2) { Toast("Choose two or more things first: with Select, drag a box round them"); return; }
        Snapshot();
        int id = NewGroupId();
        foreach (var i in chosen) i.GroupId = id;
        _group.Clear(); _group.AddRange(chosen);
        _selected = null;
        foreach (int page in chosen.Select(i => i.Page).Distinct().ToList()) RenderItems(page);
        UpdateEditButtons(); UpdateProperties();
        Toast($"Grouped {chosen.Count} things: click one and they all move together. (Until you save: after saving, group them again with a box round them)");
    }

    private void UngroupChosen()
    {
        var chosen = WithGroupMates(ChosenItems().Where(i => _items.Contains(i)));
        if (!chosen.Any(i => i.GroupId != 0)) { Toast("They are not grouped"); return; }
        Snapshot();
        foreach (var i in chosen) i.GroupId = 0;
        foreach (int page in chosen.Select(i => i.Page).Distinct().ToList()) RenderItems(page);
        UpdateEditButtons();
        Toast("Ungrouped: they move one by one again");
    }

    /// <summary>Things already in the PDF, chosen together: they become one thing on the page (a copy of them goes back in the same place, in front).</summary>
    private void GroupPageObjects(PageObjectItem po)
    {
        if (_pdf == null) return;
        int page = po.Page;
        var indices = po.Indices.ToList();
        byte[] clip;
        try { clip = _pdf.CopyPageObjects(page, indices); }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException or OutOfMemoryException or ObjectDisposedException or PdfProtectedException)
        {
            Toast("Couldn't group them: " + e.Message);
            return;
        }
        Select(null);
        bool done = PageOp(p => { p.RemovePageObjects(page, indices); p.PastePageObjects(page, clip, new System.Windows.Vector(0, 0)); }, new[] { page },
                           $"Grouped {indices.Count} things into one: click it and they all move together. Undo takes it apart until you save", keepView: true);
        if (!done) return;
        var last = PageObjects(page).LastOrDefault();
        if (last != null) Select(new PageObjectItem { Page = page, Indices = { last.Index }, Box = last.Box, Original = last.Box });
    }
}
