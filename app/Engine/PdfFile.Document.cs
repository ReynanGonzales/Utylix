using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace IdmClone.Engine;

/// <summary>What the PDF says about itself (the "Properties" of the document).</summary>
public sealed record PdfProperties(string Title, string Author, string Subject, string Keywords, string Creator, string Producer, string Created, string Modified);

public sealed record PdfAttachment(string Name, int Size);

public enum PdfLabelStyle { Decimal, UpperRoman, LowerRoman, UpperAlpha, LowerAlpha, None }

/// <summary>From page <see cref="From"/> (1-based) on, the pages are numbered like this: a style (1, i, I, a, A or no number), an optional prefix and the number the first of them gets.</summary>
public sealed record PdfLabelRange(int From, PdfLabelStyle Style, string Prefix, int Start);

public sealed partial class PdfFile
{
    private static string WideText(byte[] buffer, uint length) => length <= 2 ? "" : Encoding.Unicode.GetString(buffer, 0, (int)length - 2);

    // ---------- properties ----------
    public PdfProperties GetProperties()
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            string Meta(string tag)
            {
                uint n = Pdfium.FPDF_GetMetaText(_doc, tag, null, 0);
                if (n <= 2) return "";
                var buffer = new byte[n];
                Pdfium.FPDF_GetMetaText(_doc, tag, buffer, n);
                return WideText(buffer, n);
            }
            return new PdfProperties(Meta("Title"), Meta("Author"), Meta("Subject"), Meta("Keywords"), Meta("Creator"), Meta("Producer"), Meta("CreationDate"), Meta("ModDate"));
        }
    }

    /// <summary>Changes the title, author, subject and keywords (the open document; Save writes them). The old XMP copy of them is dropped so no reader shows the old values.</summary>
    public void SetProperties(string title, string author, string subject, string keywords)
    {
        byte[] bytes = SaveToBytes();
        Restore(PdfDocumentWriter.SetProperties(bytes, title, author, subject, keywords));
    }

    // ---------- page labels ----------
    /// <summary>The label of a page as readers show it ("iv", "A-3", "7").</summary>
    public string GetPageLabel(int index)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            uint n = Pdfium.FPDF_GetPageLabel(_doc, index, null, 0);
            if (n <= 2) return "";
            var buffer = new byte[n];
            Pdfium.FPDF_GetPageLabel(_doc, index, buffer, n);
            return WideText(buffer, n);
        }
    }

    /// <summary>The numbering ranges the PDF has (only a flat list is read; empty when there is none or it is built differently).</summary>
    public List<PdfLabelRange> GetPageLabelRanges() => PdfDocumentWriter.ReadLabels(SaveToBytes());

    /// <summary>Numbers the pages from these ranges (empty list = back to plain 1, 2, 3 for every page).</summary>
    public void SetPageLabels(IReadOnlyList<PdfLabelRange> ranges)
    {
        byte[] bytes = SaveToBytes();
        Restore(PdfDocumentWriter.SetLabels(bytes, ranges));
    }

    // ---------- attachments ----------
    public List<PdfAttachment> GetAttachments()
    {
        var list = new List<PdfAttachment>();
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            int count = Pdfium.FPDFDoc_GetAttachmentCount(_doc);
            for (int i = 0; i < count; i++)
            {
                IntPtr a = Pdfium.FPDFDoc_GetAttachment(_doc, i);
                if (a == IntPtr.Zero) continue;
                list.Add(new PdfAttachment(AttachmentName(a), AttachmentSize(a)));
            }
        }
        return list;
    }

    private static string AttachmentName(IntPtr a)
    {
        uint n = Pdfium.FPDFAttachment_GetName(a, null, 0);
        if (n <= 2) return "(no name)";
        var buffer = new byte[n];
        Pdfium.FPDFAttachment_GetName(a, buffer, n);
        return WideText(buffer, n);
    }

    private static int AttachmentSize(IntPtr a) => Pdfium.FPDFAttachment_GetFile(a, null, 0, out uint size) != 0 ? (int)size : 0;

    public byte[] GetAttachmentData(int index)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr a = Pdfium.FPDFDoc_GetAttachment(_doc, index);
            if (a == IntPtr.Zero) throw new IOException("That attached file isn't there any more.");
            if (Pdfium.FPDFAttachment_GetFile(a, null, 0, out uint size) == 0) throw new IOException("The attached file can't be read.");
            var data = new byte[size];
            if (size > 0 && Pdfium.FPDFAttachment_GetFile(a, data, size, out _) == 0) throw new IOException("The attached file can't be read.");
            return data;
        }
    }

    public void AddAttachment(string name, byte[] data)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr a = Pdfium.FPDFDoc_AddAttachment(_doc, name);
            if (a == IntPtr.Zero) throw new IOException("The file can't be attached (a file with that name is probably attached already).");
            if (Pdfium.FPDFAttachment_SetFile(a, _doc, data, (uint)data.Length) == 0) throw new IOException("The file can't be attached.");
        }
    }

    public void RemoveAttachment(int index)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            if (Pdfium.FPDFDoc_DeleteAttachment(_doc, index) == 0) throw new IOException("The attached file can't be removed.");
        }
    }
}

