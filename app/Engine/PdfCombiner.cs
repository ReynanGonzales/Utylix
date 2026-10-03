using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

/// <summary>What paper pictures are put on when they become PDF pages.</summary>
public enum PdfPaper
{
    /// <summary>A page the shape of the picture (its long side as long as an A4 page), with no margin.</summary>
    Picture,
    A4,
    Letter,
    /// <summary>"Long" bond paper, 8.5 x 13 inches.</summary>
    Long,
}

/// <summary>A picture ready to go on a PDF page, turned the right way up: its JPEG bytes when it can go in as a JPEG, otherwise its pixels.</summary>
public sealed record PdfPicture(byte[]? Jpeg, BitmapSource Pixels);

/// <summary>
/// "Combine into one PDF" and "Convert to PDF": PDFs (all their pages) and pictures (one page each), in the given order, into one new
/// PDF. Pages are copied as they are (text stays text); a JPEG photo goes in as it is, without being re-compressed. Everything runs
/// on this PC.
/// </summary>
public static class PdfCombiner
{
    /// <summary>Picture files that can become PDF pages (what Windows can read).</summary>
    public static readonly string[] PictureExtensions = { "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "webp", "heic", "heif", "avif" };

    public static bool IsPicture(string path) => PictureExtensions.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The paper size in points (1/72 inch), upright; null for <see cref="PdfPaper.Picture"/>.</summary>
    public static (double Width, double Height)? PaperSize(PdfPaper paper) => paper switch
    {
        PdfPaper.A4 => (595.28, 841.89),
        PdfPaper.Letter => (612, 792),
        PdfPaper.Long => (612, 936),
        _ => null,
    };

    /// <summary>Builds the combined PDF. Throws <see cref="IOException"/> (with a message for the person) when a file can't be used.</summary>
    public static byte[] Combine(IReadOnlyList<string> files, PdfPaper paper, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        Pdfium.Init();
        IntPtr target;
        lock (Pdfium.Sync) target = Pdfium.FPDF_CreateNewDocument();
        if (target == IntPtr.Zero) throw new IOException("Couldn't start a new PDF.");
        var sources = new List<PdfFile>();             // kept open until the new PDF is written: PDFium may still read from them
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report((i, files.Count));
                string file = files[i], name = Path.GetFileName(file);
                if (IsPdf(file))
                {
                    PdfFile pdf;
                    try { pdf = PdfFile.Open(file); }
                    catch (PdfPasswordException) { throw new PdfProtectedException($"{name} needs a password to open, so it can't be combined with other files. Take it out of the list."); }
                    sources.Add(pdf);
                    // a protected PDF would lose its protection in the combined copy (like in Acrobat, that isn't done)
                    if (pdf.IsProtected) throw new PdfProtectedException($"{name} is password-protected, so it can't be combined with other files. Take it out of the list.");
                    lock (Pdfium.Sync)
                    {
                        int at = Pdfium.FPDF_GetPageCount(target);
                        if (Pdfium.FPDF_ImportPagesByIndex(target, pdf.Handle, null, 0, at) == 0) throw new IOException($"The pages of {name} couldn't be copied.");
                    }
                }
                else
                {
                    PdfPicture picture;
                    try { picture = LoadPicture(File.ReadAllBytes(file), 0); }
                    catch (Exception e) when (e is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or COMException or OverflowException)
                    {
                        throw new IOException($"{name} couldn't be read as a picture" + (Path.GetExtension(file).ToLowerInvariant() is ".heic" or ".heif" or ".avif" ? " (Windows needs its free \"HEIF Image Extensions\" from the Microsoft Store for this kind)." : "."));
                    }
                    AddPicturePage(target, picture, paper);
                }
            }
            progress?.Report((files.Count, files.Count));
            using var ms = new MemoryStream();
            lock (Pdfium.Sync) if (!Pdfium.Save(target, ms)) throw new IOException("The PDF couldn't be written.");
            return ms.ToArray();
        }
        finally
        {
            lock (Pdfium.Sync) Pdfium.FPDF_CloseDocument(target);
            foreach (var s in sources) s.Dispose();
        }
    }

    /// <param name="index">Where the page goes (-1 = at the end).</param>
    /// <param name="sheet">A sheet of this size (points) with a small margin, instead of the paper choice.</param>
    internal static void AddPicturePage(IntPtr doc, PdfPicture picture, PdfPaper paper, int index = -1, (double Width, double Height)? sheet = null)
    {
        double pw = picture.Pixels.PixelWidth, ph = picture.Pixels.PixelHeight;
        bool wide = pw > ph;
        double width, height, margin;
        if (sheet is { } fixedSheet) { (width, height, margin) = (fixedSheet.Width, fixedSheet.Height, 18); }
        else if (PaperSize(paper) is { } size)
        {
            // the paper is turned like the picture (a wide photo goes on a landscape page)
            (width, height) = wide ? (size.Height, size.Width) : (size.Width, size.Height);
            margin = 18;                                                     // a quarter inch
        }
        else
        {
            double scale = 841.89 / Math.Max(pw, ph);
            (width, height, margin) = (pw * scale, ph * scale, 0);
        }
        double fit = Math.Min((width - 2 * margin) / pw, (height - 2 * margin) / ph);
        double w = pw * fit, h = ph * fit, x = (width - w) / 2, y = (height - h) / 2;

        lock (Pdfium.Sync)
        {
            IntPtr page = Pdfium.FPDFPage_New(doc, index < 0 ? Pdfium.FPDF_GetPageCount(doc) : index, width, height);
            if (page == IntPtr.Zero) throw new IOException("Couldn't add a page.");
            try
            {
                IntPtr image = Pdfium.FPDFPageObj_NewImageObj(doc);
                if (image == IntPtr.Zero || !SetPicture(image, picture.Jpeg, picture.Pixels)) throw new IOException("A picture couldn't be added.");
                Pdfium.FPDFImageObj_SetMatrix(image, w, 0, 0, h, x, y);
                Pdfium.FPDFPage_InsertObject(page, image);
                Pdfium.FPDFPage_GenerateContent(page);
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
    }

    /// <summary>Puts a picture into a PDFium image object: the JPEG as it is when there is one, otherwise the pixels (stored losslessly).</summary>
    internal static bool SetPicture(IntPtr imageObj, byte[]? jpeg, BitmapSource? pixels)
    {
        if (jpeg != null) return Pdfium.LoadJpeg(imageObj, jpeg);
        if (pixels == null) return false;
        BitmapSource src = pixels.Format == PixelFormats.Bgra32 ? pixels : new FormatConvertedBitmap(pixels, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var data = new byte[(long)stride * h];
        src.CopyPixels(data, stride, 0);
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr bmp = Pdfium.FPDFBitmap_CreateEx(w, h, Pdfium.BitmapBgra, pin.AddrOfPinnedObject(), stride);
            if (bmp == IntPtr.Zero) return false;
            try { return Pdfium.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, imageObj, bmp) != 0; }
            finally { Pdfium.FPDFBitmap_Destroy(bmp); }
        }
        finally { pin.Free(); }
    }

    /// <summary>
    /// Reads a picture for a PDF page, turned the way the camera says (phone photos are often stored sideways with a note on how to turn
    /// them). A JPEG that needs no change is kept byte for byte. <paramref name="maxSide"/> (0 = no limit) makes bigger pictures smaller.
    /// </summary>
    public static PdfPicture LoadPicture(byte[] bytes, int maxSide)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource picture = decoder.Frames[0];
        int rotation = 0;
        try
        {
            if (decoder.Frames[0].Metadata is BitmapMetadata meta && meta.GetQuery("/app1/ifd/{ushort=274}") is ushort o)
                rotation = o switch { 3 or 4 => 180, 5 or 6 => 90, 7 or 8 => 270, _ => 0 };
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or COMException) { }

        bool alpha = picture.Format.Masks.Count == 4 || picture.Format == PixelFormats.Bgra32 || picture.Format == PixelFormats.Pbgra32 ||
                     picture.Format == PixelFormats.Indexed8 && picture.Palette?.Colors.Any(c => c.A < 255) == true;
        double big = maxSide > 0 ? Math.Max(picture.PixelWidth, picture.PixelHeight) / (double)maxSide : 0;
        if (big > 1) picture = new TransformedBitmap(picture, new ScaleTransform(1 / big, 1 / big));
        if (rotation != 0) picture = new TransformedBitmap(picture, new RotateTransform(rotation));

        byte[]? jpeg = null;
        if (!alpha && decoder is JpegBitmapDecoder && big <= 1 && rotation == 0) jpeg = bytes;
        else if (!alpha && (decoder is JpegBitmapDecoder || IsPhotoLike(decoder)))
        {
            // photos stay JPEG (re-saved at high quality only because they had to be turned or made smaller); screenshots and
            // drawings (PNG, BMP, GIF) keep every pixel
            var enc = new JpegBitmapEncoder { QualityLevel = 92 };
            enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(picture, PixelFormats.Bgr24, null, 0)));
            using var ms = new MemoryStream(); enc.Save(ms); jpeg = ms.ToArray();
        }
        if (picture.CanFreeze) picture.Freeze();
        return new PdfPicture(jpeg, picture);
    }

    /// <summary>Kinds that hold camera photos (HEIC from iPhones, WebP, AVIF): stored as JPEG, like a photo, rather than pixel by pixel.</summary>
    private static bool IsPhotoLike(BitmapDecoder decoder)
    {
        try
        {
            string mime = decoder.CodecInfo?.MimeTypes ?? "";
            return mime.Contains("heic", StringComparison.OrdinalIgnoreCase) || mime.Contains("heif", StringComparison.OrdinalIgnoreCase) ||
                   mime.Contains("webp", StringComparison.OrdinalIgnoreCase) || mime.Contains("avif", StringComparison.OrdinalIgnoreCase);
        }
        catch (NotSupportedException) { return false; }
    }
}
