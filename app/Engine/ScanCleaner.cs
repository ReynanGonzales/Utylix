using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

public sealed record ScanOptions(bool Straighten, bool Darken, bool Edges, bool Gray);

/// <summary>
/// Cleans the picture of a scanned or photographed page: takes off the dark edges a scanner leaves, turns a crooked page straight, evens out shadows (so the paper is white)
/// and makes faint text darker. Everything is plain pixel arithmetic (no drawing objects), so it can run on any thread.
/// </summary>
public static class ScanCleaner
{
    /// <summary>The cleaned page as a JPEG, and the angle (degrees) it was turned by (0 = it was straight).</summary>
    public static (byte[] Jpeg, double Angle) Clean(BitmapSource page, ScanOptions options)
    {
        BitmapSource src = page.Format == PixelFormats.Bgra32 ? page : new FormatConvertedBitmap(page, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var bgra = new byte[stride * h];
        src.CopyPixels(bgra, stride, 0);

        var planes = new byte[3][];
        for (int c = 0; c < 3; c++) planes[c] = new byte[w * h];
        for (int i = 0, p = 0; i < w * h; i++, p += 4) { planes[0][i] = bgra[p]; planes[1][i] = bgra[p + 1]; planes[2][i] = bgra[p + 2]; }
        var gray = new byte[w * h];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)((planes[2][i] * 77 + planes[1][i] * 150 + planes[0][i] * 29) >> 8);

        byte[][] work = options.Gray ? new[] { gray } : planes;
        if (options.Edges) RemoveEdges(gray, work, w, h);

        double angle = 0;
        if (options.Straighten)
        {
            angle = EstimateSkew(gray, w, h);
            if (Math.Abs(angle) >= 0.15) work = work.Select(p => Rotate(p, w, h, angle)).ToArray();
            else angle = 0;
        }
        if (options.Darken) foreach (var p in work) Normalize(p, w, h);

        BitmapSource result;
        if (options.Gray) result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, work[0], w);
        else
        {
            var bgr = new byte[w * h * 3];
            for (int i = 0, o = 0; i < w * h; i++, o += 3) { bgr[o] = work[0][i]; bgr[o + 1] = work[1][i]; bgr[o + 2] = work[2][i]; }
            result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bgr, w * 3);
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return (ms.ToArray(), angle);
    }

    // ---------- dark edges ----------
    /// <summary>A band along an edge that is dark all the way (the scanner's lid, the table under a photo) is painted white.</summary>
    private static void RemoveEdges(byte[] gray, byte[][] planes, int w, int h)
    {
        const int dark = 90;
        int y0 = h / 10, y1 = h - h / 10, x0 = w / 10, x1 = w - w / 10;
        int maxX = Math.Max(1, w * 8 / 100), maxY = Math.Max(1, h * 8 / 100);

        int ColMean(int x) { long s = 0; for (int y = y0; y < y1; y++) s += gray[y * w + x]; return (int)(s / Math.Max(1, y1 - y0)); }
        int RowMean(int y) { long s = 0; for (int x = x0; x < x1; x++) s += gray[y * w + x]; return (int)(s / Math.Max(1, x1 - x0)); }

        int left = 0; while (left < maxX && ColMean(left) < dark) left++;
        int right = 0; while (right < maxX && ColMean(w - 1 - right) < dark) right++;
        int top = 0; while (top < maxY && RowMean(top) < dark) top++;
        int bottom = 0; while (bottom < maxY && RowMean(h - 1 - bottom) < dark) bottom++;
        if (left + right + top + bottom == 0) return;
        left = left > 0 ? left + 2 : 0; right = right > 0 ? right + 2 : 0; top = top > 0 ? top + 2 : 0; bottom = bottom > 0 ? bottom + 2 : 0;

        foreach (var plane in planes.Append(gray).Distinct())
            for (int y = 0; y < h; y++)
            {
                bool rowEdge = y < top || y >= h - bottom;
                for (int x = 0; x < w; x++)
                    if (rowEdge || x < left || x >= w - right) plane[y * w + x] = 255;
            }
    }

    // ---------- crooked pages ----------
    /// <summary>The angle (degrees) to turn the page by so that its lines of text run level; 0 when nothing clear is found.</summary>
    private static double EstimateSkew(byte[] gray, int w, int h)
    {
        int f = Math.Max(1, w / 700), gw = w / f, gh = h / f;
        if (gw < 50 || gh < 50) return 0;
        var small = new byte[gw * gh];
        var hist = new int[256];
        for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
            {
                int sum = 0, n = 0;
                for (int yy = 0; yy < f; yy++) for (int xx = 0; xx < f; xx++) { sum += gray[(y * f + yy) * w + x * f + xx]; n++; }
                byte v = (byte)(sum / n);
                small[y * gw + x] = v; hist[v]++;
            }
        int threshold = Otsu(hist, gw * gh);
        if (threshold > 200) threshold = 200;

        var xs = new System.Collections.Generic.List<float>(); var ys = new System.Collections.Generic.List<float>();
        int total = 0; for (int i = 0; i < small.Length; i++) if (small[i] < threshold) total++;
        int step = Math.Max(1, total / 80000), seen = 0;
        float cx = gw / 2f, cy = gh / 2f;
        for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
                if (small[y * gw + x] < threshold && (seen++ % step) == 0) { xs.Add(x - cx); ys.Add(y - cy); }
        if (xs.Count < 300) return 0;

        var bins = new int[gh + 2 * gw + 8];
        double Score(double deg)
        {
            double a = deg * Math.PI / 180, s = Math.Sin(a), c = Math.Cos(a);
            Array.Clear(bins);
            for (int i = 0; i < xs.Count; i++) bins[(int)(xs[i] * s + ys[i] * c + cy) + gw + 2]++;
            double sum = 0; foreach (int b in bins) sum += (double)b * b;
            return sum;
        }
        double baseScore = Score(0), best = 0, bestScore = baseScore;
        for (double a = -6; a <= 6.001; a += 0.5) { double sc = Score(a); if (sc > bestScore) { bestScore = sc; best = a; } }
        double centre = best;
        for (double a = centre - 0.5; a <= centre + 0.501; a += 0.1) { double sc = Score(a); if (sc > bestScore) { bestScore = sc; best = a; } }
        return bestScore > baseScore * 1.03 ? Math.Round(best, 2) : 0;
    }

    private static int Otsu(int[] hist, int total)
    {
        double sum = 0; for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
        double sumB = 0, best = -1; int wB = 0, threshold = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            int wF = total - wB; if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF, between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = t; }
        }
        return threshold;
    }

    /// <summary>Turns the picture by <paramref name="deg"/> degrees about its middle (the same direction the score above measured); what comes in from outside is white.</summary>
    private static byte[] Rotate(byte[] plane, int w, int h, double deg)
    {
        double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a), cx = w / 2.0, cy = h / 2.0;
        var output = new byte[w * h];
        for (int v = 0; v < h; v++)
            for (int u = 0; u < w; u++)
            {
                double dx = u - cx, dy = v - cy;
                double x = dx * c + dy * s + cx, y = -dx * s + dy * c + cy;
                int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
                if (ix < 0 || iy < 0 || ix >= w - 1 || iy >= h - 1) { output[v * w + u] = 255; continue; }
                double fx = x - ix, fy = y - iy;
                int i = iy * w + ix;
                double top = plane[i] * (1 - fx) + plane[i + 1] * fx, bottom = plane[i + w] * (1 - fx) + plane[i + w + 1] * fx;
                output[v * w + u] = (byte)(top * (1 - fy) + bottom * fy + 0.5);
            }
        return output;
    }

    // ---------- shadows and faint text ----------
    /// <summary>The paper (found as the bright level of each block) is made white, then the levels are stretched so faint text turns dark.</summary>
    private static void Normalize(byte[] plane, int w, int h)
    {
        int block = Math.Max(16, w / 60), bw = (w + block - 1) / block, bh = (h + block - 1) / block;
        var paper = new float[bw * bh];
        var hist = new int[256];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                Array.Clear(hist);
                int n = 0;
                for (int y = by * block; y < Math.Min(h, (by + 1) * block); y += 2)
                    for (int x = bx * block; x < Math.Min(w, (bx + 1) * block); x += 2) { hist[plane[y * w + x]]++; n++; }
                int target = (int)(n * 0.85), acc = 0, level = 255;
                for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= target) { level = v; break; } }
                paper[by * bw + bx] = Math.Max(150, level);
            }
        for (int pass = 0; pass < 2; pass++)
        {
            var next = new float[paper.Length];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    float sum = 0; int n = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = bx + dx, y = by + dy;
                        if (x < 0 || y < 0 || x >= bw || y >= bh) continue;
                        sum += paper[y * bw + x]; n++;
                    }
                    next[by * bw + bx] = sum / n;
                }
            paper = next;
        }

        var curve = new byte[256];
        const double low = 50, high = 210, gamma = 1.7;
        for (int v = 0; v < 256; v++)
        {
            double x = Math.Clamp((v - low) / (high - low), 0, 1);
            curve[v] = (byte)Math.Round(255 * Math.Pow(x, gamma));
        }
        for (int y = 0; y < h; y++)
        {
            double gy = Math.Clamp((y + 0.5) / block - 0.5, 0, bh - 1), fy = gy - Math.Floor(gy);
            int y0 = (int)Math.Floor(gy), y1 = Math.Min(bh - 1, y0 + 1);
            for (int x = 0; x < w; x++)
            {
                double gx = Math.Clamp((x + 0.5) / block - 0.5, 0, bw - 1), fx = gx - Math.Floor(gx);
                int x0 = (int)Math.Floor(gx), x1 = Math.Min(bw - 1, x0 + 1);
                double bg = (paper[y0 * bw + x0] * (1 - fx) + paper[y0 * bw + x1] * fx) * (1 - fy) + (paper[y1 * bw + x0] * (1 - fx) + paper[y1 * bw + x1] * fx) * fy;
                int v = (int)(plane[y * w + x] * 255.0 / bg + 0.5);
                plane[y * w + x] = curve[Math.Min(255, v)];
            }
        }
    }
}
