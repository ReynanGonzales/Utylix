using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IdmClone;

/// <summary>The little parts the PDF editor's dialogs (border, columns, watermark removal) are made of, in the app's theme.</summary>
internal static class PdfDialogKit
{
    public static object Res(string key) => Application.Current.FindResource(key);

    public static void Setup(Window w, Window owner, string title, double width)
    {
        w.Owner = owner; w.Title = title + " - Utylix Editor";
        w.Width = width; w.SizeToContent = SizeToContent.Height; w.ResizeMode = ResizeMode.NoResize;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.Background = (Brush)Res("BgBrush"); w.Foreground = (Brush)Res("TextBrush");
        w.FontFamily = new FontFamily("Segoe UI"); w.FontSize = 13.5;
        WindowTheme.DarkTitleBar(w);
        try { w.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
    }

    public static TextBlock Heading(string text, double top = 14) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 6) };

    public static TextBlock Muted(string text, double size = 12) { var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); return t; }

    public static RadioButton Chip(string text, string group, bool on, Action onChecked, string? id = null)
    {
        var r = new RadioButton { Content = text, GroupName = group, IsChecked = on, Style = (Style)Res("ChipButton") };
        if (id != null) System.Windows.Automation.AutomationProperties.SetAutomationId(r, id);
        r.Checked += (_, _) => onChecked();
        return r;
    }

    public static RadioButton Swatch(Color c, string group, bool on, Action onChecked)
    {
        var r = new RadioButton { Content = new TextBlock { Text = "■", FontSize = 17, Foreground = new SolidColorBrush(c) }, Style = (Style)Res("ChipButton"), GroupName = group, IsChecked = on, MinWidth = 38, Padding = new Thickness(6, 2, 6, 2), ToolTip = ColorName(c) };
        r.Checked += (_, _) => onChecked();
        return r;
    }

    public static string ColorName(Color c) => c.R > 200 && c.G < 90 ? "Red" : c.B > 150 && c.R < 100 ? "Blue" : c.G > 110 && c.R < 90 ? "Green" : c.R < 40 && c.G < 40 ? "Black" : c.R > 200 && c.G > 150 ? "Gold" : "Grey";

    public static Slider Bar(double min, double max, double value, double width = 200) => new() { Minimum = min, Maximum = max, Value = value, Width = width, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };

    public static StackPanel LabelRow(string label, UIElement control, TextBlock? value = null, double labelWidth = 130)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        if (value != null) { value.Margin = new Thickness(10, 0, 0, 0); value.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(value); }
        return row;
    }

    public static StackPanel Buttons(Button primary, Button cancel)
    {
        primary.Style = (Style)Res("DialogPrimary"); cancel.Style = (Style)Res("DialogButton");
        primary.Margin = new Thickness(0, 0, 10, 0); cancel.IsCancel = true;
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        p.Children.Add(primary); p.Children.Add(cancel);
        return p;
    }

    /// <summary>A page-shaped white box (the page drawn in it, if there is one) with a canvas on top for what the dialog draws.</summary>
    public static (Grid Box, Image Picture, Canvas Overlay) PagePreview(double width, double height)
    {
        var box = new Grid { Width = width, Height = height, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true };
        var picture = new Image { Stretch = Stretch.Fill };
        var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        box.Children.Add(picture); box.Children.Add(overlay);
        return (box, picture, overlay);
    }
}
