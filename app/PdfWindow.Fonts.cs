using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Choosing any font installed in Windows for the text (next to the Sans / Serif / Mono choices): the chosen font is put into the PDF.</summary>
public sealed partial class PdfWindow
{
    private string? _fontName;                       // the font chosen by name for new text (null = Sans / Serif / Mono)
    private bool _syncingFont;                       // (the bar is only showing the font of what is selected)
    private TextBlock _fontLabel = null!;
    private UIElement? _fontPickerButton;              // ("More fonts": hidden for form fields, which only have Sans / Serif / Mono)
    private Popup? _fontPopup;
    private ListBox _fontList = null!;
    private TextBox _fontFilter = null!;

    /// <summary>A small row for a font in the list: its name, written in that font.</summary>
    public sealed record FontEntry(string Name, FontFamily Family);

    private void ShowFont(PdfFontKind kind, string? name)
    {
        if (name == null) { _fontButtons[kind].IsChecked = true; _fontLabel.Text = "More fonts"; }
        else { foreach (var chip in _fontButtons.Values) chip.IsChecked = false; _fontLabel.Text = name; }
    }

    private UIElement FontPickerButton()
    {
        _fontLabel = new TextBlock { Text = "More fonts", Foreground = Brushes.White, FontSize = 12, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var arrow = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, Foreground = Brushes.White, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(_fontLabel); content.Children.Add(arrow);
        var b = new Button { Content = content, Template = BarButtonTemplate(), Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0), Focusable = false, ToolTip = "Choose any font installed on this PC (it is put into the PDF, so it looks the same everywhere)" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfFontMore");
        b.Click += (_, _) => OpenFontList(b);
        _fontPickerButton = b;
        return b;
    }

    private void OpenFontList(UIElement anchor)
    {
        if (_fontPopup == null) BuildFontList();
        _fontFilter.Text = "";
        FillFontList("");
        _fontPopup!.PlacementTarget = anchor;
        _fontPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => { _fontFilter.Focus(); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BuildFontList()
    {
        _fontFilter = new TextBox { Height = 28, Padding = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x3D)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x4B, 0x5C)), Margin = new Thickness(0, 0, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_fontFilter, "PdfFontFilter");
        _fontFilter.TextChanged += (_, _) => FillFontList(_fontFilter.Text);
        _fontList = new ListBox { Width = 270, Height = 320, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_fontList, "PdfFontList");
        ScrollViewer.SetHorizontalScrollBarVisibility(_fontList, ScrollBarVisibility.Disabled);
        _fontList.ItemTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding Name}' FontFamily='{Binding Family}' FontSize='15' Foreground='White' Padding='4,2' TextTrimming='CharacterEllipsis' /></DataTemplate>");
        _fontList.ItemContainerStyle = (Style)XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ListBoxItem'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='4' Padding='4,1' Background='Transparent'><ContentPresenter /></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#33FFFFFF' /></Trigger>" +
            "<Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='#5B8DEF' /></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        _fontList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var d = e.OriginalSource as DependencyObject;
            while (d != null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
            if (d is ListBoxItem { DataContext: FontEntry entry })
            {
                _fontPopup!.IsOpen = false;
                SetFont(null, null, entry.Name);
                ShowFont(_font, entry.Name);
            }
        };
        _fontFilter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _fontPopup!.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Enter && _fontList.Items.Count > 0)
            {
                var entry = (FontEntry)(_fontList.SelectedItem ?? _fontList.Items[0]);
                _fontPopup!.IsOpen = false;
                SetFont(null, null, entry.Name);
                ShowFont(_font, entry.Name);
                e.Handled = true;
            }
            else if (e.Key is Key.Down or Key.Up && _fontList.Items.Count > 0)
            {
                int i = Math.Clamp(_fontList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _fontList.Items.Count - 1);
                _fontList.SelectedIndex = i; _fontList.ScrollIntoView(_fontList.SelectedItem);
                e.Handled = true;
            }
        };
        var panel = new DockPanel();
        DockPanel.SetDock(_fontFilter, Dock.Top);
        panel.Children.Add(_fontFilter); panel.Children.Add(_fontList);
        var card = new Border
        {
            Child = panel, Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x29)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(0, 4, 8, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4 },
        };
        _fontPopup = new Popup { Child = card, StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade };
    }

    private void FillFontList(string filter)
    {
        filter = filter.Trim();
        var names = PdfFonts.Names().Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        _fontList.ItemsSource = names.Select(n => new FontEntry(n, new FontFamily(n))).ToList();
        var current = _fontName;
        if (current != null)
        {
            var hit = ((List<FontEntry>)_fontList.ItemsSource).FirstOrDefault(f => string.Equals(f.Name, current, StringComparison.OrdinalIgnoreCase));
            if (hit != null) { _fontList.SelectedItem = hit; _fontList.ScrollIntoView(hit); }
        }
    }
}
