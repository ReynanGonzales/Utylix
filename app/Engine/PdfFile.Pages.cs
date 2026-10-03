using System;
using System.Windows;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace IdmClone.Engine;

/// <summary>Changing the pages of the open document: turning, moving, deleting, and going back to an earlier state (undo).</summary>
public sealed partial class PdfFile
{
    /// <summary>The pages as they were, so a turned / moved / deleted page can come back: positions and texts read so far are no longer true.</summary>
    private void ForgetPages() { _texts.Clear(); _runs.Clear(); }

    /// <summary>The pieces of upright text of a page, the hidden words of a scan made searchable included (for saving as Word or Excel). Not remembered.</summary>
    public List<PdfTextRun> GetAllTextRuns(int index)
    {
        lock (Pdfium.Sync) { ThrowIfClosed(); return PdfTextRuns.Read(_doc, index, includeHidden: true); }
    }

    /// <summary>Where the pictures of a page are (as shown, points), without the ones that cover most of the page (a scan or a background).</summary>
    public List<Rect> GetPictureBoxes(int index, double maxShareOfPage = 0.55)
    {
        var boxes = new List<Rect>();
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            Pdfium.FPDF_GetPageSizeByIndexF(_doc, index, out var size);
            IntPtr page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) return boxes;
            try
            {
                var map = new PageMapping(page, size.Width, size.Height);
                int count = Pdfium.FPDFPage_CountObjects(page);
                for (int k = 0; k < count; k++)
                {
                    IntPtr obj = Pdfium.FPDFPage_GetObject(page, k);
                    if (obj == IntPtr.Zero || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjImage) continue;
                    if (Pdfium.FPDFPageObj_GetBounds(obj, out float l, out float b, out float r, out float t) == 0) continue;
                    var box = map.ToShown(l, b, r, t);
                    if (box.Width < 12 || box.Height < 12) continue;
                    if (box.Width * box.Height > size.Width * size.Height * maxShareOfPage) continue;
                    boxes.Add(box);
                }
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
        return boxes;
    }

    /// <summary>The text of the pages is read again the next time it is asked for (after something was written into the pages).</summary>
    public void ForgetText() { lock (Pdfium.Sync) ForgetPages(); }

    /// <summary>Turns pages by quarter turns (1 = a quarter clockwise, -1 = counter-clockwise), written into the PDF's /Rotate.</summary>
    public void TurnPages(IEnumerable<int> pages, int quarterTurns)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            foreach (int i in pages.Distinct())
            {
                IntPtr page = Pdfium.FPDF_LoadPage(_doc, i);
                if (page == IntPtr.Zero) throw new IOException("Page " + (i + 1) + " can't be read.");
                try { Pdfium.FPDFPage_SetRotation(page, (((Pdfium.FPDFPage_GetRotation(page) + quarterTurns) % 4) + 4) % 4); }
                finally { Pdfium.FPDF_ClosePage(page); }
            }
            ForgetPages();
        }
    }

    /// <summary>Takes pages out of the document (at least one page must stay).</summary>
    public void DeletePages(IEnumerable<int> pages)
    {
        var list = pages.Distinct().Where(i => i >= 0 && i < PageCount).OrderByDescending(i => i).ToList();
        if (list.Count == 0) return;
        if (list.Count >= PageCount) throw new InvalidOperationException("A PDF needs at least one page.");
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            ExitForm();
            try { foreach (int i in list) Pdfium.FPDFPage_Delete(_doc, i); }
            finally { PageCount = Pdfium.FPDF_GetPageCount(_doc); InitForm(); ForgetPages(); }
        }
    }

    /// <summary>Moves pages so that the first of them (in their present order) ends up at <paramref name="destination"/>; the others follow it.</summary>
    public void MovePages(IEnumerable<int> pages, int destination)
    {
        var list = pages.Distinct().Where(i => i >= 0 && i < PageCount).OrderBy(i => i).ToArray();
        if (list.Length == 0) return;
        destination = Math.Clamp(destination, 0, PageCount - list.Length);
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            ExitForm();
            try { if (Pdfium.FPDF_MovePages(_doc, list, (uint)list.Length, destination) == 0) throw new IOException("The pages couldn't be moved."); }
            finally { PageCount = Pdfium.FPDF_GetPageCount(_doc); InitForm(); ForgetPages(); }
        }
    }

    /// <summary>Writes the document out and reads it in again: pages imported from other documents become its own (those can be closed).</summary>
    public void Reload() => Restore(SaveToBytes());

    /// <summary>Goes back to a state taken with <see cref="SaveToBytes"/> (the same file, same password).</summary>
    public void Restore(byte[] bytes)
    {
        IntPtr data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr doc = Pdfium.FPDF_LoadMemDocument64(data, (UIntPtr)bytes.Length, Password);
            if (doc == IntPtr.Zero) { Marshal.FreeHGlobal(data); throw new IOException("The earlier state of the PDF couldn't be opened."); }
            ExitForm();
            Pdfium.FPDF_CloseDocument(_doc);
            Marshal.FreeHGlobal(_data);
            _doc = doc; _data = data;
            PageCount = Pdfium.FPDF_GetPageCount(_doc);
            InitForm();
            ForgetPages();
        }
    }
}
