using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Tools > "Clean up a scan": a new PDF (the original is never touched) in which every page (or this page) is its own picture again, straightened,
/// with the paper white, the text darker and the dark scanner edges gone. Made from the saved file.
/// </summary>
public sealed partial class PdfWindow
{
    private sealed record ScanChoice(ScanOptions Options, bool OnlyThisPage, int Dpi);

    private ScanChoice? AskScanOptions()
    {
        var straighten = new CheckBox { Content = "Straighten crooked pages", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        var darken = new CheckBox { Content = "Even out shadows and make faint text darker", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        var edges = new CheckBox { Content = "Remove dark scanner edges", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        var gray = new CheckBox { Content = "Black and white (a smaller file)", IsChecked = false, Margin = new Thickness(0, 8, 0, 0) };
        var all = new RadioButton { Content = _pdf!.PageCount == 1 ? "The page" : $"All {_pdf.PageCount} pages", IsChecked = true, GroupName = "scanPages", Margin = new Thickness(0, 14, 0, 0) };
        var one = new RadioButton { Content = $"Only this page (page {_current + 1})", GroupName = "scanPages", Margin = new Thickness(0, 6, 0, 0) };
        var normal = new RadioButton { Content = "Normal sharpness (200 dpi)", IsChecked = true, GroupName = "scanDpi", Margin = new Thickness(0, 14, 0, 0) };
        var high = new RadioButton { Content = "High sharpness (300 dpi, a bigger file)", GroupName = "scanDpi", Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = "Clean", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Children = { cancel, ok } };
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        panel.Children.Add(new TextBlock { Text = "Makes a new PDF next to this one (the original is not changed). Each page becomes a picture again, so its text can't be selected; use Make searchable (OCR) afterwards if you need that.", TextWrapping = TextWrapping.Wrap });
        foreach (UIElement e in new UIElement[] { straighten, darken, edges, gray, all, one, normal, high, buttons }) panel.Children.Add(e);
        var window = new Window
        {
            Title = "Clean up a scan", Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Content = panel,
            Background = (Brush)Application.Current.FindResource("BgBrush"), Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "PdfCleanScanGo");
        ok.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true) return null;
        return new ScanChoice(new ScanOptions(straighten.IsChecked == true, darken.IsChecked == true, edges.IsChecked == true, gray.IsChecked == true), one.IsChecked == true, high.IsChecked == true ? 300 : 200);
    }

    private async void CleanScan()
    {
        if (_pdf == null || _path == null) return;
        if (_dirty) { Toast("Save your changes first: the clean copy is made from the saved file"); return; }
        var choice = AskScanOptions();
        if (choice == null) return;
        if (!choice.Options.Straighten && !choice.Options.Darken && !choice.Options.Edges && !choice.Options.Gray) { Toast("Choose at least one thing to clean"); return; }
        string source = _path; int page = _current;
        _message.Text = "Cleaning the scan…"; _message.Visibility = Visibility.Visible;
        var progress = new Progress<(int Done, int Total)>(p => { if (_path == source) _message.Text = $"Cleaning page {p.Done} of {p.Total}…"; });
        string? output = null, error = null; var turned = new List<double>();
        try { (output, turned) = await Task.Run(() => CleanScanFile(source, choice, page, progress)); }
        catch (Exception e) when (e is not OutOfMemoryException) { error = e.Message; PdfWindow.Log(source, "clean scan: " + e.GetType().Name + ": " + e.Message); }
        _message.Visibility = Visibility.Collapsed;
        if (error != null || output == null) { UMessage.Show(this, "The scan could not be cleaned:\n\n" + error, "Clean up a scan", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        int straightened = turned.Count(a => a != 0);
        Toast(choice.Options.Straighten ? $"Done: {System.IO.Path.GetFileName(output)} ({straightened} of {turned.Count} page{(turned.Count == 1 ? "" : "s")} straightened)" : "Done: " + System.IO.Path.GetFileName(output));
        Open(new[] { output });
    }

    private static (string Path, List<double> Angles) CleanScanFile(string source, ScanChoice choice, int page, IProgress<(int Done, int Total)> progress)
    {
        using var pdf = PdfFile.Open(source);                                 // (its own copy: the open window is not touched)
        var pages = choice.OnlyThisPage ? new[] { Math.Clamp(page, 0, pdf.PageCount - 1) } : Enumerable.Range(0, pdf.PageCount).ToArray();
        var marks = new List<PdfMark>();
        var angles = new List<double>();
        foreach (int i in pages)
        {
            var size = pdf.PageSize(i);
            double scale = choice.Dpi / 72.0, longest = Math.Max(size.Width, size.Height) * scale;
            if (longest > 4200) scale *= 4200 / longest;
            var bitmap = pdf.Render(i, Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)), 0, forScreen: false);
            var (jpeg, angle) = ScanCleaner.Clean(bitmap, choice.Options);
            angles.Add(angle);
            var whole = new Rect(0, 0, size.Width, size.Height);
            marks.Add(new PdfRedactMark(i, whole));                                // (what was on the page goes ...)
            marks.Add(new PdfImageMark(i, whole, jpeg, null));                     // (... and the clean picture takes its place)
            progress.Report((angles.Count, pages.Length));
        }
        PdfMarkWriter.Apply(pdf, marks);
        byte[] bytes = pdf.SaveToBytes();
        string dir = System.IO.Path.GetDirectoryName(source)!;
        string output = ImageConverter.ReserveName(dir, System.IO.Path.GetFileNameWithoutExtension(source) + " (cleaned)", "pdf");
        string temp = output + ".utylix-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, output, overwrite: true);
        return (output, angles);
    }
}
