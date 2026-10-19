using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Blur (Mark up tab): drag over something to hide it behind a blur or blocks (a name, a number, a face). The blurred picture is made from the page as it looks, and goes into the PDF as a picture
/// over that place. A blur on its own would leave the words under it in the file, so saving ALSO removes what is under it (like Redact) unless that is turned off in the Blur button's menu.
/// </summary>
public sealed partial class PdfWindow
{
    private int _blurStyle = 1;                  // 0 soft blur, 1 strong blur, 2 blocks
    private bool _blurRemove = true;             // saving also removes what is under the blur
    private Button? _blurButton;

    private sealed class BlurItem : EditItem
    {
        public Rect Box;
        public int Style = 1;
        public bool RemoveUnder = true;
        public BitmapSource? Pixels;
        public Action<BlurItem>? Recapture;

        public override Rect Bounds => Box;
        public override EditItem Clone() => (BlurItem)MemberwiseClone();
        public override void MoveBy(Vector d) { Box.Offset(d); Recapture?.Invoke(this); }
        public override void ResizeTo(Rect r) { Box = r; Recapture?.Invoke(this); }

        public override FrameworkElement Build()
        {
            var image = new Image { Source = Pixels, Width = Box.Width, Height = Box.Height, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(image, Box.X); Canvas.SetTop(image, Box.Y);
            return image;
        }

        public override IEnumerable<PdfMark> Marks()
        {
            if (RemoveUnder) yield return new PdfRedactMark(Page, Box, Blur: true);                         // (what is under it goes for good when saved; the blurred picture is drawn after)
            if (Pixels != null) yield return new PdfImageMark(Page, Box, null, Pixels);
        }
    }

    /// <summary>"2 black boxes", "1 blur", "1 black box and 2 blurs": what the save question is about.</summary>
    private static string HiddenAreasText(IReadOnlyList<PdfRedactMark> marks)
    {
        int blurs = marks.Count(m => m.Blur), boxes = marks.Count - blurs;
        var parts = new List<string>();
        if (boxes > 0) parts.Add($"{boxes} black box{(boxes == 1 ? "" : "es")}");
        if (blurs > 0) parts.Add($"{blurs} blur{(blurs == 1 ? "" : "s")}");
        return string.Join(" and ", parts);
    }

    private void AddBlur(PageView pv, Rect area)
    {
        area.Intersect(new Rect(0, 0, pv.Overlay.Width, pv.Overlay.Height));
        if (area.IsEmpty || area.Width < 4 || area.Height < 4) { RenderItems(pv.Index); return; }
        var item = new BlurItem { Page = pv.Index, Box = area, Style = _blurStyle, RemoveUnder = _blurRemove, Recapture = Recapture };
        Recapture(item);
        if (item.Pixels == null) { Toast("The page isn't drawn yet: wait a moment and try again"); RenderItems(pv.Index); return; }
        Add(item, select: true);
        Toast(_blurRemove ? "Blurred. Saving also takes out what is under it. Drag it or its corner to change it" : "Blurred (what is under it stays in the file). Drag it or its corner to change it");

        void Recapture(BlurItem it)
        {
            if (it.Page >= 0 && it.Page < _pages.Count) it.Pixels = BlurredPart(_pages[it.Page], it.Box, it.Style) ?? it.Pixels;
        }
    }

    /// <summary>The part of the page picture under a box, blurred or made into blocks (null when the page isn't drawn).</summary>
    private static BitmapSource? BlurredPart(PageView pv, Rect box, int style)
    {
        if (pv.Picture.Source is not BitmapSource bmp || pv.Overlay.Width <= 0 || pv.Overlay.Height <= 0) return null;
        double kx = bmp.PixelWidth / pv.Overlay.Width, ky = bmp.PixelHeight / pv.Overlay.Height;
        int x = Math.Clamp((int)Math.Floor(box.X * kx), 0, bmp.PixelWidth - 1), y = Math.Clamp((int)Math.Floor(box.Y * ky), 0, bmp.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Ceiling(box.Width * kx), 1, bmp.PixelWidth - x), h = Math.Clamp((int)Math.Ceiling(box.Height * ky), 1, bmp.PixelHeight - y);
        var crop = new FormatConvertedBitmap(new CroppedBitmap(bmp, new Int32Rect(x, y, w, h)), PixelFormats.Bgra32, null, 0);
        var data = new byte[w * h * 4];
        crop.CopyPixels(data, w * 4, 0);
        int small = Math.Min(w, h);
        if (style == 2) Pixelate(data, w, h, Math.Max(6, (int)Math.Round(small / 5.0)));
        else
        {
            int radius = Math.Max(2, (int)Math.Round(small * (style == 0 ? 0.05 : 0.14)));
            for (int pass = 0; pass < 3; pass++) BoxBlur(data, w, h, radius);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, data, w * 4);
        result.Freeze();
        return result;
    }

