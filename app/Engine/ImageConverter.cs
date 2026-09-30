using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

public enum ImgFormat { Png, Jpg, WebP, Bmp, Gif, Tiff, Ico }

/// <param name="Quality">1-100, used by JPG and WebP.</param>
/// <param name="MaxSide">0 = keep the size; otherwise the longest side is limited to this many pixels (never enlarged).</param>
/// <param name="OutDir">null = next to the original picture.</param>
public sealed record ConvertOptions(ImgFormat Format, int Quality = 90, int MaxSide = 0, string? OutDir = null);

/// <summary>
/// Image Converter. Windows' own imaging code (WPF) reads and writes PNG/JPG/BMP/GIF/TIFF/ICO; WebP output, and
/// reading WebP/AVIF/HEIC, goes through ffmpeg when it is installed. The original file is never touched: the result
/// gets a new name if one is already taken. Call from a single-threaded-apartment (STA) thread.
/// </summary>
public static class ImageConverter
{
    /// <summary>What the right-click menu and the file picker accept.</summary>
    public static readonly string[] InputExtensions =
        { "png", "jpg", "jpeg", "jfif", "bmp", "gif", "tif", "tiff", "ico", "webp", "heic", "avif" };

    private const long MaxPixels = 200_000_000;

    public static bool IsPicture(string path) =>
        InputExtensions.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    public static string Extension(ImgFormat f) => f switch
    {
        ImgFormat.Png => "png", ImgFormat.Jpg => "jpg", ImgFormat.WebP => "webp", ImgFormat.Bmp => "bmp",
        ImgFormat.Gif => "gif", ImgFormat.Tiff => "tiff", _ => "ico",
    };

    public static string Label(ImgFormat f) => f switch
    {
        ImgFormat.Png => "PNG", ImgFormat.Jpg => "JPG", ImgFormat.WebP => "WebP", ImgFormat.Bmp => "BMP",
        ImgFormat.Gif => "GIF", ImgFormat.Tiff => "TIFF", _ => "ICO",
    };

    public static bool TryParseFormat(string? text, out ImgFormat format)
    {
        switch ((text ?? "").Trim().TrimStart('.').ToLowerInvariant())
        {
            case "png": format = ImgFormat.Png; return true;
            case "jpg": case "jpeg": case "jfif": format = ImgFormat.Jpg; return true;
            case "webp": format = ImgFormat.WebP; return true;
            case "bmp": format = ImgFormat.Bmp; return true;
            case "gif": format = ImgFormat.Gif; return true;
            case "tif": case "tiff": format = ImgFormat.Tiff; return true;
            case "ico": format = ImgFormat.Ico; return true;
            default: format = ImgFormat.Png; return false;
        }
    }

    /// <summary>Converts one picture and returns the path of the new file. Throws with a readable message on failure.</summary>
    public static string Convert(string source, ConvertOptions o)
    {
        if (!File.Exists(source)) throw new IOException("The file no longer exists.");
        if (!IsPicture(source)) throw new IOException("Not a picture type this converter reads.");

        string? temp = null;
        try
        {
            var bitmap = Decode(source, ref temp);
            if (bitmap.PixelWidth * (long)bitmap.PixelHeight > MaxPixels) throw new IOException("That picture is too large to convert.");

            string dir = o.OutDir is { Length: > 0 } ? o.OutDir : Path.GetDirectoryName(Path.GetFullPath(source))!;
            Directory.CreateDirectory(dir);
            string baseName = Path.GetFileNameWithoutExtension(source);
            string ext = Extension(o.Format);

            if (o.Format == ImgFormat.WebP) return WriteWebP(bitmap, o, dir, baseName);

            var stream = CreateUnique(dir, baseName, ext, out string outPath);
            try
            {
                using (stream)
                {
                    if (o.Format == ImgFormat.Ico) WriteIco(bitmap, stream);
                    else EncodeFrame(bitmap, o).Save(stream);
                }
            }
            catch
            {
                TryDelete(outPath);
                throw;
            }
            return outPath;
        }
        finally
        {
            if (temp != null) TryDelete(temp);
        }
    }

