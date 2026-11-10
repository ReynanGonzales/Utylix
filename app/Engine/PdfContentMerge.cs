using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace IdmClone.Engine;

/// <summary>
/// Puts what was added to a page (new text, shapes, pictures, drawn on a page of its own by PDFium) AFTER the page's own drawing instructions, and takes away or moves the things
/// that were changed, in place (<see cref="PdfContentEditor"/>). The page's own instructions are never written again, so soft shadows, gradient masks, blends, letter spacing and
/// everything else PDFium has no place for stay exactly as they were (writing the whole page again turned such parts black and moved lines of text).
/// </summary>
internal static class PdfContentMerge
{
    /// <param name="PageIndex">the page that changes</param>
    /// <param name="ScratchIndex">the page (at the end of the document) that holds the new drawing</param>
    /// <param name="ObjectCount">how many objects PDFium counts on the real page (the instructions must agree)</param>
    /// <param name="Deletes">objects to take out of the page's instructions</param>
    /// <param name="Moves">objects to move in place (offset in page space)</param>
    public sealed record Job(int PageIndex, int ScratchIndex, int ObjectCount, IReadOnlyList<int> Deletes, IReadOnlyList<(int Index, double Dx, double Dy)> Moves);

    public static string LastReason { get; private set; } = "";

    private static readonly string[] Categories = { "/Font", "/XObject", "/ExtGState", "/Pattern", "/Shading", "/ColorSpace", "/Properties" };

    /// <summary>The changed document, or null when it can't be done safely (nothing is changed then).</summary>
    public static byte[]? Run(byte[] bytes, IReadOnlyList<Job> jobs)
    {
        LastReason = "";
        try
        {
            using var ms = new MemoryStream(bytes);
            var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
            foreach (var job in jobs)
            {
                var page = doc.Pages[job.PageIndex];
                byte[]? original = ContentOf(page);
                byte[]? added = ContentOf(doc.Pages[job.ScratchIndex]);
                if (original == null) { LastReason = "the page has no readable instructions"; return null; }
                byte[] content = original;
                foreach (var move in job.Moves)
                {
                    var moved = PdfContentEditor.Apply(content, job.ObjectCount, new[] { move.Index }, new[] { 1, 0, 0, 1, move.Dx, move.Dy });
                    if (moved == null) { LastReason = "a move: " + PdfContentEditor.LastReason; return null; }
                    content = moved;
                }
                if (job.Deletes.Count > 0)
                {
                    var cut = PdfContentEditor.Apply(content, job.ObjectCount, job.Deletes, null);
                    if (cut == null) { LastReason = "a removal: " + PdfContentEditor.LastReason; return null; }
                    content = cut;
                }
                if (added != null && added.Length > 0)
                {
                    string text = Encoding.Latin1.GetString(added);
                    var names = AddResources(doc, page, doc.Pages[job.ScratchIndex], job.PageIndex, ref text);
                    if (names == null) return null;
                    var all = new MemoryStream();
                    void Put(string s) { var b = Encoding.Latin1.GetBytes(s); all.Write(b, 0, b.Length); }
                    Put("q\n"); all.Write(content, 0, content.Length); Put("\nQ\nq\n"); Put(text); Put("\nQ\n");
                    content = all.ToArray();
                }
                var stream = new PdfDictionary(doc);
                stream.CreateStream(content);
                try { stream.Stream!.Value = new PdfSharp.Pdf.Filters.FlateDecode().Encode(content); stream.Elements.SetName("/Filter", "/FlateDecode"); }
                catch (Exception e) when (e is NotSupportedException or InvalidOperationException or MissingMethodException) { }
                doc.Internals.AddObject(stream);
                page.Elements["/Contents"] = stream.Reference!;
            }
            foreach (int i in jobs.Select(j => j.ScratchIndex).Distinct().OrderByDescending(x => x)) doc.Pages.RemoveAt(i);
            using var output = new MemoryStream();
            doc.Save(output, false);
            return output.ToArray();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.PdfSharpException or ObjectDisposedException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException or NullReferenceException)
        {
            LastReason = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }

    /// <summary>All the page's content streams, one after the other (null when one can't be read).</summary>
    private static byte[]? ContentOf(PdfPage page)
    {
        var content = new MemoryStream();
        var contents = page.Contents;
        for (int i = 0; i < contents.Elements.Count; i++)
        {
            if (contents.Elements[i] is PdfSharp.Pdf.Advanced.PdfReference r && r.Value is PdfDictionary d && d.Stream != null)
            {
                var b = d.Stream.UnfilteredValue;
                if (b == null) return null;
                content.Write(b, 0, b.Length);
                content.WriteByte(10);
            }
        }
        return content.ToArray();
    }

    /// <summary>
    /// Gives the real page the fonts, pictures and graphics states of the new drawing under new names (so none of its own can clash) and writes the new names into
    /// <paramref name="text"/>. The page gets its own copy of its resources: other pages may share the original.
    /// </summary>
    private static List<string>? AddResources(PdfDocument doc, PdfPage page, PdfPage scratch, int unique, ref string text)
    {
        var from = scratch.Elements.GetDictionary("/Resources");
        var own = page.Elements.GetDictionary("/Resources");
        if (from == null) return new List<string>();
        if (own == null) { LastReason = "the page's resources can't be found"; return null; }
        var copy = new PdfDictionary(doc);
        foreach (var kv in own.Elements) copy.Elements[kv.Key] = kv.Value;
        var renamed = new List<string>();
        foreach (string cat in Categories)
        {
            var source = from.Elements.GetDictionary(cat);
            if (source == null || source.Elements.Count == 0) continue;
            var target = new PdfDictionary(doc);
            var existing = own.Elements.GetDictionary(cat);
            if (existing != null) foreach (var kv in existing.Elements) target.Elements[kv.Key] = kv.Value;
            foreach (string key in source.Elements.Keys.ToList())
            {
                string name = key, fresh = "/Ux" + unique + "_" + name.TrimStart((char)47);
                target.Elements[fresh] = source.Elements[key];
                renamed.Add(name);
                text = Regex.Replace(text, Regex.Escape(name) + @"(?![A-Za-z0-9_.\-])", fresh);
            }
            copy.Elements[cat] = target;
        }
        page.Elements["/Resources"] = copy;
        return renamed;
    }
}
