using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace IdmClone.Engine;

/// <summary>See <see cref="PdfSpacingBake"/>: done once for the open document, before anything on a page is changed.</summary>
public sealed partial class PdfFile
{
    private bool _spacingBaked;
    private const long BakeSizeLimit = 120L * 1024 * 1024;

    /// <summary>
    /// Makes the character and word spacing of the document's text survive the rewriting of pages (see <see cref="PdfSpacingBake"/>). Does nothing when the document
    /// has no such spacing, is protected, or is very large, and never fails: when anything is not understood the document stays as it is.
    /// Call it before text is read for editing (the words of letter-spaced text read a little differently afterwards) and before a page is changed.
    /// </summary>
    /// <returns>true when the document was changed (its pages look exactly the same).</returns>
    public bool BakeTextSpacing()
    {
        if (_spacingBaked) return false;
        _spacingBaked = true;
        if (IsProtected) return false;
        try
        {
            byte[] bytes = SaveToBytes();
            if (bytes.Length > BakeSizeLimit) return false;
            using var ms = new MemoryStream(bytes);
            var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
            bool any = false;
            for (int p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                if (!HasSpacingOperators(page)) continue;
                var content = new MemoryStream();
                var contents = page.Contents;
                bool readable = true;
                for (int i = 0; i < contents.Elements.Count; i++)
                {
                    if (contents.Elements[i] is PdfSharp.Pdf.Advanced.PdfReference r && r.Value is PdfDictionary d && d.Stream != null)
                    {
                        var b = d.Stream.UnfilteredValue;
                        if (b == null || b.Length == 0) { readable = false; break; }
                        content.Write(b, 0, b.Length);
                        content.WriteByte(10);
                    }
                }
                if (!readable || content.Length == 0) continue;
                var fonts = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/Font");
                int BytesPerCode(string name)
                {
                    var f = fonts?.Elements.GetDictionary(name);
                    if (f == null) return 0;
                    if (f.Elements.GetName("/Subtype") != "/Type0") return 1;
                    string enc = f.Elements.GetName("/Encoding");
                    return enc.EndsWith("-V", StringComparison.Ordinal) ? -1 : 2;
                }
                byte[]? rewritten;
                try { rewritten = PdfSpacingBake.Rewrite(content.ToArray(), BytesPerCode); }
                catch (Exception e) when (e is InvalidOperationException or IndexOutOfRangeException or ArgumentException or FormatException) { rewritten = null; }
                if (rewritten == null) continue;
                var stream = new PdfDictionary(doc);
                stream.CreateStream(rewritten);
                try { stream.Stream!.Value = new PdfSharp.Pdf.Filters.FlateDecode().Encode(rewritten); stream.Elements.SetName("/Filter", "/FlateDecode"); }     // (the page's text is small, and this keeps the file from growing)
                catch (Exception e) when (e is NotSupportedException or InvalidOperationException or MissingMethodException) { }
                doc.Internals.AddObject(stream);
                page.Elements["/Contents"] = stream.Reference!;
                any = true;
            }
            if (!any) return false;
            using var outMs = new MemoryStream();
            doc.Save(outMs, false);
            Restore(outMs.ToArray());
            _spacingBaked = true;                                  // (Restore says "a different document": this one is done)
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.PdfSharpException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return false;                                          // (the document stays as it is)
        }
    }

    /// <summary>A quick look at whether the page's content has any spacing operator at all (the content is read and tokenized only for pages that do).</summary>
    private static bool HasSpacingOperators(PdfPage page)
    {
        try
        {
            var contents = page.Contents;
            for (int i = 0; i < contents.Elements.Count; i++)
            {
                if (contents.Elements[i] is not PdfSharp.Pdf.Advanced.PdfReference r || r.Value is not PdfDictionary d || d.Stream == null) continue;
                var b = d.Stream.UnfilteredValue;
                if (b == null) continue;
                for (int k = 2; k < b.Length; k++)
                    if ((b[k] == (byte)'c' || b[k] == (byte)'w') && b[k - 1] == (byte)'T' && (b[k - 2] is 32 or 10 or 13 or 9 or (byte)'0' or (byte)'1' or (byte)'2' or (byte)'3' or (byte)'4' or (byte)'5' or (byte)'6' or (byte)'7' or (byte)'8' or (byte)'9' or (byte)'.'))
                        return true;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { return true; }
        return false;
    }
}
