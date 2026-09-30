using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Asks before installing a newer yt-dlp (sites change often; an old copy is the usual reason videos stop working).</summary>
public partial class UpdateWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;

    public UpdateWindow(string latest, string installed)
    {
        InitializeComponent();
        BodyText.Text = $"Version {latest} is out (you have {installed}). YouTube and other video sites change " +
                        "often, and an out-of-date yt-dlp is the most common reason downloads stop working. " +
                        "Updating takes a few seconds.";
        Closed += (_, _) => _cts.Cancel();
    }

    public void ShowOnTop()
    {
        Topmost = true;
        Show();
        Activate();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        UpdateBtn.IsEnabled = LaterBtn.IsEnabled = false;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        try
        {
            await Tools.InstallYtDlpAsync(s => Dispatcher.Invoke(() => StatusText.Text = s), _cts.Token);
            StatusText.Text = "Updated. Video downloads use the new version from now on.";
            await System.Threading.Tasks.Task.Delay(1800);
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't update: " + ex.Message +
                              (ex is System.IO.IOException ? "  (A video download may be running. Try again when it has finished.)" : "");
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
            UpdateBtn.IsEnabled = LaterBtn.IsEnabled = true;
            _busy = false;
        }
    }
}
