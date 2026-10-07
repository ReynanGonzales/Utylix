using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Find personal details" (Tools > PRIVACY): looks through the whole PDF for e-mail addresses, phone numbers, card numbers, ID numbers, dates, web addresses and words of your own, lists every match
/// with its page, and puts black Redact boxes on the ones you leave ticked. Nothing is removed until the file is saved (Save asks first, and checks the result).
/// </summary>
internal sealed class PdfPersonalDialog : Window
{
    private sealed record Row(PersonalHit Hit, CheckBox Box, ListBoxItem Item);

    private readonly PdfFile _pdf;
    private readonly Action<int> _show;
    private readonly List<(PdfPersonalKind Kind, CheckBox Box)> _kinds = new();
    private readonly TextBox _custom = new() { AcceptsReturn = true, Height = 62, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 0) };
    private readonly ListBox _list = new() { Height = 250, Background = Brushes.Transparent, BorderThickness = new Thickness(1), Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MinHeight = 20 };
    private readonly Button _find = new() { Content = "Look through the PDF", MinWidth = 170, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _redact = new() { Content = "Black out the ticked ones", MinWidth = 180, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _showBtn = new() { Content = "Show on the page", Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _all = new() { Content = "Tick all", Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _none = new() { Content = "Untick all", Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly List<Row> _rows = new();
    private CancellationTokenSource? _cts;

    /// <summary>What the person chose to black out (empty = nothing).</summary>
    public List<PersonalHit> Chosen { get; } = new();

    public PdfPersonalDialog(Window owner, PdfFile pdf, Action<int> show)
    {
        _pdf = pdf; _show = show; Owner = owner;
        Title = "Find personal details"; Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush"); Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        var plain = (Style)Application.Current.FindResource("DialogButton");
        var primary = (Style)Application.Current.FindResource("DialogPrimary");
        _find.Style = primary; _redact.Style = primary;
        foreach (var b in new[] { _showBtn, _all, _none }) b.Style = plain;
        _custom.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _custom.Background = new SolidColorBrush(Color.FromArgb(34, 128, 128, 128));
        _custom.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
        _list.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
        _list.Foreground = Foreground;
        _list.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Setter Property='Foreground' Value='{DynamicResource TextBrush}' /><Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ListBoxItem'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='6' Margin='2,1' Padding='6,3' Background='Transparent'><ContentPresenter /></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#22808080' /></Trigger>" +
            "<Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='#55808080' /></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        AutomationProperties.SetAutomationId(_list, "PdfPersonalList"); AutomationProperties.SetAutomationId(_status, "PdfPersonalStatus");
        AutomationProperties.SetAutomationId(_find, "PdfPersonalFind"); AutomationProperties.SetAutomationId(_redact, "PdfPersonalRedact"); AutomationProperties.SetAutomationId(_custom, "PdfPersonalCustomText");
        AutomationProperties.SetAutomationId(_all, "PdfPersonalAll"); AutomationProperties.SetAutomationId(_none, "PdfPersonalNone"); AutomationProperties.SetAutomationId(_showBtn, "PdfPersonalShow");

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Look through every page for what is usually private, then black out what you tick. Nothing is removed until you save (and the saved file is checked).", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        root.Children.Add(new TextBlock { Text = "What to look for", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        var kinds = new WrapPanel();
        foreach (var (kind, label) in new[] { (PdfPersonalKind.Email, "E-mail addresses"), (PdfPersonalKind.Phone, "Phone numbers"), (PdfPersonalKind.Card, "Card numbers"), (PdfPersonalKind.IdNumber, "ID numbers"), (PdfPersonalKind.Date, "Dates"), (PdfPersonalKind.Web, "Web addresses"), (PdfPersonalKind.Custom, "My own words") })
        {
            var box = new CheckBox { Content = label, IsChecked = kind != PdfPersonalKind.Date && kind != PdfPersonalKind.Web, Margin = new Thickness(0, 0, 18, 4) };
            AutomationProperties.SetAutomationId(box, "PdfPersonal" + kind);
            _kinds.Add((kind, box)); kinds.Children.Add(box);
        }
        root.Children.Add(kinds);
        root.Children.Add(new TextBlock { Text = "My own words: one per line (a name, an address, an account number). Start a line with re: for a pattern.", Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        root.Children.Add(_custom);
        root.Children.Add(_find);
        root.Children.Add(_status);
        root.Children.Add(_list);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(_all); actions.Children.Add(_none); actions.Children.Add(_showBtn);
        root.Children.Add(actions);
        var close = new Button { Content = "Close", Style = plain, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_redact); buttons.Children.Add(close);
        root.Children.Add(buttons);
        Content = root;

        _find.Click += async (_, _) => await FindAsync();
        _all.Click += (_, _) => { foreach (var r in _rows) r.Box.IsChecked = true; UpdateButtons(); };
        _none.Click += (_, _) => { foreach (var r in _rows) r.Box.IsChecked = false; UpdateButtons(); };
        _showBtn.Click += (_, _) => ShowChosen();
        _list.SelectionChanged += (_, _) => { _showBtn.IsEnabled = _list.SelectedIndex >= 0; ShowChosen(); };
        _redact.Click += (_, _) => { foreach (var r in _rows.Where(r => r.Box.IsChecked == true)) Chosen.Add(r.Hit); DialogResult = Chosen.Count > 0; };
        Closing += (_, _) => _cts?.Cancel();
    }

    private void ShowChosen()
    {
        if (_list.SelectedItem is ListBoxItem item && _rows.FirstOrDefault(r => r.Item == item) is { } row) _show(row.Hit.Page);
    }

    private void UpdateButtons()
    {
        int ticked = _rows.Count(r => r.Box.IsChecked == true);
        _redact.IsEnabled = ticked > 0;
        _redact.Content = ticked > 0 ? $"Black out the {ticked} ticked" : "Black out the ticked ones";
        _all.IsEnabled = _none.IsEnabled = _rows.Count > 0;
    }

    private async Task FindAsync()
    {
        var kinds = _kinds.Where(k => k.Box.IsChecked == true).Aggregate(PdfPersonalKind.None, (all, k) => all | k.Kind);
        if (kinds == PdfPersonalKind.None) { _status.Text = "Tick what to look for."; return; }
        var custom = _custom.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if ((kinds & PdfPersonalKind.Custom) != 0 && custom.Count == 0) kinds &= ~PdfPersonalKind.Custom;
        if (kinds == PdfPersonalKind.None) { _status.Text = "Type the words to look for (one per line), or tick something else."; return; }
        _find.IsEnabled = false; _list.Items.Clear(); _rows.Clear(); UpdateButtons();
        var cts = _cts = new CancellationTokenSource();
        int pages = _pdf.PageCount;
        var progress = new Progress<int>(p => _status.Text = $"Looking at page {p + 1} of {pages}…");
        try
        {
            var hits = await Task.Run(() => PdfPersonalData.Scan(_pdf, kinds, custom, progress, cts.Token));
            foreach (var hit in hits.Take(3000))
            {
                var box = new CheckBox { IsChecked = true, Content = $"Page {hit.Page + 1}  ·  {PdfPersonalData.Describe(hit.Kind)}  ·  {Shorten(hit.Text)}", ToolTip = hit.Text };
                box.Checked += (_, _) => UpdateButtons(); box.Unchecked += (_, _) => UpdateButtons();
                var item = new ListBoxItem { Content = box };
                _list.Items.Add(item);
                _rows.Add(new Row(hit, box, item));
            }
            _status.Text = hits.Count == 0 ? "Nothing like that found in the text of this PDF. (A scanned page has no text: use Tools > OCR first.)"
                : $"{hits.Count} found" + (hits.Count > 3000 ? " (the first 3000 are listed)" : "") + ". Leave ticked what must go, untick the rest.";
            UpdateButtons();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException or InvalidOperationException) { _status.Text = "Couldn't look through the PDF: " + e.Message; }
        finally { _find.IsEnabled = true; }
    }

    private static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
