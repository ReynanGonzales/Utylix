using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// A table drawn on the page: drag its size, choose how many rows and columns, and it is a grid of lines (colour and thickness from the bar). Type in the cells with the Text tool.
/// Move and resize it like anything else; double-click it (or press Enter) to change the rows and columns.
/// </summary>
public sealed partial class PdfWindow
{
    private int _tableRows = 3, _tableCols = 3;

    private sealed class TableItem : EditItem
    {
        public Rect Box;
        public int Rows = 3, Cols = 3;
        public double Width = 1;

        public override Rect Bounds => Box;
        public override EditItem Clone() => (TableItem)MemberwiseClone();
        public override void MoveBy(Vector d) => Box.Offset(d);
        public override void ResizeTo(Rect r) => Box = r;

        public List<PdfFigure> Figures()
        {
            var r = Box;
            var figures = new List<PdfFigure> { new(r.TopLeft, new[] { PdfSegment.Line(r.TopRight), PdfSegment.Line(r.BottomRight), PdfSegment.Line(r.BottomLeft) }, true) };
            for (int row = 1; row < Rows; row++)
            {
                double y = r.Y + r.Height * row / Rows;
                figures.Add(new PdfFigure(new Point(r.X, y), new[] { PdfSegment.Line(new Point(r.Right, y)) }, false));
            }
            for (int col = 1; col < Cols; col++)
            {
                double x = r.X + r.Width * col / Cols;
                figures.Add(new PdfFigure(new Point(x, r.Y), new[] { PdfSegment.Line(new Point(x, r.Bottom)) }, false));
            }
            return figures;
        }

        public override FrameworkElement Build() => new System.Windows.Shapes.Path
        {
            Data = Geometry(Figures()), Stroke = new SolidColorBrush(Color), StrokeThickness = Width, StrokeLineJoin = PenLineJoin.Miter, SnapsToDevicePixels = true,
        };

        public override IEnumerable<PdfMark> Marks() { yield return new PdfPathMark(Page, Figures(), Color, Width, null, false); }
    }

    /// <summary>A small window for the number of rows and columns. Null = cancelled.</summary>
    private (int Rows, int Cols)? AskTableSize(int rows, int cols)
    {
        var rowsBox = new TextBox { Text = rows.ToString(), Width = 70, TextAlignment = TextAlignment.Center };
        var colsBox = new TextBox { Text = cols.ToString(), Width = 70, TextAlignment = TextAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(rowsBox, "PdfTableRows");
        System.Windows.Automation.AutomationProperties.SetAutomationId(colsBox, "PdfTableCols");
        var dlg = new Window
        {
            Title = "Table", Width = 330, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("BgBrush"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        WindowTheme.DarkTitleBar(dlg);
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "PdfTableOk");
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; dlg.Close(); };
        var grid = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        grid.Children.Add(new TextBlock { Text = "Rows", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        grid.Children.Add(rowsBox);
        grid.Children.Add(new TextBlock { Text = "Columns", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
        grid.Children.Add(colsBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "How many rows and columns?", Opacity = 0.8 });
        root.Children.Add(grid);
        root.Children.Add(new TextBlock { Text = "A grid of lines: type in the cells with the Text tool. Colour and thickness come from the bar.", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.Loaded += (_, _) => { rowsBox.Focus(); rowsBox.SelectAll(); };
        dlg.ShowDialog();
        if (!accepted) return null;
        if (!int.TryParse(rowsBox.Text.Trim(), out int r) || !int.TryParse(colsBox.Text.Trim(), out int c)) return null;
        return (Math.Clamp(r, 1, 60), Math.Clamp(c, 1, 30));
    }

    /// <summary>Mouse let go after dragging a table's size.</summary>
    private void FinishTable(TableItem table)
    {
        var r = new Rect(table.Box.TopLeft, table.Box.BottomRight);
        if (r.Width < 12 || r.Height < 12) { RenderItems(table.Page); return; }
        table.Box = r;
        if (AskTableSize(_tableRows, _tableCols) is not { } size) { RenderItems(table.Page); return; }
        _tableRows = table.Rows = size.Rows; _tableCols = table.Cols = size.Cols;
        Add(table, select: true);
    }

    private void EditTable(TableItem table)
    {
        if (AskTableSize(table.Rows, table.Cols) is not { } size) return;
        Snapshot();
        _tableRows = table.Rows = size.Rows; _tableCols = table.Cols = size.Cols;
        RenderItems(table.Page);
    }
}
