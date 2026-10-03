using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IdmClone;

/// <summary>A small window with a progress bar and a Stop button for a job that takes a while (the person's window waits underneath).</summary>
public sealed class BusyDialog : Window
{
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 20, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CancellationTokenSource _cts = new();

    private BusyDialog(Window owner, string title, string first)
    {
        Owner = owner;
        Title = title;
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
        _text.Text = first;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_text, "BusyText");
        root.Children.Add(_text); root.Children.Add(_bar);
        var stop = new Button { Content = "Stop", Style = (Style)Application.Current.FindResource("DialogButton"), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        stop.Click += (_, _) => { _cts.Cancel(); _text.Text = "Stopping…"; stop.IsEnabled = false; };
        root.Children.Add(stop);
        Content = root;
        Closing += (_, e) => { if (!_done) { e.Cancel = true; _cts.Cancel(); } };
    }

    private bool _done;

    /// <summary>Runs the job (which reports "done of total" and a line of text) with this window shown; returns false when it was stopped. Errors pass through.</summary>
    public static async Task<bool> RunAsync(Window owner, string title, string first, Func<Action<int, int, string>, CancellationToken, Task> job)
    {
        var dialog = new BusyDialog(owner, title, first);
        owner.IsEnabled = false;
        dialog.Show();
        try
        {
            await job((done, total, text) => dialog.Dispatcher.Invoke(() => { dialog._bar.Value = total == 0 ? 0 : (double)done / total; if (text.Length > 0) dialog._text.Text = text; }), dialog._cts.Token);
            return !dialog._cts.IsCancellationRequested;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            dialog._done = true;
            owner.IsEnabled = true;
            dialog.Close();
            owner.Activate();
        }
    }
}
