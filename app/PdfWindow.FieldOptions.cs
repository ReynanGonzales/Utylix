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
        var formatKind = new ComboBox(); var decimalsBox = new TextBox { Width = 50 }; var commaBox = new CheckBox { Content = "Decimal comma (1234,50)" };
        var datePattern = new ComboBox { Margin = new Thickness(0, 6, 0, 0) }; var timePattern = new ComboBox { Margin = new Thickness(0, 6, 0, 0) };
        var numberRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var calcOp = new ComboBox(); var calcNames = new TextBox();
        System.Windows.Automation.AutomationProperties.SetAutomationId(formatKind, "PdfFieldFormat");
        System.Windows.Automation.AutomationProperties.SetAutomationId(decimalsBox, "PdfFieldDecimals");
        System.Windows.Automation.AutomationProperties.SetAutomationId(commaBox, "PdfFieldComma");
        System.Windows.Automation.AutomationProperties.SetAutomationId(datePattern, "PdfFieldDatePattern");
        System.Windows.Automation.AutomationProperties.SetAutomationId(timePattern, "PdfFieldTimePattern");
        System.Windows.Automation.AutomationProperties.SetAutomationId(calcOp, "PdfFieldCalcOp");
        System.Windows.Automation.AutomationProperties.SetAutomationId(calcNames, "PdfFieldCalcNames");
        System.Windows.Automation.AutomationProperties.SetAutomationId(multiline, "PdfFieldMultiline");
        System.Windows.Automation.AutomationProperties.SetAutomationId(maxBox, "PdfFieldMax");
        if (field.Kind == PdfNewFieldKind.Text)
        {
            root.Children.Add(multiline);
            root.Children.Add(Label("Longest text (number of letters, empty = no limit)"));
            root.Children.Add(maxBox);
            root.Children.Add(Label("Text to start with (empty = nothing)"));
            root.Children.Add(startBox);

            // how it shows what is typed (a number, a date, a time) and whether it is worked out from other boxes
            formatKind.ItemsSource = new[] { "Plain text", "Number", "Date", "Time" };
            formatKind.SelectedIndex = field.Format?.Kind switch { FieldFormatKind.Number => 1, FieldFormatKind.Date => 2, FieldFormatKind.Time => 3, _ => 0 };
            decimalsBox.Text = (field.Format is { Kind: FieldFormatKind.Number } nf ? nf.Decimals : 2).ToString();
            commaBox.IsChecked = field.Format is { Kind: FieldFormatKind.Number, CommaDecimal: true };
            datePattern.ItemsSource = PdfFormLogic.DatePatterns; datePattern.SelectedItem = field.Format is { Kind: FieldFormatKind.Date } df && PdfFormLogic.DatePatterns.Contains(df.Pattern) ? df.Pattern : PdfFormLogic.DatePatterns[0];
            timePattern.ItemsSource = PdfFormLogic.TimePatterns; timePattern.SelectedItem = field.Format is { Kind: FieldFormatKind.Time } tf && PdfFormLogic.TimePatterns.Contains(tf.Pattern) ? tf.Pattern : PdfFormLogic.TimePatterns[0];
            root.Children.Add(Label("Shows what is typed as"));
            root.Children.Add(formatKind);
            numberRow.Children.Add(new TextBlock { Text = "Decimal places", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            numberRow.Children.Add(decimalsBox);
            commaBox.Margin = new Thickness(14, 0, 0, 0); commaBox.VerticalAlignment = VerticalAlignment.Center;
            numberRow.Children.Add(commaBox);
            root.Children.Add(numberRow); root.Children.Add(datePattern); root.Children.Add(timePattern);
            void ShowFormat()
            {
                numberRow.Visibility = formatKind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                datePattern.Visibility = formatKind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
                timePattern.Visibility = formatKind.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            }
            formatKind.SelectionChanged += (_, _) => ShowFormat();
            ShowFormat();
            root.Children.Add(Hint("Something typed that doesn't fit (letters in a number, say) is not accepted. No thousands separator, on purpose: readers read the box again to calculate."));

            calcOp.ItemsSource = new[] { "Typed by hand", "The sum of", "The product of", "The average of", "The smallest of", "The largest of" };
            calcOp.SelectedIndex = field.Calc == null ? 0 : (int)field.Calc.Op + 1;
            calcNames.Text = field.Calc == null ? "" : string.Join("; ", field.Calc.Fields);
            root.Children.Add(Label("Worked out"));
            root.Children.Add(calcOp);
            calcNames.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(calcNames);
            void ShowCalc() => calcNames.Visibility = calcOp.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
            calcOp.SelectionChanged += (_, _) => ShowCalc();
            ShowCalc();
            root.Children.Add(Hint("The names of the other boxes, separated by ; (for example  Price 1; Price 2). It works itself out whenever one of them changes."));
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
        dlg.MaxHeight = SystemParameters.WorkArea.Height - 40;
        dlg.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
            field.Format = formatKind.SelectedIndex switch
            {
                1 => new FieldFormat(FieldFormatKind.Number, int.TryParse(decimalsBox.Text.Trim(), out int dec) ? Math.Clamp(dec, 0, 6) : 2, commaBox.IsChecked == true),
                2 => new FieldFormat(FieldFormatKind.Date, Pattern: (string)datePattern.SelectedItem),
                3 => new FieldFormat(FieldFormatKind.Time, Pattern: (string)timePattern.SelectedItem),
                _ => null,
            };
            if (field.Format != null && field.DefaultText.Trim().Length > 0)
            {
                string? shown = PdfFormLogic.Format(field.Format, field.DefaultText);
                if (shown == null) { Toast("The starting text is not " + PdfFormLogic.Describe(field.Format) + ", so it was left out"); field.DefaultText = ""; }
                else field.DefaultText = shown;
            }
            var names = calcNames.Text.Split(';', '\n').Select(s => s.Trim()).Where(s => s.Length > 0 && !string.Equals(s, field.Name, StringComparison.Ordinal)).Distinct().ToList();
            field.Calc = calcOp.SelectedIndex > 0 && names.Count > 0 ? new FieldCalc((FieldCalcOp)(calcOp.SelectedIndex - 1), names) : null;
            if (field.Calc != null)
            {
                var known = new HashSet<string>(_items.OfType<FieldItem>().Where(i => i != field).Select(i => i.Name));
                for (int p = 0; p < (_pdf?.PageCount ?? 0); p++) foreach (var f in Fields(p)) known.Add(f.Name);
                var unknown = field.Calc.Fields.Where(n => !known.Contains(n)).ToList();
                if (unknown.Count > 0) Toast("No field called " + string.Join(", ", unknown.Select(n => "\"" + n + "\"")) + " (yet): check the names");
            }
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
