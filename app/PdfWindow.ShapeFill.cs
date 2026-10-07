using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace IdmClone;

/// <summary>The inside colour of a box or circle (Mark up tab > Shapes): the "Fill" button of the tool bar; it changes the chosen shape and is what the next one starts with.</summary>
public sealed partial class PdfWindow
{
    private Color? _shapeFill;
    private Button? _fillButton;

    private UIElement FillButton()
    {
        var b = new Button { Content = BarLabel("Fill ▾"), Template = BarButtonTemplate(), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "The colour inside a box or circle", Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfShapeFill");
        b.Click += (_, _) => ShowFillMenu(b);
        _fillButton = b;
        return b;
    }

    private static readonly (string Name, Color Color)[] FillColors =
    {
        ("White", Colors.White), ("Yellow", Color.FromRgb(0xFF, 0xF1, 0x9A)), ("Light blue", Color.FromRgb(0xCF, 0xE3, 0xFF)), ("Light green", Color.FromRgb(0xD3, 0xF0, 0xD0)),
        ("Pink", Color.FromRgb(0xFB, 0xD3, 0xDC)), ("Light grey", Color.FromRgb(0xE3, 0xE5, 0xEA)), ("Black", Colors.Black),
    };

    private void ShowFillMenu(FrameworkElement anchor)
    {
        var shape = _selected as ShapeItem;
        var current = shape != null ? shape.Fill : _shapeFill;
        var menu = new ContextMenu();
        void Item(string name, Color? color, string id, Brush? swatch)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Rectangle { Width = 16, Height = 12, Margin = new Thickness(0, 0, 8, 0), Fill = swatch ?? Brushes.Transparent, Stroke = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), StrokeThickness = 1 });
            header.Children.Add(new TextBlock { Text = name });
            var m = new MenuItem { Header = header, IsCheckable = true, IsChecked = current == color };
            System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) => SetShapeFill(color);
            menu.Items.Add(m);
        }
        Item("No fill (see-through)", null, "PdfFillNone", null);
        if (shape != null) Item("Same colour as the edge", shape.Color, "PdfFillSame", new SolidColorBrush(shape.Color));
        foreach (var (name, color) in FillColors) Item(name, color, "PdfFill" + name.Replace(" ", ""), new SolidColorBrush(color));
        Themed(menu);
        menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void SetShapeFill(Color? color)
    {
        _shapeFill = color;
        if (_selected is ShapeItem { Kind: ShapeKind.Rectangle or ShapeKind.Ellipse } shape)
        {
            Snapshot();
            shape.Fill = color;
            RenderItems(shape.Page);
        }
        UpdateProperties();
    }
}
