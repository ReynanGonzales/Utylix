using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Little pictures used in the archive window.</summary>
internal static class Glyphs
{
    /// <summary>A yellow folder like the one in Explorer.</summary>
    public static ImageSource Folder => (ImageSource)Application.Current.FindResource("FolderIcon");
}

/// <summary>A row in the archive listing (a file or a folder inside the archive).</summary>
public sealed class ArchItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsFolder { get; init; }
    public long Size { get; set; }
    public long Packed { get; set; }
    public DateTime? Modified { get; set; }
    public bool Encrypted { get; init; }
    public ImageSource? Icon { get; init; }
    public string SizeText => IsFolder && Size == 0 ? "" : Format.Bytes(Size);
    public string PackedText => IsFolder ? "" : Format.Bytes(Packed);
    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public Visibility LockVisibility => Encrypted ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A file or folder on the "to be archived" list.</summary>
public sealed class AddItem : System.ComponentModel.INotifyPropertyChanged
{
    public string FullPath { get; init; } = "";
    public string Display => Path.GetFileName(FullPath.TrimEnd(Path.DirectorySeparatorChar));
    /// <summary>The yellow folder for folders, the file's own Windows icon for files.</summary>
    public ImageSource? Icon => Directory.Exists(FullPath) ? Glyphs.Folder : ShellIcons.ForFile(FullPath);
    private string _size = "…";
    public long Bytes { get; set; }
    public string SizeText { get => _size; set { _size = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SizeText))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Archives: open and extract ZIP/RAR/7z/TAR..., or create ZIP / TAR.GZ. A small WinRAR-style manager.</summary>
public partial class ArchivePage : UserControl
{
    private enum Mode { Extract, Create }

    /// <summary>The window title follows what is open ("photos.zip", "New archive").</summary>
    public event Action<string>? TitleChanged;
    public string? CurrentArchive => _archivePath;
    public bool IsBusy => _busy;
    public void CancelCurrent() => _cts?.Cancel();

    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<AddItem> _adds = new();
    private Dictionary<string, List<ArchItem>> _children = new();

    private string? _archivePath;
    private ArchiveListing? _listing;
    private string? _password;
    private string _folder = "";
    private bool _busy;
    private CancellationTokenSource? _cts;
    private string? _lastReveal;
    private bool _nameEdited, _settingName;

    private OverwriteMode _overwrite = OverwriteMode.Rename;
    private ArchiveFormat _format = ArchiveFormat.Zip;
    private CompressionLevel _level = CompressionLevel.Optimal;

    public ArchivePage()
    {
        InitializeComponent();
        CreateList.ItemsSource = _adds;

        AddChip(ModeChips, "Extract", "mode", () => SetMode(Mode.Extract), true);
        AddChip(ModeChips, "Create", "mode", () => SetMode(Mode.Create));
        foreach (var (label, mode) in new[] { ("Rename it", OverwriteMode.Rename), ("Overwrite", OverwriteMode.Overwrite), ("Skip it", OverwriteMode.Skip) })
            AddChip(OverwriteChips, label, "ow", () => _overwrite = mode, mode == OverwriteMode.Rename);
        foreach (var (label, fmt) in new[] { ("ZIP", ArchiveFormat.Zip), ("TAR.GZ", ArchiveFormat.TarGz), ("TAR", ArchiveFormat.Tar) })
            AddChip(FormatChips, label, "fmt", () => { _format = fmt; FormatChanged(); }, fmt == ArchiveFormat.Zip);
        foreach (var (label, level) in new[] { ("Store", CompressionLevel.NoCompression), ("Fast", CompressionLevel.Fastest), ("Normal", CompressionLevel.Optimal), ("Best", CompressionLevel.SmallestSize) })
            AddChip(LevelChips, label, "lvl", () => _level = level, level == CompressionLevel.Optimal);
        NameBox.TextChanged += (_, _) => { if (!_settingName) _nameEdited = true; };
        FormatChanged();
        UpdateState();
        CleanOldTempFiles();
    }

    private void AddChip(WrapPanel host, string text, string group, Action onChecked, bool isChecked = false)
    {
        var chip = new RadioButton { Content = text, GroupName = group, Style = (Style)FindResource("ChipButton"), IsChecked = isChecked };
        chip.Checked += (_, _) => onChecked();
        host.Children.Add(chip);
    }