/// <summary>The changes PDFium can't make: document info and page labels (PDFsharp, on the bytes PDFium saved).</summary>
internal static class PdfDocumentWriter
{
    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    private static PdfSharp.Pdf.PdfDocument Open(MemoryStream input) => PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

    public static byte[] SetProperties(byte[] bytes, string title, string author, string subject, string keywords)
    {
        using var input = new MemoryStream(bytes);
        using var doc = Open(input);
        doc.Info.Title = title; doc.Info.Author = author; doc.Info.Subject = subject; doc.Info.Keywords = keywords;
        doc.Internals.Catalog.Elements.Remove("/Metadata");
        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    private static string Code(PdfLabelStyle s) => s switch { PdfLabelStyle.UpperRoman => "/R", PdfLabelStyle.LowerRoman => "/r", PdfLabelStyle.UpperAlpha => "/A", PdfLabelStyle.LowerAlpha => "/a", _ => "/D" };

    public static byte[] SetLabels(byte[] bytes, IReadOnlyList<PdfLabelRange> ranges)
    {
        using var input = new MemoryStream(bytes);
        using var doc = Open(input);
        var catalog = doc.Internals.Catalog;
        if (ranges.Count == 0) catalog.Elements.Remove("/PageLabels");
        else
        {
            var sorted = ranges.OrderBy(r => r.From).ToList();
            if (sorted[0].From > 1) sorted.Insert(0, new PdfLabelRange(1, PdfLabelStyle.Decimal, "", 1));      // (a number tree starts at page 0)
            var nums = new PdfArray(doc);
            int last = 0;
            foreach (var r in sorted)
            {
                int index = Math.Clamp(r.From - 1, 0, Math.Max(0, doc.PageCount - 1));
                if (nums.Elements.Count > 0 && index <= last) continue;
                last = index;
                var d = new PdfDictionary(doc);
                if (r.Style != PdfLabelStyle.None) d.Elements.SetName("/S", Code(r.Style));
                if (!string.IsNullOrEmpty(r.Prefix)) d.Elements.SetString("/P", r.Prefix);
                if (r.Start != 1) d.Elements.SetInteger("/St", Math.Max(1, r.Start));
                nums.Elements.Add(new PdfInteger(index));
                nums.Elements.Add(d);
            }
            var tree = new PdfDictionary(doc);
            tree.Elements["/Nums"] = nums;
            catalog.Elements["/PageLabels"] = tree;
        }
        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    public static List<PdfLabelRange> ReadLabels(byte[] bytes)
    {
        var list = new List<PdfLabelRange>();
        try
        {
            using var input = new MemoryStream(bytes);
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            if (Resolve(doc.Internals.Catalog.Elements["/PageLabels"]) is not PdfDictionary tree || Resolve(tree.Elements["/Nums"]) is not PdfArray nums) return list;
            for (int i = 0; i + 1 < nums.Elements.Count; i += 2)
            {
                if (Resolve(nums.Elements[i]) is not PdfInteger index || Resolve(nums.Elements[i + 1]) is not PdfDictionary d) continue;
                string s = d.Elements.GetName("/S");
                var style = s switch { "/R" => PdfLabelStyle.UpperRoman, "/r" => PdfLabelStyle.LowerRoman, "/A" => PdfLabelStyle.UpperAlpha, "/a" => PdfLabelStyle.LowerAlpha, "/D" => PdfLabelStyle.Decimal, _ => PdfLabelStyle.None };
                int start = d.Elements.ContainsKey("/St") ? d.Elements.GetInteger("/St") : 1;
                list.Add(new PdfLabelRange(index.Value + 1, style, d.Elements.GetString("/P") ?? "", start));
            }
        }
        catch (Exception e) when (e is PdfSharp.Pdf.IO.PdfReaderException or InvalidCastException or NotImplementedException or NullReferenceException or ArgumentException) { list.Clear(); }
        return list;
    }
}
