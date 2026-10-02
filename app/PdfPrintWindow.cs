using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Documents.Serialization;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Utylix PDF's print window, like Acrobat's: printer (and its own settings), copies, which pages (all, current, "1-3, 5", odd / even,
/// reverse), size on the paper (fit, actual size, shrink big pages), pages per sheet, orientation, paper, colour or black and white,
/// two-sided, quality - with a preview of every sheet.
/// </summary>
public sealed class PdfPrintWindow : Window
{
    public enum Sizing { Fit, Actual, Shrink }
    private enum Turn { Auto, Portrait, Landscape }

    // remembered while Utylix runs
    private static string? _lastPrinter;
    private static Sizing _lastSizing = Sizing.Fit;
    private static bool _lastGray, _lastHigh;

    private readonly PdfFile _pdf;
    private readonly int _currentPage;
    private readonly string _name;
    private PrintQueue? _queue;
    private PrintTicket? _ticket;
    private PrintCapabilities? _caps;
    private PrintLayout? _layout;
    private int _previewSheet;
    private readonly Dictionary<(int, int, int), BitmapSource> _previewCache = new();
    private XpsDocumentWriter? _writer;
    private bool _loading;

    // ---------- parts ----------
    private readonly ComboBox _printer = new() { MinWidth = 260 };
    private readonly TextBlock _printerState = new() { FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _props = new() { Content = "Printer settings…", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox _copies = new() { Text = "1", Width = 56, TextAlignment = TextAlignment.Center };
    private readonly CheckBox _collate = new() { Content = "Collate", IsChecked = true, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _all = new() { Content = "All", GroupName = "pages", IsChecked = true };
    private readonly RadioButton _current = new() { GroupName = "pages" };
    private readonly RadioButton _range = new() { Content = "Pages", GroupName = "pages" };
    private readonly TextBox _rangeBox = new() { Width = 150, ToolTip = "For example 1-3, 5, 8-" };
    private readonly ComboBox _subset = new() { Width = 170 };
    private readonly CheckBox _reverse = new() { Content = "Reverse order", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ComboBox _perSheet = new() { Width = 170 };
    private readonly ComboBox _paper = new() { MinWidth = 260 };
    private readonly Dictionary<Sizing, RadioButton> _sizing = new();
    private readonly Dictionary<Turn, RadioButton> _turn = new();
    private readonly RadioButton _color = new() { Content = "Colour", GroupName = "color" };
    private readonly RadioButton _gray = new() { Content = "Black and white", GroupName = "color" };
    private readonly RadioButton _oneSided = new() { Content = "One-sided", GroupName = "duplex", IsChecked = true };
    private readonly RadioButton _longEdge = new() { Content = "Both sides, flip on long edge", GroupName = "duplex" };
    private readonly RadioButton _shortEdge = new() { Content = "Both sides, flip on short edge", GroupName = "duplex" };
    private readonly RadioButton _standard = new() { Content = "Standard", GroupName = "quality" };
    private readonly RadioButton _high = new() { Content = "High (slower)", GroupName = "quality" };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Border _sheet = new() { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sheetText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), MinWidth = 110, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _print = new() { Content = "Print", MinWidth = 110, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 96 };

    public PdfPrintWindow(PdfFile pdf, int currentPage, string name)
    {
        _pdf = pdf; _currentPage = Math.Clamp(currentPage, 0, Math.Max(0, pdf.PageCount - 1)); _name = name;
        Title = "Print - " + name;
        Width = 1000; Height = 720; MinWidth = 860; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is System.IO.IOException or UriFormatException) { }

        foreach (var b in new[] { _props, _print, _cancel }) b.Style = (Style)Application.Current.FindResource(b == _print ? "DialogPrimary" : "DialogButton");
        foreach (var t in new[] { _copies, _rangeBox }) { t.Style = (Style)Application.Current.FindResource("Field"); }
        foreach (var r in new[] { _all, _current, _range }) { r.Foreground = Foreground; r.VerticalContentAlignment = VerticalAlignment.Center; r.Margin = new Thickness(0, 0, 16, 0); }
        _current.Content = $"Current page ({_currentPage + 1})";
        foreach (var r in new[] { _color, _gray, _oneSided, _longEdge, _shortEdge, _standard, _high }) r.Style = (Style)Application.Current.FindResource("ChipButton");
        foreach (var c in new[] { _collate, _reverse }) c.Foreground = Foreground;
        Auto(_printer, "PrintPrinter"); Auto(_copies, "PrintCopies"); Auto(_rangeBox, "PrintRange"); Auto(_paper, "PrintPaper"); Auto(_perSheet, "PrintPerSheet");
        Auto(_print, "PrintGo"); Auto(_status, "PrintStatus"); Auto(_all, "PrintAll"); Auto(_current, "PrintCurrent"); Auto(_range, "PrintPagesRange");

        // ---- the settings ----
        var form = new StackPanel { Margin = new Thickness(22, 18, 16, 10) };
        StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var i in items) p.Children.Add(i); return p; }
        void Section(string title, UIElement body)
        {
            form.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, form.Children.Count == 0 ? 0 : 14, 0, 6) });
            form.Children.Add(body);
        }
        WrapPanel Chips(params RadioButton[] chips) { var w = new WrapPanel(); foreach (var c in chips) w.Children.Add(c); return w; }

        var printerBox = new StackPanel();
        printerBox.Children.Add(Row(_printer, _props));
        printerBox.Children.Add(_printerState);
        Section("Printer", printerBox);
        Section("Copies", Row(_copies, _collate));

        _subset.ItemsSource = new[] { "All pages in range", "Odd pages only", "Even pages only" };
        _subset.SelectedIndex = 0;
        var pages = new StackPanel();
        pages.Children.Add(Row(_all, _current, _range, _rangeBox));
        pages.Children.Add(Row(_subset, _reverse));
        ((FrameworkElement)pages.Children[1]).Margin = new Thickness(0, 8, 0, 0);
        Section("Pages", pages);

        foreach (var (s, text) in new[] { (Sizing.Fit, "Fit to paper"), (Sizing.Actual, "Actual size"), (Sizing.Shrink, "Shrink big pages") })
        {
            var r = new RadioButton { Content = text, GroupName = "sizing", Style = (Style)Application.Current.FindResource("ChipButton") };
            Auto(r, "PrintSizing" + s);
            _sizing[s] = r;
        }
        _perSheet.ItemsSource = new[] { "1 page per sheet", "2 pages per sheet", "4 pages per sheet", "6 pages per sheet", "9 pages per sheet", "16 pages per sheet" };
        _perSheet.SelectedIndex = 0;
        _perSheet.HorizontalAlignment = HorizontalAlignment.Left;
        _paper.HorizontalAlignment = HorizontalAlignment.Left;
        var size = new StackPanel();
        size.Children.Add(Chips(_sizing.Values.ToArray()));
        size.Children.Add(_perSheet);
        Section("Size on the paper", size);

        foreach (var (t, text) in new[] { (Turn.Auto, "Automatic"), (Turn.Portrait, "Portrait"), (Turn.Landscape, "Landscape") })
        {
            var r = new RadioButton { Content = text, GroupName = "turn", Style = (Style)Application.Current.FindResource("ChipButton") };
            Auto(r, "PrintTurn" + t);
            _turn[t] = r;
        }
        _turn[Turn.Auto].IsChecked = true;
        Section("Orientation", Chips(_turn.Values.ToArray()));
        Section("Paper", _paper);
        Section("Colour", Chips(_color, _gray));
        Section("Sides", Chips(_oneSided, _longEdge, _shortEdge));
        Section("Quality", Chips(_standard, _high));

        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 470 };

        // ---- the preview ----
        _sheet.Child = _preview;
        _sheet.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 };
        var prev = Small("", "Previous sheet", () => ShowSheet(_previewSheet - 1));
        var next = Small("", "Next sheet", () => ShowSheet(_previewSheet + 1));
        var nav = Row(prev, _sheetText, next);
        nav.HorizontalAlignment = HorizontalAlignment.Center;
        nav.Margin = new Thickness(0, 10, 0, 0);
        var previewArea = new DockPanel { Margin = new Thickness(10, 18, 22, 10) };
        DockPanel.SetDock(nav, Dock.Bottom);
        previewArea.Children.Add(nav);
        var stage = new Border { Background = (Brush)Application.Current.FindResource("CardBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(24), Child = _sheet };
        previewArea.Children.Add(stage);

        // ---- bottom ----
        var bottom = new DockPanel { Margin = new Thickness(22, 4, 22, 18) };
        var buttons = Row(_print, _cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var layout = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);
        DockPanel.SetDock(formScroll, Dock.Left);
        layout.Children.Add(formScroll);
        layout.Children.Add(previewArea);
        Content = layout;

        // ---- behaviour ----
        _sizing[_lastSizing].IsChecked = true;
        (_lastGray ? _gray : _color).IsChecked = true;
        (_lastHigh ? _high : _standard).IsChecked = true;
        _printer.SelectionChanged += (_, _) => { if (!_loading) LoadPrinter(); };
        _paper.SelectionChanged += (_, _) => { if (!_loading) Changed(); };
        _props.Click += (_, _) => PrinterSettings();
        foreach (var r in new[] { _all, _current, _range, _color, _gray, _oneSided, _longEdge, _shortEdge, _standard, _high }.Concat(_sizing.Values).Concat(_turn.Values))
            r.Checked += (_, _) => { if (!_loading) Changed(); };
        _rangeBox.GotKeyboardFocus += (_, _) => _range.IsChecked = true;
        _rangeBox.TextChanged += (_, _) => { if (!_loading) Changed(); };
        _copies.TextChanged += (_, _) => { if (!_loading) Changed(); };
        _subset.SelectionChanged += (_, _) => { if (!_loading) Changed(); };
        _perSheet.SelectionChanged += (_, _) => { if (!_loading) Changed(); };
        _reverse.Checked += (_, _) => Changed(); _reverse.Unchecked += (_, _) => Changed();
        _print.Click += (_, _) => Print();
        _cancel.Click += (_, _) => { if (_writer != null) { _writer.CancelAsync(); _status.Text = "Cancelling…"; } else Close(); };
        stage.SizeChanged += (_, _) => ShowSheet(_previewSheet);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _writer == null) { Close(); e.Handled = true; }
            else if (e.Key == Key.PageDown) { ShowSheet(_previewSheet + 1); e.Handled = true; }
            else if (e.Key == Key.PageUp) { ShowSheet(_previewSheet - 1); e.Handled = true; }
        };
        Loaded += (_, _) => LoadPrinters();
    }

    private static void Auto(UIElement e, string id) => System.Windows.Automation.AutomationProperties.SetAutomationId(e, id);

    private Button Small(string glyph, string tip, Action action)
    {
        var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 }, ToolTip = tip, Width = 34, Height = 30, Padding = new Thickness(0) };
        b.Click += (_, _) => action();
        return b;
    }

    // ---------- printers ----------
    private sealed record PrinterItem(string Name, string FullName) { public override string ToString() => Name; }
    private sealed record PaperItem(PageMediaSize Size, string Text) { public override string ToString() => Text; }

    private void LoadPrinters()
    {
        _loading = true;
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })
                               .Select(q => new PrinterItem(q.Name, q.FullName)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            string? wanted = _lastPrinter;
            if (wanted == null) try { wanted = LocalPrintServer.GetDefaultPrintQueue().FullName; } catch (PrintQueueException) { }
            _printer.ItemsSource = queues;
            _printer.SelectedItem = queues.FirstOrDefault(p => p.FullName == wanted) ?? queues.FirstOrDefault();
        }
        catch (Exception e) when (e is PrintSystemException or PrintQueueException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "Couldn't list the printers: " + e.Message;
        }
        finally { _loading = false; }
        if (_printer.SelectedItem == null) { _print.IsEnabled = false; _printerState.Text = "No printer is installed."; ShowSheet(0); return; }
        LoadPrinter();
    }

    private void LoadPrinter()
    {
        if (_printer.SelectedItem is not PrinterItem item) return;
        _loading = true;
        try
        {
            _queue?.Dispose();
            int cut = item.FullName.StartsWith(@"\\", StringComparison.Ordinal) ? item.FullName.IndexOf('\\', 2) : -1;
            _queue = cut > 0
                ? new PrintQueue(new PrintServer(item.FullName[..cut]), item.FullName[(cut + 1)..])     // a printer shared by another PC
                : new PrintQueue(new LocalPrintServer(), item.Name);
            _ticket = _queue.UserPrintTicket?.Clone() ?? _queue.DefaultPrintTicket?.Clone() ?? new PrintTicket();
            _caps = _queue.GetPrintCapabilities(_ticket);
            _lastPrinter = item.FullName;
            _queue.Refresh();
            _printerState.Text = State(_queue);

            // paper sizes this printer knows
            var papers = _caps.PageMediaSizeCapability.Where(m => m.Width is > 0 && m.Height is > 0)
                              .Select(m => new PaperItem(m, PaperName(m))).GroupBy(p => p.Text).Select(g => g.First()).ToList();
            _paper.ItemsSource = papers;
            var current = _ticket.PageMediaSize;
            _paper.SelectedItem = papers.FirstOrDefault(p => current != null && p.Size.PageMediaSizeName == current.PageMediaSizeName && current.PageMediaSizeName != PageMediaSizeName.Unknown)
                                  ?? papers.FirstOrDefault(p => current != null && Math.Abs((p.Size.Width ?? 0) - (current.Width ?? 0)) < 2 && Math.Abs((p.Size.Height ?? 0) - (current.Height ?? 0)) < 2)
                                  ?? papers.FirstOrDefault();

            // what it can do
            bool color = _caps.OutputColorCapability.Contains(OutputColor.Color);
            _color.IsEnabled = color;
            if (!color) _gray.IsChecked = true;
            var duplex = _caps.DuplexingCapability;
            _longEdge.IsEnabled = duplex.Contains(Duplexing.TwoSidedLongEdge);
            _shortEdge.IsEnabled = duplex.Contains(Duplexing.TwoSidedShortEdge);
            if (_ticket.Duplexing == Duplexing.TwoSidedLongEdge && _longEdge.IsEnabled) _longEdge.IsChecked = true;
            else if (_ticket.Duplexing == Duplexing.TwoSidedShortEdge && _shortEdge.IsEnabled) _shortEdge.IsChecked = true;
            else _oneSided.IsChecked = true;
            _collate.IsEnabled = _caps.CollationCapability.Contains(Collation.Collated) || _caps.CollationCapability.Count == 0;
        }
        catch (Exception e) when (e is PrintSystemException or PrintQueueException or InvalidOperationException or ArgumentException or COMException)
        {
            _printerState.Text = "This printer can't be reached: " + e.Message;
        }
        finally { _loading = false; }
        Changed();
    }

    private static string State(PrintQueue q)
    {
        if (q.IsOffline) return "Offline";
        if (q.IsPaperJammed) return "Paper jam";
        if (q.IsOutOfPaper) return "Out of paper";
        if (q.HasPaperProblem) return "Paper problem";
        if (q.IsInError) return "Has an error";
        if (q.IsPaused) return "Paused";
        int jobs = q.NumberOfJobs;
        return jobs > 0 ? $"Ready · {jobs} job{(jobs == 1 ? "" : "s")} waiting" : "Ready";
    }

    private static string PaperName(PageMediaSize m)
    {
        double wmm = (m.Width ?? 0) / 96 * 25.4, hmm = (m.Height ?? 0) / 96 * 25.4;
        string name = m.PageMediaSizeName switch
        {
            PageMediaSizeName.NorthAmericaLetter => "Letter",
            PageMediaSizeName.NorthAmericaLegal => "Legal",
            PageMediaSizeName.NorthAmericaExecutive => "Executive",
            PageMediaSizeName.NorthAmericaTabloid => "Tabloid",
            PageMediaSizeName.ISOA3 => "A3",
            PageMediaSizeName.ISOA4 => "A4",
            PageMediaSizeName.ISOA5 => "A5",
            PageMediaSizeName.ISOA6 => "A6",
            PageMediaSizeName.ISOB5Envelope => "B5 envelope",
            PageMediaSizeName.JISB4 => "B4 (JIS)",
            PageMediaSizeName.JISB5 => "B5 (JIS)",
            PageMediaSizeName.NorthAmerica4x6 => "4 × 6 in",
            PageMediaSizeName.NorthAmerica5x7 => "5 × 7 in",
            PageMediaSizeName.Unknown => "",
            _ => m.PageMediaSizeName?.ToString() ?? "",
        };
        // 8.5 × 13 in is "Long" / folio, used a lot in the Philippines
        if (name.Length == 0 && Math.Abs(wmm - 215.9) < 1.5 && Math.Abs(hmm - 330.2) < 1.5) name = "Long (8.5 × 13 in)";
        string dims = (wmm > 0 ? $"{wmm:0} × {hmm:0} mm" : "");
        return name.Length == 0 ? dims : $"{name}   ({dims})";
    }

    // ---------- the printer's own settings window ----------
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv")] private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode)] private static extern int DocumentProperties(IntPtr hwnd, IntPtr printer, string name, IntPtr output, IntPtr input, int mode);

    private void PrinterSettings()
    {
        if (_queue == null || _ticket == null) return;
        try
        {
            using var converter = new PrintTicketConverter(_queue.FullName, _queue.ClientPrintSchemaVersion);
            byte[] devmode = converter.ConvertPrintTicketToDevMode(_ticket, BaseDevModeType.UserDefault);
            if (!OpenPrinter(_queue.FullName, out IntPtr handle, IntPtr.Zero)) { _status.Text = "Couldn't open the printer's settings."; return; }
            IntPtr input = Marshal.AllocHGlobal(devmode.Length), output = IntPtr.Zero;
            try
            {
                Marshal.Copy(devmode, 0, input, devmode.Length);
                int size = DocumentProperties(IntPtr.Zero, handle, _queue.FullName, IntPtr.Zero, IntPtr.Zero, 0);
                if (size <= 0) return;
                output = Marshal.AllocHGlobal(size);
                const int DM_OUT_BUFFER = 2, DM_IN_PROMPT = 4, DM_IN_BUFFER = 8;
                int result = DocumentProperties(new WindowInteropHelper(this).Handle, handle, _queue.FullName, output, input, DM_IN_BUFFER | DM_IN_PROMPT | DM_OUT_BUFFER);
                if (result != 1) return;                                  // (cancelled)
                var changed = new byte[size];
                Marshal.Copy(output, changed, 0, size);
                _ticket = converter.ConvertDevModeToPrintTicket(changed);
            }
            finally
            {
                Marshal.FreeHGlobal(input);
                if (output != IntPtr.Zero) Marshal.FreeHGlobal(output);
                ClosePrinter(handle);
            }
            // show what the printer's window changed
            _loading = true;
            try
            {
                var papers = (IEnumerable<PaperItem>)_paper.ItemsSource;
                var m = _ticket.PageMediaSize;
                var match = papers?.FirstOrDefault(p => m != null && Math.Abs((p.Size.Width ?? 0) - (m.Width ?? 0)) < 2 && Math.Abs((p.Size.Height ?? 0) - (m.Height ?? 0)) < 2);
                if (match != null) _paper.SelectedItem = match;
                if (_ticket.OutputColor is OutputColor.Grayscale or OutputColor.Monochrome) _gray.IsChecked = true; else if (_color.IsEnabled) _color.IsChecked = true;
                if (_ticket.Duplexing == Duplexing.TwoSidedLongEdge) _longEdge.IsChecked = true;
                else if (_ticket.Duplexing == Duplexing.TwoSidedShortEdge) _shortEdge.IsChecked = true;
                else _oneSided.IsChecked = true;
                if (_ticket.PageOrientation == PageOrientation.Landscape) _turn[Turn.Landscape].IsChecked = true;
                else if (_ticket.PageOrientation == PageOrientation.Portrait && _turn[Turn.Landscape].IsChecked == true) _turn[Turn.Portrait].IsChecked = true;
                if (_ticket.CopyCount is int n and > 0) _copies.Text = n.ToString(CultureInfo.InvariantCulture);
            }
            finally { _loading = false; }
            Changed();
        }
        catch (Exception e) when (e is PrintQueueException or PrintSystemException or ArgumentException or InvalidOperationException or COMException)
        {
            _status.Text = "Couldn't open the printer's settings: " + e.Message;
        }
    }

    // ---------- settings -> layout ----------
    private int PerSheet => _perSheet.SelectedIndex switch { 1 => 2, 2 => 4, 3 => 6, 4 => 9, 5 => 16, _ => 1 };

    private int Copies => int.TryParse(_copies.Text.Trim(), out int n) ? Math.Clamp(n, 1, 999) : 0;

    private void Changed()
    {
        if (!IsLoaded) return;
        _status.Foreground = (Brush)Application.Current.FindResource("TextBrush");
        _status.Text = "";
        _print.IsEnabled = _queue != null && _ticket != null;

        // which pages
        List<int>? pages = _current.IsChecked == true ? new List<int> { _currentPage }
                         : _range.IsChecked == true ? ParseRange(_rangeBox.Text, _pdf.PageCount, out string? error) is { } p ? p : Fail(error!)
                         : Enumerable.Range(0, _pdf.PageCount).ToList();
        if (pages == null) return;
        if (_subset.SelectedIndex == 1) pages = pages.Where(i => (i + 1) % 2 == 1).ToList();
        else if (_subset.SelectedIndex == 2) pages = pages.Where(i => (i + 1) % 2 == 0).ToList();
        if (_reverse.IsChecked == true) pages.Reverse();
        if (pages.Count == 0) { Fail("No pages to print with these choices."); return; }
        if (Copies == 0) { Fail("Type how many copies (1 to 999)."); return; }

        // the paper, turned as chosen ("automatic" follows the pages; several per sheet: what fits them best)
        double pw = 816, ph = 1056;                                          // (Letter, when the printer says nothing)
        if (_paper.SelectedItem is PaperItem paper) { pw = paper.Size.Width ?? pw; ph = paper.Size.Height ?? ph; }
        int per = PerSheet;
        var first = _pdf.PageSize(pages[0]);
        bool landscape = _turn[Turn.Landscape].IsChecked == true
            || (_turn[Turn.Auto].IsChecked == true && per == 1 && first.Width > first.Height)
            || (_turn[Turn.Auto].IsChecked == true && per is 2 or 6 && first.Height >= first.Width);
        var sheet = landscape ? new Size(Math.Max(pw, ph), Math.Min(pw, ph)) : new Size(Math.Min(pw, ph), Math.Max(pw, ph));

        // the part of the paper the printer can print on
        Thickness margins = new(18);
        if (_ticket != null && _queue != null)
        {
            try
            {
                if (_paper.SelectedItem is PaperItem pi) _ticket.PageMediaSize = pi.Size;
                _ticket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
                _ticket.OutputColor = _gray.IsChecked == true ? (_caps?.OutputColorCapability.Contains(OutputColor.Grayscale) == true ? OutputColor.Grayscale : OutputColor.Monochrome) : OutputColor.Color;
                _ticket.Duplexing = _longEdge.IsChecked == true ? Duplexing.TwoSidedLongEdge : _shortEdge.IsChecked == true ? Duplexing.TwoSidedShortEdge : Duplexing.OneSided;
                _ticket.CopyCount = Copies;
                _ticket.Collation = _collate.IsChecked == true ? Collation.Collated : Collation.Uncollated;
                var area = _queue.GetPrintCapabilities(_ticket).PageImageableArea;
                if (area != null)
                {
                    double l = area.OriginWidth, t = area.OriginHeight, r = Math.Max(0, Math.Min(pw, ph) - area.OriginWidth - area.ExtentWidth), b = Math.Max(0, Math.Max(pw, ph) - area.OriginHeight - area.ExtentHeight);
                    double m = Math.Max(Math.Max(l, r), Math.Max(t, b));         // (turned paper: keep the widest margin on every side)
                    margins = landscape ? new Thickness(m) : new Thickness(l, t, r, b);
                }
            }
            catch (Exception e) when (e is PrintQueueException or PrintSystemException or ArgumentException or InvalidOperationException) { }
        }

        var sizing = _sizing.First(kv => kv.Value.IsChecked == true).Key;
        _layout = new PrintLayout(sheet, margins, pages, per, sizing, autoTurn: _turn[Turn.Auto].IsChecked == true || per > 1, i => _pdf.PageSize(i));
        _previewSheet = Math.Min(_previewSheet, _layout.Sheets - 1);
        int sheets = _layout.Sheets * Copies;
        _status.Text = $"{pages.Count} page{(pages.Count == 1 ? "" : "s")} on {_layout.Sheets} sheet{(_layout.Sheets == 1 ? "" : "s")}" +
                       (Copies > 1 ? $" × {Copies} copies" : "") + (_longEdge.IsChecked == true || _shortEdge.IsChecked == true ? $" ({(sheets + 1) / 2} sheets of paper, both sides)" : "");
        ShowSheet(_previewSheet);
    }

    private List<int>? Fail(string message)
    {
        _status.Text = message;
        _status.Foreground = (Brush)Application.Current.FindResource("ErrBrush");
        _print.IsEnabled = false;
        _layout = null;
        ShowSheet(0);
        return null;
    }

    /// <summary>"1-3, 5, 8-" (1-based, "8-" = 8 to the end) to page indexes. Null with a message when it can't be read.</summary>
    public static List<int>? ParseRange(string text, int count, out string? error)
    {
        error = null;
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) { error = "Type which pages, for example 1-3, 5"; return null; }
        foreach (string raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            int dash = part.IndexOf('-');
            int from, to;
            if (dash < 0) { if (!int.TryParse(part, out from)) { error = $"\"{part}\" isn't a page number."; return null; } to = from; }
            else
            {
                string a = part[..dash].Trim(), b = part[(dash + 1)..].Trim();
                if (!int.TryParse(a.Length == 0 ? "1" : a, out from) || !int.TryParse(b.Length == 0 ? count.ToString(CultureInfo.InvariantCulture) : b, out to)) { error = $"\"{part}\" isn't a range of pages."; return null; }
            }
            if (from < 1 || to < 1 || from > count || to > count) { error = $"This PDF has pages 1 to {count}."; return null; }
            if (from <= to) for (int i = from; i <= to; i++) result.Add(i - 1);
            else for (int i = from; i >= to; i--) result.Add(i - 1);
        }
        if (result.Count == 0) { error = "Type which pages, for example 1-3, 5"; return null; }
        return result;
    }

    // ---------- preview ----------
    private void ShowSheet(int index)
    {
        if (_layout == null || _layout.Sheets == 0) { _preview.Source = null; _sheetText.Text = ""; _sheet.Width = _sheet.Height = double.NaN; return; }
        _previewSheet = Math.Clamp(index, 0, _layout.Sheets - 1);
        _sheetText.Text = $"Sheet {_previewSheet + 1} of {_layout.Sheets}";
        var stage = (FrameworkElement)_sheet.Parent;
        double aw = Math.Max(100, stage.ActualWidth - 48), ah = Math.Max(100, stage.ActualHeight - 48);
        double k = Math.Min(aw / _layout.Sheet.Width, ah / _layout.Sheet.Height);
        _sheet.Width = Math.Round(_layout.Sheet.Width * k); _sheet.Height = Math.Round(_layout.Sheet.Height * k);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        bool gray = _gray.IsChecked == true;
        var visual = new DrawingVisual();
        using (var g = visual.RenderOpen())
        {
            g.PushTransform(new ScaleTransform(k, k));
            g.DrawRectangle(Brushes.White, null, new Rect(_layout.Sheet));
            g.PushClip(new RectangleGeometry(new Rect(_layout.Sheet)));
            foreach (var (page, box, turn) in _layout.Place(_previewSheet))
            {
                int w = Math.Max(1, (int)(box.Width * k * dpi)), h = Math.Max(1, (int)(box.Height * k * dpi));
                if (!_previewCache.TryGetValue((page, w, turn), out var picture))
                {
                    try { picture = _pdf.Render(page, w, h, turn, forScreen: false); } catch (Exception e) when (e is ObjectDisposedException or System.IO.IOException) { continue; }
                    if (_previewCache.Count > 60) _previewCache.Clear();
                    _previewCache[(page, w, turn)] = picture;
                }
                g.DrawImage(gray ? Gray(picture) : picture, box);
            }
            g.Pop();
            // where the printer can't print
            var dash = new Pen(new SolidColorBrush(Color.FromArgb(70, 90, 110, 160)), 1 / k) { DashStyle = DashStyles.Dash };
            g.DrawRectangle(null, dash, _layout.Printable);
            g.Pop();
        }
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)(_sheet.Width * dpi)), Math.Max(1, (int)(_sheet.Height * dpi)), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        _preview.Source = bmp;
    }

    private static BitmapSource Gray(BitmapSource b) { var g = new FormatConvertedBitmap(b, PixelFormats.Gray8, null, 0); g.Freeze(); return g; }

    // ---------- printing ----------
    private void Print()
    {
        if (_layout == null || _queue == null || _ticket == null) return;
        _lastSizing = _sizing.First(kv => kv.Value.IsChecked == true).Key;
        _lastGray = _gray.IsChecked == true; _lastHigh = _high.IsChecked == true;
        PrintTicket ticket;
        try { ticket = _queue.MergeAndValidatePrintTicket(_queue.UserPrintTicket ?? _ticket, _ticket).ValidatedPrintTicket; }
        catch (Exception e) when (e is PrintQueueException or PrintSystemException or ArgumentException or InvalidOperationException) { ticket = _ticket; }

        // copies the printer can't make itself are sent as more sheets
        int copies = Copies;
        bool printerCopies = ticket.CopyCount == copies && (_caps?.MaxCopyCount ?? 1) >= copies;
        var paginator = new SheetPaginator(_pdf, _layout, _high.IsChecked == true ? 300 : 200, _gray.IsChecked == true, printerCopies ? 1 : copies, _collate.IsChecked == true);
        if (!printerCopies) ticket.CopyCount = 1;

        try
        {
            _writer = PrintQueue.CreateXpsDocumentWriter(_queue);
            _writer.WritingProgressChanged += (_, e) => { if (e.WritingLevel == WritingProgressChangeLevel.FixedPageWritingProgress) _status.Text = $"Sending sheet {Math.Min(e.Number + 1, paginator.PageCount)} of {paginator.PageCount} to the printer…"; };
            _writer.WritingCompleted += (_, e) =>
            {
                _writer = null;
                Mouse.OverrideCursor = null;
                if (e.Cancelled) { _status.Text = "Printing cancelled."; Unlock(); return; }
                if (e.Error != null) { _status.Text = "Couldn't print: " + e.Error.Message; Unlock(); return; }
                DialogResult = true;
            };
            _writer.WritingCancelled += (_, _) => { _writer = null; Mouse.OverrideCursor = null; _status.Text = "Printing cancelled."; Unlock(); };
            foreach (var c in Lockable()) c.IsEnabled = false;
            _status.Text = "Preparing…";
            Mouse.OverrideCursor = Cursors.AppStarting;
            _writer.WriteAsync(paginator, ticket);
        }
        catch (Exception e) when (e is PrintQueueException or PrintSystemException or InvalidOperationException or ArgumentException or COMException or OutOfMemoryException)
        {
            _writer = null; Mouse.OverrideCursor = null;
            _status.Text = "Couldn't print: " + e.Message;
            Unlock();
        }
    }

    private IEnumerable<UIElement> Lockable() => new UIElement[] { _print, _printer, _props, _paper, _perSheet, _subset, _copies, _rangeBox };
    private void Unlock() { foreach (var c in Lockable()) c.IsEnabled = true; }

    /// <summary>Where each page goes on each sheet.</summary>
    public sealed class PrintLayout
    {
        public Size Sheet { get; }
        public Rect Printable { get; }
        private readonly List<int> _pages;
        private readonly int _per;
        private readonly Sizing _sizing;
        private readonly bool _autoTurn;
        private readonly Func<int, Size> _pageSize;
        private readonly int _cols, _rows;

        public PrintLayout(Size sheet, Thickness margins, List<int> pages, int perSheet, Sizing sizing, bool autoTurn, Func<int, Size> pageSize)
        {
            Sheet = sheet; _pages = pages; _per = perSheet; _sizing = sizing; _autoTurn = autoTurn; _pageSize = pageSize;
            Printable = new Rect(margins.Left, margins.Top, Math.Max(10, sheet.Width - margins.Left - margins.Right), Math.Max(10, sheet.Height - margins.Top - margins.Bottom));
            (int a, int b) = perSheet switch { 2 => (2, 1), 4 => (2, 2), 6 => (3, 2), 9 => (3, 3), 16 => (4, 4), _ => (1, 1) };
            (_cols, _rows) = sheet.Width >= sheet.Height ? (a, b) : (b, a);
        }

        public int Sheets => (_pages.Count + _per - 1) / _per;

        /// <summary>The pages of one sheet: page index, the box it is drawn in (1/96 inch) and how many quarter turns.</summary>
        public IEnumerable<(int Page, Rect Box, int Turn)> Place(int sheet)
        {
            const double gap = 10;
            double cw = (Printable.Width - gap * (_cols - 1)) / _cols, ch = (Printable.Height - gap * (_rows - 1)) / _rows;
            for (int slot = 0; slot < _per; slot++)
            {
                int n = sheet * _per + slot;
                if (n >= _pages.Count) yield break;
                int page = _pages[n];
                var cell = new Rect(Printable.X + (slot % _cols) * (cw + gap), Printable.Y + (slot / _cols) * (ch + gap), cw, ch);
                var s = _pageSize(page);
                double w = s.Width * 96 / 72, h = s.Height * 96 / 72;
                int turn = 0;
                if (_autoTurn && (w > h) != (cell.Width > cell.Height) && Math.Abs(w - h) > 1) { turn = 1; (w, h) = (h, w); }
                double fit = Math.Min(cell.Width / w, cell.Height / h);
                double k = _per > 1 || _sizing == Sizing.Fit ? fit : _sizing == Sizing.Shrink ? Math.Min(1, fit) : 1;
                w *= k; h *= k;
                // actual size is centred on the paper itself, everything else in its part of the printable area
                var centre = _per == 1 && _sizing != Sizing.Fit ? new Point(Sheet.Width / 2, Sheet.Height / 2) : new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2);
                yield return (page, new Rect(centre.X - w / 2, centre.Y - h / 2, w, h), turn);
            }
        }
    }

    /// <summary>Hands the sheets to the printer one at a time, each page drawn at the chosen dpi.</summary>
    private sealed class SheetPaginator : DocumentPaginator
    {
        private readonly PdfFile _pdf;
        private readonly PrintLayout _layout;
        private readonly double _dpi;
        private readonly bool _gray, _collate;
        private readonly int _copies;
        public SheetPaginator(PdfFile pdf, PrintLayout layout, double dpi, bool gray, int copies, bool collate) { _pdf = pdf; _layout = layout; _dpi = dpi; _gray = gray; _copies = copies; _collate = collate; }
        public override bool IsPageCountValid => true;
        public override int PageCount => _layout.Sheets * _copies;
        public override Size PageSize { get => _layout.Sheet; set { } }
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            int sheets = _layout.Sheets;
            int sheet = _collate ? pageNumber % sheets : pageNumber / _copies;
            var visual = new DrawingVisual();
            using (var g = visual.RenderOpen())
            {
                g.PushClip(new RectangleGeometry(new Rect(_layout.Sheet)));
                foreach (var (page, box, turn) in _layout.Place(sheet))
                {
                    double big = box.Width / 96 * _dpi * (box.Height / 96 * _dpi) / 60_000_000;     // (keep one page under ~60 megapixels)
                    double dpi = big > 1 ? _dpi / Math.Sqrt(big) : _dpi;
                    BitmapSource picture = _pdf.Render(page, Math.Max(1, (int)(box.Width / 96 * dpi)), Math.Max(1, (int)(box.Height / 96 * dpi)), turn, forScreen: false);
                    if (_gray) picture = Gray(picture);
                    g.DrawImage(picture, box);
                }
                g.Pop();
            }
            return new DocumentPage(visual, _layout.Sheet, new Rect(_layout.Sheet), new Rect(_layout.Sheet));
        }
    }
}
