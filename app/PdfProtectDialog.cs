using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>"Add a password": a password to open the PDF, and / or limits (no printing, no copying, no changing) that need another password to lift.</summary>
public sealed class PdfProtectDialog : Window
{
    private readonly PasswordBox _open = Box("PdfProtectOpen"), _open2 = Box("PdfProtectOpen2"), _limit = Box("PdfProtectLimit"), _limit2 = Box("PdfProtectLimit2");
    private readonly CheckBox _useOpen = new() { Content = "Ask for a password to open it", FontWeight = FontWeights.SemiBold, IsChecked = true };
    private readonly CheckBox _useLimit = new() { Content = "Limit what people can do with it", FontWeight = FontWeights.SemiBold };
    private readonly CheckBox _print = new() { Content = "Allow printing", IsChecked = true }, _copy = new() { Content = "Allow copying text and pictures", IsChecked = true }, _edit = new() { Content = "Allow changing it (pages, text, comments)", IsChecked = true };
    private readonly StackPanel _openPanel = new(), _limitPanel = new();
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, MinHeight = 20, Margin = new Thickness(0, 10, 0, 0) };

    public string OpenPassword { get; private set; } = "";
    public string LimitPassword { get; private set; } = "";
    public PdfLimits Limits { get; private set; } = new();

    private static PasswordBox Box(string id)
    {
        var b = new PasswordBox { Height = 32, Padding = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14, Margin = new Thickness(0, 4, 0, 0) };
        b.SetResourceReference(Control.BackgroundProperty, "CardBrush"); b.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        b.SetResourceReference(Control.BorderBrushProperty, "LineBrush"); b.SetResourceReference(PasswordBox.CaretBrushProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    public PdfProtectDialog(Window owner)
    {
        PdfDialogKit.Setup(this, owner, "Add a password", 470);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_useOpen, "PdfProtectUseOpen");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_useLimit, "PdfProtectUseLimit");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(PdfDialogKit.Muted("The new file is encrypted with AES-256. Write the passwords down: a lost password can't be recovered."));

        root.Children.Add(Spaced(_useOpen));
        _openPanel.Margin = new Thickness(22, 0, 0, 0);
        _openPanel.Children.Add(PdfDialogKit.Muted("Password")); _openPanel.Children.Add(_open);
        _openPanel.Children.Add(PdfDialogKit.Muted("Type it again")); _openPanel.Children.Add(_open2);
        root.Children.Add(_openPanel);

        root.Children.Add(Spaced(_useLimit));
        _limitPanel.Margin = new Thickness(22, 0, 0, 0);
        foreach (var c in new[] { _print, _copy, _edit }) { c.Margin = new Thickness(0, 3, 0, 0); _limitPanel.Children.Add(c); }
        _limitPanel.Children.Add(new TextBlock { Text = "Password to lift the limits", Margin = new Thickness(0, 10, 0, 0) });
        ((TextBlock)_limitPanel.Children[^1]).SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _limitPanel.Children.Add(_limit);
        _limitPanel.Children.Add(PdfDialogKit.Muted("Type it again")); ((TextBlock)_limitPanel.Children[^1]).Margin = new Thickness(0, 6, 0, 0);
        _limitPanel.Children.Add(_limit2);
        _limitPanel.Children.Add(PdfDialogKit.Muted("Without it, the password to open the PDF also lifts the limits."));
        ((TextBlock)_limitPanel.Children[^1]).Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(_limitPanel);

        root.Children.Add(_problem);
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");

        var go = new Button { Content = "Choose where to save…", MinWidth = 170, IsDefault = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(go, "PdfProtectGo");
        go.Click += (_, _) => Accept();
        root.Children.Add(PdfDialogKit.Buttons(go, new Button { Content = "Cancel", MinWidth = 96 }));
        Content = root;

        _useOpen.Click += (_, _) => Refresh(); _useLimit.Click += (_, _) => Refresh();
        Refresh();
        Loaded += (_, _) => _open.Focus();
    }

    private static UIElement Spaced(CheckBox c) { c.Margin = new Thickness(0, 14, 0, 4); return c; }

    private void Refresh()
    {
        _openPanel.IsEnabled = _useOpen.IsChecked == true; _openPanel.Opacity = _openPanel.IsEnabled ? 1 : 0.45;
        _limitPanel.IsEnabled = _useLimit.IsChecked == true; _limitPanel.Opacity = _limitPanel.IsEnabled ? 1 : 0.45;
        _problem.Text = "";
    }

    private void Accept()
    {
        bool open = _useOpen.IsChecked == true, limit = _useLimit.IsChecked == true;
        if (!open && !limit) { _problem.Text = "Tick at least one: a password to open it, or limits."; return; }
        if (open && _open.Password.Length == 0) { _problem.Text = "Type the password to open the PDF."; return; }
        if (open && _open.Password != _open2.Password) { _problem.Text = "The two passwords to open it are different."; return; }
        if (limit && _limit.Password != _limit2.Password) { _problem.Text = "The two passwords for the limits are different."; return; }
        if (limit && _limit.Password.Length == 0 && !open) { _problem.Text = "Limits need a password to lift them: type one."; return; }
        if (limit && _print.IsChecked == true && _copy.IsChecked == true && _edit.IsChecked == true) { _problem.Text = "Every box is ticked, so nothing is limited. Untick what must not be allowed."; return; }
        OpenPassword = open ? _open.Password : "";
        LimitPassword = limit ? _limit.Password : "";
        Limits = limit ? new PdfLimits(_print.IsChecked == true, _copy.IsChecked == true, _edit.IsChecked == true) : new PdfLimits();
        DialogResult = true;
    }
}
