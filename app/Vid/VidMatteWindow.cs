using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone.Vid;

/// <summary>"Remove the background (AI)": explains, gets the model when it is missing (after asking), shows progress, hands back the new file.</summary>
public sealed class VidMatteWindow : Window
{
    private readonly MediaItem _media;
    private readonly double _in, _length, _fps;
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _go, _cancel;
    private readonly RadioButton _best, _fast;
    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>The finished file (transparent video or picture), or null.</summary>
    public string? ResultPath { get; private set; }

    public VidMatteWindow(Window owner, MediaItem media, double sourceIn, double sourceLength, double fps)
    {
        Owner = owner;
        _media = media; _in = sourceIn; _length = sourceLength; _fps = fps;
        Title = "Remove the background - Utylix Vid Editor";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        var chip = (Style)FindResource("ChipButton");
        var plain = (Style)FindResource("DialogButton");
        var primary = (Style)FindResource("DialogPrimary");

        bool picture = media.Kind == MediaKind.Image;
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock { Text = "Remove the background", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var what = new TextBlock
        {
            Text = picture
                ? "Cuts the person or thing out of this picture so whatever is under it on the timeline shows through. It runs on this PC; nothing is uploaded."
                : "Cuts the person or thing out of every frame of this clip so whatever is under it on the timeline shows through. It runs on this PC (an AI model, nothing is uploaded) and makes a new transparent file; your original is not changed.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        };
        what.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.Children.Add(what);

        _best = new RadioButton { Content = "Best: every frame", Style = chip, GroupName = "matte", IsChecked = true };
        _fast = new RadioButton { Content = "Faster: every second frame", Style = chip, GroupName = "matte" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_fast, "VidMatteFast");
        if (!picture)
        {
            var q = new WrapPanel();
            q.Children.Add(_best); q.Children.Add(_fast);
            stack.Children.Add(q);
            int frames = Math.Max(1, (int)Math.Round(_length * _fps));
            var est = new TextBlock { Text = $"{frames} frames. On this PC about {Math.Ceiling(frames * 0.65 / 60):0} min for the best quality, half that for faster." };
            est.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(est);
        }
        stack.Children.Add(_bar);
        stack.Children.Add(_state);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _cancel = new Button { Content = "Close", Style = plain, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        _go = new Button { Content = BackgroundRemover.HasModel ? "Start" : "Get the AI model", Style = primary, Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "VidMatteGo");
        buttons.Children.Add(_cancel); buttons.Children.Add(_go);
        stack.Children.Add(buttons);
        Content = stack;

        if (!BackgroundRemover.HasModel)
            _state.Text = $"This needs the AI model once ({BackgroundRemover.ModelBytes / 1048576} MB, from the rembg project, checked against its fingerprint). Pressing the button downloads it.";
        _go.Click += async (_, _) => await GoAsync();
        _cancel.Click += (_, _) => { if (_running) _cts?.Cancel(); else Close(); };
        Closing += (_, e) => { if (_running) { e.Cancel = true; _cts?.Cancel(); } };
    }

    private async System.Threading.Tasks.Task GoAsync()
    {
        _running = true;
        _go.IsEnabled = false;
        _cancel.Content = "Cancel";
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        _cts = new CancellationTokenSource();
        try
        {
            if (!BackgroundRemover.HasModel)
            {
                _state.Text = "Downloading the AI model…";
                await BackgroundRemover.InstallModelAsync(s => Dispatcher.Invoke(() => _state.Text = s), _cts.Token);
                _go.Content = "Start";
                _state.Text = "The model is ready.";
                _bar.Visibility = Visibility.Collapsed;
                _go.IsEnabled = true; _running = false; _cancel.Content = "Close";
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (_media.Kind == MediaKind.Image)
            {
                _state.Text = "Cutting the picture out…";
                string path = await System.Threading.Tasks.Task.Run(() => BackgroundRemover.Remove(_media.Path, VidMatte.OutputFolder));
                ResultPath = path;
            }
            else
            {
                var progress = new Progress<double>(p =>
                {
                    _bar.Value = p * 100;
                    string eta = "";
                    if (p > 0.03) { var left = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - p) / p); eta = "  ·  about " + (left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes} min {left.Seconds} s" : $"{left.Seconds} s") + " left"; }
                    _state.Text = $"Cutting out the frames…  {p * 100:0} %{eta}";
                });
                ResultPath = await VidMatte.RunAsync(_media, _in, _length, _fps, _fast.IsChecked == true ? 2 : 1, progress, _cts.Token);
            }
            _running = false;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _state.Text = "Cancelled. Nothing was changed.";
            _bar.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _state.Text = e.Message;
            _bar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _running = false;
            _cancel.Content = "Close";
            _go.IsEnabled = true;
            _cts?.Dispose(); _cts = null;
        }
    }
}
