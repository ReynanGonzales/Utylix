using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

public enum PdfReduceLevel
{
    /// <summary>Pictures at 150 dpi, good JPEG quality. Text, links and forms stay as they are.</summary>
    Recommended,
    /// <summary>Pictures at 110 dpi, lower JPEG quality.</summary>
    Smaller,
    /// <summary>Every page becomes one picture (100 dpi). Smallest for scans, but text can no longer be selected or searched.</summary>
    Smallest,
}

public sealed record PdfReduceResult(long Before, long After, int PicturesChanged, byte[]? Output)
{
    public bool Helped => Output != null;
}

/// <summary>
/// "Reduce file size", like Acrobat's: the big pictures inside a PDF (scans, photos) are scaled down to what is needed on paper and
/// stored as JPEG; pictures that are really grey are stored grey. PDFium reads each picture; PDFsharp then puts the smaller one in the
/// very place of the old one and writes the file without leftovers (PDFium alone would keep the old pictures in the file).
/// Everything runs on this PC.
/// </summary>
public static class PdfCompressor
{
    /// <param name="openPassword">the password the PDF was opened with (null when it opens without one)</param>
    /// <param name="ownerPassword">its owner (permissions) password, when the person gave it</param>
    public static PdfReduceResult Reduce(string path, string? openPassword, string? ownerPassword, PdfReduceLevel level, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        byte[] original = File.ReadAllBytes(path);
        string? password = ownerPassword ?? openPassword;
        using var pdf = PdfFile.Open(path, password);
        Protection? protection = null;
        if (pdf.IsProtected)
        {
            // a protected PDF stays protected: it is only changed with the owner (permissions) password, like in Acrobat, and the
            // smaller copy gets the same passwords and permissions back. Its pages are never re-made as pictures (that copy couldn't be).
            if (level == PdfReduceLevel.Smallest) throw new PdfProtectedException("\"Smallest\" can't be used on a password-protected PDF, because the copy would lose its protection. Use Recommended or Smaller.");
            if (!CanChange(original, password)) throw new PdfProtectedException(OwnerPasswordNeeded);
            string owner = ownerPassword ?? openPassword ?? "";
            if (owner.Length > 0 || !string.IsNullOrEmpty(openPassword)) protection = new Protection(openPassword ?? "", owner, pdf.Permissions);
        }
        byte[]? output;
        int changed;
        if (level == PdfReduceLevel.Smallest) { output = Rasterize(pdf, 100, 45, progress, cancel); changed = pdf.PageCount; }
        else
        {
            var (dpi, quality) = level == PdfReduceLevel.Recommended ? (150, 72) : (110, 55);
            var replacements = ShrinkPictures(pdf, dpi, quality, progress, cancel);
            output = Repack(original, password, replacements.Count == 0 ? null : replacements, protection);
            changed = replacements.Count;
        }
        // only worth it when it really got smaller
        if (output == null || output.Length >= original.Length * 0.97) return new PdfReduceResult(original.Length, original.Length, changed, null);
        return new PdfReduceResult(original.Length, output.Length, changed, output);
    }

    public const string OwnerPasswordNeeded = "This PDF is protected against changes. Making it smaller needs its owner (permissions) password.";

    private sealed record Protection(string UserPassword, string OwnerPassword, uint Permissions);

    private static bool CanChange(byte[] original, string? password)
    {
        try
        {
            using var input = new MemoryStream(original, writable: false);
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, password ?? "", PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
            return true;
        }
        catch (PdfSharp.Pdf.IO.PdfReaderException) { return false; }
    }

    // ---------- pictures ----------
    private sealed record Replacement(byte[] Jpeg, int Width, int Height, bool Gray);

    /// <summary>Goes through every picture of every page; gives the smaller JPEG for each picture worth replacing, by a fingerprint of its stored bytes.</summary>
    private static Dictionary<string, Replacement> ShrinkPictures(PdfFile pdf, int targetDpi, int quality, IProgress<(int, int)>? progress, CancellationToken cancel)
    {
        var result = new Dictionary<string, Replacement>();
        var seen = new HashSet<string>();
        for (int i = 0; i < pdf.PageCount; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((i, pdf.PageCount));
            var pictures = new List<(string Key, BitmapSource Bitmap, double Dpi, int RawLength)>();
            lock (Pdfium.Sync)
            {
                IntPtr page = Pdfium.FPDF_LoadPage(pdf.Handle, i);
                if (page == IntPtr.Zero) continue;
                try { Collect(page, page, 0, seen, pictures); }
                finally { Pdfium.FPDF_ClosePage(page); }
            }
            // the slow part (scaling and JPEG) runs without holding PDFium
            foreach (var p in pictures)
            {
                cancel.ThrowIfCancellationRequested();
                var smaller = Shrink(p.Bitmap, p.Dpi, targetDpi, quality);
                if (smaller != null && smaller.Jpeg.Length < p.RawLength * 0.8) result[p.Key] = smaller;
            }
        }
        progress?.Report((pdf.PageCount, pdf.PageCount));
        return result;
    }

