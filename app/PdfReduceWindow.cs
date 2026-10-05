using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private static readonly List<PdfReduceWindow> Open_ = new();
    public static int Count => Open_.Count;
    public static event Action? AnyClosed;

    private readonly string _path;
    private readonly IReadOnlyList<string>? _batch;  // several PDFs at once (from Explorer): each copy is saved next to its original
    private readonly string? _password;          // the one the PDF was opened with
    private string? _ownerPassword;              // asked for when the PDF is protected against changes
    private bool _askedOwner;
    private readonly RadioButton _recommended, _smaller, _smallest, _toSize;
    private RadioButton _unitKb = null!, _unitMb = null!;
    private readonly TextBox _limit = new() { Text = "2", Width = 58, Margin = new Thickness(8, 0, 6, 0), Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), MinHeight = 20 };
    private readonly Button _go = new() { Content = "Reduce", MinWidth = 120, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _close = new() { Content = "Close", MinWidth = 96 };
    private readonly StackPanel _choices = new();
    private CancellationTokenSource? _cts;
    private string? _saved;

    /// <summary>Several PDFs at once (Explorer's right-click menu): each smaller copy is saved next to its original.</summary>
    public static void Show(IReadOnlyList<string> paths)
    {
        var w = paths.Count == 1 ? new PdfReduceWindow(paths[0], null) : new PdfReduceWindow(paths[0], null, paths);
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
        w.Activate();
    }

    public PdfReduceWindow(string path, string? password, IReadOnlyList<string>? batch = null)
    {
        _path = path; _password = password; _batch = batch;
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
        foreach (var f in batch ?? new[] { path })
            try { size += new FileInfo(f).Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        string what = batch != null ? $"{batch.Count} PDFs" : System.IO.Path.GetFileName(path);
        root.Children.Add(new TextBlock { Text = what + "   ·   " + Bytes(size), Opacity = 0.75, Margin = new Thickness(0, 4, 0, 14), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = batch != null ? string.Join("\n", batch.Select(System.IO.Path.GetFileName)) : null });

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
        // KB or MB: two small choices next to the box (typing "500 KB" works too)
        _unitKb = new RadioButton { Content = "KB", GroupName = "reduceunit", Style = (Style)Application.Current.FindResource("ChipButton"), Margin = new Thickness(2, 0, 0, 0), MinWidth = 40 };
        _unitMb = new RadioButton { Content = "MB", GroupName = "reduceunit", Style = (Style)Application.Current.FindResource("ChipButton"), Margin = new Thickness(0, 0, 12, 0), MinWidth = 40, IsChecked = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_unitKb, "PdfReduceUnitKb");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_unitMb, "PdfReduceUnitMb");
        _unitKb.Checked += (_, _) => _toSize.IsChecked = true; _unitMb.Checked += (_, _) => _toSize.IsChecked = true;
        line.Children.Add(_unitKb); line.Children.Add(_unitMb);
        title.Children.Insert(0, line);
        var quick = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (n, unit) in new[] { ("100", "KB"), ("200", "KB"), ("500", "KB"), ("1", "MB"), ("2", "MB"), ("5", "MB"), ("10", "MB"), ("25", "MB") })
        {
            var chip = new Button { Content = n + " " + unit, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 5, 3), FontSize = 12 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "PdfReduceLimit" + n + unit);
            chip.Click += (_, _) => { _limit.Text = n; (unit == "KB" ? _unitKb : _unitMb).IsChecked = true; _toSize.IsChecked = true; };
            quick.Children.Add(chip);
        }
        title.Children.Insert(1, quick);
        root.Children.Add(_choices);
        root.Children.Add(new TextBlock
        {
            Text = batch != null ? "Your original files are not changed: each smaller copy is saved next to its original, as \"name (reduced).pdf\"."
                                 : "Your original file is not changed: you choose where the smaller copy is saved.",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
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
        Closed += (_, _) => { Open_.Remove(this); AnyClosed?.Invoke(); };
        Open_.Add(this);
    }

    private PdfReduceLevel Level => _smallest.IsChecked == true ? PdfReduceLevel.Smallest : _smaller.IsChecked == true ? PdfReduceLevel.Smaller : PdfReduceLevel.Recommended;

    private async Task GoAsync()
    {
        if (_saved != null && _batch != null) { Reveal(_saved); return; }     // (after several, the button shows them in their folder)
        if (_saved != null) { Open(_saved); return; }          // (after saving, the button opens the smaller copy)
        if (_batch != null) { await GoBatchAsync(); return; }
        bool toSize = _toSize.IsChecked == true;
        long target = 0;
        string limit = "";
        if (toSize && !TryLimit(out target, out limit)) { _status.Text = "Type the size, for example 500 KB or 1.5 MB (choose KB or MB next to the box)."; _limit.Focus(); return; }
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
        folder.Click += (_, _) => Reveal(_saved!);
        ((StackPanel)_go.Parent).Children.Insert(0, folder);
    }

    /// <summary>
    /// Several PDFs: each one is made smaller in turn and saved next to its original as "name (reduced).pdf" (never over a file
    /// that is there already). Nothing is asked on the way; what couldn't be done is listed at the end.
    /// </summary>
    private async Task GoBatchAsync()
    {
        bool toSize = _toSize.IsChecked == true;
        long target = 0;
        string limit = "";
        if (toSize && !TryLimit(out target, out limit)) { _status.Text = "Type the size, for example 500 KB or 1.5 MB (choose KB or MB next to the box)."; _limit.Focus(); return; }
        var level = Level;
        var files = _batch!;
        _cts = new CancellationTokenSource();
        _go.IsEnabled = false; _choices.IsEnabled = false;
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        int current = 0;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            double part = p.Total == 0 ? 0 : Math.Min(1, (double)p.Done / p.Total);
            _bar.Value = (current + part) / files.Count;
            _status.Text = $"{System.IO.Path.GetFileName(files[current])} ({current + 1} of {files.Count}): page {Math.Min(p.Done + 1, p.Total)} of {p.Total}";
        });

        long before = 0, after = 0;
        int made = 0;
        string? firstSaved = null;
        var notes = new List<string>();
        for (current = 0; current < files.Count; current++)
        {
            string file = files[current], name = System.IO.Path.GetFileName(file);
            PdfReduceResult result;
            try
            {
                result = await Task.Run(() => toSize
                    ? PdfCompressor.ReduceToSize(file, null, null, target, progress, _cts.Token)
                    : PdfCompressor.Reduce(file, null, null, level, progress, _cts.Token));
            }
            catch (OperationCanceledException) { return; }
            catch (PdfPasswordException) { notes.Add($"{name}: needs a password - open it in Utylix Editor to reduce it there."); continue; }
            catch (PdfProtectedException) { notes.Add($"{name}: protected against changes - open it in Utylix Editor to reduce it there."); continue; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException or DllNotFoundException) { notes.Add($"{name}: {e.Message}"); continue; }

            if (!result.Helped)
            {
                notes.Add(toSize && result.Fits ? $"{name}: already under {limit}." : $"{name}: already small, nothing to gain.");
                continue;
            }
            try
            {
                string dest = ReducedName(file);
                string temp = dest + ".utylix-tmp";
                await File.WriteAllBytesAsync(temp, result.Output!);
                File.Move(temp, dest);
                firstSaved ??= dest;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add($"{name}: couldn't save the copy ({e.Message})."); continue; }
            made++;
            before += result.Before; after += result.After;
            if (toSize && !result.Fits) notes.Add($"{name}: {Bytes(result.After)} is the smallest it gets with the text kept, not under {limit}.");
        }

        _bar.Value = 1;
        string summary = made == 0 ? "No smaller copies were made."
            : $"Done: {made} of {files.Count} PDFs made smaller, {Bytes(before)} → {Bytes(after)} ({(int)Math.Round(100 - 100.0 * after / before)}% smaller), saved next to the originals.";
        if (notes.Count > 0) summary += "\n" + string.Join("\n", notes.Take(6)) + (notes.Count > 6 ? $"\n…and {notes.Count - 6} more." : "");
        _status.Text = summary;
        _cts = null;
        _choices.IsEnabled = true;
        if (firstSaved == null) { _go.IsEnabled = true; _bar.Visibility = Visibility.Collapsed; return; }
        // the button now shows the copies in their folder
        _go.Content = "Show in folder";
        _go.IsEnabled = true;
        _saved = firstSaved;
    }

    /// <summary>"name (reduced).pdf" next to the original, or "name (reduced 2).pdf" … when that is taken.</summary>
    private static string ReducedName(string file)
    {
        string dir = System.IO.Path.GetDirectoryName(file)!, stem = System.IO.Path.GetFileNameWithoutExtension(file);
        string candidate = System.IO.Path.Combine(dir, stem + " (reduced).pdf");
        for (int n = 2; File.Exists(candidate); n++) candidate = System.IO.Path.Combine(dir, $"{stem} (reduced {n}).pdf");
        return candidate;
    }

    private void Done(string message)
    {
        _status.Text = message;
        _go.IsEnabled = true; _choices.IsEnabled = true;
        if (_saved == null) _bar.Visibility = Visibility.Collapsed;
    }

    private void Open(string path) { PdfWindow.Open(new[] { path }); Close(); }

    private static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>The size typed in the box, in bytes: "2", "1.5", "1,5", "2 MB" or "500 KB". A megabyte counts as 1,000,000 bytes here, the
    /// smaller of the two ways sites count it, so the copy fits either way.</summary>
    private bool TryLimit(out long bytes, out string label)
    {
        bytes = 0; label = "";
        string s = _limit.Text.Trim().ToLowerInvariant().Replace(',', '.');
        double unit = _unitKb?.IsChecked == true ? 1_000 : 1_000_000;                // (the unit chosen next to the box, unless the text says its own)
        if (s.EndsWith("kb")) { unit = 1_000; s = s[..^2]; }
        else if (s.EndsWith("mb")) { unit = 1_000_000; s = s[..^2]; }
        else if (s.EndsWith('k')) { unit = 1_000; s = s[..^1]; }
        else if (s.EndsWith('m')) { unit = 1_000_000; s = s[..^1]; }
        if (!double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) || n <= 0) return false;
        bytes = (long)(n * unit);
        label = n.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + (unit == 1_000 ? " KB" : " MB");
        return bytes >= 20_000 && bytes <= 100_000_000_000;
    }

    public static string Bytes(long n) => n >= 1048576
        ? (n / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, (int)Math.Round(n / 1024.0)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " KB";
}
