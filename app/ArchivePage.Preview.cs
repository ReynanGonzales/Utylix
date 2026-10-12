using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Hover preview in the archive listing: rest the pointer on a picture for a moment and a small preview appears next to it. The picture is taken out of the archive into a
/// temporary file (only that one file), decoded at a small size, and the temporary file is deleted at once. The last few previews are kept, so going back is instant.
/// </summary>
public partial class ArchivePage
{
    private static readonly HashSet<string> PreviewTypes = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".webp" };
    private const long PreviewMaxBytes = 25L * 1024 * 1024;

    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly Dictionary<string, (BitmapSource Picture, string Info)> _previewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _previewOrder = new();
    private ArchItem? _hoverItem;
    private CancellationTokenSource? _previewCts;
    private Popup? _previewPopup;
    private Image? _previewImage;
    private TextBlock? _previewInfo;

    private void InitPreview()
    {
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); if (_hoverItem != null) _ = ShowPreviewAsync(_hoverItem); };
        EntryList.MouseMove += (_, e) => HoverPreview(ItemUnder(e.OriginalSource), e.LeftButton == System.Windows.Input.MouseButtonState.Pressed);
        EntryList.MouseLeave += (_, _) => HidePreview();
        EntryList.PreviewMouseWheel += (_, _) => HidePreview();
        EntryList.PreviewMouseDown += (_, _) => HidePreview();
    }

    private bool CanPreview(ArchItem? item) =>
        item is { IsFolder: false } && !_busy && _archivePath != null && PreviewTypes.Contains(Path.GetExtension(item.Name)) && item.Size > 0 && item.Size <= PreviewMaxBytes
        && (!item.Encrypted || _password != null);

    private void HoverPreview(ArchItem? item, bool buttonDown)
    {
        if (buttonDown || !CanPreview(item)) { HidePreview(); return; }
        if (ReferenceEquals(item, _hoverItem)) return;               // still on the same row: the timer or the popup is already there
        HidePreview();
        _hoverItem = item;
        _previewTimer.Start();
    }

    private void HidePreview()
    {
        _previewTimer.Stop();
        _hoverItem = null;
        _previewCts?.Cancel();
        if (_previewPopup != null) _previewPopup.IsOpen = false;
    }

    private async Task ShowPreviewAsync(ArchItem item)
    {
        string key = (_archivePath ?? "") + "|" + item.FullPath;
        if (!_previewCache.TryGetValue(key, out var ready))
        {
            var cts = _previewCts = new CancellationTokenSource();
            string temp = Path.Combine(Path.GetTempPath(), "Utylix-open", "p" + Guid.NewGuid().ToString("N")[..9]);
            try
            {
                await ArchiveService.ExtractAsync(_archivePath!, temp, new[] { item.FullPath }, _password, OverwriteMode.Overwrite, null, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(item, _hoverItem)) return;
                string? file = ArchiveService.SafeTarget(temp, item.FullPath);
                if (file == null || !File.Exists(file)) return;
                var bytes = await File.ReadAllBytesAsync(file, cts.Token);
                var picture = await Task.Run(() =>
                {
                    using var ms = new MemoryStream(bytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.DecodePixelWidth = 560;                                 // (never decodes more than a preview needs)
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }, cts.Token);
                // the real size of the picture, for the caption (a second light read of the header)
                string dims = "";
                try
                {
                    using var ms2 = new MemoryStream(bytes);
                    var frame = BitmapDecoder.Create(ms2, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                    dims = $"{frame.PixelWidth} × {frame.PixelHeight}  ·  ";
                }
                catch (Exception) { /* a format whose header can't be read alone */ }
                ready = (picture, dims + Format.Bytes(item.Size));
                _previewCache[key] = ready;
                _previewOrder.Enqueue(key);
                while (_previewOrder.Count > 24) _previewCache.Remove(_previewOrder.Dequeue());
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArchivePasswordException || e.GetType().Name.Contains("Decod"))
            {
                return;                                                       // not a picture WPF can read, or taken away meanwhile: no preview
            }
            finally { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception) { } }
        }
        if (!ReferenceEquals(item, _hoverItem)) return;                       // the pointer has moved on while the picture was coming out

        if (_previewPopup == null)
        {
            _previewImage = new Image { MaxWidth = 440, MaxHeight = 330, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.HighQuality);
            _previewInfo = new TextBlock { Margin = new Thickness(2, 6, 2, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440 };
            _previewInfo.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var panel = new StackPanel { Margin = new Thickness(8) };
            panel.Children.Add(_previewImage);
            panel.Children.Add(_previewInfo);
            var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 } };
            frame.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            _previewPopup = new Popup { Child = frame, AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false, Placement = PlacementMode.Mouse, HorizontalOffset = 18, VerticalOffset = 18, PopupAnimation = PopupAnimation.Fade };
        }
        _previewImage!.Source = ready.Picture;
        _previewInfo!.Text = item.Name + "   " + ready.Info;
        _previewPopup.IsOpen = false;                                         // (re-open so that it is placed at the pointer's present spot)
        _previewPopup.IsOpen = true;
    }
}
