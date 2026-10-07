using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The look of text you add, beyond font, size, bold and colour: italic, underline, alignment (left / middle / right), and a box round it (a fixed width that the words wrap in, a background colour, a frame).
/// All from the "Style" button of the tool bar; it changes the text being typed or chosen, and is the starting look of the next text.
/// </summary>
public sealed partial class PdfWindow
{
    private bool _italic, _underline;
    private int _align;
    private Button? _styleButton;

    private UIElement StyleButton()
    {
        var b = new Button { Content = BarLabel("Style ▾"), Template = BarButtonTemplate(), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "Italic, underline, left / middle / right, and a box round the text (width, background, frame)" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfTextStyle");
        b.Click += (_, _) => ShowStyleMenu(b);
        _styleButton = b;
        return b;
    }

    private void ShowStyleMenu(FrameworkElement anchor)
    {
        var target = _typing ?? _selected as TextItem;
        bool italic = target?.Italic ?? _italic, underline = target?.Underline ?? _underline;
        int align = target?.Align ?? _align;
        var menu = new ContextMenu();
        void Item(string header, bool on, Action action, string id)
        {
            var m = new MenuItem { Header = header, IsCheckable = true, IsChecked = on };
            System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) => action();
            menu.Items.Add(m);
        }
        Item("Italic", italic, () => ChangeTextStyle(t => t.Italic = !italic, () => _italic = !italic), "PdfStyleItalic");
        Item("Underline", underline, () => ChangeTextStyle(t => t.Underline = !underline, () => _underline = !underline), "PdfStyleUnderline");
        menu.Items.Add(new Separator());
        Item("Align left", align == 0, () => ChangeTextStyle(t => t.Align = 0, () => _align = 0), "PdfStyleLeft");
        Item("Align in the middle", align == 1, () => ChangeTextStyle(t => t.Align = 1, () => _align = 1), "PdfStyleMiddle");
        Item("Align right", align == 2, () => ChangeTextStyle(t => t.Align = 2, () => _align = 2), "PdfStyleRight");
        menu.Items.Add(new Separator());
        var box = new MenuItem { Header = "Box, width and background…", IsEnabled = target != null };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "PdfStyleBox");
        box.Click += (_, _) => { if (target != null) EditTextBox(target); };
        menu.Items.Add(box);
        Themed(menu);
        menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    /// <summary>Changes the text being typed or chosen (one undo step) and what the next text starts with.</summary>
    private void ChangeTextStyle(Action<TextItem> apply, Action defaults)
    {
        defaults();
        var target = _typing ?? _selected as TextItem;
        if (target == null) return;
        if (_typing == null) Snapshot();
        apply(target);
        _dirty = true; UpdateTitle();
        if (_textBox != null && _typing != null) StyleTextBox(_textBox, _typing); else RenderItems(target.Page);
        UpdateProperties();
    }

    private static readonly (string Name, Color? Color)[] BoxFills =
    {
        ("None", null), ("White", Colors.White), ("Yellow", Color.FromRgb(0xFF, 0xF1, 0x9A)), ("Blue", Color.FromRgb(0xCF, 0xE3, 0xFF)),
        ("Green", Color.FromRgb(0xD3, 0xF0, 0xD0)), ("Pink", Color.FromRgb(0xFB, 0xD3, 0xDC)), ("Grey", Color.FromRgb(0xE3, 0xE5, 0xEA)),
    };

    /// <summary>The box round a text: width (fits the words, or a fixed width they wrap in), frame and background.</summary>
    private void EditTextBox(TextItem item)
    {
        var dlg = new Window
        {
            Title = "Box round the text", Width = 470, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("BgBrush"), Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        WindowTheme.DarkTitleBar(dlg);
        var chip = (Style)Application.Current.FindResource("ChipButton");
        RadioButton Chip(string text, string group, bool on, string id) { var r = new RadioButton { Content = text, Style = chip, GroupName = group, IsChecked = on }; System.Windows.Automation.AutomationProperties.SetAutomationId(r, id); return r; }
        TextBlock Label(string text, double top = 14) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 6) };

        bool fixedNow = item.BoxWidth > 0;
        var fits = Chip("Fits the text", "tbwidth", !fixedNow, "PdfBoxFits"); var fixedChip = Chip("A width the words wrap in", "tbwidth", fixedNow, "PdfBoxFixed");
        var width = new TextBox { Text = Math.Round(fixedNow ? item.BoxWidth : Math.Max(120, item.Bounds.Width)).ToString(CultureInfo.InvariantCulture), Width = 70, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = fixedNow };
        System.Windows.Automation.AutomationProperties.SetAutomationId(width, "PdfBoxWidth");
        fixedChip.Checked += (_, _) => width.IsEnabled = true; fits.Checked += (_, _) => width.IsEnabled = false;
        var widthRow = new StackPanel { Orientation = Orientation.Horizontal };
        widthRow.Children.Add(fixedChip); widthRow.Children.Add(width); widthRow.Children.Add(new TextBlock { Text = "points (72 = 1 inch)", Opacity = 0.7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var frames = new List<(RadioButton Chip, double Width)>();
        var frameRow = new WrapPanel();
        foreach (var (name, w) in new[] { ("None", 0.0), ("Thin", 0.75), ("Medium", 1.5), ("Thick", 3.0) })
        {
            var c = Chip(name, "tbframe", Math.Abs(item.BorderWidth - w) < 0.01, "PdfBoxFrame" + name); frames.Add((c, w)); frameRow.Children.Add(c);
        }
        if (!frames.Any(f => f.Chip.IsChecked == true)) frames[2].Chip.IsChecked = true;
        var fills = new List<(RadioButton Chip, Color? Color)>();
        var fillRow = new WrapPanel();
        foreach (var (name, color) in BoxFills)
        {
            bool on = color == null ? item.Fill == null : item.Fill is Color cur && cur == color;
            var c = Chip(name, "tbfill", on, "PdfBoxFill" + name);
            if (color is Color col) c.Foreground = new SolidColorBrush(col.R + col.G + col.B > 600 ? Colors.Black : Colors.White);
            fills.Add((c, color)); fillRow.Children.Add(c);
        }
        if (!fills.Any(f => f.Chip.IsChecked == true)) fills[0].Chip.IsChecked = true;

        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "PdfBoxOk");
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; dlg.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Label("Width", 0));
        var widthChips = new WrapPanel(); widthChips.Children.Add(fits);
        root.Children.Add(widthChips); root.Children.Add(widthRow);
        root.Children.Add(Label("Frame (in the text's colour)")); root.Children.Add(frameRow);
        root.Children.Add(Label("Background")); root.Children.Add(fillRow);
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.ShowDialog();
        if (!accepted) return;

        double newWidth = 0;
        if (fixedChip.IsChecked == true)
        {
            if (!double.TryParse(width.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out newWidth) && !double.TryParse(width.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out newWidth)) newWidth = 0;
            newWidth = newWidth <= 0 ? 0 : Math.Clamp(newWidth, 24, 2000);
        }
        double newFrame = frames.First(f => f.Chip.IsChecked == true).Width;
        Color? newFill = fills.First(f => f.Chip.IsChecked == true).Color;
        if (_typing == null) Snapshot();
        item.BoxWidth = newWidth; item.BorderWidth = newFrame; item.Fill = newFill;
        _dirty = true; UpdateTitle();
        if (_textBox != null && _typing != null) StyleTextBox(_textBox, _typing); else RenderItems(item.Page);
        UpdateProperties();
    }
}
