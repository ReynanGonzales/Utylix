using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace IdmClone.Engine;

public sealed partial class PdfFile
{
    /// <summary>
    /// Moves / stretches (<paramref name="matrixFor"/> gives the PDF matrix a b c d e f in page coordinates) or takes away (null) things already on a page by changing the page's own
    /// drawing instructions in place (see <see cref="PdfContentEditor"/>). Nothing else on the page is written again, so soft shadows, gradients and blends stay.
    /// </summary>
    /// <returns>true when it was done; false when it could not be done safely (the document is then untouched and the caller uses the other way).</returns>
    internal bool EditPageContent(int pageIndex, IReadOnlyList<int> objectIndices, Func<PageMapping, double[]>? matrixFor)
    {
        if (IsProtected || objectIndices.Count == 0) return false;
        try
        {
            int count; double[]? matrix = null;
            lock (Pdfium.Sync)
            {
                ThrowIfClosed();
                Pdfium.FPDF_GetPageSizeByIndexF(_doc, pageIndex, out var size);
                IntPtr page = LoadPageOrThrow(pageIndex);
                try
                {
                    count = Pdfium.FPDFPage_CountObjects(page);
                    if (objectIndices.Any(i => i < 0 || i >= count)) return false;
                    if (matrixFor != null) matrix = matrixFor(new PageMapping(page, size.Width, size.Height));
                }
                finally { Pdfium.FPDF_ClosePage(page); }
            }

            byte[] bytes = SaveToBytes();
            using var ms = new MemoryStream(bytes);
            var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
            var pdfPage = doc.Pages[pageIndex];
            var content = new MemoryStream();
            var contents = pdfPage.Contents;
            for (int i = 0; i < contents.Elements.Count; i++)
            {
                if (contents.Elements[i] is PdfSharp.Pdf.Advanced.PdfReference r && r.Value is PdfDictionary d && d.Stream != null)
                {
                    var b = d.Stream.UnfilteredValue;
                    if (b == null || b.Length == 0) return false;
                    content.Write(b, 0, b.Length);
                    content.WriteByte(10);
                }
            }
            if (content.Length == 0) return false;
            byte[]? edited = PdfContentEditor.Apply(content.ToArray(), count, objectIndices, matrix);
            if (edited == null)
            {
                try { File.AppendAllText(System.IO.Path.Combine(App.DataDir, "pdf-open.log"), $"{DateTime.Now:s}  in-place edit declined on page {pageIndex + 1}: {PdfContentEditor.LastReason}{Environment.NewLine}"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return false;
            }

            var stream = new PdfDictionary(doc);
            stream.CreateStream(edited);
            try { stream.Stream!.Value = new PdfSharp.Pdf.Filters.FlateDecode().Encode(edited); stream.Elements.SetName("/Filter", "/FlateDecode"); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or MissingMethodException) { }
            doc.Internals.AddObject(stream);
            pdfPage.Elements["/Contents"] = stream.Reference!;
            using var output = new MemoryStream();
            doc.Save(output, false);
            Restore(output.ToArray());
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.PdfSharpException or ObjectDisposedException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }
}
