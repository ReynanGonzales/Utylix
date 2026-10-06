using System;
using System.IO;

namespace IdmClone.Engine;

public sealed partial class PdfFile
{
    /// <summary>
    /// Changes the text of a comment already in the PDF (a sticky note, or the comment on highlighted text), or takes the comment away (<paramref name="text"/> null).
    /// <paramref name="annotIndex"/> is <see cref="PdfNote.Index"/>. It is a change of the open document: Save writes it.
    /// </summary>
    public void SetNote(int pageIndex, int annotIndex, string? text)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr page = Pdfium.FPDF_LoadPage(_doc, pageIndex);
            if (page == IntPtr.Zero) throw new IOException("Page " + (pageIndex + 1) + " can't be changed.");
            try
            {
                if (text == null)
                {
                    if (Pdfium.FPDFPage_RemoveAnnot(page, annotIndex) == 0) throw new IOException("That note isn't there any more.");
                }
                else
                {
                    IntPtr annot = Pdfium.FPDFPage_GetAnnot(page, annotIndex);
                    if (annot == IntPtr.Zero) throw new IOException("That note isn't there any more.");
                    try
                    {
                        Pdfium.FPDFAnnot_SetStringValue(annot, "Contents", text);
                        Pdfium.FPDFAnnot_SetStringValue(annot, "M", "D:" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    finally { Pdfium.FPDFPage_CloseAnnot(annot); }
                }
            }
            finally { Pdfium.FPDF_ClosePage(page); }
            _texts.Remove(pageIndex); _runs.Remove(pageIndex);
        }
    }
}
