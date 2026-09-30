using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IdmClone.Engine;

/// <summary>
/// Takes the background out of a picture with an AI model (isnet-general-use, Apache-2.0, from the rembg project) that runs
/// on this PC through ONNX Runtime: nothing is uploaded anywhere. The model (170 MB) is downloaded once, after asking, and
/// checked against its fingerprint. The result is a PNG with a transparent background next to the original.
/// </summary>
public static class BackgroundRemover
{
    private const string ModelFile = "isnet-general-use.onnx";
    private const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/" + ModelFile;
    private const string ModelSha256 = "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a";
    public const long ModelBytes = 178_648_008;
    private const int Size = 1024;                                   // the model looks at the picture as 1024 x 1024
    private const long MaxPixels = 150_000_000;

    public static string ModelDir => Path.Combine(Tools.Dir, "models");
    public static string ModelPath => Path.Combine(ModelDir, ModelFile);
    public static bool HasModel => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelBytes;

    public static bool IsPicture(string path) =>
        Path.GetExtension(path).TrimStart('.').ToLowerInvariant() is "png" or "jpg" or "jpeg" or "jfif" or "bmp" or "webp" or "tif" or "tiff" or "heic" or "avif";

    // ---------- the model file ----------
    public static async Task InstallModelAsync(Action<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelDir);
        string tmp = ModelPath + ".download";
        try
        {
            await Tools.DownloadAsync(ModelUrl, tmp, "Downloading the AI model", status, ct);
            status("Verifying…");
            Tools.VerifySha256(tmp, ModelSha256);
            File.Move(tmp, ModelPath, true);
            status("Ready.");
        }
        finally { Tools.TryDelete(tmp); }
    }

    // ---------- running it ----------
    private static InferenceSession? _session;
    private static readonly object SessionLock = new();

    private static InferenceSession Session()
    {
        lock (SessionLock)
        {
            if (_session != null) return _session;
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            options.IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount - 1);
            return _session = new InferenceSession(ModelPath, options);
        }
    }

    /// <summary>Removes the background of one picture and returns the new file's path. Call from an STA thread.</summary>
    public static string Remove(string source, string? outDir = null)
    {
        if (!File.Exists(source)) throw new IOException("The file no longer exists.");
        if (!IsPicture(source)) throw new IOException("Not a picture type this can read.");
        if (!HasModel) throw new IOException("The AI model is not installed yet.");

        var picture = ImageConverter.LoadPicture(source);
        int w = picture.PixelWidth, h = picture.PixelHeight;
        if (w * (long)h > MaxPixels) throw new IOException("That picture is too large.");

        var pixels = new byte[w * h * 4];
        new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, w * 4, 0);

        float[] mask = Predict(picture);                                   // 1024 x 1024, 0..1
        ApplyMask(pixels, w, h, mask);

        string dir = outDir is { Length: > 0 } ? outDir : Path.GetDirectoryName(Path.GetFullPath(source))!;
        Directory.CreateDirectory(dir);
        string output = ImageConverter.ReserveName(dir, Path.GetFileNameWithoutExtension(source) + " (no background)", "png");
        try
        {
            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using var fs = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(fs);
        }
        catch
        {
            try { File.Delete(output); } catch (Exception) { }
            throw;
        }
        return output;
    }

    /// <summary>The picture squeezed to 1024 x 1024 (on white, so see-through parts count as background), run through the model.</summary>
    private static float[] Predict(BitmapSource picture)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Size, Size));
            dc.DrawImage(picture, new Rect(0, 0, Size, Size));
        }
        var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var bgra = new byte[Size * Size * 4];
        rtb.CopyPixels(bgra, Size * 4, 0);

        // what the model was trained with: scale so the brightest value is 1, then subtract the usual mean per color
        int count = Size * Size;
        byte max = 1;
        for (int i = 0; i < bgra.Length; i += 4) max = Math.Max(max, Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])));
        float[] mean = { 0.485f, 0.456f, 0.406f };                          // R, G, B
        var input = new float[3 * count];
        for (int p = 0; p < count; p++)
        {
            input[p] = (bgra[p * 4 + 2] / (float)max - mean[0]);            // R
            input[count + p] = (bgra[p * 4 + 1] / (float)max - mean[1]);    // G
            input[2 * count + p] = (bgra[p * 4] / (float)max - mean[2]);    // B
        }

        var session = Session();
        var tensor = new DenseTensor<float>(input, new[] { 1, 3, Size, Size });
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor) });
        var output = results.First().AsEnumerable<float>().ToArray();
        if (output.Length < count) throw new IOException("The AI model gave an unexpected answer.");

        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < count; i++) { lo = Math.Min(lo, output[i]); hi = Math.Max(hi, output[i]); }
        var mask = new float[count];
        float range = Math.Max(1e-6f, hi - lo);
        for (int i = 0; i < count; i++) mask[i] = (output[i] - lo) / range;
        return mask;
    }

    /// <summary>Stretch the small mask over the full-size picture (smoothly) and use it as the picture's see-through channel.</summary>
    private static void ApplyMask(byte[] bgra, int w, int h, float[] mask)
    {
        Parallel.For(0, h, y =>
        {
            float fy = h == 1 ? 0 : y * (Size - 1f) / (h - 1);
            int y0 = (int)fy, y1 = Math.Min(Size - 1, y0 + 1);
            float ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                float fx = w == 1 ? 0 : x * (Size - 1f) / (w - 1);
                int x0 = (int)fx, x1 = Math.Min(Size - 1, x0 + 1);
                float tx = fx - x0;
                float top = mask[y0 * Size + x0] * (1 - tx) + mask[y0 * Size + x1] * tx;
                float bottom = mask[y1 * Size + x0] * (1 - tx) + mask[y1 * Size + x1] * tx;
                float m = top * (1 - ty) + bottom * ty;
                // clean the edge: faint haze becomes fully clear, the solid part fully solid, a smooth ramp in between
                float t = Math.Clamp((m - 0.06f) / 0.84f, 0f, 1f);
                float alpha = t * t * (3 - 2 * t);
                int i = (y * w + x) * 4;
                bgra[i + 3] = (byte)Math.Round(bgra[i + 3] * alpha);       // keep any see-through the picture already had
            }
        });
    }
}
