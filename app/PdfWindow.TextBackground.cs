using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace IdmClone;

/// <summary>
/// "Background" of Edit text: the colour behind the words of a line that is already in the PDF. Automatic keeps the page as it is; a colour is painted behind
/// the words (on screen, and in the PDF when saved, just under the words so nothing else is hidden).
/// </summary>
public sealed partial class PdfWindow
{
    private Button? _runBackButton;

    private UIElement RunBackButton()
    {
        var b = new Button { Content = BarLabel("Background ▾"), Template = BarButtonTemplate(), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "The colour behind the words of this line", Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfRunBackground");
        b.Click += (_, _) => ShowRunBackMenu(b);
        _runBackButton = b;
        return b;
    }

    /// <summary>A colour from Windows' colour dialog (null = cancelled).</summary>
    private static Color? PickCustomColour(Color? start)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (start is Color s) dlg.Color = System.Drawing.Color.FromArgb(s.R, s.G, s.B);
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B) : null;
    }

    private void ShowRunBackMenu(FrameworkElement anchor)
    {
        if (_runTyping == null) return;
        _runMenuOpen = true;                                           // (the menu takes the keyboard: the line stays open until it is closed again)
        var box = _runBox;
        ShowColourMenu(anchor, _runTyping.BackColor, "Automatic (as the page is)", "PdfRunBack", SetRunBack, () =>
        {
            _runMenuOpen = false;
            if (box != null && _runBox == box) Dispatcher.BeginInvoke(() => { if (_runBox == box) box.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        });
    }

    private bool _runMenuOpen;

    /// <summary>A menu of colours (with the one in use ticked) and "Other colour…"; <paramref name="noneLabel"/> is the first entry, which gives null.</summary>
    private void ShowColourMenu(FrameworkElement anchor, Color? current, string? noneLabel, string idPrefix, Action<Color?> choose, Action? closed = null)
    {
        var menu = new ContextMenu();
        if (closed != null) menu.Closed += (_, _) => closed();
        MenuItem Entry(string name, Color? color, string id, Brush? swatch, bool on)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Rectangle { Width = 16, Height = 12, Margin = new Thickness(0, 0, 8, 0), Fill = swatch ?? Brushes.Transparent, Stroke = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), StrokeThickness = 1 });
            header.Children.Add(new TextBlock { Text = name });
            var m = new MenuItem { Header = header, IsCheckable = true, IsChecked = on };
            System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) => choose(color);
            menu.Items.Add(m);
            return m;
        }
        if (noneLabel != null)
        {
            Entry(noneLabel, null, idPrefix + "Auto", null, current == null);
            menu.Items.Add(new Separator());
        }
        foreach (var (name, color) in FillColors) Entry(name, color, idPrefix + name.Replace(" ", ""), new SolidColorBrush(color), current == color);
        if (current is Color cc && FillColors.All(f => f.Color != cc)) Entry("Chosen colour", cc, idPrefix + "Chosen", new SolidColorBrush(cc), true);
        menu.Items.Add(new Separator());
        var other = new MenuItem { Header = "Other colour…" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(other, idPrefix + "Other");
        other.Click += (_, _) => { if (PickCustomColour(current) is Color picked) choose(picked); };
        menu.Items.Add(other);
        Themed(menu);
        menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void SetRunBack(Color? color)
    {
        if (_runTyping == null) return;
        _runTyping.BackColor = color;
        StyleRunBox(); RenderItems(_runTyping.Page);
    }
}
