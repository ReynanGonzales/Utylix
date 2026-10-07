using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace IdmClone;

/// <summary>
/// Alignment guides: while you move something (one thing, several chosen things, or things already in the PDF) it snaps to the edges and middles of the other things on the page and to the
/// page's own edges and middle, and a thin pink line shows what it lined up with. Hold Alt to move freely. Gone when you let go.
/// </summary>
public sealed partial class PdfWindow
{
    private sealed record Guide(int Page, bool Vertical, double At, double From, double To);

    private readonly List<Guide> _guides = new();
    private List<Rect>? _snapTargets;                         // the other things on the page (read once per drag)
    private Rect? _groupStartBox;                              // a chosen group: where it was when the drag began
    private Vector _groupApplied;                              // and how far it has been moved since

    private void ResetSnap()
    {
        _snapTargets = null; _groupStartBox = null; _groupApplied = default;
        _guides.Clear();
    }

    private static readonly Brush GuideBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0x1E, 0x8C));

    /// <summary>The things a moving thing can line up with: what else is on the page, and the page itself.</summary>
    private List<Rect> SnapTargetsFor(int page, IReadOnlyCollection<EditItem> movingItems, IReadOnlyCollection<int>? movingObjects)
    {
        var list = new List<Rect>();
        var size = page < _sizes.Length ? _sizes[page] : new Size(595, 842);
        list.Add(new Rect(0, 0, size.Width, size.Height));
        foreach (var item in _items)
        {
            if (item.Page != page || movingItems.Contains(item) || item is LinkDraft or LinkPick or PageObjectItem) continue;
            var b = item.Bounds;
            if (!b.IsEmpty && b.Width + b.Height > 1) list.Add(b);
        }
        foreach (var f in Fields(page))                                                      // (form fields already saved in the PDF are not page objects: they are lined up with too)
            if (f.Box.Width + f.Box.Height > 1) list.Add(f.Box);
        foreach (var o in PageObjects(page))
        {
            if (movingObjects != null && movingObjects.Contains(o.Index)) continue;
            if (IsBackdrop(o, page) || o.Box.Width + o.Box.Height <= 1) continue;
            list.Add(o.Box);
        }
        return list;
    }

    /// <summary>
    /// How far to shift a thing that is being moved so that it lines up (0 when nothing is close, or when Alt is held). The lines that matched are kept in <see cref="_guides"/>.
    /// </summary>
    private Vector SnapOffset(int page, Rect moving, IReadOnlyCollection<EditItem> movingItems, IReadOnlyCollection<int>? movingObjects)
    {
        _guides.Clear();
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 || moving.IsEmpty) return default;
        _snapTargets ??= SnapTargetsFor(page, movingItems, movingObjects);
        double k = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX);        // screen pixels -> points
        double threshold = 6 * k;

        static double[] Xs(Rect r) => new[] { r.Left, r.Left + r.Width / 2, r.Right };
        static double[] Ys(Rect r) => new[] { r.Top, r.Top + r.Height / 2, r.Bottom };
        double bestX = double.MaxValue, bestY = double.MaxValue;
        foreach (var t in _snapTargets)
        {
            foreach (double mx in Xs(moving)) foreach (double tx in Xs(t)) if (Math.Abs(tx - mx) < Math.Abs(bestX) && Math.Abs(tx - mx) <= threshold) bestX = tx - mx;
            foreach (double my in Ys(moving)) foreach (double ty in Ys(t)) if (Math.Abs(ty - my) < Math.Abs(bestY) && Math.Abs(ty - my) <= threshold) bestY = ty - my;
        }
        var offset = new Vector(bestX == double.MaxValue ? 0 : bestX, bestY == double.MaxValue ? 0 : bestY);
        var snapped = new Rect(moving.X + offset.X, moving.Y + offset.Y, moving.Width, moving.Height);

        // the lines to show: everything that is now (nearly) on the same edge or middle
        foreach (var t in _snapTargets)
        {
            foreach (double mx in Xs(snapped)) foreach (double tx in Xs(t))
                if (Math.Abs(tx - mx) < 0.4 * k + 0.05) AddGuide(new Guide(page, true, tx, Math.Min(snapped.Top, t.Top), Math.Max(snapped.Bottom, t.Bottom)));
            foreach (double my in Ys(snapped)) foreach (double ty in Ys(t))
                if (Math.Abs(ty - my) < 0.4 * k + 0.05) AddGuide(new Guide(page, false, ty, Math.Min(snapped.Left, t.Left), Math.Max(snapped.Right, t.Right)));
        }
        return offset;
    }

    /// <summary>
    /// The same lining up while a thing is made bigger or smaller by its corner (not for turned things): the right and bottom edge snap to the edges / middles of the others.
    /// A thing that keeps its shape follows whichever edge is closer and the other side is worked out from it.
    /// </summary>
    private Rect SnapSize(int page, Rect size, bool keepAspect, IReadOnlyCollection<EditItem> movingItems, IReadOnlyCollection<int>? movingObjects)
    {
        _guides.Clear();
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 || size.IsEmpty) return size;
        _snapTargets ??= SnapTargetsFor(page, movingItems, movingObjects);
        double k = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX), threshold = 6 * k;
        double dx = double.MaxValue, dy = double.MaxValue;
        foreach (var t in _snapTargets)
        {
            foreach (double tx in new[] { t.Left, t.Left + t.Width / 2, t.Right }) if (Math.Abs(tx - size.Right) <= threshold && Math.Abs(tx - size.Right) < Math.Abs(dx)) dx = tx - size.Right;
            foreach (double ty in new[] { t.Top, t.Top + t.Height / 2, t.Bottom }) if (Math.Abs(ty - size.Bottom) <= threshold && Math.Abs(ty - size.Bottom) < Math.Abs(dy)) dy = ty - size.Bottom;
        }
        double w = size.Width, h = size.Height;
        if (keepAspect && size.Width > 0 && size.Height > 0)
        {
            double ratio = size.Height / size.Width;
            if (dx != double.MaxValue && (dy == double.MaxValue || Math.Abs(dx) <= Math.Abs(dy))) { w += dx; h = w * ratio; }
            else if (dy != double.MaxValue) { h += dy; w = h / ratio; }
        }
        else
        {
            if (dx != double.MaxValue) w += dx;
            if (dy != double.MaxValue) h += dy;
        }
        w = Math.Max(4, w); h = Math.Max(4, h);
        var snapped = new Rect(size.X, size.Y, w, h);
        foreach (var t in _snapTargets)
        {
            foreach (double tx in new[] { t.Left, t.Left + t.Width / 2, t.Right })
                if (Math.Abs(tx - snapped.Right) < 0.4 * k + 0.05) AddGuide(new Guide(page, true, tx, Math.Min(snapped.Top, t.Top), Math.Max(snapped.Bottom, t.Bottom)));
            foreach (double ty in new[] { t.Top, t.Top + t.Height / 2, t.Bottom })
                if (Math.Abs(ty - snapped.Bottom) < 0.4 * k + 0.05) AddGuide(new Guide(page, false, ty, Math.Min(snapped.Left, t.Left), Math.Max(snapped.Right, t.Right)));
        }
        return snapped;
    }

    private void AddGuide(Guide g)
    {
        int i = _guides.FindIndex(x => x.Vertical == g.Vertical && Math.Abs(x.At - g.At) < 0.3);
        if (i < 0) _guides.Add(g);
        else _guides[i] = g with { From = Math.Min(_guides[i].From, g.From), To = Math.Max(_guides[i].To, g.To) };
    }

    /// <summary>Draws the guide lines of a page on its editing layer (called by <see cref="RenderItems"/>).</summary>
    private void DrawGuides(int page, System.Windows.Controls.Canvas overlay)
    {
        if (_guides.Count == 0) return;
        double k = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX);
        foreach (var g in _guides.Where(g => g.Page == page))
        {
            var line = new Line { Stroke = GuideBrush, StrokeThickness = 1 * k, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
            if (g.Vertical) { line.X1 = line.X2 = g.At; line.Y1 = g.From - 6 * k; line.Y2 = g.To + 6 * k; }
            else { line.Y1 = line.Y2 = g.At; line.X1 = g.From - 6 * k; line.X2 = g.To + 6 * k; }
            overlay.Children.Add(line);
        }
    }
}
