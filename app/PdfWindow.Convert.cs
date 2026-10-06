using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The "Convert" strip under the top bar (shown while not editing): big icon-over-label buttons for the conversions people look for,
/// like Acrobat's: To Word, To Excel, To Pictures. They open the same dialogs as Tools &gt; Convert.
/// </summary>
public sealed partial class PdfWindow
{
    private Border? _convertBar;

    private Border BuildConvertBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Convert", Foreground = Soft, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(ConvertButton("W", Color.FromRgb(0x2B, 0x57, 0x9A), "To Word", "Make a Word document (.docx) from this PDF", "PdfConvertWord", () => ExportToOffice(excel: false)));
        row.Children.Add(ConvertButton("X", Color.FromRgb(0x1D, 0x6F, 0x42), "To Excel", "Make an Excel workbook (.xlsx) from this PDF", "PdfConvertExcel", () => ExportToOffice(excel: true)));
        row.Children.Add(ConvertButton("", Color.FromRgb(0xC2, 0x6A, 0x1B), "To Pictures", "Save the pages as PNG or JPG pictures", "PdfConvertPictures", () => OpenPagesDialog(split: false), glyphFont: "Segoe MDL2 Assets"));
        _convertBar = new Border
        {
            Child = row, Background = new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x34)), Padding = new Thickness(0, 4, 8, 4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A)), BorderThickness = new Thickness(0, 1, 0, 1),
        };
        return _convertBar;
    }

    private Button ConvertButton(string glyph, Color colour, string label, string tip, string id, Action action, string glyphFont = "Segoe UI")
    {
        var icon = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(colour), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = glyph, Foreground = Brushes.White, FontFamily = new FontFamily(glyphFont), FontSize = glyphFont == "Segoe UI" ? 16 : 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var stack = new StackPanel();
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });
        var b = new Button { Content = stack, Template = ToolTemplateWide(), Margin = new Thickness(2, 0, 2, 0), Focusable = false, ToolTip = tip, IsEnabled = false };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
        System.Windows.Automation.AutomationProperties.SetName(b, label);
        b.Click += (_, _) => action();
        _needsDocument.Add(b);
        return b;
    }

    private static ControlTemplate ToolTemplateWide() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='bd' Background='Transparent' CornerRadius='6' MinWidth='76' Padding='8,4'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#33FFFFFF' /></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='#55FFFFFF' /></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    /// <summary>Shown when the tools are closed; the editing bar takes its place.</summary>
    private void UpdateConvertBar()
    {
        if (_convertBar != null) _convertBar.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
    }
}
