using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Reduce file size" for a PDF, like Acrobat's: pick how small, Utylix makes a smaller copy (the original is never changed) and
/// asks where to save it. Everything runs on this PC, without internet.
/// </summary>
public sealed class PdfReduceWindow : Window
{
    private readonly string _path;
    private readonly string? _password;          // the one the PDF was opened with
    private string? _ownerPassword;              // asked for when the PDF is protected against changes
    private bool _askedOwner;
    private readonly RadioButton _recommended, _smaller, _smallest;
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), MinHeight = 20 };
    private readonly Button _go = new() { Content = "Reduce", MinWidth = 120, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _close = new() { Content = "Close", MinWidth = 96 };
    private readonly StackPanel _choices = new();
    private CancellationTokenSource? _cts;
    private string? _saved;

    public PdfReduceWindow(string path, string? password)
    {
        _path = path; _password = password;
        Title = "Reduce file size - Utylix Editor";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Reduce file size", FontSize = 20, FontWeight = FontWeights.SemiBold });
        long size = 0;
        try { size = new FileInfo(path).Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        root.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path) + "   ·   " + Bytes(size), Opacity = 0.75, Margin = new Thickness(0, 4, 0, 14), TextTrimming = TextTrimming.CharacterEllipsis });

        RadioButton Choice(string title, string detail, string id)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = detail, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
            var r = new RadioButton { Content = text, GroupName = "level", Margin = new Thickness(0, 0, 0, 10), Foreground = Foreground, VerticalContentAlignment = VerticalAlignment.Top };
            System.Windows.Automation.AutomationProperties.SetAutomationId(r, id);
            _choices.Children.Add(r);
            return r;
        }
        _recommended = Choice("Recommended", "Pictures and scans are made lighter, still sharp to read and print. Text, links and form fields stay as they are.", "PdfReduceRecommended");
        _smaller = Choice("Smaller", "Pictures at lower quality. Good for sending by e-mail or uploading where the size is limited.", "PdfReduceSmaller");
        _smallest = Choice("Smallest (pages become pictures)", "For scans that are still too big. Every page is saved as one picture, so text can't be selected or searched afterwards.", "PdfReduceSmallest");
        _recommended.IsChecked = true;
        root.Children.Add(_choices);
        root.Children.Add(new TextBlock { Text = "Your original file is not changed: you choose where the smaller copy is saved.", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_bar);
        root.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _go.Style = (Style)Application.Current.FindResource("DialogPrimary");
        _close.Style = (Style)Application.Current.FindResource("DialogButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "PdfReduceGo");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "PdfReduceStatus");
        buttons.Children.Add(_go);
        buttons.Children.Add(_close);
        root.Children.Add(buttons);
        Content = root;

        _go.Click += async (_, _) => await GoAsync();
        _close.Click += (_, _) => Close();
        Closing += (_, _) => _cts?.Cancel();
    }

    private PdfReduceLevel Level => _smallest.IsChecked == true ? PdfReduceLevel.Smallest : _smaller.IsChecked == true ? PdfReduceLevel.Smaller : PdfReduceLevel.Recommended;

    private async Task GoAsync()
    {
        if (_saved != null) { Open(_saved); return; }          // (after saving, the button opens the smaller copy)
        _cts = new CancellationTokenSource();
        _go.IsEnabled = false; _choices.IsEnabled = false;
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        _status.Text = "Making it smaller…";
        var progress = new Progress<(int Done, int Total)>(p => { _bar.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total; _status.Text = $"Making it smaller… page {Math.Min(p.Done + 1, p.Total)} of {p.Total}"; });
        var level = Level;
        PdfReduceResult result;
        try { result = await Task.Run(() => PdfCompressor.Reduce(_path, _password, _ownerPassword, level, progress, _cts.Token)); }
        catch (OperationCanceledException) { return; }
        catch (PdfProtectedException e) when (e.Message == PdfCompressor.OwnerPasswordNeeded)
        {
            // protected against changes: ask for the owner (permissions) password and try again with it
            Done(e.Message);
            var ask = new PasswordWindow(System.IO.Path.GetFileName(_path), _askedOwner, "Enter the PDF's owner (permissions) password") { Owner = this };
            _askedOwner = true;
            if (ask.ShowDialog() == true) { _ownerPassword = ask.Password; await GoAsync(); }
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfPasswordException or PdfProtectedException or OutOfMemoryException or DllNotFoundException)
        {
            Done("Couldn't make it smaller: " + e.Message);
            return;
        }
        _bar.Value = 1;
        if (!result.Helped)
        {
            Done(level == PdfReduceLevel.Smallest
                ? "This PDF is already as small as Utylix can make it."
                : "This PDF is already small: its pictures can't get much lighter. \"Smallest\" may still help for a scan.");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the smaller PDF",
            Filter = "PDF|*.pdf",
            FileName = System.IO.Path.GetFileNameWithoutExtension(_path) + " (reduced).pdf",
            InitialDirectory = System.IO.Path.GetDirectoryName(_path),
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) { Done($"It would be {Bytes(result.After)} (was {Bytes(result.Before)}). Press Reduce again to save it."); return; }
        try
        {
            if (string.Equals(System.IO.Path.GetFullPath(dlg.FileName), System.IO.Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                // replacing the original: write next to it first, so a failure can't leave half a file
                string temp = dlg.FileName + ".utylix-tmp";
                File.WriteAllBytes(temp, result.Output!);
                File.Move(temp, dlg.FileName, overwrite: true);
            }
            else File.WriteAllBytes(dlg.FileName, result.Output!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Done("Couldn't save it: " + e.Message); return; }

        _saved = dlg.FileName;
        int percent = (int)Math.Round(100 - 100.0 * result.After / result.Before);
        Done($"Done: {Bytes(result.Before)} → {Bytes(result.After)} ({percent}% smaller).\nSaved as {System.IO.Path.GetFileName(dlg.FileName)}");
        _go.Content = "Open it";
        _go.IsEnabled = true;
        var folder = new Button { Content = "Show in folder", MinWidth = 120, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("DialogButton") };
        folder.Click += (_, _) => { try { Process.Start("explorer.exe", $"/select,\"{_saved}\""); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } };
        ((StackPanel)_go.Parent).Children.Insert(0, folder);
    }

    private void Done(string message)
    {
        _status.Text = message;
        _go.IsEnabled = true; _choices.IsEnabled = true;
        if (_saved == null) _bar.Visibility = Visibility.Collapsed;
    }

    private void Open(string path) { PdfWindow.Open(new[] { path }); Close(); }

    public static string Bytes(long n) => n >= 1048576
        ? (n / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, (int)Math.Round(n / 1024.0)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " KB";
}
