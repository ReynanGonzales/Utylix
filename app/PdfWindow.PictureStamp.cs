using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Picture stamps: your own logo, seal or a scan of a signature, kept in the Stamp menu and placed with one click (then moved and resized like any picture).</summary>
public sealed partial class PdfWindow
{
    private string? _stampPicture;                 // when set, the Stamp tool places this picture instead of a word stamp

    private void ChoosePictureStamp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "A picture to use as a stamp", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        try { PdfCombiner.LoadPicture(File.ReadAllBytes(dlg.FileName), 200); }                // (is it a picture Windows can read?)
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Toast("Couldn't read that picture: " + e.Message);
            return;
        }
        var settings = PdfStampSettings.Current;
        settings.Pictures.RemoveAll(p => string.Equals(p, dlg.FileName, StringComparison.OrdinalIgnoreCase));
        settings.Pictures.Insert(0, dlg.FileName);
        if (settings.Pictures.Count > 6) settings.Pictures.RemoveRange(6, settings.Pictures.Count - 6);
        settings.Save();
        _stampPicture = dlg.FileName;
        _toolButtons[EditTool.Stamp].ToolTip = "Click on the page to put the picture " + Path.GetFileName(dlg.FileName) + " (click this button again to choose another)";
        PickStampTool();
        Toast("Click on the page where the picture stamp should go");
    }

    /// <summary>Puts the chosen picture stamp on the page (about 4 cm wide, centred on the click). False when it can't be read (the word stamp is used then).</summary>
    private bool PlacePictureStamp(PageView pv, Point at)
    {
        if (_stampPicture == null) return false;
        BitmapSource picture; byte[]? jpeg;
        try
        {
            var loaded = PdfCombiner.LoadPicture(File.ReadAllBytes(_stampPicture), 1600);
            jpeg = loaded.Jpeg;
            picture = new FormatConvertedBitmap(loaded.Pixels, PixelFormats.Bgra32, null, 0);
            picture.Freeze();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Toast("The picture stamp can't be read any more: " + e.Message);
            _stampPicture = null;
            return false;
        }
        double w = Math.Min(115, pv.Overlay.Width * 0.4), h = w * picture.PixelHeight / picture.PixelWidth;
        if (h > pv.Overlay.Height * 0.4) { h = pv.Overlay.Height * 0.4; w = h * picture.PixelWidth / picture.PixelHeight; }
        var box = new Rect(Math.Clamp(at.X - w / 2, 0, Math.Max(0, pv.Overlay.Width - w)), Math.Clamp(at.Y - h / 2, 0, Math.Max(0, pv.Overlay.Height - h)), w, h);
        Add(new ImageItem { Page = pv.Index, Box = box, Pixels = picture, Jpeg = jpeg }, select: true);
        return true;
    }
}
