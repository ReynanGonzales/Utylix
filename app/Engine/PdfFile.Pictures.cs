using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

/// <summary>Pictures already in a PDF: look at one, replace it (same place), crop it, and save them all out as files.</summary>
public sealed partial class PdfFile
{
    /// <summary>The picture of an object of the page (null when it can't be read).</summary>
    public BitmapSource? GetPictureBitmap(int pageIndex, int objectIndex)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr page = LoadPageOrThrow(pageIndex);
            try
            {
                IntPtr obj = Pdfium.FPDFPage_GetObject(page, objectIndex);
                return obj == IntPtr.Zero || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjImage ? null : BitmapOf(obj);
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
    }

    private static BitmapSource? BitmapOf(IntPtr imageObj)
    {
        IntPtr bmp = Pdfium.FPDFImageObj_GetBitmap(imageObj);
        if (bmp == IntPtr.Zero) return null;
        try { return PdfCompressor.ToBitmapSource(bmp); }
        finally { Pdfium.FPDFBitmap_Destroy(bmp); }
    }

    /// <summary>The picture's bytes as stored when it is a JPEG (they can be saved as a .jpg without losing anything), else null.</summary>
    private static byte[]? RawJpeg(IntPtr imageObj)
    {
        uint n = Pdfium.FPDFImageObj_GetImageDataRaw(imageObj, null, 0);
        if (n < 4 || n > 200 * 1024 * 1024) return null;
        var data = new byte[n];
        if (Pdfium.FPDFImageObj_GetImageDataRaw(imageObj, data, n) != n) return null;
        return data[0] == 0xFF && data[1] == 0xD8 ? data : null;
    }

    /// <summary>
    /// Puts another picture in place of this one: the same spot, as big as fits inside the old picture's box (never stretched), centred. A picture that is turned on the page comes out
    /// upright. The old picture's cut-out (if the PDF cut it) is kept, so a very different shape may be cut at the edges.
    /// </summary>
    public void ReplacePicture(int pageIndex, int objectIndex, PdfPicture picture)
    {
        double pw = picture.Pixels.PixelWidth, ph = picture.Pixels.PixelHeight;
        if (pw < 1 || ph < 1) throw new IOException("That picture is empty.");
        ChangeObjects(pageIndex, new[] { objectIndex }, (map, obj) =>
        {
            if (Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjImage) throw new IOException("That isn't a picture.");
            if (Pdfium.FPDFPageObj_GetBounds(obj, out float l, out float b, out float r, out float t) == 0) throw new IOException("The old picture's place can't be read.");
            if (!PdfCombiner.SetPicture(obj, picture.Jpeg, picture.Pixels)) throw new IOException("The picture couldn't be put in.");
            double boxW = r - l, boxH = t - b, scale = Math.Min(boxW / pw, boxH / ph);
            double w = pw * scale, h = ph * scale;
            Pdfium.FPDFImageObj_SetMatrix(obj, w, 0, 0, h, l + (boxW - w) / 2, b + (boxH - h) / 2);
        });
    }

    /// <summary>
    /// Cuts a picture down to a part of itself. The fractions (0..1) are cut from each edge of the picture as it looks. The unused pixels are really removed from the picture (a JPEG stays a JPEG),
    /// and the picture's place on the page is the part that is left, so it doesn't move or stretch.
    /// </summary>
    public void CropPicture(int pageIndex, int objectIndex, double left, double top, double right, double bottom)
    {
        ChangeObjects(pageIndex, new[] { objectIndex }, (map, obj) =>
        {
            if (Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjImage) throw new IOException("That isn't a picture.");
            var source = BitmapOf(obj) ?? throw new IOException("This picture can't be read, so it can't be cropped.");
            bool jpeg = RawJpeg(obj) != null;
            int w = source.PixelWidth, h = source.PixelHeight;
            int x0 = (int)Math.Round(w * Math.Clamp(left, 0, 0.95)), y0 = (int)Math.Round(h * Math.Clamp(top, 0, 0.95));
            int cw = Math.Max(1, w - x0 - (int)Math.Round(w * Math.Clamp(right, 0, 0.95))), ch = Math.Max(1, h - y0 - (int)Math.Round(h * Math.Clamp(bottom, 0, 0.95)));
            cw = Math.Min(cw, w - x0); ch = Math.Min(ch, h - y0);
            if (Pdfium.FPDFPageObj_GetMatrix(obj, out var m) == 0) throw new IOException("The picture's place can't be read.");
            var cropped = new CroppedBitmap(source, new Int32Rect(x0, y0, cw, ch));
            if (jpeg)
            {
                var enc = new JpegBitmapEncoder { QualityLevel = 92 };
                enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(cropped, PixelFormats.Bgr24, null, 0)));
                using var ms = new MemoryStream(); enc.Save(ms);
                if (!Pdfium.LoadJpeg(obj, ms.ToArray())) throw new IOException("The cropped picture couldn't be put in.");
            }
            else if (!PdfCombiner.SetPicture(obj, null, cropped)) throw new IOException("The cropped picture couldn't be put in.");
            // the picture is a unit square mapped to the page by m; its new square is the part that stays (the fractions are measured from the top, the square's y goes up)
            double fl = (double)x0 / w, fr = 1 - (double)(x0 + cw) / w, ft = (double)y0 / h, fb = 1 - (double)(y0 + ch) / h;
            double sx = 1 - fl - fr, sy = 1 - ft - fb;
            Pdfium.FPDFImageObj_SetMatrix(obj, sx * m.A, sx * m.B, sy * m.C, sy * m.D, fl * m.A + fb * m.C + m.E, fl * m.B + fb * m.D + m.F);
        });
    }

    /// <summary>One picture as a file: a JPEG as it is stored, anything else as a PNG. Returns the file's extension.</summary>
    public string SavePictureTo(int pageIndex, int objectIndex, string pathWithoutExtension)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr page = LoadPageOrThrow(pageIndex);
            try
            {
                IntPtr obj = Pdfium.FPDFPage_GetObject(page, objectIndex);
                if (obj == IntPtr.Zero || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjImage) throw new IOException("That isn't a picture.");
                if (RawJpeg(obj) is { } jpeg) { File.WriteAllBytes(pathWithoutExtension + ".jpg", jpeg); return ".jpg"; }
                var bitmap = BitmapOf(obj) ?? throw new IOException("This picture can't be read.");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bitmap));
                using var fs = File.Create(pathWithoutExtension + ".png");
                enc.Save(fs);
                return ".png";
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
    }

    /// <summary>Every picture of every page (at least <paramref name="minSide"/> pixels on its short side) as files in a folder. Returns how many were saved.</summary>
    public int SaveAllPictures(string folder, string namePrefix, int minSide, IProgress<int>? progress, System.Threading.CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        int saved = 0;
        for (int page = 0; page < PageCount; page++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(page);
            int n = 0;
            foreach (var o in GetPageObjects(page).Where(o => o.Kind == Pdfium.ObjImage))
            {
                var picture = GetPictureBitmap(page, o.Index);
                if (picture == null || Math.Min(picture.PixelWidth, picture.PixelHeight) < minSide) continue;
                n++;
                string baseName = System.IO.Path.Combine(folder, $"{namePrefix} - page {page + 1} - picture {n}");
                try { SavePictureTo(page, o.Index, baseName); saved++; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* this one can't be saved: the others go on */ }
            }
        }
        return saved;
    }
}
