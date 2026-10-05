using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone;

/// <summary>
/// The song info editor: title, artist, album artist, album, year, track and disc numbers, genre, composer, comment, lyrics and the cover picture,
/// written into the file itself (MP3, FLAC, M4A, OGG ...). With several songs chosen, only what you change is written to all of them.
/// </summary>
public sealed class MusicTagWindow : Window
{
    private sealed record Field(string Key, string Label, Func<TagLib.Tag, string> Get, Action<TagLib.Tag, string> Set);

    private static string Join(string[]? parts) => parts == null ? "" : string.Join("; ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    private static string[] Split(string text) => text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string Num(uint n) => n == 0 ? "" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static uint ToNum(string text) => uint.TryParse(text.Trim(), out uint n) ? n : 0;

    private static readonly Field[] Fields =
    {
        new("title", "Title", t => t.Title ?? "", (t, v) => t.Title = v),
        new("artist", "Artist", t => Join(t.Performers), (t, v) => t.Performers = Split(v)),
        new("albumartist", "Album artist", t => Join(t.AlbumArtists), (t, v) => t.AlbumArtists = Split(v)),
        new("album", "Album", t => t.Album ?? "", (t, v) => t.Album = v),
        new("year", "Year", t => Num(t.Year), (t, v) => t.Year = ToNum(v)),
        new("track", "Track", t => Num(t.Track), (t, v) => t.Track = ToNum(v)),
        new("trackcount", "of", t => Num(t.TrackCount), (t, v) => t.TrackCount = ToNum(v)),
        new("disc", "Disc", t => Num(t.Disc), (t, v) => t.Disc = ToNum(v)),
        new("disccount", "of", t => Num(t.DiscCount), (t, v) => t.DiscCount = ToNum(v)),
        new("genre", "Genre", t => Join(t.Genres), (t, v) => t.Genres = Split(v)),
        new("composer", "Composer", t => Join(t.Composers), (t, v) => t.Composers = Split(v)),
        new("comment", "Comment", t => t.Comment ?? "", (t, v) => t.Comment = v),
        new("lyrics", "Lyrics", t => t.Lyrics ?? "", (t, v) => t.Lyrics = v),
    };

    private readonly List<Track> _tracks;
    private readonly List<Track> _readable = new();
    private readonly Func<IEnumerable<Track>, Task<Action>> _release;
    private readonly Action _after;
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly Dictionary<string, TextBlock> _hints = new();
    private readonly HashSet<string> _changed = new();
    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _coverNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAE, 0xBF)), Margin = new Thickness(0, 6, 0, 0), MaxWidth = 150 };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _save = new() { Content = "Save", Style = null };
    private byte[]? _newCover, _newCoverMime0;
    private string _newCoverMime = "image/jpeg";
    private bool _coverChanged, _loading = true;

    public MusicTagWindow(List<Track> tracks, Func<IEnumerable<Track>, Task<Action>> release, Action afterSave)
    {
        _tracks = tracks; _release = release; _after = afterSave;
        Title = tracks.Count == 1 ? "Song info - " + System.IO.Path.GetFileName(tracks[0].Path) : $"Song info - {tracks.Count} songs";
        Width = 700; MinWidth = 640; Height = Math.Min(700, SystemParameters.WorkArea.Height * 0.92); MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x21)); FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5; Foreground = Brushes.White;
        WindowTheme.DarkTitleBar(this);

        var muted = new SolidColorBrush(Color.FromRgb(0xA7, 0xAE, 0xBF));
        bool many = tracks.Count > 1;

        // ---- read the songs ----
        var values = new List<Dictionary<string, string>>();
        byte[]? firstCover = null;
        foreach (var t in tracks)
        {
            try
            {
                using var f = TagLib.File.Create(t.Path);
                values.Add(Fields.ToDictionary(x => x.Key, x => x.Get(f.Tag)));
                firstCover ??= f.Tag.Pictures.Length > 0 ? f.Tag.Pictures[0].Data.Data : null;
                _readable.Add(t);
            }
            catch (Exception e) when (e is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException)
            {
                _error.Text = "Some songs could not be read: " + System.IO.Path.GetFileName(t.Path);
            }
        }

        // ---- the fields ----
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        TextBox MakeBox(Field f, bool multiline = false)
        {
            string common = values.Count == 0 ? "" : values[0][f.Key];
            bool mixed = values.Any(v => v[f.Key] != common);
            var box = new TextBox
            {
                Text = mixed ? "" : common, Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2B)), Foreground = Brushes.White, CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x34, 0x46)), Padding = new Thickness(8, 6, 8, 6), VerticalContentAlignment = VerticalAlignment.Center,
                SpellCheck = { IsEnabled = false },
            };
            if (multiline) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.MinHeight = 90; box.MaxHeight = 150; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; box.VerticalContentAlignment = VerticalAlignment.Top; }
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, "Tag" + f.Key);
            box.TextChanged += (_, _) => { if (_loading) return; _changed.Add(f.Key); if (_hints.TryGetValue(f.Key, out var h)) h.Visibility = box.Text.Length == 0 && mixed && !_changed.Contains(f.Key + "!") ? Visibility.Visible : Visibility.Collapsed; };
            _boxes[f.Key] = box;
            if (mixed) { _hints[f.Key] = new TextBlock { Text = "(different in each song)", Foreground = muted, IsHitTestVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; }
            return box;
        }
        UIElement WithHint(Field f, TextBox box)
        {
            var g = new Grid(); g.Children.Add(box);
            if (_hints.TryGetValue(f.Key, out var h)) g.Children.Add(h);
            return g;
        }
        void AddRow(string label, UIElement content, bool top = false)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = muted, VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = new Thickness(0, top ? 8 : 0, 8, 0) };
            Grid.SetRow(l, row); Grid.SetRow((FrameworkElement)content, row); Grid.SetColumn((FrameworkElement)content, 1);
            ((FrameworkElement)content).Margin = new Thickness(0, 0, 0, 10);
            grid.Children.Add(l); grid.Children.Add(content);
            row++;
        }
        Field Of(string key) => Fields.First(f => f.Key == key);

        var title = MakeBox(Of("title")); if (!many) AddRow("Title", title); else { title.IsEnabled = false; }
        AddRow("Artist", WithHint(Of("artist"), MakeBox(Of("artist"))));
        AddRow("Album artist", WithHint(Of("albumartist"), MakeBox(Of("albumartist"))));
        AddRow("Album", WithHint(Of("album"), MakeBox(Of("album"))));
        var year = MakeBox(Of("year")); year.Width = 90; year.HorizontalAlignment = HorizontalAlignment.Left;
        AddRow("Year", year);
        StackPanel Pair(Field a, Field b)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var first = MakeBox(a); first.Width = 70; var second = MakeBox(b); second.Width = 70;
            row.Children.Add(first);
            row.Children.Add(new TextBlock { Text = "of", Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) });
            row.Children.Add(second);
            return row;
        }
        if (!many) AddRow("Track", Pair(Of("track"), Of("trackcount")));
        AddRow("Disc", Pair(Of("disc"), Of("disccount")));
        AddRow("Genre", WithHint(Of("genre"), MakeBox(Of("genre"))));
        AddRow("Composer", WithHint(Of("composer"), MakeBox(Of("composer"))));
        AddRow("Comment", WithHint(Of("comment"), MakeBox(Of("comment"))));
        AddRow("Lyrics", WithHint(Of("lyrics"), MakeBox(Of("lyrics"), multiline: true)), top: true);
        foreach (var h in _hints.Values) h.Visibility = Visibility.Visible;

        // ---- the cover ----
        var coverBox = new Border { Width = 150, Height = 150, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2B)), ClipToBounds = true };
        coverBox.Child = new Grid { Children = { new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 56, Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x5E)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, _cover } };
        if (firstCover != null && !many) ShowCover(firstCover);
        Button Small(string text, Action a) { var b = new Button { Content = text, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }; b.Click += (_, _) => a(); return b; }
        var coverPanel = new StackPanel { Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        coverPanel.Children.Add(new TextBlock { Text = "Cover", Foreground = muted, Margin = new Thickness(0, 0, 0, 6) });
        coverPanel.Children.Add(coverBox);
        coverPanel.Children.Add(Small("Change picture…", ChooseCover));
        coverPanel.Children.Add(Small("Remove picture", () => { _newCover = null; _coverChanged = true; _cover.Source = null; _coverNote.Text = "The picture will be removed."; }));
        if (!many && firstCover != null) coverPanel.Children.Add(Small("Save picture as…", () => SaveCoverAs(firstCover)));
        coverPanel.Children.Add(_coverNote);
        if (many) _coverNote.Text = "Changing the picture sets it on every chosen song.";

        // ---- footer ----
        _save.Style = (Style)FindResource("DialogPrimary");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_save, "TagSave");
        _save.Click += async (_, _) => await SaveAsync();
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("DialogButton"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(_save);
        var footer = new DockPanel { Margin = new Thickness(24, 10, 24, 16), LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons); footer.Children.Add(_error);

        var content = new Grid { Margin = new Thickness(24, 20, 24, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(coverPanel, 1);
        content.Children.Add(grid); content.Children.Add(coverPanel);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        if (_readable.Count == 0) { _save.IsEnabled = false; if (_error.Text.Length == 0) _error.Text = "These songs can't be read."; }
        Loaded += (_, _) => { _loading = false; if (_boxes.TryGetValue("title", out var tb) && tb.IsEnabled) tb.Focus(); };
    }

    private void ShowCover(byte[] data)
    {
        try
        {
            var b = new BitmapImage();
            using var ms = new MemoryStream(data);
            b.BeginInit(); b.StreamSource = ms; b.DecodePixelWidth = 320; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
            _cover.Source = b;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or FileFormatException) { _cover.Source = null; }
    }

    private void ChooseCover()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a cover picture", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > 20 * 1024 * 1024) { _error.Text = "That picture is too big (over 20 MB)."; return; }
            byte[] bytes = File.ReadAllBytes(dlg.FileName);
            string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg") { _newCover = bytes; _newCoverMime = "image/jpeg"; }
            else if (ext == ".png") { _newCover = bytes; _newCoverMime = "image/png"; }
            else
            {
                // anything else becomes a PNG (what players expect inside a song)
                var img = new BitmapImage(); using var ms = new MemoryStream(bytes);
                img.BeginInit(); img.StreamSource = ms; img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit();
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img));
                using var o = new MemoryStream(); enc.Save(o);
                _newCover = o.ToArray(); _newCoverMime = "image/png";
            }
            _coverChanged = true;
            ShowCover(_newCover);
            _coverNote.Text = "The new picture is written when you press Save.";
            _error.Text = "";
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException or InvalidOperationException)
        {
            _error.Text = "That picture can't be used: " + e.Message;
        }
    }

    private void SaveCoverAs(byte[] data)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save the cover picture", FileName = "cover.jpg", Filter = "JPEG|*.jpg|PNG|*.png" };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllBytes(dlg.FileName, data); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _error.Text = "Couldn't save the picture: " + e.Message; }
    }

    private async Task SaveAsync()
    {
        if (_changed.Count == 0 && !_coverChanged) { DialogResult = false; return; }
        _save.IsEnabled = false; _error.Text = "Saving…";
        var restore = await _release(_readable);                    // a song that is playing is let go first: Windows won't let it be written otherwise
        var failures = new List<string>();
        var changed = _changed.ToList();
        var cover = _newCover; string mime = _newCoverMime; bool coverChanged = _coverChanged;
        var texts = _boxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim());
        await Task.Run(() =>
        {
            foreach (var t in _readable)
            {
                try
                {
                    using var f = TagLib.File.Create(t.Path);
                    foreach (string key in changed) Fields.First(x => x.Key == key).Set(f.Tag, texts[key]);
                    if (coverChanged)
                        f.Tag.Pictures = cover == null ? Array.Empty<TagLib.IPicture>()
                            : new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = mime } };
                    f.Save();
                }
                catch (Exception e) when (e is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(System.IO.Path.GetFileName(t.Path) + ": " + e.Message);
                }
            }
        });
        // what the playlist shows follows the file
        foreach (var t in _readable)
        {
            if (failures.Any(x => x.StartsWith(System.IO.Path.GetFileName(t.Path) + ":"))) continue;
            if (changed.Contains("title")) t.Title = texts["title"];
            if (changed.Contains("artist")) t.Artist = Join(Split(texts["artist"]));
            if (changed.Contains("album")) t.Album = texts["album"];
        }
        restore();
        _after();
        if (failures.Count > 0)
        {
            _save.IsEnabled = true;
            _error.Text = "Some songs could not be saved (is the file read-only or open in another program?):  " + string.Join("   ", failures.Take(3));
            return;
        }
        DialogResult = true;
    }
}