    // ---------- reading ----------
    /// <summary>A picture as Windows shows it (phone rotation applied); WebP/AVIF/HEIC go through ffmpeg. Call from an STA thread.</summary>
    public static BitmapSource LoadPicture(string source)
    {
        string? temp = null;
        try { return Decode(source, ref temp); }
        finally { if (temp != null) TryDelete(temp); }
    }

    private static BitmapSource Decode(string source, ref string? temp)
    {
        try { return Read(source); }
        catch (Exception first) when (first is NotSupportedException or FileFormatException or InvalidOperationException
                                              or System.Runtime.InteropServices.COMException or IOException)
        {
            // WebP/AVIF/HEIC (or a codec Windows doesn't have): let ffmpeg turn it into a PNG first
            if (!Tools.HasFfmpeg)
            {
                bool needsFfmpeg = Path.GetExtension(source).TrimStart('.').ToLowerInvariant() is "webp" or "avif" or "heic";
                throw new IOException(needsFfmpeg
                    ? "Windows can't open this type here. Install ffmpeg in Settings → Video sites to convert it."
                    : "This doesn't look like a valid picture.");
            }
            temp = Path.Combine(Path.GetTempPath(), "utylix-" + Guid.NewGuid().ToString("N") + ".png");
            RunFfmpeg(new[] { "-y", "-i", source, "-frames:v", "1", temp });
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new IOException("This doesn't look like a valid picture.");
            return Read(temp);
        }
    }

    private static BitmapSource Read(string path)
    {
        BitmapDecoder decoder;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);   // OnLoad: file is free afterwards
        if (decoder.Frames.Count == 0) throw new FileFormatException("no picture in file");

        // an .ico holds several sizes: take the biggest; everything else: the first frame (GIF: first picture)
        var frame = decoder.CodecInfo?.FileExtensions?.Contains(".ico", StringComparison.OrdinalIgnoreCase) == true
            ? decoder.Frames.OrderByDescending(f => f.PixelWidth * (long)f.PixelHeight).First()
            : decoder.Frames[0];

