using System.Collections.Generic;
using System.Windows;

namespace IdmClone;

/// <summary>Pictures dropped on the editor window go onto the page they were dropped on (one after the other, a little apart), ready to move and resize.</summary>
public sealed partial class PdfWindow
{
    private void DropPictures(List<string> files, DragEventArgs e)
    {
        if (_pdf == null || _path == null || _pages.Count == 0) { Toast("Open a PDF first, then drop the picture on a page"); return; }
        if (!_editing) EnterEditing();
        if (!_editing) return;                                       // (a password was refused, or editing is not allowed)

        // the page under the mouse (else the one being looked at)
        PageView? page = null; Point where = default;
        foreach (var pv in _pages)
        {
            var p = e.GetPosition(pv.Overlay);
            if (p.X >= 0 && p.Y >= 0 && p.X <= pv.Overlay.Width && p.Y <= pv.Overlay.Height) { page = pv; where = p; break; }
        }
        int n = 0;
        foreach (string file in files)
        {
            if (page == null) AddPictureFile(file, null, null);
            else AddPictureFile(file, page, new Point(where.X + n * 24, where.Y + n * 24));
            n++;
        }
    }
}
