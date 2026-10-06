using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Field options": what a form field needs besides its look. Opens when a field is double-clicked (Select), with Enter, from the right-click menu, and right after a drop-down list is placed
/// (to type its choices). Name, must be filled in; text box: several lines, longest text, starting text; drop-down: the choices; check box: ticked at the start; option: its choice name.
/// </summary>
public sealed partial class PdfWindow
{
    private void EditFieldOptions(FieldItem field, bool firstTime = false)
    {
        var dlg = new Window
        {
            Title = field.Kind switch { PdfNewFieldKind.Text => "Text box options", PdfNewFieldKind.Dropdown => "Drop-down list options", PdfNewFieldKind.CheckBox => "Check box options", PdfNewFieldKind.Radio => "Option options", _ => "Signature box options" },
            Width = 430, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("BgBrush"), Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        WindowTheme.DarkTitleBar(dlg);
        var root = new StackPanel { Margin = new Thickness(20) };
        TextBlock Label(string text, double top = 12) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };
        TextBlock Hint(string text) => new() { Text = text, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };

        bool radio = field.Kind == PdfNewFieldKind.Radio;
        var nameBox = new TextBox { Text = field.Name };
        System.Windows.Automation.AutomationProperties.SetAutomationId(nameBox, "PdfFieldName");
        root.Children.Add(Label(radio ? "Group name" : "Field name", 0));
        root.Children.Add(nameBox);
        root.Children.Add(Hint(radio ? "Option buttons with the same group name belong together: only one of them can be chosen." : "The name other programs see when the filled-in form is read."));

        var valueBox = new TextBox { Text = field.Value };
        if (radio) { root.Children.Add(Label("This choice is called")); root.Children.Add(valueBox); }

        var multiline = new CheckBox { Content = "Several lines (Enter makes a new line)", IsChecked = field.Multiline, Margin = new Thickness(0, 12, 0, 0) };
        var maxBox = new TextBox { Text = field.MaxLength > 0 ? field.MaxLength.ToString() : "", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        var startBox = new TextBox { Text = field.DefaultText };
        System.Windows.Automation.AutomationProperties.SetAutomationId(multiline, "PdfFieldMultiline");
        System.Windows.Automation.AutomationProperties.SetAutomationId(maxBox, "PdfFieldMax");
        if (field.Kind == PdfNewFieldKind.Text)
        {
            root.Children.Add(multiline);
            root.Children.Add(Label("Longest text (number of letters, empty = no limit)"));
            root.Children.Add(maxBox);
            root.Children.Add(Label("Text to start with (empty = nothing)"));
            root.Children.Add(startBox);
        }

        var choicesBox = new TextBox { Text = string.Join("\n", field.Choices), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        System.Windows.Automation.AutomationProperties.SetAutomationId(choicesBox, "PdfFieldChoices");
        if (field.Kind == PdfNewFieldKind.Dropdown)
        {
            root.Children.Add(Label("Choices (one on each line)"));
            root.Children.Add(choicesBox);
            root.Children.Add(Label("Start with"));
            root.Children.Add(startBox);
            root.Children.Add(Hint("Empty, or one of the choices exactly as written above."));
        }

        var ticked = new CheckBox { Content = "Ticked at the start", IsChecked = field.Ticked, Margin = new Thickness(0, 12, 0, 0) };
        if (field.Kind == PdfNewFieldKind.CheckBox) root.Children.Add(ticked);

        var required = new CheckBox { Content = "Must be filled in (other readers can mark it as required)", IsChecked = field.Required, Margin = new Thickness(0, 14, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(required, "PdfFieldRequired");
        root.Children.Add(required);

        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = field.Kind != PdfNewFieldKind.Dropdown, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "PdfFieldOk");
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; dlg.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.Loaded += (_, _) => { if (field.Kind == PdfNewFieldKind.Dropdown && firstTime) { choicesBox.Focus(); choicesBox.SelectAll(); } else { nameBox.Focus(); nameBox.SelectAll(); } };
        dlg.ShowDialog();
        if (!accepted)
        {
            if (firstTime && field.Kind == PdfNewFieldKind.Dropdown && field.Choices.Count == 0) { field.Choices = new List<string> { "Option 1", "Option 2", "Option 3" }; RenderItems(field.Page); }
            return;
        }
        if (!firstTime) Snapshot();
        string name = nameBox.Text.Trim();
        if (name.Length > 0) field.Name = name;
        if (radio && valueBox.Text.Trim().Length > 0) field.Value = valueBox.Text.Trim();
        field.Required = required.IsChecked == true;
        if (field.Kind == PdfNewFieldKind.Text)
        {
            field.Multiline = multiline.IsChecked == true;
            field.MaxLength = int.TryParse(maxBox.Text.Trim(), out int max) && max > 0 ? Math.Min(max, 100000) : 0;
            field.DefaultText = startBox.Text;
        }
        if (field.Kind == PdfNewFieldKind.Dropdown)
        {
            field.Choices = choicesBox.Text.Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
            if (field.Choices.Count == 0) field.Choices = new List<string> { "Option 1", "Option 2", "Option 3" };
            field.DefaultText = field.Choices.Contains(startBox.Text.Trim()) ? startBox.Text.Trim() : "";
        }
        if (field.Kind == PdfNewFieldKind.CheckBox) field.Ticked = ticked.IsChecked == true;
        RenderItems(field.Page);
        UpdateProperties();
    }
}