        BitmapSource result = frame;
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort orientation)
                result = ApplyOrientation(frame, orientation);   // phone photos are stored sideways with a rotation note
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        result.Freeze();
        return result;
    }

    private static BitmapSource ApplyOrientation(BitmapSource s, ushort orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        return t == null ? s : new TransformedBitmap(s, t);
    }

    // ---------- writing ----------
    private static bool NeedsWhiteBackground(ImgFormat f) => f is ImgFormat.Jpg or ImgFormat.Bmp;   // these can't hold transparency

    private static BitmapEncoder EncodeFrame(BitmapSource bitmap, ConvertOptions o)
    {
        var (w, h) = TargetSize(bitmap, o.MaxSide);
        var frame = Render(bitmap, w, h, NeedsWhiteBackground(o.Format));
        BitmapEncoder encoder = o.Format switch
        {
            ImgFormat.Jpg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(o.Quality, 1, 100) },
            ImgFormat.Bmp => new BmpBitmapEncoder(),
            ImgFormat.Gif => new GifBitmapEncoder(),
            ImgFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        return encoder;
    }

    private static (int W, int H) TargetSize(BitmapSource s, int maxSide)
    {
        int w = s.PixelWidth, h = s.PixelHeight;
        if (maxSide <= 0 || Math.Max(w, h) <= maxSide) return (w, h);
        double k = maxSide / (double)Math.Max(w, h);
        return (Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
    }

    /// <summary>Draws the picture at the given pixel size with high-quality scaling (optionally on white), always at 96 dpi.</summary>
    private static BitmapSource Render(BitmapSource s, int w, int h, bool whiteBackground)
    {
        bool same = w == s.PixelWidth && h == s.PixelHeight && !whiteBackground;
        if (same)
            return s.Format == PixelFormats.Bgra32 || s.Format == PixelFormats.Bgr32 || s.Format == PixelFormats.Bgr24 ||
                   s.Format == PixelFormats.Gray8 || s.Format == PixelFormats.Indexed8
                ? s : new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
        return Draw(s, w, h, new Rect(0, 0, w, h), whiteBackground);
    }

    private static BitmapSource Draw(BitmapSource s, int canvasW, int canvasH, Rect target, bool whiteBackground)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            if (whiteBackground) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, canvasW, canvasH));
            dc.DrawImage(s, target);
        }
        var rtb = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        BitmapSource result = new FormatConvertedBitmap(rtb, whiteBackground ? PixelFormats.Bgr32 : PixelFormats.Bgra32, null, 0);   // straight alpha for the encoders
        result.Freeze();
        return result;
    }

    /// <summary>A Windows icon: the picture at every standard size that is not bigger than the original (16 always).</summary>
    private static void WriteIco(BitmapSource bitmap, Stream output)
    {
        int longest = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        var sizes = new[] { 16, 32, 48, 64, 128, 256 }.Where(z => z <= longest).ToList();
        if (sizes.Count == 0) sizes.Add(16);

        var pngs = new List<byte[]>();
        foreach (int z in sizes)
        {
            double k = z / (double)longest;                                  // fit inside a square, centered, transparent around
            double w = bitmap.PixelWidth * k, h = bitmap.PixelHeight * k;
            var square = Draw(bitmap, z, z, new Rect((z - w) / 2, (z - h) / 2, w, h), false);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(square));
            using var ms = new MemoryStream();
            enc.Save(ms);
            pngs.Add(ms.ToArray());
        }

        using var bw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Count);
        int offset = 6 + 16 * sizes.Count;
        for (int i = 0; i < sizes.Count; i++)
        {
            byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
            bw.Write(dim); bw.Write(dim); bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32); bw.Write(pngs[i].Length); bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) bw.Write(p);
    }

    private static string WriteWebP(BitmapSource bitmap, ConvertOptions o, string dir, string baseName)
    {
        if (!Tools.HasFfmpeg) throw new IOException("WebP needs ffmpeg. Install it in Settings → Video sites.");
        var (w, h) = TargetSize(bitmap, o.MaxSide);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Render(bitmap, w, h, false)));

        string temp = Path.Combine(Path.GetTempPath(), "utylix-" + Guid.NewGuid().ToString("N") + ".png");
        string outPath = "";
        try
        {
            using (var fs = File.Create(temp)) enc.Save(fs);
            CreateUnique(dir, baseName, "webp", out outPath).Dispose();      // reserve the name, ffmpeg then fills it
            RunFfmpeg(new[] { "-y", "-i", temp, "-c:v", "libwebp", "-quality", Math.Clamp(o.Quality, 1, 100).ToString(), outPath });
            if (new FileInfo(outPath).Length == 0) throw new IOException("ffmpeg couldn't write the WebP file.");
            return outPath;
        }
        catch
        {
            if (outPath.Length > 0) TryDelete(outPath);
            throw;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void RunFfmpeg(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Tools.Ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("ffmpeg didn't start.");
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(90_000))
        {
            try { p.Kill(true); } catch (Exception) { }
            throw new IOException("ffmpeg took too long.");
        }
        if (p.ExitCode != 0)
        {
            string text = err.GetAwaiter().GetResult().Trim();
            throw new IOException("ffmpeg couldn't convert it" + (text.Length > 0 ? ": " + text.Split('\n')[^1].Trim() : "."));
        }
    }

    // ---------- file names ----------
    /// <summary>"photo.jpg", then "photo (1).jpg", ... whichever is free. The file is created (empty) so two runs can't pick the same name.</summary>
    private static FileStream CreateUnique(string dir, string baseName, string ext, out string path)
    {
        baseName = Util.Sanitize(baseName);
        for (int i = 0; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, i == 0 ? $"{baseName}.{ext}" : $"{baseName} ({i}).{ext}");
            try
            {
                var fs = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                path = candidate;
                return fs;
            }
            catch (IOException) when (File.Exists(candidate)) { /* taken: try the next number */ }
        }
        throw new IOException("Couldn't find a free file name.");
    }

    /// <summary>Finds a free "name.ext" / "name (1).ext" in a folder and creates it empty, so nobody else takes it. Returns the path.</summary>
    public static string ReserveName(string dir, string baseName, string ext)
    {
        CreateUnique(dir, baseName, ext, out string path).Dispose();
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }
}
