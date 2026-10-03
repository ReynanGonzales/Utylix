using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

/// <summary>How a straightened photo of a page looks.</summary>
public enum DocFilter
{
    /// <summary>As photographed.</summary>
    Original,
    /// <summary>Shadows and uneven light evened out, colours kept.</summary>
    Colour,
    /// <summary>Evened out and grey.</summary>
    Grey,
    /// <summary>Only black and white (every letter dark, the paper white, whatever the light).</summary>
    BlackWhite,
}

/// <summary>
/// Phone photo of a document -> a flat, clean page, in plain C# (nothing extra to install, nothing uploaded):
/// <see cref="FindCorners"/> finds the paper, <see cref="Warp"/> straightens it, <see cref="Apply"/> cleans the light.
/// Corners are always in the order top-left, top-right, bottom-right, bottom-left, in pixels of the picture.
/// </summary>
public static class DocScan
{
    public readonly record struct Pt(double X, double Y);

    // ---------- pixels ----------
    private static byte[] Bgra(BitmapSource src, out int w, out int h)
    {
        BitmapSource s = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        w = s.PixelWidth; h = s.PixelHeight;
        var data = new byte[(long)w * h * 4];
        s.CopyPixels(data, w * 4, 0);
        return data;
    }

    private static BitmapSource Make(byte[] bgra, int w, int h)
    {
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        b.Freeze();
        return b;
    }

    private static float Lum(byte[] p, int i) => (0.114f * p[i] + 0.587f * p[i + 1] + 0.299f * p[i + 2]);

    // ---------- finding the paper ----------
    /// <summary>The four corners of the page in the photo (or, when no page can be told from its surroundings, a frame just inside the picture).</summary>
    public static Pt[] FindCorners(BitmapSource src)
    {
        double scale = Math.Min(1.0, 520.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        BitmapSource small = scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
        var px = Bgra(small, out int w, out int h);
        var fallback = Inset(src.PixelWidth, src.PixelHeight);

        var lum = new float[w * h];
        for (int i = 0; i < lum.Length; i++) lum[i] = Lum(px, i * 4);
        lum = BoxBlur(lum, w, h, 2);

        // Otsu: the level that best separates the bright paper from the darker table
        var hist = new int[256];
        foreach (float v in lum) hist[Math.Clamp((int)v, 0, 255)]++;
        double total = lum.Length, sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += i * (double)hist[i];
        double wB = 0, sumB = 0, best = -1; int level = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            double wF = total - wB; if (wF == 0) break;
            sumB += t * (double)hist[t];
            double between = wB * wF * Math.Pow(sumB / wB - (sumAll - sumB) / wF, 2);
            if (between > best) { best = between; level = t; }
        }

        // the biggest bright patch (4-connected), by flood fill
        var bright = new bool[w * h];
        for (int i = 0; i < bright.Length; i++) bright[i] = lum[i] > level;
        var label = new int[w * h];
        int bestLabel = 0, bestArea = 0, next = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < bright.Length; s++)
        {
            if (!bright[s] || label[s] != 0) continue;
            next++; int area = 0;
            stack.Push(s); label[s] = next;
            while (stack.Count > 0)
            {
                int p = stack.Pop(); area++;
                int x = p % w, y = p / w;
                if (x > 0 && bright[p - 1] && label[p - 1] == 0) { label[p - 1] = next; stack.Push(p - 1); }
                if (x < w - 1 && bright[p + 1] && label[p + 1] == 0) { label[p + 1] = next; stack.Push(p + 1); }
                if (y > 0 && bright[p - w] && label[p - w] == 0) { label[p - w] = next; stack.Push(p - w); }
                if (y < h - 1 && bright[p + w] && label[p + w] == 0) { label[p + w] = next; stack.Push(p + w); }
            }
            if (area > bestArea) { bestArea = area; bestLabel = next; }
        }
        if (bestLabel == 0 || bestArea < 0.12 * w * h) return fallback;                     // nothing page-sized stands out