    private static void Collect(IntPtr page, IntPtr container, int depth, HashSet<string> seen, List<(string, BitmapSource, double, int)> into)
    {
        bool isPage = container == page;
        int count = isPage ? Pdfium.FPDFPage_CountObjects(page) : Pdfium.FPDFFormObj_CountObjects(container);
        for (int k = 0; k < count; k++)
        {
            IntPtr obj = isPage ? Pdfium.FPDFPage_GetObject(page, k) : Pdfium.FPDFFormObj_GetObject(container, (uint)k);
            if (obj == IntPtr.Zero) continue;
            int type = Pdfium.FPDFPageObj_GetType(obj);
            if (type == Pdfium.ObjForm && depth < 8) { Collect(page, obj, depth + 1, seen, into); continue; }
            if (type != Pdfium.ObjImage) continue;

            // black-and-white scans (1 bit) are already tiny. (See-through pictures are fine: their soft mask is a picture of its own
            // and stays; pictures whose transparency can't survive JPEG are left alone in Repack.)
            if (Pdfium.FPDFImageObj_GetImageMetadata(obj, page, out var meta) == 0 || meta.BitsPerPixel < 8) continue;
            if ((long)meta.Width * meta.Height < 96 * 96) continue;
            uint rawLength = Pdfium.FPDFImageObj_GetImageDataRaw(obj, null, 0);
            if (rawLength < 12 * 1024) continue;
            var raw = new byte[rawLength];
            Pdfium.FPDFImageObj_GetImageDataRaw(obj, raw, rawLength);
            string key = Fingerprint(raw);
            if (!seen.Add(key)) continue;                     // the same picture again (a logo on every page): done once

            // how sharp it is on paper: its pixels over the area it covers (works for turned pictures too)
            double dpi = Math.Max(meta.HorizontalDpi, meta.VerticalDpi);
            if (Pdfium.FPDFPageObj_GetBounds(obj, out float l, out float b, out float r, out float t) != 0)
            {
                double inches2 = Math.Abs((r - l) * (t - b)) / (72.0 * 72.0);
                if (inches2 > 0.01) dpi = Math.Sqrt((double)meta.Width * meta.Height / inches2);
            }

            IntPtr bmp = Pdfium.FPDFImageObj_GetBitmap(obj);
            if (bmp == IntPtr.Zero) continue;
            try
            {
                var picture = ToBitmapSource(bmp);
                if (picture != null) into.Add((key, picture, dpi, (int)rawLength));
            }
            finally { Pdfium.FPDFBitmap_Destroy(bmp); }
        }
    }

    private static BitmapSource? ToBitmapSource(IntPtr bmp)
    {
        int w = Pdfium.FPDFBitmap_GetWidth(bmp), h = Pdfium.FPDFBitmap_GetHeight(bmp), stride = Pdfium.FPDFBitmap_GetStride(bmp);
        PixelFormat? format = Pdfium.FPDFBitmap_GetFormat(bmp) switch
        {
            Pdfium.BitmapGray => PixelFormats.Gray8,
            Pdfium.BitmapBgr => PixelFormats.Bgr24,
            Pdfium.BitmapBgrx or Pdfium.BitmapBgra => PixelFormats.Bgr32,
            _ => null,
        };
        if (format == null || w <= 0 || h <= 0) return null;
        var picture = BitmapSource.Create(w, h, 96, 96, format.Value, null, Pdfium.FPDFBitmap_GetBuffer(bmp), stride * h, stride);
        picture.Freeze();
        return picture;
    }

