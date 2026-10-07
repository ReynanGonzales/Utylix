using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Filling in PDF forms: click a field to type in it (Tab goes to the next one), tick boxes and round options, pick from lists; a
/// signature field opens "Your signature". The values go into the PDF itself when it is saved.
/// </summary>
public sealed partial class PdfWindow
{
    private readonly Dictionary<int, List<PdfField>> _fields = new();
    private FrameworkElement? _fieldEditor;
    private PdfField? _fieldEditing;
    private Border _formBar = null!;
    private TextBlock _formBarText = null!;

    private List<PdfField> Fields(int page)
    {
        if (_pdf == null || !_pdf.HasForm) return new List<PdfField>();
        if (_fields.TryGetValue(page, out var f)) return f;
        try { f = _pdf.GetFields(page); }
        catch (Exception e) when (e is ObjectDisposedException or System.IO.IOException) { f = new List<PdfField>(); }
        _fields[page] = f;
        return f;
    }

    private PdfField? FieldAt(int page, Point p) => Fields(page).LastOrDefault(f => f.Box.Contains(p) && f.Kind != PdfFieldKind.Button);

    /// <summary>The bar over the pages of a PDF with fields to fill in.</summary>
    private UIElement FormBar()
    {
        _formBarText = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 0) };
        var save = new Button { Content = "Save", Style = (Style)Application.Current.FindResource("DialogPrimary"), Height = 28, MinWidth = 70, Padding = new Thickness(12, 0, 12, 0), Focusable = false };
        System.Windows.Automation.AutomationProperties.SetAutomationId(save, "PdfFormSave");
        save.Click += async (_, _) => await SaveEditsAsync(saveAs: false);
        var close = SmallBar("", "Hide this bar", () => _formBar.Visibility = Visibility.Collapsed);
        var icon = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Brushes.White, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        // (remembered: a marker file in the data folder)
        string marker = System.IO.Path.Combine(App.DataDir, "pdf-field-highlight");
        try { PdfFile.HighlightFields = System.IO.File.Exists(marker); } catch (System.IO.IOException) { }
        var tint = new CheckBox { Content = new TextBlock { Text = "Tint the fields", Foreground = Brushes.White }, IsChecked = PdfFile.HighlightFields, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), Focusable = false, ToolTip = "Colour every fillable field lightly, so they are easy to find (only on screen: it is not printed or saved)" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(tint, "PdfFormTint");
        RoutedEventHandler tintChanged = (_, _) =>
        {
            PdfFile.HighlightFields = tint.IsChecked == true;
            try { if (PdfFile.HighlightFields) System.IO.File.WriteAllText(marker, "1"); else System.IO.File.Delete(marker); } catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
            _pdf?.ApplyHighlight();
            RedrawPages();
        };
        tint.Checked += tintChanged; tint.Unchecked += tintChanged;
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right); row.Children.Add(close);
        DockPanel.SetDock(save, Dock.Right); row.Children.Add(save);
        DockPanel.SetDock(tint, Dock.Right); row.Children.Add(tint);
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        row.Children.Add(_formBarText);
        _formBar = new Border { Child = row, Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x4A, 0x8A)), Padding = new Thickness(0, 6, 6, 6), Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_formBarText, "PdfFormBarText");
        return _formBar;
    }

    /// <summary>A PDF was opened: does it have fields?</summary>
    private void OnFormLoaded()
    {
        CloseFieldEditor(commit: false);
        _fields.Clear(); _ownFields.Clear(); _formHasCalc = null;
        if (_pdf?.HasForm == true)
        {
            _formBarText.Text = "This PDF has fields to fill in. Click a field to type in it; Tab goes to the next one.";
            _formBar.Visibility = Visibility.Visible;
        }
        else if (_pdf?.IsXfaForm == true)
        {
            _formBarText.Text = "This is an XFA form (made with Adobe LiveCycle). Utylix can't fill its fields; use Edit → Text to type on it instead.";
            _formBar.Visibility = Visibility.Visible;
        }
        else _formBar.Visibility = Visibility.Collapsed;
    }

    // ---------- the mouse on a field ----------
    /// <summary>A click on a field (not while editing): fill it. True when it was a field.</summary>
    private bool FieldDown(PageView pv, MouseButtonEventArgs e)
    {
        if (_pdf == null || !_pdf.HasForm) return false;
        var p = e.GetPosition(pv.Overlay);
        if (_fieldEditor != null && IsInside((DependencyObject)e.OriginalSource, _fieldEditor)) return true;
        CloseFieldEditor(commit: true);
        var field = FieldAt(pv.Index, p);
        if (field == null) return false;
        e.Handled = true;
        OpenField(field);
        return true;
    }

    private string? FieldTip(int page, Point p, out Cursor cursor)
    {
        cursor = Cursors.Arrow;
        var f = FieldAt(page, p);
        if (f == null) return null;
        cursor = f.ReadOnly ? Cursors.No : f.Kind == PdfFieldKind.Text ? Cursors.IBeam : Cursors.Hand;
        string what = f.Hint.Length > 0 ? f.Hint : f.Name;
        return (what.Length > 0 ? what : f.Kind.ToString()) + (f.Required ? " (required)" : "") + (f.ReadOnly ? " (can't be changed)" : "");
    }

    private void OpenField(PdfField field)
    {
        if (field.ReadOnly) { Toast("This field can't be changed"); return; }
        if (field.Calc != null) { Toast("This box works itself out from other boxes"); return; }
        switch (field.Kind)
        {
            case PdfFieldKind.CheckBox or PdfFieldKind.Radio:
                Change(field, () => _pdf!.ClickField(field));
                return;
            case PdfFieldKind.Signature:
                SignField(field);
                return;
            case PdfFieldKind.Combo or PdfFieldKind.List:
                OpenChoice(field);
                return;
            case PdfFieldKind.Text:
                OpenTextField(field);
                return;
        }
    }

    /// <summary>A change to the form: done, marked as unsaved, the page drawn again.</summary>
    private void Change(PdfField field, Action action)
    {
        try { action(); }
        catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException) { Toast("Couldn't change it: " + e.Message); return; }
        _fields.Remove(field.Page);
        _dirty = true;
        UpdateTitle();
        RefreshPage(field.Page);
        RecalculateForm();                                  // (boxes that are worked out from this one)
    }

    /// <summary>Draws one page again (its picture and its small picture), keeping the old picture until the new one is ready.</summary>
    private void RefreshPage(int page)
    {
        if (page < 0 || page >= _pages.Count || _pdf == null) return;
        _pages[page].RenderedWidth = 0; _pages[page].WantedWidth = 0;
        if (page < _thumbs.Count) { var t = _thumbs[page]; _thumbs[page] = new PdfThumb { Pdf = _pdf, Index = page, Rotation = t.Rotation, Width = t.Width, Height = t.Height }; }
        RenderVisible();
    }

    private double FieldFontSize(PdfField f) => f.FontSize > 0 ? f.FontSize : f.Multiline ? 11 : Math.Clamp(f.Box.Height * 0.62, 6, 18);

    private void OpenTextField(PdfField field)
    {
        var box = new TextBox
        {
            Text = field.Value.Replace("\r\n", "\n").Replace('\r', '\n'), AcceptsReturn = field.Multiline, TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Width = field.Box.Width, Height = field.Box.Height, FontFamily = new FontFamily("Arial"), FontSize = FieldFontSize(field),
            Foreground = Brushes.Black, Background = Brushes.White, CaretBrush = Brushes.Black, Padding = new Thickness(1.5, 0, 1.5, 0),
            VerticalContentAlignment = field.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'>" +
                "<Border Background='{TemplateBinding Background}' BorderBrush='#2F6BEA' BorderThickness='1.2'><ScrollViewer x:Name='PART_ContentHost' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='{TemplateBinding Padding}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden' /></Border></ControlTemplate>"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "PdfFieldBox");
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab) { var f = field; CloseFieldEditor(commit: true); NextField(f, (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.Enter && !field.Multiline) { CloseFieldEditor(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFieldEditor(commit: false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (_fieldEditor == box && !box.IsKeyboardFocusWithin) CloseFieldEditor(commit: true); });
        ShowFieldEditor(field, box);
        Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OpenChoice(PdfField field)
    {
        var combo = new ComboBox
        {
            ItemsSource = field.Options, SelectedIndex = field.Selected, IsEditable = field.EditableCombo, Width = Math.Max(field.Box.Width, 60), Height = field.Box.Height,
            FontSize = FieldFontSize(field), Background = Brushes.White, Foreground = Brushes.Black, Style = null,
        };
        if (field.EditableCombo) combo.Text = field.Value;
        // the list in paper colours: dark text on white, the chosen / pointed entry blue with white text. (Without this, Utylix's own
        // list style - light text for its dark windows - was used on this white list: unreadable.)
        combo.ItemContainerStyle = (Style)XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ComboBoxItem'>" +
            "<Setter Property='Foreground' Value='#1C2333' /><Setter Property='Padding' Value='8,5' /><Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ComboBoxItem'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='White' Padding='{TemplateBinding Padding}'><ContentPresenter /></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='#DCE7FD' /></Trigger>" +
            "<Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='#2F6BEA' /><Setter Property='Foreground' Value='White' /></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        combo.Resources[SystemColors.WindowBrushKey] = Brushes.White;                        // (the list's own background)
        // the words themselves take the colour of their entry (Utylix's rule for all text - light in the dark theme - came first otherwise)
        combo.ItemTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding}' Foreground='{Binding Foreground, RelativeSource={RelativeSource AncestorType=Control}}' /></DataTemplate>");
        System.Windows.Automation.AutomationProperties.SetAutomationId(combo, "PdfFieldChoice");
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            CloseFieldEditor(commit: false);
            if (!commit) return;
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex != field.Selected) Change(field, () => _pdf!.SelectFieldOption(field, combo.SelectedIndex));
            else if (field.EditableCombo && combo.SelectedIndex < 0 && combo.Text != field.Value) Change(field, () => _pdf!.SetFieldText(field, combo.Text));
        }
        combo.SelectionChanged += (_, _) => { if (!combo.IsDropDownOpen || !field.EditableCombo) Dispatcher.BeginInvoke(() => Finish(true)); };
        combo.DropDownClosed += (_, _) => Dispatcher.BeginInvoke(() => { if (!field.EditableCombo) Finish(true); });
        combo.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Finish(false); e.Handled = true; } else if (e.Key is Key.Enter or Key.Tab) { Finish(true); e.Handled = true; } };
        ShowFieldEditor(field, combo);
        // the list opens only once the mouse button is up: opened under a pressed button, letting go picked the entry under the pointer
        var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        wait.Tick += (_, _) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed) return;
            wait.Stop();
            if (_fieldEditor != combo) return;
            combo.Focus();
            if (!field.EditableCombo) combo.IsDropDownOpen = true;
        };
        wait.Start();
    }

    private void ShowFieldEditor(PdfField field, FrameworkElement editor)
    {
        CloseFieldEditor(commit: true);
        _fieldEditor = editor; _fieldEditing = field;
        Canvas.SetLeft(editor, field.Box.X); Canvas.SetTop(editor, field.Box.Y);
        _pages[field.Page].Overlay.Children.Add(editor);
    }

    private void CloseFieldEditor(bool commit)
    {
        var editor = _fieldEditor; var field = _fieldEditing;
        if (editor == null || field == null) return;
        _fieldEditor = null; _fieldEditing = null;
        if (editor.Parent is Canvas c) c.Children.Remove(editor);
        if (commit && editor is TextBox t)
        {
            string text = t.Text.Replace("\r\n", "\n");
            if (text != field.Value.Replace("\r\n", "\n").Replace('\r', '\n') && AcceptFieldText(field, ref text)) Change(field, () => _pdf!.SetFieldText(field, text));
        }
        _scroll.Focus();
    }

    /// <summary>Tab: the next field that can be filled (on this page, then the next pages).</summary>
    private void NextField(PdfField from, int dir)
    {
        if (_pdf == null) return;
        for (int page = from.Page, n = 0; n < _pdf.PageCount && page >= 0 && page < _pdf.PageCount; page += dir, n++)
        {
            var list = Fields(page).Where(f => !f.ReadOnly && f.Kind is PdfFieldKind.Text or PdfFieldKind.Combo or PdfFieldKind.List).ToList();
            var candidates = page != from.Page ? list : dir > 0 ? list.Where(f => f.AnnotIndex > from.AnnotIndex).ToList() : list.Where(f => f.AnnotIndex < from.AnnotIndex).ToList();
            var next = dir > 0 ? candidates.FirstOrDefault() : candidates.LastOrDefault();
            if (next == null) continue;
            if (Text(page) is { } && next.Page != _current) ScrollToBox(next.Page, next.Box);
            OpenField(next);
            return;
        }
    }

    /// <summary>A signature field: sign it (your saved signature, placed in the field).</summary>
    private void SignField(PdfField field)
    {
        if (!_editing) { EnterEditing(); if (!_editing) return; }
        AddSignature(field.Page, field.Box);
    }
}