    private void SetMode(Mode mode)
    {
        if (ExtractPanel == null) return;
        TitleChanged?.Invoke(mode == Mode.Create ? "New archive" : _archivePath != null ? Path.GetFileName(_archivePath) : "Utylix Archives");
        ExtractPanel.Visibility = mode == Mode.Extract ? Visibility.Visible : Visibility.Collapsed;
        CreatePanel.Visibility = mode == Mode.Create ? Visibility.Visible : Visibility.Collapsed;
        ExtractActions.Visibility = mode == Mode.Extract ? Visibility.Visible : Visibility.Collapsed;
        CreateBtn.Visibility = mode == Mode.Create ? Visibility.Visible : Visibility.Collapsed;
        ShowBtn.Visibility = Visibility.Collapsed;
        SetStatus("", false);
        UpdateState();
    }

    private void SelectMode(Mode mode)
    {
        foreach (RadioButton chip in ModeChips.Children) chip.IsChecked = (string)chip.Content == mode.ToString();   // (raises Checked -> SetMode)
    }

    // ---------- shared bits ----------
    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrBrush" : "TextBrush");
    }

    private void SetBusy(bool busy, bool indeterminate = false)
    {
        _busy = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = busy && indeterminate;
        if (busy) Progress.Value = 0;
        CancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateState();
    }

    private void UpdateState()
    {
        if (OpenBtn == null) return;
        bool open = _listing != null;
        OpenBtn.IsEnabled = !_busy;
        ExtractAllBtn.IsEnabled = open && !_busy;
        ExtractSelBtn.IsEnabled = open && !_busy;
        TestBtn.IsEnabled = open && !_busy;
        SelectAllBtn.IsEnabled = open && !_busy;
        CreateBtn.IsEnabled = _adds.Count > 0 && !_busy;
        ExtractEmpty.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        CreateEmpty.Visibility = _adds.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UpBtn.IsEnabled = open && _folder.Length > 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private IProgress<(double Fraction, string Current)> ProgressReporter(string verb) =>
        new Progress<(double Fraction, string Current)>(p =>
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
            SetStatus($"{verb}… {(int)(p.Fraction * 100)}%   {Shorten(p.Current, 60)}", false);
        });

    private static string Shorten(string text, int max) => text.Length <= max ? text : "…" + text[^(max - 1)..];

    private string? AskPassword(string archive, bool wrong)
    {
        var dlg = new PasswordWindow(Path.GetFileName(archive), wrong) { Owner = Window.GetWindow(this) };
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    private ImageSource? IconFor(string name)
    {
        string ext = Path.GetExtension(name);
        if (!_iconCache.TryGetValue(ext, out var icon)) _iconCache[ext] = icon = ShellIcons.ForFile(name);
        return icon;
    }

    private static void CleanOldTempFiles()
    {
        try
        {
            foreach (string name in new[] { "Utylix-open", "Utylix-drag" })         // files opened from an archive, and files dragged out of one
            {
                string root = Path.Combine(Path.GetTempPath(), name);
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.GetDirectories(root))
                    if (Directory.GetCreationTime(d) < DateTime.Now.AddHours(-2)) Directory.Delete(d, true);
            }
        }
        catch (Exception) { /* a file is still open: try next time */ }
    }