    private static Replacement? Shrink(BitmapSource picture, double dpi, int targetDpi, int quality)
    {
        BitmapSource src = picture;
        bool gray = src.Format == PixelFormats.Gray8 || LooksGray(src);
        if (gray && src.Format != PixelFormats.Gray8) src = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
        else if (!gray && src.Format != PixelFormats.Bgr24) src = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        double scale = dpi > targetDpi * 1.05 ? targetDpi / dpi : 1.0;
        if (scale < 1)
        {
            int w = Math.Max(1, (int)Math.Round(picture.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(picture.PixelHeight * scale));
            src = new TransformedBitmap(src, new ScaleTransform((double)w / picture.PixelWidth, (double)h / picture.PixelHeight));
        }
        byte[] jpeg = Jpeg(src, quality);
        return new Replacement(jpeg, src.PixelWidth, src.PixelHeight, gray);
    }

    /// <summary>True when every sampled pixel has (nearly) equal red, green and blue: a grey scan stored in colour.</summary>
    private static bool LooksGray(BitmapSource src)
    {
        BitmapSource bgr = src.Format == PixelFormats.Bgr32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
        int w = bgr.PixelWidth, h = bgr.PixelHeight;
        int rows = Math.Min(h, 64);
        var line = new byte[w * 4];
        for (int s = 0; s < rows; s++)
        {
            int y = (int)((long)s * (h - 1) / Math.Max(1, rows - 1));
            bgr.CopyPixels(new System.Windows.Int32Rect(0, y, w, 1), line, w * 4, 0);
            for (int x = 0; x < w; x += 3)
            {
                int bl = line[x * 4], g = line[x * 4 + 1], r = line[x * 4 + 2];
                if (Math.Abs(r - g) > 14 || Math.Abs(g - bl) > 14 || Math.Abs(r - bl) > 14) return false;
            }
        }
        return true;
    }

    private static byte[] Jpeg(BitmapSource src, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static string Fingerprint(byte[] data) => data.Length + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    /// <summary>
    /// Opens the file with PDFsharp, swaps in the smaller pictures (matched by the fingerprint of their stored bytes) and writes it again.
    /// Objects nothing points to any more are left out. Null when PDFsharp can't read this file.
    /// </summary>
    private static byte[]? Repack(byte[] original, string? password, Dictionary<string, Replacement>? replacements, Protection? protection)
    {
        try
        {
            using var input = new MemoryStream(original, writable: false);
            using var doc = password == null
                ? PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify)
                : PdfSharp.Pdf.IO.PdfReader.Open(input, password, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
            if (replacements != null)
            {
                foreach (var obj in doc.Internals.GetAllObjects())
                {
                    if (obj is not PdfSharp.Pdf.PdfDictionary d || d.Stream == null || d.Elements.GetName("/Subtype") != "/Image") continue;
                    if (!replacements.TryGetValue(Fingerprint(d.Stream.Value), out var rep)) continue;
                    // colour-key transparency ("this exact colour is see-through") and transparency stored inside a JPEG 2000
                    // picture don't survive a JPEG; a soft mask (a picture of its own) does
                    if (d.Elements["/Mask"] is PdfSharp.Pdf.PdfArray || d.Elements.GetInteger("/SMaskInData") != 0) continue;
                    d.Stream.Value = rep.Jpeg;
                    d.Elements.SetName("/Filter", "/DCTDecode");
                    d.Elements.Remove("/DecodeParms");
                    d.Elements.Remove("/Decode");              // (the picture read by PDFium already shows the final colours)
                    d.Elements.SetInteger("/Width", rep.Width);
                    d.Elements.SetInteger("/Height", rep.Height);
                    d.Elements.SetInteger("/BitsPerComponent", 8);
                    d.Elements.SetName("/ColorSpace", rep.Gray ? "/DeviceGray" : "/DeviceRGB");
                    d.Elements.SetInteger("/Length", rep.Jpeg.Length);
                }
            }
            RemoveDuplicates(doc);
            if (protection != null)
            {
                var security = doc.SecuritySettings;
                security.UserPassword = protection.UserPassword;
                security.OwnerPassword = protection.OwnerPassword;
                uint p = protection.Permissions;
                security.PermitPrint = (p & 4) != 0;
                security.PermitModifyDocument = (p & 8) != 0;
                security.PermitExtractContent = (p & 16) != 0;
                security.PermitAnnotations = (p & 32) != 0;
                security.PermitFormsFill = (p & 256) != 0;
                security.PermitAssembleDocument = (p & 1024) != 0;
                security.PermitFullQualityPrint = (p & 2048) != 0;
                doc.SecurityHandler.SetEncryptionToV5();                // AES-256
            }
            doc.Options.CompressContentStreams = true;
            using var output = new MemoryStream();
            doc.Save(output, false);
            return output.ToArray();
        }
        catch (Exception e) when (e is PdfSharp.Pdf.IO.PdfReaderException or InvalidOperationException or NotImplementedException or NotSupportedException or IOException or ArgumentException or InvalidCastException or NullReferenceException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// The same font or picture stored many times over (common in PDFs put together from several files): everything is pointed at one
    /// copy, and the others are left out when the file is written. Nothing changes in how the pages look.
    /// </summary>
    private static void RemoveDuplicates(PdfSharp.Pdf.PdfDocument doc)
    {
        var first = new Dictionary<string, PdfSharp.Pdf.Advanced.PdfReference>();
        var redirect = new Dictionary<PdfSharp.Pdf.PdfObjectID, PdfSharp.Pdf.Advanced.PdfReference>();
        var all = doc.Internals.GetAllObjects();
        foreach (var obj in all)
        {
            if (obj is not PdfSharp.Pdf.PdfDictionary d || d.Stream == null || d.Reference == null) continue;
            byte[] bytes = d.Stream.Value;
            if (bytes.Length < 2048) continue;                                // small ones aren't worth it
            if (d.Elements.GetName("/Type") is "/XRef" or "/ObjStm" or "/Metadata") continue;
            string key = Fingerprint(bytes) + "|" + d.ToString();             // same bytes AND the same description (filters, sizes, links)
            if (first.TryGetValue(key, out var keep)) redirect[d.Reference.ObjectID] = keep;
            else first[key] = d.Reference;
        }
        if (redirect.Count == 0) return;

        void Fix(PdfSharp.Pdf.PdfItem? item, int depth)
        {
            if (depth > 64) return;
            if (item is PdfSharp.Pdf.PdfDictionary dict)
            {
                foreach (string k in dict.Elements.Keys.ToArray())
                {
                    var v = dict.Elements[k];
                    if (v is PdfSharp.Pdf.Advanced.PdfReference r) { if (redirect.TryGetValue(r.ObjectID, out var to)) dict.Elements[k] = to; }
                    else Fix(v, depth + 1);
                }
            }
            else if (item is PdfSharp.Pdf.PdfArray array)
            {
                for (int i = 0; i < array.Elements.Count; i++)
                {
                    var v = array.Elements[i];
                    if (v is PdfSharp.Pdf.Advanced.PdfReference r) { if (redirect.TryGetValue(r.ObjectID, out var to)) array.Elements[i] = to; }
                    else Fix(v, depth + 1);
                }
            }
        }
        foreach (var obj in all) Fix(obj, 0);
        Fix(doc.Internals.Catalog, 0);
    }

    // ---------- every page as one picture ----------
    private static byte[]? Rasterize(PdfFile pdf, int dpi, int quality, IProgress<(int, int)>? progress, CancellationToken cancel)
    {
        Pdfium.Init();
        IntPtr target;
        lock (Pdfium.Sync) target = Pdfium.FPDF_CreateNewDocument();
        if (target == IntPtr.Zero) return null;
        try
        {
            for (int i = 0; i < pdf.PageCount; i++)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report((i, pdf.PageCount));
                var size = pdf.PageSize(i);
                int w = Math.Max(1, (int)Math.Round(size.Width / 72 * dpi)), h = Math.Max(1, (int)Math.Round(size.Height / 72 * dpi));
                var picture = pdf.Render(i, w, h, forScreen: false);
                bool gray = LooksGray(picture);
                byte[] jpeg = Jpeg(gray ? new FormatConvertedBitmap(picture, PixelFormats.Gray8, null, 0) : picture, quality);
                lock (Pdfium.Sync)
                {
                    IntPtr page = Pdfium.FPDFPage_New(target, i, size.Width, size.Height);
                    if (page == IntPtr.Zero) return null;
                    try
                    {
                        IntPtr image = Pdfium.FPDFPageObj_NewImageObj(target);
                        if (image == IntPtr.Zero || !Pdfium.LoadJpeg(image, jpeg)) return null;
                        Pdfium.FPDFImageObj_SetMatrix(image, size.Width, 0, 0, size.Height, 0, 0);
                        Pdfium.FPDFPage_InsertObject(page, image);
                        Pdfium.FPDFPage_GenerateContent(page);
                    }
                    finally { Pdfium.FPDF_ClosePage(page); }
                }
            }
            progress?.Report((pdf.PageCount, pdf.PageCount));
            using var ms = new MemoryStream();
            lock (Pdfium.Sync) if (!Pdfium.Save(target, ms)) return null;
            return ms.ToArray();
        }
        finally { lock (Pdfium.Sync) Pdfium.FPDF_CloseDocument(target); }
    }
}
