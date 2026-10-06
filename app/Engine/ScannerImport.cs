using System;
using System.IO;
using System.Runtime.InteropServices;

namespace IdmClone.Engine;

/// <summary>
/// Takes a page from a scanner (or any camera / device Windows lists as an image source) with Windows' own scanning component (WIA, the same "Scan" window other programs use).
/// Nothing is installed: if Windows has no scanner for it, it says so.
/// </summary>
public static class ScannerImport
{
    private const string Jpeg = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    /// <summary>Opens Windows' scan window; returns the picture it scanned (a file in <paramref name="folder"/>), or null (cancelled, or <paramref name="error"/> says why).</summary>
    public static string? ScanOne(string folder, out string error)
    {
        error = "";
        try
        {
            var type = Type.GetTypeFromProgID("WIA.CommonDialog");
            if (type == null) { error = "Windows' scanning component isn't on this PC."; return null; }
            dynamic dialog = Activator.CreateInstance(type)!;
            // (a scanner, colour, best quality, JPEG if the scanner can; Windows' own window for choosing the scanner and the settings)
            dynamic? image = dialog.ShowAcquireImage(1, 1, 0x20000, Jpeg, false, true, false);
            if (image == null) return null;
            string ext = "";
            try { ext = ((string)image.FileExtension).Trim('.'); } catch (Exception) { /* use the default below */ }
            if (ext.Length == 0) ext = "jpg";
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"scan-{DateTime.Now:yyyyMMdd-HHmmss-fff}.{ext}");
            image.SaveFile(path);
            return File.Exists(path) ? path : null;
        }
        catch (COMException e)
        {
            uint code = (uint)e.HResult;
            error = code switch
            {
                0x80210015 => "No scanner was found. Connect it, switch it on, and try again (Windows must list it under Printers & scanners).",
                0x80210005 => "The scanner is busy or switched off.",
                0x80210006 => "The scanner is busy: wait for it, then try again.",
                0x80210003 => "The scanner has no paper (check the feeder or the lid).",
                0x80210001 => "The scanner reported an error: " + e.Message.Trim(),
                _ => "The scanner couldn't be used: " + e.Message.Trim(),
            };
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            error = "The scan couldn't be saved: " + e.Message;
            return null;
        }
    }
}
