using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IdmClone.Vid;

/// <summary>Settings of Utylix Vid Editor: they belong to the editor and are kept in its own file; Utylix's settings are not touched.</summary>
public sealed class VidSettingsWindow : Window
{
    private readonly VidSettings _before;
    private readonly VidSettings _now;
    private bool _saved;

    public VidSettingsWindow(Window owner)
    {
        Owner = owner;
        _before = VidSettings.Current.Copy();
        _now = VidSettings.Current.Copy();
        Title = "Settings - Utylix Vid Editor";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        WindowTheme.OwnTaskbarButton(this, "Utylix.VidEditor");

        var chip = (Style)FindResource("ChipButton");
        var plain = (Style)FindResource("DialogButton");
        var primary = (Style)FindResource("DialogPrimary");
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock { Text = "Settings", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var note = new TextBlock { Text = "These belong to the video editor only. Utylix's own settings are separate.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.Children.Add(note);

        // appearance
        stack.Children.Add(new TextBlock { Text = "Appearance", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var themes = new WrapPanel();
        foreach (var (key, label) in new[] { ("premiere", "Premiere dark"), ("system", "Like Windows"), ("dark", "Dark"), ("light", "Light") })
        {
            var rb = new RadioButton { Content = label, Style = chip, GroupName = "theme", IsChecked = _now.Theme == key };
            System.Windows.Automation.AutomationProperties.SetAutomationId(rb, "VidSetTheme" + key);
            rb.Checked += (_, _) => { _now.Theme = key; Preview(); };
            themes.Children.Add(rb);
        }
        stack.Children.Add(themes);
        stack.Children.Add(new TextBlock { Text = "Accent colour", Margin = new Thickness(0, 4, 0, 6) });
        var accents = new WrapPanel();
        foreach (var a in App.Accents)
        {
            var rb = new RadioButton { Content = a.Name, Style = chip, GroupName = "accent", IsChecked = _now.Accent == a.Key };
            System.Windows.Automation.AutomationProperties.SetAutomationId(rb, "VidSetAccent" + a.Key);
            string key = a.Key;
            rb.Checked += (_, _) => { _now.Accent = key; Preview(); };
            accents.Children.Add(rb);
        }
        stack.Children.Add(accents);

        // export
        stack.Children.Add(new TextBlock { Text = "Exporting", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
        var gpu = new CheckBox { Content = "Use the video card's encoder when there is one (much faster)", IsChecked = _now.UseGpu };
        System.Windows.Automation.AutomationProperties.SetAutomationId(gpu, "VidSetGpu");
        gpu.Checked += (_, _) => _now.UseGpu = true;
        gpu.Unchecked += (_, _) => _now.UseGpu = false;
        stack.Children.Add(gpu);
        stack.Children.Add(new TextBlock { Text = "Quality to start with", Margin = new Thickness(0, 10, 0, 6) });
        var quality = new WrapPanel();
        foreach (var (label, level) in new[] { ("High (big file)", 0), ("Balanced", 1), ("Small file", 2) })
        {
            var rb = new RadioButton { Content = label, Style = chip, GroupName = "quality", IsChecked = _now.Quality == level };
            rb.Checked += (_, _) => _now.Quality = level;
            quality.Children.Add(rb);
        }
        stack.Children.Add(quality);

        // safety copies
        stack.Children.Add(new TextBlock { Text = "Recovery copy", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
        stack.Children.Add(new TextBlock { Text = "Keep a copy of an unsaved project, so a crash or power cut loses almost nothing:", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var auto = new WrapPanel();
        foreach (var (label, secs) in new[] { ("Every 15 seconds", 15), ("Every 45 seconds", 45), ("Every 2 minutes", 120), ("Off", 0) })
        {
            var rb = new RadioButton { Content = label, Style = chip, GroupName = "autosave", IsChecked = _now.AutosaveSeconds == secs };
            rb.Checked += (_, _) => _now.AutosaveSeconds = secs;
            auto.Children.Add(rb);
        }
        stack.Children.Add(auto);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = plain, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var ok = new Button { Content = "Save", Style = primary, Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "VidSettingsSave");
        ok.Click += (_, _) => { _saved = true; Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        stack.Children.Add(buttons);
        Content = stack;

        Closing += (_, _) =>
        {
            if (_saved)
            {
                VidSettings.Current = _now;
                _now.Save();
            }
            else App.PreviewTheme(_before.Theme, _before.Accent);                 // cancelled: the colours go back
        };
    }

    private void Preview()
    {
        App.PreviewTheme(_now.Theme, _now.Accent);
        Owner?.InvalidateVisual();
    }

    /// <summary>True when the choices were saved.</summary>
    public bool Saved => _saved;
}
