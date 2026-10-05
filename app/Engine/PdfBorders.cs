using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace IdmClone.Engine;

public enum PdfBorderStyle { Solid, Dashed, Double }

/// <summary>A frame around the pages.</summary>
public sealed class PdfBorder
{
    public Color Color { get; set; } = Colors.Black;
    /// <summary>Line thickness, points.</summary>
    public double Width { get; set; } = 2;
    /// <summary>Distance from the edge of the page, points.</summary>
    public double Margin { get; set; } = 24;
    public PdfBorderStyle Style { get; set; } = PdfBorderStyle.Solid;
    public bool Rounded { get; set; }
}

/// <summary>Works out the lines of a page border (the same ones are drawn in the preview and written into the PDF).</summary>
public static class PdfBorders
{
    private const double Kappa = 0.5522847498;

    /// <summary>The outline of a rectangle (rounded or not) as points all the way round, for dashes.</summary>
    private static List<Point> Outline(Rect r, double radius)
    {
        var pts = new List<Point>();
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        if (radius < 0.5) { pts.AddRange(new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft }); return pts; }
        void Corner(Point centre, double from)
        {
            for (int i = 0; i <= 8; i++) { double a = (from + 90.0 * i / 8) * Math.PI / 180; pts.Add(new Point(centre.X + radius * Math.Cos(a), centre.Y + radius * Math.Sin(a))); }
        }
        Corner(new Point(r.Left + radius, r.Top + radius), 180);
        Corner(new Point(r.Right - radius, r.Top + radius), 270);
        Corner(new Point(r.Right - radius, r.Bottom - radius), 0);
        Corner(new Point(r.Left + radius, r.Bottom - radius), 90);
        return pts;
    }

    private static PdfFigure Frame(Rect r, double radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        if (radius < 0.5)
            return new PdfFigure(r.TopLeft, new[] { PdfSegment.Line(r.TopRight), PdfSegment.Line(r.BottomRight), PdfSegment.Line(r.BottomLeft) }, true);
        double k = radius * Kappa;
        var segs = new List<PdfSegment>
        {
            PdfSegment.Line(new Point(r.Right - radius, r.Top)),
            new(new Point(r.Right - radius + k, r.Top), new Point(r.Right, r.Top + radius - k), new Point(r.Right, r.Top + radius), true),
            PdfSegment.Line(new Point(r.Right, r.Bottom - radius)),
            new(new Point(r.Right, r.Bottom - radius + k), new Point(r.Right - radius + k, r.Bottom), new Point(r.Right - radius, r.Bottom), true),
            PdfSegment.Line(new Point(r.Left + radius, r.Bottom)),
            new(new Point(r.Left + radius - k, r.Bottom), new Point(r.Left, r.Bottom - radius + k), new Point(r.Left, r.Bottom - radius), true),
            PdfSegment.Line(new Point(r.Left, r.Top + radius)),
            new(new Point(r.Left, r.Top + radius - k), new Point(r.Left + radius - k, r.Top), new Point(r.Left + radius, r.Top), true),
        };
        return new PdfFigure(new Point(r.Left + radius, r.Top), segs, true);
    }

    private static IEnumerable<PdfFigure> Dashes(Rect r, double radius, double width)
    {
        var pts = Outline(r, radius);
        pts.Add(pts[0]);
        double dash = Math.Max(4, width * 4.5), gap = Math.Max(3, width * 3);
        double into = 0; bool on = true;
        var current = new List<Point> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            double len = (b - a).Length;
            if (len < 1e-6) continue;
            double done = 0;
            while (len - done > 1e-6)
            {
                double limit = on ? dash : gap;
                double step = Math.Min(limit - into, len - done);
                done += step; into += step;
                var at = new Point(a.X + (b.X - a.X) * done / len, a.Y + (b.Y - a.Y) * done / len);
                if (on) current.Add(at);
                if (into >= limit - 1e-6)
                {
                    if (on && current.Count >= 2) yield return new PdfFigure(current[0], current.Skip(1).Select(PdfSegment.Line).ToList(), false);
                    on = !on; into = 0;
                    current = new List<Point> { at };
                }
            }
        }
        if (on && current.Count >= 2) yield return new PdfFigure(current[0], current.Skip(1).Select(PdfSegment.Line).ToList(), false);
    }

    /// <summary>The figures of the border of one page of this size (points).</summary>
    public static List<PdfFigure> Figures(Size page, PdfBorder b)
    {
        var figures = new List<PdfFigure>();
        double m = Math.Clamp(b.Margin, 0, Math.Min(page.Width, page.Height) / 2 - 4);
        double radius = b.Rounded ? Math.Clamp(Math.Min(page.Width, page.Height) * 0.04, 8, 26) : 0;
        var outer = new Rect(m, m, page.Width - 2 * m, page.Height - 2 * m);
        switch (b.Style)
        {
            case PdfBorderStyle.Dashed: figures.AddRange(Dashes(outer, radius, b.Width)); break;
            case PdfBorderStyle.Double:
            {
                double gap = b.Width * 2.2 + 2;
                figures.Add(Frame(outer, radius));
                var inner = new Rect(outer.X + gap, outer.Y + gap, outer.Width - 2 * gap, outer.Height - 2 * gap);
                if (inner.Width > 4 && inner.Height > 4) figures.Add(Frame(inner, Math.Max(0, radius - gap)));
                break;
            }
            default: figures.Add(Frame(outer, radius)); break;
        }
        return figures;
    }

    public static List<PdfMark> Build(IEnumerable<int> pages, Func<int, Size> size, PdfBorder b)
    {
        var marks = new List<PdfMark>();
        foreach (int page in pages)
            marks.Add(new PdfPathMark(page, Figures(size(page), b), b.Color, Math.Max(0.25, b.Width), null, false));
        return marks;
    }
}