    // ---------- drop / open ----------
    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        bool create = CreatePanel.Visibility == Visibility.Visible;
        if (!create && files.Length == 1 && File.Exists(files[0]) && ArchiveService.IsArchiveName(files[0])) _ = OpenArchiveAsync(files[0]);
        else if (create || !files.Any(f => File.Exists(f) && ArchiveService.IsArchiveName(f))) AddSources(files);       // files/folders: they go into a new archive
        else _ = OpenArchiveAsync(files.First(f => ArchiveService.IsArchiveName(f)));
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var filter = string.Join(";", ArchiveService.Extensions.Select(x => "*." + x));
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open an archive", Filter = $"Archives|{filter}|All files|*.*" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _ = OpenArchiveAsync(dlg.FileName);
    }

    /// <summary>Opens an archive in the listing (also used by "Open with Utylix" and the right-click menu).</summary>
    public async Task OpenArchiveAsync(string path)
    {
        SelectMode(Mode.Extract);
        if (_busy) return;
        string? password = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                SetBusy(true, indeterminate: true);
                SetStatus("Opening " + Path.GetFileName(path) + "…", false);
                var listing = await Task.Run(() => ArchiveService.List(path, password));
                SetBusy(false);
                ShowListing(listing, password);
                SetStatus("", false);
                return;
            }
            catch (ArchivePasswordException ex)
            {
                SetBusy(false);
                password = AskPassword(path, ex.Wrong);
                if (password == null) { SetStatus("Cancelled.", false); return; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetBusy(false);
                SetStatus(ex.Message, true);
                return;
            }
        }
        SetStatus("Too many wrong passwords.", true);
    }

    private void ShowListing(ArchiveListing listing, string? password)
    {
        _listing = listing;
        _archivePath = listing.Path;
        TitleChanged?.Invoke(Path.GetFileName(listing.Path));
        _password = password;
        BuildTree(listing);
        ArchiveTitle.Text = Path.GetFileName(listing.Path);
        double ratio = listing.TotalSize > 0 ? 100.0 * listing.TotalPacked / listing.TotalSize : 0;
        ArchiveStats.Text = $"{listing.Format}  ·  {listing.FileCount:N0} file{(listing.FileCount == 1 ? "" : "s")}  ·  {Format.Bytes(listing.TotalSize)} unpacked, {Format.Bytes(listing.TotalPacked)} on disk" +
                            (listing.TotalSize > 0 ? $" ({ratio:0}%)" : "") + (listing.HasEncrypted ? "  ·  🔒 password protected" : "");
        ShowFolder("");
        UpdateState();
    }

    // ---------- the listing ----------
    private static string ParentOf(string path) { int i = path.LastIndexOf('/'); return i < 0 ? "" : path[..i]; }
    private static string LeafOf(string path) { int i = path.LastIndexOf('/'); return i < 0 ? path : path[(i + 1)..]; }

    private void BuildTree(ArchiveListing listing)
    {
        _children = new Dictionary<string, List<ArchItem>> { [""] = new() };
        var folders = new Dictionary<string, ArchItem>(StringComparer.OrdinalIgnoreCase);

        ArchItem EnsureFolder(string path, DateTime? modified = null)
        {
            if (folders.TryGetValue(path, out var existing)) { if (modified != null && existing.Modified == null) existing.Modified = modified; return existing; }
            string parent = ParentOf(path);
            if (parent.Length > 0) EnsureFolder(parent);
            var item = new ArchItem { Name = LeafOf(path), FullPath = path, IsFolder = true, Modified = modified, Icon = Glyphs.Folder };
            folders[path] = item;
            _children[path] = new();
            if (!_children.TryGetValue(parent, out var siblings)) _children[parent] = siblings = new();
            siblings.Add(item);
            return item;
        }

        foreach (var e in listing.Entries)
        {
            if (e.IsDirectory) { EnsureFolder(e.Path, e.Modified); continue; }
            string parent = ParentOf(e.Path);
            if (parent.Length > 0) EnsureFolder(parent);
            var file = new ArchItem { Name = LeafOf(e.Path), FullPath = e.Path, Size = e.Size, Packed = e.Packed, Modified = e.Modified, Encrypted = e.Encrypted, Icon = IconFor(LeafOf(e.Path)) };
            if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = new();
            list.Add(file);
            for (string p = parent; p.Length > 0; p = ParentOf(p)) { folders[p].Size += e.Size; folders[p].Packed += e.Packed; }     // folder totals
        }
    }

    private void ShowFolder(string path)
    {
        _folder = path;
        var items = (_children.TryGetValue(path, out var list) ? list : new())
            .OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        EntryList.ItemsSource = items;
        PathText.Text = path.Length == 0 ? "" : path.Replace("/", "  ›  ");
        PathIcon.Visibility = path.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpBtn.IsEnabled = path.Length > 0;
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_listing != null && _folder.Length > 0) ShowFolder(ParentOf(_folder));
    }

    private void EntryList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Back) Up_Click(sender, e);
        else if (e.Key == System.Windows.Input.Key.Enter && EntryList.SelectedItem is ArchItem) Entry_DoubleClick(sender, null!);
    }

    // ---------- select all, and dragging files out to a folder ----------
    private System.Windows.Point _dragStart;
    private ArchItem? _pressed;
    private bool _collapseOnRelease;

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        EntryList.Focus();
        EntryList.SelectAll();
    }

    private ArchItem? ItemUnder(object? source) =>
        source is DependencyObject d && ItemsControl.ContainerFromElement(EntryList, d) is ListBoxItem row ? row.DataContext as ArchItem : null;

    private bool _boxCandidate, _boxing, _boxAdds;
    private System.Windows.Point _boxStart;
    private HashSet<ArchItem> _boxBase = new();

    /// <summary>true when the pointer is on a file's icon or name (that is what you grab to drag it); the rest of a row is "empty" like in Explorer.</summary>
    private static bool OnNameOrIcon(object? source) =>
        source is FrameworkElement fe && (System.Windows.Automation.AutomationProperties.GetAutomationId(fe) == "EntryName" || fe is System.Windows.Controls.Image || fe.Parent is StackPanel { Orientation: Orientation.Horizontal });

    private void Entry_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _boxStart = e.GetPosition(EntryList);
        _pressed = null; _collapseOnRelease = false; _boxCandidate = false;
        if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.RepeatButton) return;      // the scroll bar
        var item = ItemUnder(e.OriginalSource);
        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        if (item != null && OnNameOrIcon(e.OriginalSource))
        {
            _pressed = item;                                                         // a file: it can be dragged out
            // pressing on one of several selected rows must not throw the selection away: the whole selection is what gets dragged
            if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None && EntryList.SelectedItems.Count > 1 && EntryList.SelectedItems.Contains(item))
            {
                e.Handled = true;
                _collapseOnRelease = true;                                           // (a plain click without dragging still selects just that row)
                EntryList.Focus();
            }
            return;
        }
        // empty space (below the rows, or beside a name): a drag from here draws a selection box
        _boxCandidate = true;
        _boxAdds = ctrl;
        _boxBase = ctrl ? EntryList.SelectedItems.Cast<ArchItem>().ToHashSet() : new();
        if (item == null && !ctrl) EntryList.SelectedItems.Clear();
        EntryList.Focus();
    }

    private void Entry_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_boxing)
        {
            _boxing = false;
            EntryList.ReleaseMouseCapture();
            BoxRect.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (_collapseOnRelease && _pressed != null)
        {
            EntryList.SelectedItems.Clear();
            EntryList.SelectedItem = _pressed;
        }
        _boxCandidate = false;
        _collapseOnRelease = false;
        _pressed = null;
    }

    private void Entry_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { if (_boxing) { _boxing = false; EntryList.ReleaseMouseCapture(); BoxRect.Visibility = Visibility.Collapsed; } return; }
        if (_busy || _archivePath == null) return;

        if (_boxCandidate || _boxing)
        {
            var now = e.GetPosition(EntryList);
            if (!_boxing)
            {
                if (Math.Abs(now.X - _boxStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - _boxStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _boxing = true;
                EntryList.CaptureMouse();
                BoxRect.Visibility = Visibility.Visible;
            }
            var box = new Rect(_boxStart, now);
            System.Windows.Controls.Canvas.SetLeft(BoxRect, box.X); System.Windows.Controls.Canvas.SetTop(BoxRect, box.Y);
            BoxRect.Width = box.Width; BoxRect.Height = box.Height;
            foreach (var item in EntryList.Items.Cast<ArchItem>())
            {
                if (EntryList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem row) continue;
                var bounds = row.TransformToAncestor(EntryList).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                row.IsSelected = bounds.IntersectsWith(box) || (_boxAdds && _boxBase.Contains(item));
            }
            e.Handled = true;
            return;
        }

        if (_pressed == null) return;
        var moved = e.GetPosition(null) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _collapseOnRelease = false;
        _pressed = null;
        StartDrag();
    }

    /// <summary>What is selected, with the names it gets where it is dropped (relative to the folder being viewed) and everything inside folders.</summary>
    private List<DragEntry> CollectDragEntries(IEnumerable<ArchItem> selected, out bool encrypted)
    {
        string baseKey = _folder.Length == 0 ? "" : _folder + "/";
        var list = new List<DragEntry>();
        bool anyEncrypted = false;
        void Add(ArchItem item)
        {
            string relative = item.FullPath.StartsWith(baseKey, StringComparison.OrdinalIgnoreCase) ? item.FullPath[baseKey.Length..] : item.FullPath;
            list.Add(new DragEntry(relative.Replace('/', '\\'), item.FullPath, item.IsFolder, item.IsFolder ? 0 : item.Size, item.Modified));
            if (item.Encrypted) anyEncrypted = true;
            if (item.IsFolder && _children.TryGetValue(item.FullPath, out var kids)) foreach (var kid in kids) Add(kid);
        }
        foreach (var item in selected) Add(item);
        encrypted = anyEncrypted;
        return list;
    }

    private void StartDrag()
    {
        var selected = EntryList.SelectedItems.Cast<ArchItem>().ToList();
        if (selected.Count == 0 || _archivePath == null) return;
        var entries = CollectDragEntries(selected, out bool encrypted);
        if (!entries.Any(x => !x.IsDirectory)) return;
        if (encrypted && _password == null)                                        // ask now: at the moment of the drop there is no window to ask in
        {
            var pw = AskPassword(_archivePath, false);
            if (pw == null) return;
            _password = pw;
        }
        var data = new ArchiveDragData(entries, _archivePath, selected.Select(i => i.FullPath).ToList(), _password);
        SetStatus("Drop them on a folder (or the desktop) to extract them there…", false);
        bool dropped = data.Drag();
        data.CleanUpLater();
        if (data.Failure is ArchivePasswordException) { _password = null; SetStatus("Wrong password: nothing was extracted. Drag again to type it again.", true); }
        else if (data.Failure != null) SetStatus("Couldn't extract: " + data.Failure.Message, true);
        else if (dropped) SetStatus($"Extracted {data.FileCount:N0} file{(data.FileCount == 1 ? "" : "s")} by dragging.", false);
        else if (data.Result == 0x00040101) SetStatus("", false);                                       // cancelled with Esc
        else if (data.Result == 0x00040100) SetStatus("That place doesn't accept files: drop them on a folder in Explorer or on the desktop.", true);
        else SetStatus($"The drag didn't work (Windows error 0x{data.Result:X8}).", true);
    }

    private async void Entry_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (EntryList.SelectedItem is not ArchItem item || _busy || _archivePath == null) return;
        if (item.IsFolder) { ShowFolder(item.FullPath); return; }

        // a file: take it out into a temporary folder and open it with whatever opens that type
        string temp = Path.Combine(Path.GetTempPath(), "Utylix-open", Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var result = await RunExtractAsync(new[] { item.FullPath }, temp, OverwriteMode.Overwrite, "Opening");
            if (result == null) return;
            string? file = ArchiveService.SafeTarget(temp, item.FullPath);
            if (file == null || !File.Exists(file)) { SetStatus("Couldn't get that file out of the archive.", true); return; }
            if (Util.IsRunnable(file) &&
                UMessage.Show(Window.GetWindow(this), $"\"{item.Name}\" is a program or script. Opening it will run it.\n\nRun it?", "Utylix", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            SetStatus("", false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { SetStatus("Nothing on this PC opens that type of file: " + ex.Message, true); }
    }

    // ---------- extracting ----------
    private void Dest_Changed(object sender, RoutedEventArgs e)
    {
        if (DestBox == null) return;
        DestBox.IsEnabled = DestBrowse.IsEnabled = DestOtherRadio.IsChecked == true;
    }

    private void DestBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Extract to…", InitialDirectory = DestBox.Text };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) DestBox.Text = dlg.FolderName;
    }

    private string? Destination()
    {
        if (_archivePath == null) return null;
        string dir = Path.GetDirectoryName(_archivePath)!;
        if (DestOtherRadio.IsChecked == true)
        {
            if (DestBox.Text.Trim().Length == 0) { SetStatus("Choose a folder to extract into.", true); return null; }
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(DestBox.Text.Trim())); }
            catch (Exception) { SetStatus("That folder name isn't valid.", true); return null; }
        }
        return DestHereRadio.IsChecked == true ? dir : Path.Combine(dir, Util.Sanitize(ArchiveService.BaseName(_archivePath)));
    }

    private void ExtractAll_Click(object sender, RoutedEventArgs e) => _ = ExtractToDestinationAsync(null);

    private void ExtractSelected_Click(object sender, RoutedEventArgs e)
    {
        var chosen = EntryList.SelectedItems.Cast<ArchItem>().Select(i => i.FullPath).ToList();
        if (chosen.Count == 0) { SetStatus("Click the files or folders you want first (Ctrl or Shift for several).", true); return; }
        _ = ExtractToDestinationAsync(chosen);
    }

    private async Task ExtractToDestinationAsync(IReadOnlyCollection<string>? only)
    {
        if (_busy || _archivePath == null) return;
        string? dest = Destination();
        if (dest == null) return;
        var result = await RunExtractAsync(only, dest, _overwrite, "Extracting");
        if (result == null) return;
        _lastReveal = result.Folder;
        ShowBtn.Visibility = Visibility.Visible;
        SetStatus($"Extracted {result.Files:N0} file{(result.Files == 1 ? "" : "s")} ({Format.Bytes(result.Bytes)}) to {result.Folder}" + (result.Skipped > 0 ? $"  ·  {result.Skipped} skipped" : ""), false);
    }

    /// <summary>Runs an extraction with progress, asking for the password when needed. Returns null when cancelled or failed (the status says why).</summary>
    private async Task<ExtractResult?> RunExtractAsync(IReadOnlyCollection<string>? only, string dest, OverwriteMode overwrite, string verb)
    {
        SetBusy(true, indeterminate: true);
        ShowBtn.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();
        try
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try { return await ArchiveService.ExtractAsync(_archivePath!, dest, only, _password, overwrite, ProgressReporter(verb), _cts.Token); }
                catch (ArchivePasswordException ex)
                {
                    var pw = AskPassword(_archivePath!, ex.Wrong || _password != null);
                    if (pw == null) { SetStatus("Cancelled.", false); return null; }
                    _password = pw;
                }
            }
            SetStatus("Too many wrong passwords.", true);
        }
        catch (OperationCanceledException) { SetStatus("Cancelled.", false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); _cts?.Dispose(); _cts = null; }
        return null;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _archivePath == null) return;
        SetBusy(true, indeterminate: true);
        _cts = new CancellationTokenSource();
        try
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try { SetStatus("Testing… " + Path.GetFileName(_archivePath), false); SetStatus(await ArchiveService.TestAsync(_archivePath, _password, ProgressReporter("Testing"), _cts.Token), false); return; }
                catch (ArchivePasswordException ex)
                {
                    var pw = AskPassword(_archivePath, ex.Wrong || _password != null);
                    if (pw == null) { SetStatus("Cancelled.", false); return; }
                    _password = pw;
                }
            }
        }
        catch (OperationCanceledException) { SetStatus("Cancelled.", false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("Problem found: " + ex.Message, true); }
        finally { SetBusy(false); _cts?.Dispose(); _cts = null; }
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReveal == null) return;
        if (File.Exists(_lastReveal)) Process.Start("explorer.exe", $"/select,\"{_lastReveal}\"");
        else if (Directory.Exists(_lastReveal)) Process.Start("explorer.exe", $"\"{_lastReveal}\"");
    }

    // ---------- creating ----------
    /// <summary>Puts files/folders on the "to be archived" list and shows the Create view.</summary>
    public void AddSources(IEnumerable<string> paths)
    {
        SelectMode(Mode.Create);
        foreach (var raw in paths)
        {
            string path;
            try { path = Path.GetFullPath(raw); } catch (Exception) { continue; }
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (_adds.Any(a => string.Equals(a.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new AddItem { FullPath = path };
            _adds.Add(item);
            _ = Task.Run(() => ArchiveService.Expand(new[] { path }).Sum(f => f.Size)).ContinueWith(t =>
                Dispatcher.BeginInvoke(() => { item.Bytes = t.IsCompletedSuccessfully ? t.Result : 0; item.SizeText = Format.Bytes(item.Bytes); RefreshTotal(); }));
        }
        SuggestName();
        UpdateState();
    }

    private void RefreshTotal() => CreateTotal.Text = _adds.Count == 0 ? "" : $"{_adds.Count} item{(_adds.Count == 1 ? "" : "s")}  ·  {Format.Bytes(_adds.Sum(a => a.Bytes))}";

    private void SuggestName()
    {
        if (_nameEdited) return;
        string name = _adds.Count == 1
            ? (Directory.Exists(_adds[0].FullPath) ? Path.GetFileName(_adds[0].FullPath.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(_adds[0].FullPath))
            : _adds.Count > 1 ? Path.GetFileName((Path.GetDirectoryName(_adds[0].FullPath) ?? "").TrimEnd(Path.DirectorySeparatorChar)) : "";
        if (_adds.Count > 0 && name.Length == 0) name = "Archive";
        _settingName = true;
        NameBox.Text = name;
        _settingName = false;
    }

    private void FormatChanged()
    {
        if (FormatNote == null) return;
        FormatNote.Text = _format switch
        {
            ArchiveFormat.Zip => "Opens on every computer without extra programs.",
            ArchiveFormat.TarGz => "The common format on Linux and for developers: bundles the files, then compresses them all together.",
            _ => "Only bundles the files into one, without shrinking them.",
        };
        if (LevelChips != null) LevelChips.IsEnabled = _format != ArchiveFormat.Tar;
        if (PasswordPanel != null) PasswordPanel.Visibility = _format == ArchiveFormat.Zip ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add files", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddSources(dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add folders", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddSources(dlg.FolderNames);
    }

    private void RemoveSel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        foreach (var a in CreateList.SelectedItems.Cast<AddItem>().ToList()) _adds.Remove(a);
        RefreshTotal(); SuggestName(); UpdateState();
    }

    private void ClearList_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _adds.Clear();
        _nameEdited = false;
        RefreshTotal(); SuggestName(); UpdateState();
    }

    private void CreateList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete) RemoveSel_Click(sender, e);
    }

    private void CreateDest_Changed(object sender, RoutedEventArgs e)
    {
        if (CreateDestBox == null) return;
        CreateDestBox.IsEnabled = CreateDestBrowse.IsEnabled = CreateOtherRadio.IsChecked == true;
    }

    private void CreateBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Save the archive in…", InitialDirectory = CreateDestBox.Text };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) CreateDestBox.Text = dlg.FolderName;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _adds.Count == 0) return;
        string name = NameBox.Text.Trim();
        if (name.Length == 0) { SetStatus("Give the archive a name.", true); return; }
        string dir;
        try
        {
            dir = CreateOtherRadio.IsChecked == true && CreateDestBox.Text.Trim().Length > 0
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(CreateDestBox.Text.Trim()))
                : Path.GetDirectoryName(_adds[0].FullPath.TrimEnd(Path.DirectorySeparatorChar)) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }
        catch (Exception) { SetStatus("That folder name isn't valid.", true); return; }
        if (CreateOtherRadio.IsChecked == true && CreateDestBox.Text.Trim().Length == 0) { SetStatus("Choose a folder to save into.", true); return; }

        string? password = null;
        if (_format == ArchiveFormat.Zip && (PassBox.Password.Length > 0 || PassBox2.Password.Length > 0))
        {
            if (PassBox.Password != PassBox2.Password) { SetStatus("The two passwords are different. Type the same one in both boxes.", true); return; }
            password = PassBox.Password;
        }

        string output = ArchiveService.UniquePath(dir, name, ArchiveService.Extension(_format));
        SetBusy(true, indeterminate: true);
        ShowBtn.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();
        try
        {
            await ArchiveService.CreateAsync(_adds.Select(a => a.FullPath).ToList(), output, _format, _level, ProgressReporter("Adding"), _cts.Token, password);
            _lastReveal = output;
            ShowBtn.Visibility = Visibility.Visible;
            PassBox.Clear(); PassBox2.Clear();
            long size = new FileInfo(output).Length;
            long original = _adds.Sum(a => a.Bytes);
            SetStatus($"Created {Path.GetFileName(output)}{(password != null ? " (password protected)" : "")} ({Format.Bytes(size)}" + (original > 0 ? $", {100.0 * size / original:0}% of the original" : "") + ")", false);
        }
        catch (OperationCanceledException) { SetStatus("Cancelled. Nothing was left behind.", false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); _cts?.Dispose(); _cts = null; }
    }
}
