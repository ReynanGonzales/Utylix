using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Bring to front / Bring forward / Send backward / Send to back for what is chosen: things added now (their order is the stacking order in the saved page) and things already drawn on the page.</summary>
public sealed partial class PdfWindow
{
    private enum ZMove { ToFront = 0, Forward = 1, Backward = 2, ToBack = 3 }

    private bool CanArrange => _selected != null || _group.Count > 0;

    private void Arrange(ZMove move)
    {
        if (_selected is PageObjectItem picked) { ArrangePageObjects(picked, move); return; }
        var chosen = ChosenItems().Where(i => _items.Contains(i)).ToList();
        if (chosen.Count == 0) return;
        Snapshot();
        var set = new HashSet<EditItem>(chosen);
        var inOrder = _items.Where(set.Contains).ToList();
        switch (move)
        {
            case ZMove.ToFront: _items.RemoveAll(set.Contains); _items.AddRange(inOrder); break;
            case ZMove.ToBack: _items.RemoveAll(set.Contains); _items.InsertRange(0, inOrder); break;
            case ZMove.Forward:
                foreach (var item in Enumerable.Reverse(inOrder))                        // (from the top one down, so a chosen row moves as one)
                {
                    int i = _items.IndexOf(item);
                    int j = _items.FindIndex(i + 1, o => o.Page == item.Page);          // the next thing on the same page
                    if (j < 0 || set.Contains(_items[j])) continue;
                    _items.RemoveAt(i); _items.Insert(j, item);
                }
                break;
            default:
                foreach (var item in inOrder)
                {
                    int i = _items.IndexOf(item);
                    if (i <= 0) continue;
                    int j = _items.FindLastIndex(i - 1, o => o.Page == item.Page);      // the thing just below it on the same page
                    if (j < 0 || set.Contains(_items[j])) continue;
                    _items.RemoveAt(i); _items.Insert(j, item);
                }
                break;
        }
        foreach (int page in chosen.Select(i => i.Page).Distinct().ToList()) RenderItems(page);
        UpdateEditButtons();
    }

    private void ArrangePageObjects(PageObjectItem po, ZMove move)
    {
        int page = po.Page;
        var indices = po.Indices.ToList();
        List<int> after = indices;
        var box = po.Box;
        bool done = PageOp(p => after = p.ReorderPageObjects(page, indices, (int)move), new[] { page },
                           move switch { ZMove.ToFront => "Brought to the front. Undo puts it back", ZMove.ToBack => "Sent to the back. Undo puts it back", ZMove.Forward => "Brought forward. Undo puts it back", _ => "Sent backward. Undo puts it back" },
                           keepView: true);
        if (done) Select(new PageObjectItem { Page = page, Indices = after, Box = box, Original = box });
        else Select(null);
    }
}
