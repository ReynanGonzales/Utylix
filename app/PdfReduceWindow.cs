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
    private readonly RadioButton _recommended, _smaller, _smallest, _toSize;
    private readonly TextBox _limit = new() { Text = "2", Width = 58, Margin = new Thickness(8, 0, 6, 0), Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
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
        _toSize = Choice("Under a size", "As sharp as still fits under the size you set, for upload and e-mail limits. Text, links and form fields stay as they are.", "PdfReduceToSize");
        _recommended.IsChecked = true;
        // "Under a size: [2] MB" with a few common limits. Typing in the box picks this choice.
        var title = (StackPanel)_toSize.Content;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        title.Children.RemoveAt(0);
        line.Children.Add(new TextBlock { Text = "Under", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        System.Windows.Automation.AutomationProperties.SetAutomationId(_limit, "PdfReduceLimit");
        _limit.GotKeyboardFocus += (_, _) => { _toSize.IsChecked = true; _limit.SelectAll(); };
        line.Children.Add(_limit);
        line.Children.Add(new TextBlock { Text = "MB", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        foreach (var mb in new[] { "1", "2", "5", "10", "25" })
        {
            var chip = new Button { Content = mb + " MB", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 5, 0), FontSize = 12 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "PdfReduceLimit" + mb);
            chip.Click += (_, _) => { _limit.Text = mb; _toSize.IsChecked = true; };
            line.Children.Add(chip);
        }
        title.Children.Insert(0, line);
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
        bool toSize = _toSize.IsChecked == true;
        long target = 0;
        string limit = "";
        if (toSize && !TryLimit(out target, out limit)) { _status.Text = "Type the size in MB, for example 2 or 1.5."; _limit.Focus(); return; }
        _cts = new CancellationTokenSource();
        _go.IsEnabled = false; _choices.IsEnabled = false;
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        string doing = "Making it smaller…";
        _status.Text = doing;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            _bar.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            _status.Text = toSize && p.Done >= p.Total ? $"Finding the sharpest copy under {limit}…" : $"{doing} page {Math.Min(p.Done + 1, p.Total)} of {p.Total}";
        });
        var level = Level;
        PdfReduceResult result;
        bool keepAsked = false;                                // the person already chose to keep a copy that doesn't fit
        try
        {
            result = await Task.Run(() => toSize
                ? PdfCompressor.ReduceToSize(_path, _password, _ownerPassword, target, progress, _cts.Token)
                : PdfCompressor.Reduce(_path, _password, _ownerPassword, level, progress, _cts.Token));
            if (toSize && !result.Fits && result.CanTryPages)
            {
                // keeping the text doesn't get there: saving every page as a picture usually does, but only when the person says so
                string text = (result.Output != null ? $"With the text kept, the smallest Utylix can make it is {Bytes(result.After)}" : "With the text kept, Utylix can't make it much smaller")
                    + $", which is not under {limit}.\n\nUtylix can save every page as a picture instead. That is usually much smaller, but text can't be selected, searched or copied in that copy.";
                var buttons = new System.Collections.Generic.List<(string, MessageBoxResult)> { ("Save pages as pictures", MessageBoxResult.Yes) };
                if (result.Output != null) buttons.Add(($"Keep text ({Bytes(result.After)})", MessageBoxResult.No));
                buttons.Add(("Cancel", MessageBoxResult.Cancel));
                var answer = UMessage.Ask(this, text, "Reduce file size", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel, buttons.ToArray());
                if (answer == MessageBoxResult.Cancel) { Done("Nothing was saved."); return; }
                if (answer == MessageBoxResult.No) keepAsked = true;
                else
                {
                    doing = "Saving the pages as pictures…";
                    _bar.Value = 0;
                    _status.Text = doing;
                    var pages = await Task.Run(() => PdfCompressor.ReducePagesToSize(_path, _password, target, progress, _cts.Token));
                    if (pages.Fits || result.Output == null || (pages.Output != null && pages.After < result.After)) result = pages;
                }
            }
        }
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
        if (toSize && result.Fits && !result.Helped) { Done($"It is already under {limit}: {Bytes(result.Before)}. Nothing needs to change."); return; }
        if (toSize && !result.Fits)
        {
            if (!result.Helped) { Done($"Utylix can't make this PDF smaller than {limit}, or much smaller than it is."); return; }
            if (!keepAsked && UMessage.Ask(this, $"Utylix can't get it under {limit}. The smallest copy it can make is {Bytes(result.After)} (was {Bytes(result.Before)}).\n\nSave that copy anyway?",
                    "Reduce file size", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel, ("Save it", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)) != MessageBoxResult.Yes)
            {
                Done("Nothing was saved.");
                return;
            }
        }
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
        Done($"Done: {Bytes(result.Before)} → {Bytes(result.After)} ({percent}% smaller{(toSize && result.Fits ? ", under " + limit : "")}).\nSaved as {System.IO.Path.GetFileName(dlg.FileName)}"
            + (result.PagesArePictures ? "\nIts pages are pictures, so text can't be selected or searched in this copy." : ""));
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

    /// <summary>The size typed in the box, in bytes: "2", "1.5", "1,5", "2 MB" or "500 KB". A megabyte counts as 1,000,000 bytes here, the
    /// smaller of the two ways sites count it, so the copy fits either way.</summary>
    private bool TryLimit(out long bytes, out string label)
    {
        bytes = 0; label = "";
        string s = _limit.Text.Trim().ToLowerInvariant().Replace(',', '.');
        double unit = 1_000_000;
        if (s.EndsWith("kb")) { unit = 1_000; s = s[..^2]; }
        else if (s.EndsWith("mb")) s = s[..^2];
        else if (s.EndsWith('m')) s = s[..^1];
        if (!double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) || n <= 0) return false;
        bytes = (long)(n * unit);
        label = n.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + (unit == 1_000 ? " KB" : " MB");
        return bytes >= 20_000 && bytes <= 100_000_000_000;
    }

    public static string Bytes(long n) => n >= 1048576
        ? (n / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, (int)Math.Round(n / 1024.0)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " KB";
}
