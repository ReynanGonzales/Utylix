using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IdmClone.Engine;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IdmClone.Vid;

/// <summary>
/// Takes the background out of a video clip with the same AI model Utylix's picture Background Remover uses (runs on this PC; nothing is uploaded).
/// Every frame is run through the model, the result becomes a new file WITH transparency (ProRes 4444 in a .mov) that replaces the clip's picture,
/// so everything under it on the timeline shows through behind the person or thing.
/// </summary>
public static class VidMatte
{
    private const int Size = 1024;                                   // the model looks at every frame as 1024 x 1024

    public static string OutputFolder => Path.Combine(VidSettings.Folder.Length > 0 ? VidSettings.Folder : Path.Combine(Path.GetTempPath(), "UtylixVid"), "mattes");

    /// <summary>
    /// Makes the transparent version of a part of a file.
    /// <paramref name="every"/> = 1 runs the model on every frame; 2 on every second frame (the mask is kept for the one between): twice as fast.
    /// Returns the new file's path.
    /// </summary>
    public static async Task<string> RunAsync(MediaItem m, double srcIn, double length, double fps, int every, IProgress<double> progress, CancellationToken ct)
    {
        if (!BackgroundRemover.HasModel) throw new IOException("The AI model is not installed yet.");
        if (!m.HasVideo || m.Width <= 0) throw new IOException("This clip has no picture to cut out.");
        Directory.CreateDirectory(OutputFolder);
        string output = Path.Combine(OutputFolder, Path.GetFileNameWithoutExtension(m.Path) + " (no background) " + Guid.NewGuid().ToString("N")[..6] + ".mov");

        // work at most 1280 wide: the model sees 1024 x 1024 anyway, and the new file stays a sane size
        double scale = Math.Min(1, 1280.0 / m.Width);
        int w = Math.Max(2, (int)Math.Round(m.Width * scale) / 2 * 2), h = Math.Max(2, (int)Math.Round(m.Height * scale) / 2 * 2);
        int total = Math.Max(1, (int)Math.Round(length * fps));
        string f = fps.ToString("0.###", CultureInfo.InvariantCulture);

        var dec = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-ss", srcIn.ToString("0.###", CultureInfo.InvariantCulture), "-t", length.ToString("0.###", CultureInfo.InvariantCulture),
                                  "-i", m.Path, "-an", "-vf", $"fps={f},scale={w}:{h},setsar=1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }) dec.ArgumentList.Add(a);
        var enc = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-r", f, "-i", "pipe:0",
                                  "-c:v", "prores_ks", "-profile:v", "4444", "-pix_fmt", "yuva444p10le", "-vendor", "apl0", "-y", output }) enc.ArgumentList.Add(a);

        Process? pd = null, pe = null;
        try
        {
            pd = Process.Start(dec) ?? throw new IOException("ffmpeg could not be started.");
            pe = Process.Start(enc) ?? throw new IOException("ffmpeg could not be started.");
            using var reg = ct.Register(() => { try { pd.Kill(true); } catch (InvalidOperationException) { } try { pe.Kill(true); } catch (InvalidOperationException) { } });
            _ = pd.StandardError.ReadToEndAsync();
            var decErr = pe.StandardError.ReadToEndAsync();

            int frameBytes = w * h * 4;
            var frame = new byte[frameBytes];
            var input = pd.StandardOutput.BaseStream;
            var sink = pe.StandardInput.BaseStream;
            float[]? mask = null, previous = null;
            int index = 0;
            await Task.Run(() =>
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int got = 0;
                    while (got < frameBytes)
                    {
                        int n = input.Read(frame, got, frameBytes - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < frameBytes) break;
                    if (mask == null || index % Math.Max(1, every) == 0)
                    {
                        var fresh = Predict(frame, w, h);
                        if (previous != null)
                            for (int i = 0; i < fresh.Length; i++) fresh[i] = previous[i] * 0.3f + fresh[i] * 0.7f;       // steadies the edge from frame to frame
                        mask = fresh;
                        previous = fresh;
                    }
                    ApplyMask(frame, w, h, mask!);
                    sink.Write(frame, 0, frameBytes);
                    index++;
                    if (index % 3 == 0) progress.Report(Math.Min(1, index / (double)total));
                }
            }, ct);
            sink.Close();
            await pe.WaitForExitAsync(ct);
            string err = await decErr;
            if (pe.ExitCode != 0) throw new IOException("The new file could not be made:\n" + err.Trim());
            if (index == 0) throw new IOException("No picture could be read from this clip.");
            progress.Report(1);
            return output;
        }
        catch
        {
            try { pd?.Kill(true); } catch (Exception) { }
            try { pe?.Kill(true); } catch (Exception) { }
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
            throw;
        }
        finally { pd?.Dispose(); pe?.Dispose(); }
    }

    /// <summary>The frame squeezed to 1024 x 1024, run through the model; the mask comes back 1024 x 1024, 0..1.</summary>
    private static float[] Predict(byte[] bgra, int w, int h)
    {
        int count = Size * Size;
        // resize (bilinear) straight from the frame's bytes, on white where the frame is see-through (never the case for video, kept for safety)
        var small = new byte[count * 3];                                     // R, G, B
        System.Threading.Tasks.Parallel.For(0, Size, y =>
        {
            float fy = y * (h - 1f) / (Size - 1);
            int y0 = (int)fy, y1 = Math.Min(h - 1, y0 + 1);
            float ty = fy - y0;
            for (int x = 0; x < Size; x++)
            {
                float fx = x * (w - 1f) / (Size - 1);
                int x0 = (int)fx, x1 = Math.Min(w - 1, x0 + 1);
                float tx = fx - x0;
                for (int c = 0; c < 3; c++)
                {
                    int ch = 2 - c;                                          // BGRA -> R, G, B
                    float a = bgra[(y0 * w + x0) * 4 + ch] * (1 - tx) + bgra[(y0 * w + x1) * 4 + ch] * tx;
                    float b = bgra[(y1 * w + x0) * 4 + ch] * (1 - tx) + bgra[(y1 * w + x1) * 4 + ch] * tx;
                    small[(y * Size + x) * 3 + c] = (byte)(a * (1 - ty) + b * ty);
                }
            }
        });
        // what the model was trained with: scale so the brightest value is 1, then subtract the usual mean per colour
        byte max = 1;
        for (int i = 0; i < small.Length; i++) if (small[i] > max) max = small[i];
        float[] mean = { 0.485f, 0.456f, 0.406f };
        var tensorData = new float[3 * count];
        for (int p = 0; p < count; p++)
        {
            tensorData[p] = small[p * 3] / (float)max - mean[0];
            tensorData[count + p] = small[p * 3 + 1] / (float)max - mean[1];
            tensorData[2 * count + p] = small[p * 3 + 2] / (float)max - mean[2];
        }
        var session = BackgroundRemover.Session();
        var tensor = new DenseTensor<float>(tensorData, new[] { 1, 3, Size, Size });
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor) });
        var output = results.First().AsEnumerable<float>().ToArray();
        if (output.Length < count) throw new IOException("The AI model gave an unexpected answer.");
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < count; i++) { if (output[i] < lo) lo = output[i]; if (output[i] > hi) hi = output[i]; }
        var mask = new float[count];
        float range = Math.Max(1e-6f, hi - lo);
        for (int i = 0; i < count; i++) mask[i] = (output[i] - lo) / range;
        return mask;
    }

    /// <summary>Stretches the mask over the frame (smoothly) and writes it into the frame's see-through channel.</summary>
    private static void ApplyMask(byte[] bgra, int w, int h, float[] mask)
    {
        System.Threading.Tasks.Parallel.For(0, h, y =>
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
                float t = Math.Clamp((m - 0.06f) / 0.84f, 0f, 1f);       // faint haze becomes clear, the solid part solid, a smooth ramp between
                float alpha = t * t * (3 - 2 * t);
                bgra[(y * w + x) * 4 + 3] = (byte)Math.Round(255 * alpha);
            }
        });
    }
}
