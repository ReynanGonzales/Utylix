using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Text in columns, a border around the pages, and taking a watermark out: the "…" menu's newer page tools.</summary>
public sealed partial class PdfWindow
{
    /// <summary>A block of text set in columns (like Word's): the width can be changed, the height follows the words.</summary>
    private sealed class ColumnsItem : EditItem
    {
        public Point TopLeft;
        public ColumnsSpec Spec = new();
        public double AngleDeg;                          // turned clockwise around the middle of the block
        private string _key = "";
        private (List<List<string>> Columns, double Height, double LineHeight) _flow;

        private (List<List<string>> Columns, double Height, double LineHeight) Flow()
        {
            string k = $"{Spec.Text.GetHashCode()}|{Spec.Columns}|{Spec.Gap}|{Spec.Width}|{Spec.Font}|{Spec.FontName}|{Spec.Bold}|{Spec.Size}";
            if (k != _key) { _flow = PdfColumns.Flow(Spec); _key = k; }
            return _flow;
        }

        public override Rect Bounds => new(TopLeft, new Size(Math.Max(20, Spec.Width), Math.Max(4, Flow().Height)));
        public override bool CanRotate => true;
        public override double Angle => AngleDeg;
        public override void SetAngle(double degrees) => AngleDeg = degrees;
        Point Centre { get { var b = Bounds; return new Point(b.X + b.Width / 2, b.Y + b.Height / 2); } }
        public override bool Hit(Point p) => Inflate(Bounds, 3).Contains(AngleDeg == 0 ? p : Rot(p, Centre, -AngleDeg));
        public override EditItem Clone() { var c = (ColumnsItem)MemberwiseClone(); c.Spec = Spec.Clone(); return c; }

        public override FrameworkElement Build()
        {
            var canvas = new Canvas();
            var (columns, _, _) = Flow();
            double colWidth = PdfColumns.ColumnWidth(Spec);
            for (int c = 0; c < columns.Count; c++)
            {
                var t = new TextBlock
                {
                    Text = string.Join("\n", columns[c]), FontFamily = PdfColumns.Family(Spec), FontSize = Spec.Size, FontWeight = Spec.Bold ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(Color),
                };
                Canvas.SetLeft(t, TopLeft.X + c * (colWidth + Spec.Gap)); Canvas.SetTop(t, TopLeft.Y);
                canvas.Children.Add(t);
            }
            if (AngleDeg != 0) canvas.RenderTransform = new RotateTransform(AngleDeg, Centre.X, Centre.Y);
            return canvas;
        }

        public override IEnumerable<PdfMark> Marks()
        {
            var (columns, _, _) = Flow();
            double colWidth = PdfColumns.ColumnWidth(Spec);
            var f = PdfColumns.Family(Spec);
            for (int c = 0; c < columns.Count; c++)
            {
                string text = string.Join("\n", columns[c]);
                if (text.Trim().Length == 0) continue;
                yield return new PdfTextMark(Page, new Point(TopLeft.X + c * (colWidth + Spec.Gap), TopLeft.Y), text, Spec.Font, Spec.Bold, Spec.Size, Color, f.LineSpacing, f.Baseline, Spec.FontName, AngleDeg, AngleDeg != 0 ? Centre : null);
            }
        }

        public override void MoveBy(Vector d) => TopLeft += d;
        public override void ResizeTo(Rect r)                                                                  // (only the width: the height follows the words)
        {
            var corner = AngleDeg == 0 ? r.TopLeft : Rot(Bounds.TopLeft, Centre, AngleDeg);
            Spec.Width = Math.Max(60, r.Width);
            TopLeft = AngleDeg == 0 ? r.TopLeft : TopLeftFor(corner, Bounds.Size, AngleDeg);
        }
    }

    // ---------- text in columns ----------
    private void AddColumns()
    {
        if (_pdf == null || _pages.Count == 0) return;
        if (!_editing) { EnterEditing(); if (!_editing) return; }
        var (pv, at) = VisibleSpot();
        double pageWidth = pv.Overlay.Width;
        var spec = new ColumnsSpec { Width = Math.Round(Math.Min(pageWidth - 72, pageWidth * 0.8)), Color = _toolColors.TryGetValue(EditTool.Text, out var c) ? c : Colors.Black };
        var dialog = new PdfColumnsDialog(this, spec, pageWidth, editing: false);
        if (dialog.ShowDialog() != true) return;
        var item = new ColumnsItem { Page = pv.Index, Spec = dialog.Spec, Color = dialog.Spec.Color };
        item.TopLeft = new Point(Math.Max(0, (pageWidth - item.Spec.Width) / 2), Math.Clamp(at.Y - 40, 24, Math.Max(24, pv.Overlay.Height - 80)));
        Add(item, select: true);
        Toast("Drag it into place; double-click it to change the words or the columns");
    }

    private void EditColumns(ColumnsItem item)
    {
        var spec = item.Spec.Clone();
        spec.Color = item.Color;
        var dialog = new PdfColumnsDialog(this, spec, _pages[item.Page].Overlay.Width, editing: true);
        if (dialog.ShowDialog() != true) return;
        Snapshot();
        item.Spec = dialog.Spec; item.Color = dialog.Spec.Color;
        RenderItems(item.Page);
        UpdateEditButtons();
    }

    // ---------- a border around the pages ----------
    private void AddBorder()
    {
        if (_pdf == null || _path == null) return;
        var dialog = new PdfBorderDialog(this, _pdf, _current);
        if (dialog.ShowDialog() != true) return;
        var pdf = _pdf;
        var marks = PdfBorders.Build(dialog.Pages, pdf.PageSize, dialog.Border);
        int n = dialog.Pages.Count;
        PageOp(p => PdfMarkWriter.Apply(p, marks), new[] { Math.Clamp(_current, 0, pdf.PageCount - 1) },
               $"Border added to {(n == 1 ? "page " + (dialog.Pages[0] + 1) : n == pdf.PageCount ? "all pages" : n + " pages")}. Undo takes it away until you save");
    }

    // ---------- taking a watermark out ----------
    private void RemoveWatermark()
    {
        if (_pdf == null || _path == null || !PreparePageOp()) return;
        var dialog = new PdfWatermarkDialog(this, _pdf);
        if (dialog.ShowDialog() != true) return;
        var pdf = _pdf;
        int removed = 0;
        PageOp(p => removed = PdfWatermarks.Remove(p, dialog.Chosen), () => new[] { Math.Clamp(_current, 0, pdf.PageCount - 1) },
               () => removed == 0 ? "Nothing was removed" : $"Removed {removed} piece{(removed == 1 ? "" : "s")} ({dialog.Summary}). Undo brings {(removed == 1 ? "it" : "them")} back until you save");
    }
}