    private static void Pixelate(byte[] data, int w, int h, int block)
    {
        for (int by = 0; by < h; by += block)
            for (int bx = 0; bx < w; bx += block)
            {
                int x1 = Math.Min(w, bx + block), y1 = Math.Min(h, by + block), n = (x1 - bx) * (y1 - by);
                long b = 0, g = 0, r = 0;
                for (int y = by; y < y1; y++) for (int x = bx; x < x1; x++) { int i = (y * w + x) * 4; b += data[i]; g += data[i + 1]; r += data[i + 2]; }
                byte ab = (byte)(b / n), ag = (byte)(g / n), ar = (byte)(r / n);
                for (int y = by; y < y1; y++) for (int x = bx; x < x1; x++) { int i = (y * w + x) * 4; data[i] = ab; data[i + 1] = ag; data[i + 2] = ar; data[i + 3] = 255; }
            }
    }

    /// <summary>One pass of a box blur (across, then down) with a running sum; the edges repeat their last pixel.</summary>
    private static void BoxBlur(byte[] data, int w, int h, int radius)
    {
        var temp = new byte[data.Length];
        int size = radius * 2 + 1;
        for (int y = 0; y < h; y++)
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++) sum += data[(y * w + Math.Clamp(k, 0, w - 1)) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    temp[(y * w + x) * 4 + c] = (byte)(sum / size);
                    sum += data[(y * w + Math.Min(w - 1, x + radius + 1)) * 4 + c] - data[(y * w + Math.Max(0, x - radius)) * 4 + c];
                }
            }
        for (int x = 0; x < w; x++)
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++) sum += temp[(Math.Clamp(k, 0, h - 1) * w + x) * 4 + c];
                for (int y = 0; y < h; y++)
                {
                    data[(y * w + x) * 4 + c] = (byte)(sum / size);
                    sum += temp[(Math.Min(h - 1, y + radius + 1) * w + x) * 4 + c] - temp[(Math.Max(0, y - radius) * w + x) * 4 + c];
                }
            }
        for (int i = 3; i < data.Length; i += 4) data[i] = 255;
    }

    private UIElement BlurButton()
    {
        var b = new Button { Content = BarLabel("Blur ▾"), Template = BarButtonTemplate(), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "How strong the blur is, and whether saving also removes what is under it", Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfBlurOptions");
        b.Click += (_, _) => ShowBlurMenu(b);
        _blurButton = b;
        return b;
    }

    private void ShowBlurMenu(FrameworkElement anchor)
    {
        var item = _selected as BlurItem;
        int style = item?.Style ?? _blurStyle;
        bool remove = item?.RemoveUnder ?? _blurRemove;
        var menu = new ContextMenu();
        void Entry(string text, bool on, Action action, string id)
        {
            var m = new MenuItem { Header = text, IsCheckable = true, IsChecked = on };
            System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) => action();
            menu.Items.Add(m);
        }
        Entry("Soft blur (you can still tell what it is)", style == 0, () => SetBlur(0, null), "PdfBlurSoft");
        Entry("Strong blur", style == 1, () => SetBlur(1, null), "PdfBlurStrong");
        Entry("Blocks (pixelated)", style == 2, () => SetBlur(2, null), "PdfBlurBlocks");
        menu.Items.Add(new Separator());
        Entry("Saving also removes what is under it (safer)", remove, () => SetBlur(null, !remove), "PdfBlurRemove");
        Themed(menu);
        menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void SetBlur(int? style, bool? remove)
    {
        if (style is int s) _blurStyle = s;
        if (remove is bool r) _blurRemove = r;
        if (_selected is BlurItem item)
        {
            Snapshot();
            if (style is int s2) { item.Style = s2; if (item.Page < _pages.Count) item.Pixels = BlurredPart(_pages[item.Page], item.Box, s2) ?? item.Pixels; }
            if (remove is bool r2) item.RemoveUnder = r2;
            _dirty = true; UpdateTitle();
            RenderItems(item.Page);
        }
        UpdateProperties();
    }
}
