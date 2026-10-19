using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace IdmClone;

/// <summary>The Highlight tool's own colours: soft highlighter shades (yellow, green, pink, orange, blue, purple) instead of the dark dots of the other tools.</summary>
public sealed partial class PdfWindow
{
    private readonly StackPanel _highlightRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

    private static readonly Color[] HighlighterColours =
    {
        Color.FromRgb(0xFF, 0xE0, 0x30), Color.FromRgb(0x8A, 0xE0, 0x6B), Color.FromRgb(0xFF, 0x9A, 0xCB), Color.FromRgb(0xFF, 0xB3, 0x47), Color.FromRgb(0x7F, 0xD0, 0xFF), Color.FromRgb(0xC9, 0xA0, 0xFF),
    };

    private static readonly string[] HighlighterNames = { "Yellow", "Green", "Pink", "Orange", "Blue", "Purple" };

    /// <summary>A round colour button of the tool bar; the chosen colour gets a white ring with a gap (the ring is there even for the white dot).</summary>
    private Button ColourDot(Color c, string idPrefix, string? name = null)
    {
        var b = new Button
        {
            Width = 24, Height = 24, Margin = new Thickness(1, 0, 1, 0), Focusable = false, ToolTip = name ?? ColorName(c), Cursor = System.Windows.Input.Cursors.Hand,
            Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Grid><Border x:Name='ring' CornerRadius='12' BorderBrush='Transparent' BorderThickness='2' />" +
                "<Border x:Name='bd' Margin='3' Background='{TemplateBinding Background}' CornerRadius='9' BorderBrush='#55FFFFFF' BorderThickness='1' /></Grid>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='White' /></Trigger>" +
                "<Trigger Property='Tag' Value='picked'><Setter TargetName='ring' Property='BorderBrush' Value='White' /></Trigger></ControlTemplate.Triggers></ControlTemplate>"),
            Background = new SolidColorBrush(c),
        };
        b.Click += (_, _) => SetColor(c);
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, idPrefix + _swatchButtons.Count);
        System.Windows.Automation.AutomationProperties.SetName(b, name ?? ColorName(c));
        _swatchButtons.Add((b, c));
        return b;
    }

    private void BuildHighlighterRow()
    {
        _highlightRow.Children.Add(new TextBlock { Text = "Highlighter", Foreground = Soft, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        for (int i = 0; i < HighlighterColours.Length; i++) _highlightRow.Children.Add(ColourDot(HighlighterColours[i], "PdfHighlight", HighlighterNames[i]));
        var more = new Button { Content = "…", Width = 24, Height = 22, Padding = new Thickness(0), Margin = new Thickness(3, 0, 0, 0), Focusable = false, ToolTip = "More colours", Background = Brushes.Transparent, Foreground = Brushes.White };
        more.Click += (_, _) => { if (PickCustomColour(null) is Color picked) SetColor(picked); };
        _highlightRow.Children.Add(more);
    }
}
