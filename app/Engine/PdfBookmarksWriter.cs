using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Pdf;

namespace IdmClone.Engine;

public sealed partial class PdfFile
{
    /// <summary>Replaces the PDF's bookmarks (its table of contents) with these. A change of the open document: Save writes it, Undo takes it back.</summary>
    public void SetBookmarks(IReadOnlyList<PdfBookmark> bookmarks)
    {
        byte[] bytes = SaveToBytes();
        Restore(PdfBookmarksWriter.Write(bytes, bookmarks));
    }
}

internal static class PdfBookmarksWriter
{
    public static byte[] Write(byte[] bytes, IReadOnlyList<PdfBookmark> bookmarks)
    {
        using var input = new MemoryStream(bytes);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        var outlines = doc.Outlines;
        outlines.Clear();
        void AddAll(PdfSharp.Pdf.PdfOutlineCollection parent, IReadOnlyList<PdfBookmark> items)
        {
            foreach (var b in items)
            {
                var page = doc.Pages[Math.Clamp(b.Page < 0 ? 0 : b.Page, 0, doc.PageCount - 1)];
                var outline = parent.Add(string.IsNullOrWhiteSpace(b.Title) ? "(untitled)" : b.Title, page, true);
                if (b.Children.Count > 0) AddAll(outline.Outlines, b.Children);
            }
        }
        AddAll(outlines, bookmarks);
        if (bookmarks.Count > 0) doc.PageMode = PdfPageMode.UseOutlines;
        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }
}