        // the corners are the points of that patch farthest in each diagonal direction
        double tl = double.MaxValue, br = double.MinValue, tr = double.MinValue, bl = double.MaxValue;
        Pt pTl = default, pBr = default, pTr = default, pBl = default;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (label[y * w + x] != bestLabel) continue;
                double sum = x + y, diff = x - y;
                if (sum < tl) { tl = sum; pTl = new Pt(x, y); }
                if (sum > br) { br = sum; pBr = new Pt(x, y); }
                if (diff > tr) { tr = diff; pTr = new Pt(x, y); }
                if (diff < bl) { bl = diff; pBl = new Pt(x, y); }
            }
        // pixels are squares: the far corner of a pixel is a pixel further
        var found = new[] { new Pt(pTl.X, pTl.Y), new Pt(pTr.X + 1, pTr.Y), new Pt(pBr.X + 1, pBr.Y + 1), new Pt(pBl.X, pBl.Y + 1) };
        for (int i = 0; i < 4; i++) found[i] = new Pt(found[i].X / scale, found[i].Y / scale);
        // a hair inside the page: a corner a pixel or two off would otherwise let a sliver of the table into the straightened page
        double cx = found.Average(p => p.X), cy = found.Average(p => p.Y);
        for (int i = 0; i < 4; i++) found[i] = new Pt(cx + (found[i].X - cx) * 0.992, cy + (found[i].Y - cy) * 0.992);
        found = Clamp(found, src.PixelWidth, src.PixelHeight);
        return IsSensible(found, src.PixelWidth, src.PixelHeight) ? found : fallback;
    }

    private static Pt[] Inset(int w, int h) { double mx = w * 0.06, my = h * 0.06; return new[] { new Pt(mx, my), new Pt(w - mx, my), new Pt(w - mx, h - my), new Pt(mx, h - my) }; }

    private static Pt[] Clamp(Pt[] q, int w, int h)
    {
        var r = new Pt[4];
        for (int i = 0; i < 4; i++) r[i] = new Pt(Math.Clamp(q[i].X, 0, w), Math.Clamp(q[i].Y, 0, h));
        return r;
    }

    /// <summary>A quadrilateral that is a real, convex page: big enough, corners in order.</summary>
    public static bool IsSensible(Pt[] q, int w, int h)
    {
        double area = 0;
        for (int i = 0; i < 4; i++) { var a = q[i]; var b = q[(i + 1) % 4]; area += a.X * b.Y - b.X * a.Y; }
        if (Math.Abs(area) / 2 < 0.10 * w * h) return false;
        double sign = 0;                                                          // convex: all turns the same way
        for (int i = 0; i < 4; i++)
        {
            var a = q[i]; var b = q[(i + 1) % 4]; var c = q[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (sign == 0) sign = Math.Sign(cross); else if (Math.Sign(cross) != sign && cross != 0) return false;
        }
        return true;
    }

    private static float[] BoxBlur(float[] src, int w, int h, int radius)
    {
        // a box blur twice over (separable, with running sums): fast and smooth enough
        var a = (float[])src.Clone(); var b = new float[a.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 0; y < h; y++)                                               // across
            {
                double sum = 0; int count = 0;
                for (int x = 0; x <= Math.Min(radius, w - 1); x++) { sum += a[y * w + x]; count++; }
                for (int x = 0; x < w; x++)
                {
                    b[y * w + x] = (float)(sum / count);
                    int add = x + radius + 1, drop = x - radius;
                    if (add < w) { sum += a[y * w + add]; count++; }
                    if (drop >= 0) { sum -= a[y * w + drop]; count--; }
                }
            }
            for (int x = 0; x < w; x++)                                               // down
            {
                double sum = 0; int count = 0;
                for (int y = 0; y <= Math.Min(radius, h - 1); y++) { sum += b[y * w + x]; count++; }
                for (int y = 0; y < h; y++)
                {
                    a[y * w + x] = (float)(sum / count);
                    int add = y + radius + 1, drop = y - radius;
                    if (add < h) { sum += b[add * w + x]; count++; }
                    if (drop >= 0) { sum -= b[drop * w + x]; count--; }
                }
            }
        }
        return a;
    }

    // ---------- straightening ----------
    /// <summary>Straightens the picture inside the quadrilateral into a flat page (perspective correction). The long side is at most <paramref name="maxSide"/> pixels.</summary>
    public static BitmapSource Warp(BitmapSource src, Pt[] q, int maxSide)
    {
        var px = Bgra(src, out int sw, out int sh);
        double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        double width = Math.Max(Dist(q[0], q[1]), Dist(q[3], q[2])), height = Math.Max(Dist(q[0], q[3]), Dist(q[1], q[2]));
        double shrink = Math.Min(1.0, maxSide / Math.Max(width, height));
        int ow = Math.Max(8, (int)Math.Round(width * shrink)), oh = Math.Max(8, (int)Math.Round(height * shrink));

        // unit square -> the four corners (Heckbert's projective mapping)
        double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3, a, b, c, d, e, f, g, hh;
        if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9) { a = x1 - x0; b = x3 - x0; c = x0; d = y1 - y0; e = y3 - y0; f = y0; g = hh = 0; }
        else
        {
            double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2, det = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(det) < 1e-12) { a = x1 - x0; b = x3 - x0; c = x0; d = y1 - y0; e = y3 - y0; f = y0; g = hh = 0; }
            else
            {
                g = (sx * dy2 - dx2 * sy) / det; hh = (dx1 * sy - sx * dy1) / det;
                a = x1 - x0 + g * x1; b = x3 - x0 + hh * x3; c = x0; d = y1 - y0 + g * y1; e = y3 - y0 + hh * y3; f = y0;
            }
        }

        var outPx = new byte[ow * oh * 4];
        Parallel.For(0, oh, oy =>
        {
            double v = (oy + 0.5) / oh;
            for (int ox = 0; ox < ow; ox++)
            {
                double u = (ox + 0.5) / ow, den = g * u + hh * v + 1;
                double X = (a * u + b * v + c) / den - 0.5, Y = (d * u + e * v + f) / den - 0.5;
                int ix = (int)Math.Floor(X), iy = (int)Math.Floor(Y);
                double fx = X - ix, fy = Y - iy;
                int xa = Math.Clamp(ix, 0, sw - 1), xb = Math.Clamp(ix + 1, 0, sw - 1), ya = Math.Clamp(iy, 0, sh - 1), yb = Math.Clamp(iy + 1, 0, sh - 1);
                int o = (oy * ow + ox) * 4;
                for (int k = 0; k < 3; k++)
                {
                    double p00 = px[(ya * sw + xa) * 4 + k], p10 = px[(ya * sw + xb) * 4 + k], p01 = px[(yb * sw + xa) * 4 + k], p11 = px[(yb * sw + xb) * 4 + k];
                    outPx[o + k] = (byte)Math.Clamp((int)Math.Round(p00 * (1 - fx) * (1 - fy) + p10 * fx * (1 - fy) + p01 * (1 - fx) * fy + p11 * fx * fy), 0, 255);
                }
                outPx[o + 3] = 255;
            }
        });
        return Make(outPx, ow, oh);
    }

    // ---------- cleaning the light ----------
    /// <summary>Evens out shadows and uneven light, then makes it colour, grey or black and white.</summary>
    public static BitmapSource Apply(BitmapSource page, DocFilter filter)
    {
        if (filter == DocFilter.Original) return page;
        var px = Bgra(page, out int w, out int h);
        var lum = new float[w * h];
        for (int i = 0; i < lum.Length; i++) lum[i] = Lum(px, i * 4);

        // what the paper's own brightness is around every point: the local average, big enough to ignore the letters, then the brighter half of it
        int radius = Math.Max(8, Math.Max(w, h) / 28);
        var mean = BoxBlur(lum, w, h, radius);
        var paper = new float[lum.Length];
        for (int i = 0; i < paper.Length; i++) paper[i] = Math.Max(mean[i], 1f);
        // dark letters pull the average down: raise it toward the brightest nearby paper
        paper = Raise(paper, lum, w, h, radius);

        var o = new byte[px.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, p = i * 4;
                float bg = Math.Max(paper[i], 40f);
                if (filter == DocFilter.BlackWhite)
                {
                    float ratio = lum[i] / bg;
                    byte v = ratio < 0.80f && lum[i] < 190 ? (byte)0 : (byte)255;
                    o[p] = o[p + 1] = o[p + 2] = v;
                }
                else
                {
                    // divide out the light, then stretch a little so the paper is white and the ink dark
                    float gain = 250f / bg;
                    for (int k = 0; k < 3; k++)
                    {
                        float c = px[p + k] * gain;
                        if (filter == DocFilter.Grey) c = lum[i] * gain;
                        c = (c - 20f) * (255f / (240f - 20f));
                        o[p + k] = (byte)Math.Clamp((int)Math.Round(c), 0, 255);
                    }
                }
                o[p + 3] = 255;
            }
        });
        return Make(o, w, h);
    }

    /// <summary>The paper level around each point: the average, pulled up toward the bright pixels near it (so text does not darken the estimate).</summary>
    private static float[] Raise(float[] mean, float[] lum, int w, int h, int radius)
    {
        // pixels at least as bright as the local average are the paper: average only those
        var onlyPaper = new float[lum.Length];
        var weight = new float[lum.Length];
        for (int i = 0; i < lum.Length; i++) { bool isPaper = lum[i] >= mean[i] * 0.97f; onlyPaper[i] = isPaper ? lum[i] : 0f; weight[i] = isPaper ? 1f : 0f; }
        var a = BoxBlur(onlyPaper, w, h, radius);
        var b = BoxBlur(weight, w, h, radius);
        var r = new float[lum.Length];
        for (int i = 0; i < r.Length; i++) r[i] = b[i] > 0.02f ? a[i] / b[i] : mean[i];
        return r;
    }
}
