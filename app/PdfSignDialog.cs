using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>"Sign with a certificate": who signs (a certificate of this Windows user, a certificate file, or a new one the person makes), the reason, and an optional visible box.</summary>
public sealed class PdfSignDialog : Window
{
    private readonly int _pageCount, _current;
    private readonly StackPanel _certList = new();
    private readonly List<(RadioButton Chip, X509Certificate2 Cert)> _certs = new();
    private readonly TextBox _reason = Field(), _where = Field(), _name = Field(), _email = Field();
    private readonly CheckBox _visible = new() { Content = "Show a box on the page: who signed, when, why", IsChecked = true, FontWeight = FontWeights.SemiBold };
    private readonly StackPanel _visiblePanel = new(), _makePanel = new();
    private readonly List<(RadioButton Chip, PdfSignSpot Spot)> _spots = new();
    private readonly List<(RadioButton Chip, string Which)> _pages = new();
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, MinHeight = 20, Margin = new Thickness(0, 8, 0, 0) };

    public PdfSignOptions? Result { get; private set; }

    private static TextBox Field() => new() { Height = 30, Padding = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };

    public PdfSignDialog(Window owner, int pageCount, int currentPage)
    {
        _pageCount = pageCount; _current = Math.Clamp(currentPage, 0, pageCount - 1);
        PdfDialogKit.Setup(this, owner, "Sign with a certificate", 560);
        foreach (var f in new[] { _reason, _where, _name, _email }) f.SetResourceReference(Control.BackgroundProperty, "CardBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_name, "PdfSignName");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_email, "PdfSignEmail");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_reason, "PdfSignReason");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(PdfDialogKit.Muted("A signature proves the PDF has not been changed since you signed it, and who signed. Sign last: any change afterwards shows as \"changed since signing\". Your open PDF stays as it is; the signed copy is a new file."));

        root.Children.Add(PdfDialogKit.Heading("Who signs"));
        root.Children.Add(_certList);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var file = new Button { Content = "Use a certificate file…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0), Style = (Style)PdfDialogKit.Res("DialogButton") };
        var make = new Button { Content = "Make my own certificate…", Padding = new Thickness(10, 4, 10, 4), Style = (Style)PdfDialogKit.Res("DialogButton") };
        System.Windows.Automation.AutomationProperties.SetAutomationId(make, "PdfSignMake");
        file.Click += (_, _) => UseFile();
        make.Click += (_, _) => { _makePanel.Visibility = _makePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
        buttons.Children.Add(file); buttons.Children.Add(make);
        root.Children.Add(buttons);

        _makePanel.Visibility = Visibility.Collapsed; _makePanel.Margin = new Thickness(0, 8, 0, 0);
        _makePanel.Children.Add(PdfDialogKit.Muted("Your name"));
        _makePanel.Children.Add(_name);
        _makePanel.Children.Add(PdfDialogKit.Muted("E-mail (optional)")); ((TextBlock)_makePanel.Children[^1]).Margin = new Thickness(0, 6, 0, 0);
        _makePanel.Children.Add(_email);
        var create = new Button { Content = "Make it", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)PdfDialogKit.Res("DialogPrimary") };
        System.Windows.Automation.AutomationProperties.SetAutomationId(create, "PdfSignCreate");
        create.Click += (_, _) => CreateOwn();
        _makePanel.Children.Add(create);
        _makePanel.Children.Add(PdfDialogKit.Muted("A certificate you make yourself is kept in this Windows user's certificate store for 5 years. Readers show it as \"signer not verified\" until the person who gets the file chooses to trust it; the signature itself still proves the file was not changed. For a signature everyone trusts, use a certificate from your employer, a school, a government or a certificate authority: it appears in the list above once it is installed in Windows."));
        ((TextBlock)_makePanel.Children[^1]).Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_makePanel);

        root.Children.Add(PdfDialogKit.Heading("Why (optional)"));
        root.Children.Add(PdfDialogKit.Muted("Reason, for example \"I approve this\""));
        root.Children.Add(_reason);
        root.Children.Add(PdfDialogKit.Muted("Place")); ((TextBlock)root.Children[^1]).Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(_where);

        _visible.Margin = new Thickness(0, 14, 0, 4);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_visible, "PdfSignVisible");
        root.Children.Add(_visible);
        _visiblePanel.Margin = new Thickness(22, 0, 0, 0);
        var spots = new WrapPanel();
        foreach (var (label, spot) in new[] { ("Bottom right", PdfSignSpot.BottomRight), ("Bottom left", PdfSignSpot.BottomLeft), ("Top right", PdfSignSpot.TopRight), ("Top left", PdfSignSpot.TopLeft) })
        {
            var chip = PdfDialogKit.Chip(label, "signspot", spot == PdfSignSpot.BottomRight, () => { }, "PdfSignSpot" + spot);
            _spots.Add((chip, spot)); spots.Children.Add(chip);
        }
        _visiblePanel.Children.Add(spots);
        var pages = new WrapPanel();
        foreach (var (label, which) in new[] { ("On this page", "this"), ("On the first page", "first"), ("On the last page", "last") })
        {
            var chip = PdfDialogKit.Chip(label, "signpage", which == "last", () => { }, "PdfSignPage" + which);
            _pages.Add((chip, which)); pages.Children.Add(chip);
        }
        _visiblePanel.Children.Add(pages);
        root.Children.Add(_visiblePanel);
        _visible.Click += (_, _) => { _visiblePanel.IsEnabled = _visible.IsChecked == true; _visiblePanel.Opacity = _visiblePanel.IsEnabled ? 1 : 0.45; };

        root.Children.Add(_problem);
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
        var go = new Button { Content = "Sign and choose where to save…", MinWidth = 230, IsDefault = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(go, "PdfSignGo");
        go.Click += (_, _) => Accept();
        root.Children.Add(PdfDialogKit.Buttons(go, new Button { Content = "Cancel", MinWidth = 96 }));
        Content = root;

        LoadStore();
    }

    private void LoadStore()
    {
        List<X509Certificate2> found;
        try { found = PdfSigning.UsableCertificates(); }
        catch (CryptographicException) { found = new List<X509Certificate2>(); }
        foreach (var c in found) AddCert(c, select: false);
        if (_certs.Count > 0) _certs[0].Chip.IsChecked = true;
        else
        {
            _certList.Children.Add(PdfDialogKit.Muted("No certificate that can sign was found in Windows. Make your own below, or use a certificate file (.pfx / .p12)."));
            _makePanel.Visibility = Visibility.Visible;
        }
    }

    private void AddCert(X509Certificate2 c, bool select)
    {
        if (_certs.Count == 0 && _certList.Children.Count > 0) _certList.Children.Clear();            // (the "none found" hint)
        var text = new StackPanel { Width = 440 };
        string who = PdfSigning.Name(c);
        text.Children.Add(new TextBlock { Text = who, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        string detail = (PdfSigning.SelfSigned(c) ? "made by you" : "issued by " + PdfSigning.IssuerName(c)) + " · valid until " + c.NotAfter.ToString("d");
        text.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        var chip = new RadioButton { Content = text, GroupName = "signcert", Style = (Style)PdfDialogKit.Res("ChipButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 5) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "PdfSignCert" + _certs.Count);
        _certs.Add((chip, c));
        _certList.Children.Add(chip);
        if (select) chip.IsChecked = true;
    }

    private void UseFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose your certificate file", Filter = "Certificate files|*.pfx;*.p12|All files|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        bool wrong = false;
        while (true)
        {
            var ask = new PasswordWindow(System.IO.Path.GetFileName(dlg.FileName), wrong, "Enter the password of this certificate file") { Owner = this };
            if (ask.ShowDialog() != true) return;
            try { AddCert(PdfSigning.LoadFile(dlg.FileName, ask.Password), select: true); _problem.Text = ""; return; }
            catch (CryptographicException e) when (e.Message.Contains("private key", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("expired", StringComparison.OrdinalIgnoreCase)) { _problem.Text = e.Message; return; }
            catch (CryptographicException) { wrong = true; }
            catch (System.IO.IOException e) { _problem.Text = "Couldn't read the file: " + e.Message; return; }
        }
    }

    private void CreateOwn()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) { _problem.Text = "Type your name for the certificate."; return; }
        try
        {
            AddCert(PdfSigning.CreateOwn(name, _email.Text), select: true);
            _makePanel.Visibility = Visibility.Collapsed; _problem.Text = "";
        }
        catch (CryptographicException e) { _problem.Text = "Couldn't make the certificate: " + e.Message; }
    }

    private void Accept()
    {
        var chosen = _certs.FirstOrDefault(c => c.Chip.IsChecked == true);
        if (chosen.Cert == null) { _problem.Text = "Choose who signs: pick a certificate, or make your own."; return; }
        var spot = _visible.IsChecked == true ? _spots.First(s => s.Chip.IsChecked == true).Spot : PdfSignSpot.None;
        string which = _pages.First(p => p.Chip.IsChecked == true).Which;
        int page = which == "first" ? 0 : which == "last" ? _pageCount - 1 : _current;
        Result = new PdfSignOptions(chosen.Cert, _reason.Text.Trim(), _where.Text.Trim(), "", spot, page);
        DialogResult = true;
    }
}
