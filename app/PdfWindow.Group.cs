using System;
using System.Collections.Generic;
using System.Linq;
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
