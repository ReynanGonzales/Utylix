using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IdmClone;

/// <summary>
/// "This feature needs a one-time download. Download it now?" with a progress line. Nothing is downloaded unless the
/// person presses the button. DialogResult is true once the download is finished and checked.
/// </summary>
public sealed class ToolDownloadWindow : Window
{
    private readonly Func<Action<string>, CancellationToken, Task> _install;
    private readonly CancellationTokenSource _cts = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), MinHeight = 20 };
    private readonly Button _go = new() { MinWidth = 150, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _cancel = new() { Content = "Not now", MinWidth = 96 };
    private bool _busy;

    public ToolDownloadWindow(string title, string what, string details, string buttonText, Func<Action<string>, CancellationToken, Task> install)
    {
        _install = install;
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico"));

        _go.Content = buttonText;
        _go.Style = (Style)Application.Current.FindResource("DialogPrimary");
        _cancel.Style = (Style)Application.Current.FindResource("DialogButton");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties_SetId(_go, "DownloadGo");
        AutomationProperties_SetId(_cancel, "DownloadCancel");
        AutomationProperties_SetId(_status, "DownloadStatus");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(_go);
        buttons.Children.Add(_cancel);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = what, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(detail);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        Content = panel;

        _go.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => { _cts.Cancel(); DialogResult = false; };
        Closing += (_, _) => _cts.Cancel();
    }

    private static void AutomationProperties_SetId(DependencyObject o, string id) => System.Windows.Automation.AutomationProperties.SetAutomationId(o, id);

    private async Task RunAsync()
    {
        if (_busy) return;
        _busy = true;
        _go.IsEnabled = false;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        try
        {
            await _install(s => Dispatcher.Invoke(() => _status.Text = s), _cts.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException) { /* closed */ }
        catch (Exception ex)
        {
            _status.Text = "Couldn't finish: " + ex.Message;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
            _go.Content = "Try again";
            _go.IsEnabled = true;
        }
        finally { _busy = false; }
    }
}
