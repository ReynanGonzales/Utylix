using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Moving, resizing or deleting a thing that is already in the PDF writes the page again (PDFium makes its content from its own picture of the objects). Pages that use see-through
/// effects (soft shadows, gradient masks, blends) lose them that way: parts of the page come out solid black. Before the change a small picture of the page is taken; after it, the
/// page must look the same everywhere except where the thing was and is. If not, the change is taken back and the person is told, instead of a page that has silently changed.
/// </summary>
public sealed partial class PdfWindow
{
    private sealed class PageLook
    {
        public byte[] Pixels = Array.Empty<byte>();
        public int Width, Height;
        public Size Page;
    }

    private PageLook? LookOf(int page)
    {
        try
        {
            if (_pdf == null) return null;
            var size = _pdf.PageSize(page);
            if (size.Width < 1 || size.Height < 1) return null;
            int w = 700, h = Math.Clamp((int)Math.Round(w * size.Height / size.Width), 50, 2400);
            var picture = _pdf.Render(page, w, h, 0, forScreen: false);
            var converted = picture.Format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            var look = new PageLook { Width = w, Height = h, Page = size, Pixels = new byte[w * h * 4] };
            converted.CopyPixels(look.Pixels, w * 4, 0);
            return look;
        }
        catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException or OutOfMemoryException or ArgumentException) { return null; }
    }

    /// <summary>True when the page looks the same as <paramref name="before"/> outside <paramref name="changed"/> (points, as shown).</summary>
    private bool LooksTheSame(int page, PageLook? before, Rect changed)
    {
        if (before == null) return true;
        var after = LookOf(page);
        if (after == null || after.Width != before.Width || after.Height != before.Height) return true;
        double sx = before.Width / before.Page.Width, sy = before.Height / before.Page.Height;
        changed.Inflate(8, 8);
        int x0 = (int)Math.Floor(changed.Left * sx), x1 = (int)Math.Ceiling(changed.Right * sx), y0 = (int)Math.Floor(changed.Top * sy), y1 = (int)Math.Ceiling(changed.Bottom * sy);
        long outside = 0, different = 0;
        for (int y = 0; y < before.Height; y++)
            for (int x = 0; x < before.Width; x++)
            {
                if (x >= x0 && x <= x1 && y >= y0 && y <= y1) continue;
                outside++;
                int i = (y * before.Width + x) * 4;
                int d = Math.Abs(before.Pixels[i] - after.Pixels[i]) + Math.Abs(before.Pixels[i + 1] - after.Pixels[i + 1]) + Math.Abs(before.Pixels[i + 2] - after.Pixels[i + 2]);
                if (d > 150) different++;
            }
        return outside == 0 || different < outside * 0.004;
    }

    /// <summary>The change just made to the page changed more than the thing itself: it is taken back.</summary>
    private void TakeBackBrokenChange()
    {
        if (PageHistory(redo: false)) _pageRedo.Clear();
        Select(null);
    }

    /// <summary>
    /// After a change was taken back: offers the safe way. The part of the page is cut out as a picture and put where it should go (<paramref name="to"/>; null = taken away),
    /// and the old place is covered with the colour of the page. The page itself is not rewritten, so nothing turns black; the part is a picture afterwards, not drawn parts.
    /// </summary>
    private void OfferPictureMove(int page, Rect from, Rect? to)
    {
        string what = to == null ? "cover it with the colour of the page" : "move it as a picture of that part";
        var answer = UMessage.Ask(this, "This page uses see-through effects (soft shadows or gradients). Writing the change into the page would turn parts of it black, so nothing was changed.\n\n" +
                                        $"Instead, Utylix can {what}: the page keeps all its colours, but the part is a picture from then on (not drawn lines and text), and what was under it stays hidden under the cover.",
                                  "Can't change this page the usual way", MessageBoxImage.Information, MessageBoxResult.Yes, MessageBoxResult.No,
                                  (to == null ? "Cover it" : "Move it as a picture", MessageBoxResult.Yes), ("Leave the page as it is", MessageBoxResult.No));
        if (answer != MessageBoxResult.Yes || _pdf == null || page >= _pages.Count) return;
        var pv = _pages[page];
        BitmapSource? crop = null;
        if (to != null)
        {
            try
            {
                var size = _pdf.PageSize(page);
                double scale = Math.Min(3.0, 4200 / Math.Max(size.Width, size.Height));
                var whole = _pdf.Render(page, (int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale), 0, forScreen: false);
                int x = Math.Clamp((int)Math.Floor(from.X * scale), 0, whole.PixelWidth - 1), y = Math.Clamp((int)Math.Floor(from.Y * scale), 0, whole.PixelHeight - 1);
                int w = Math.Clamp((int)Math.Ceiling(from.Width * scale), 1, whole.PixelWidth - x), h = Math.Clamp((int)Math.Ceiling(from.Height * scale), 1, whole.PixelHeight - y);
                crop = new FormatConvertedBitmap(new CroppedBitmap(whole, new Int32Rect(x, y, w, h)), PixelFormats.Bgra32, null, 0);
                crop.Freeze();
            }
            catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException or OutOfMemoryException or ArgumentException) { Toast("Couldn't make the picture: " + e.Message); return; }
        }
        Snapshot();
        _items.Add(new ShapeItem { Page = page, Kind = ShapeKind.WhiteOut, A = from.TopLeft, B = from.BottomRight, Color = CoverColour(pv, from), Width = 1 });
        if (to != null && crop != null)
        {
            var image = new ImageItem { Page = page, Box = to.Value, Pixels = crop };
            _items.Add(image);
            Select(image);
        }
        RenderItems(page);
        UpdateEditButtons();
        Toast(to == null ? "Covered. Undo takes the cover away" : "Moved as a picture. Undo puts it back");
    }
}
