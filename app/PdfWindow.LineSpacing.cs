using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IdmClone;

/// <summary>
/// "Line spacing" of Edit text: how far apart the lines of a changed line are when it has several (Shift+Enter makes a new line). The choices are multiples of the font's own spacing;
/// the line being typed in follows at once.
/// </summary>
public sealed partial class PdfWindow
{
    private Button? _runSpacingButton;

    private static readonly (string Name, double Factor)[] SpacingChoices =
    {
        ("Tight  (0.9)", 0.9), ("Single  (1.0)", 1.0), ("1.15", 1.15), ("1.5", 1.5), ("Double  (2.0)", 2.0), ("2.5", 2.5),
    };

    private UIElement RunSpacingButton()
    {
        var b = new Button { Content = BarLabel("Spacing ▾"), Template = BarButtonTemplate(), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "Line spacing: how far apart the lines are when this line has several (Shift+Enter makes a new line)", Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfRunSpacing");
        b.Click += (_, _) => ShowRunSpacingMenu(b);
        _runSpacingButton = b;
        return b;
    }

    private void ShowRunSpacingMenu(FrameworkElement anchor)
    {
        if (_runTyping == null) return;
        _runMenuOpen = true;                                           // (the menu takes the keyboard: the line stays open until it is closed again)
        var box = _runBox;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Closed += (_, _) =>
        {
            _runMenuOpen = false;
            if (box != null && _runBox == box) Dispatcher.BeginInvoke(() => { if (_runBox == box) box.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        };
        double? now = _runTyping.Spacing;
        foreach (var (name, factor) in SpacingChoices)
        {
            var m = new MenuItem { Header = name, IsCheckable = true, IsChecked = now is double n && Math.Abs(n - factor) < 0.001 || now == null && factor == 1.0 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(m, "PdfRunSpacing" + factor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            double f = factor;
            m.Click += (_, _) => SetRunSpacing(f);
            menu.Items.Add(m);
        }
        Themed(menu);
        menu.IsOpen = true;
    }

    private void SetRunSpacing(double factor)
    {
        if (_runTyping == null) return;
        _runTyping.Spacing = factor;
        _runTyping.AdvanceOverride = null;
        _dirty = true; UpdateTitle();
        ApplySpacingToBox();
        RenderItems(_runTyping.Page);
        if (_runTyping.NewText.IndexOf('\n') < 0) Toast("Line spacing applies to lines made with Shift+Enter");
    }

    /// <summary>The box being typed in shows the lines as far apart as they will be.</summary>
    private void ApplySpacingToBox()
    {
        if (_runBox == null || _runTyping == null) return;
        if (_runTyping.Spacing == null && _runTyping.AdvanceOverride == null) return;
        TextBlock.SetLineStackingStrategy(_runBox, LineStackingStrategy.BlockLineHeight);
        TextBlock.SetLineHeight(_runBox, _runTyping.LineAdvance);
    }
}
