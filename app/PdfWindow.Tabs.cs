using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Tabs for the PDFs that are open: once two or more editor windows exist, a strip of tabs (one per PDF) shows on top of each of them. Clicking a tab brings that PDF to the front
/// in the same place and size as this window, so it feels like one window with tabs; the cross closes that PDF (asking first when it has changes); "+" opens another PDF.
/// Pages can be copied from one PDF to another (page strip, right-click > Copy the page(s) to another PDF).
/// </summary>
public sealed partial class PdfWindow
{
    private readonly Border _tabBar = new() { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1F)), Height = 34 };
    private readonly StackPanel _tabRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };

    private UIElement TabBar()
    {
        System.Windows.Automation.AutomationProperties.SetAutomationId(_tabBar, "PdfTabs");
        _tabBar.Child = new ScrollViewer { Content = _tabRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(6, 0, 6, 0) };
        // the tab shows the name of the file: refresh every window's strip when a title changes (opened, changed, saved)
        DependencyPropertyDescriptor.FromProperty(TitleProperty, typeof(Window)).AddValueChanged(this, (_, _) => RefreshAllTabs());
        Loaded += (_, _) => RefreshAllTabs();
        return _tabBar;
    }

    private static void RefreshAllTabs()
    {
        foreach (var w in Windows.ToList()) w.BuildTabs();
    }

    private void BuildTabs()
    {
        var all = Windows.Where(w => w._path != null || w == this).ToList();
        bool show = all.Count(w => w._path != null) >= 2;
        _tabBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _tabRow.Children.Clear();
        if (!show) return;
        foreach (var w in all)
        {
            bool active = w == this;
            string name = w._path == null ? "New" : System.IO.Path.GetFileName(w._path);
            if (name.Length > 26) name = name[..24] + "…";
            var label = new TextBlock { Text = (w._dirty ? "• " : "") + name, Foreground = active ? Brushes.White : Soft, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal };
            var cross = new Button
            {
                Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, Foreground = Soft }, Width = 18, Height = 18, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0),
                Focusable = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Close this PDF", Cursor = Cursors.Hand,
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='9'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
                    "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#44FFFFFF' /></Trigger></ControlTemplate.Triggers></ControlTemplate>"),
            };
            var target = w;
            cross.Click += (_, e) => { e.Handled = true; CloseTab(target); };
            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(label); inner.Children.Add(cross);
            var tab = new Border
            {
                Child = inner, Padding = new Thickness(12, 0, 6, 0), Height = 28, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom, Cursor = active ? Cursors.Arrow : Cursors.Hand,
                CornerRadius = new CornerRadius(7, 7, 0, 0), Background = new SolidColorBrush(active ? Color.FromRgb(0x23, 0x28, 0x34) : Color.FromRgb(0x1E, 0x22, 0x2D)), ToolTip = w._path,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(tab, "PdfTab_" + System.IO.Path.GetFileNameWithoutExtension(w._path ?? "New"));
            System.Windows.Automation.AutomationProperties.SetName(tab, name);
            tab.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left && !active) { SwitchTo(target); e.Handled = true; } };
            tab.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { CloseTab(target); e.Handled = true; } };
            _tabRow.Children.Add(tab);
        }
        var plus = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = Soft }, Width = 28, Height = 26, Margin = new Thickness(4, 0, 0, 1), Padding = new Thickness(0),
            Focusable = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Open another PDF", Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Bottom,
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#33FFFFFF' /></Trigger></ControlTemplate.Triggers></ControlTemplate>"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(plus, "PdfTabNew");
        plus.Click += (_, _) => Browse(this);
        _tabRow.Children.Add(plus);
    }

    /// <summary>Another PDF comes to the front, in the place and size of this window.</summary>
    private void SwitchTo(PdfWindow target)
    {
        if (target == this) return;
        if (WindowState == WindowState.Maximized) target.WindowState = WindowState.Maximized;
        else
        {
            if (target.WindowState != WindowState.Normal) target.WindowState = WindowState.Normal;
            target.Left = Left; target.Top = Top; target.Width = Width; target.Height = Height;
        }
        WindowTheme.BringToFront(target);
    }

    private void CloseTab(PdfWindow target)
    {
        if (target != this) WindowTheme.BringToFront(target);          // (its "save the changes?" question is about that PDF: show it)
        target.Close();
        if (target != this && !Windows.Contains(target) && IsLoaded) WindowTheme.BringToFront(this);     // (closed: back to this PDF; kept after "Cancel": stays on that one)
    }

    /// <summary>A window opened while another PDF is open takes its place and size (a new tab, not a window somewhere else).</summary>
    private void TakePlaceOf(PdfWindow other)
    {
        if (other.WindowState == WindowState.Maximized) { WindowState = WindowState.Maximized; return; }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = other.Left; Top = other.Top; Width = other.Width; Height = other.Height;
    }

    // ---------- pages from one PDF into another ----------
    private void AddSendToMenu(ContextMenu menu)
    {
        // one entry per other open PDF, put in each time the menu opens (the menu style has no sub-menus)
        var dynamic = new List<Control>();
        menu.Opened += (_, _) =>
        {
            foreach (var old in dynamic) menu.Items.Remove(old);
            dynamic.Clear();
            var others = Windows.Where(w => w != this && w._pdf != null && w._path != null).ToList();
            if (others.Count == 0) return;
            var line = new Separator { Style = (Style)Application.Current.FindResource("ThemedSeparator") };
            menu.Items.Add(line); dynamic.Add(line);
            foreach (var w in others)
            {
                var target = w;
                var m = new MenuItem { Header = "Copy the page(s) to " + System.IO.Path.GetFileName(w._path), Style = (Style)Application.Current.FindResource("ThemedMenuItem") };
                System.Windows.Automation.AutomationProperties.SetAutomationId(m, "PdfSendTo_" + System.IO.Path.GetFileNameWithoutExtension(w._path));
                m.Click += (_, _) => SendPagesTo(target);
                menu.Items.Add(m); dynamic.Add(m);
            }
        };
    }
    private void SendPagesTo(PdfWindow target)
    {
        if (_pdf == null || target._pdf == null || target._path == null || !CommitItems()) return;
        var source = _pdf;
        var sel = SelectedPages();
        int at = target.SelectedPages()[^1] + 1;                         // (after the chosen page of that PDF)
        int added = 0;
        bool done = target.PageOp(p => added = PdfPageTools.InsertPages(p, source, sel, at), () => Enumerable.Range(at, added).ToList(),
                                  () => (added == 1 ? "1 page" : added + " pages") + " copied in after page " + at + ", from " + System.IO.Path.GetFileName(_path));
        if (done) Toast((added == 1 ? "1 page" : added + " pages") + " copied to " + System.IO.Path.GetFileName(target._path) + " (it is in the tab of that PDF)");
    }
}
