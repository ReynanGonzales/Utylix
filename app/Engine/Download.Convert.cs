using System;
using System.IO;
using System.Threading;

namespace IdmClone.Engine;

/// <summary>"Convert to..." chosen in the New download window: once the picture has arrived, turn it into another format.</summary>
public sealed partial class Download
{
    private string? _afterDone;          // "open" or "folder" (null = just notify): chosen in the New download window

    /// <summary>What to do when this download has finished (the tray notice always appears).</summary>
    public string? AfterDone { get { lock (_lock) return _afterDone; } }

    public void SetAfterDone(string? what)
    {
        lock (_lock) _afterDone = what is "open" or "folder" ? what : null;
    }

    private string? _convertTo;          // "ico", "jpg", ... (null = keep the file as it came)
    private bool _keepOriginal;

    public void SetConvert(string? format, bool keepOriginal)
    {
        lock (_lock)
        {
            _convertTo = string.IsNullOrEmpty(format) ? null : format;
            _keepOriginal = keepOriginal;
        }
    }

    /// <summary>Runs after the file is saved and before the download counts as complete. A failure never loses the download.</summary>
    private void ConvertIfWanted()
    {
        string? format; bool keep; string src;
        lock (_lock)
        {
            format = _convertTo;
            keep = _keepOriginal;
            if (format == null || _status != DlStatus.Completed || FileName == null) return;
            src = FinalPath;
        }
        string name = Path.GetFileName(src);
        try
        {
            if (!ImageConverter.TryParseFormat(format, out var f)) throw new IOException("Unknown picture format.");
            string? output = null; Exception? error = null;
            var worker = new Thread(() =>
            {
                try { output = ImageConverter.Convert(src, new ConvertOptions(f, f == ImgFormat.Jpg ? 92 : 90, 0, Path.GetDirectoryName(src))); }
                catch (Exception e) { error = e; }
            }) { IsBackground = true, Name = "convert-download" };
            worker.SetApartmentState(ApartmentState.STA);            // Windows' imaging code needs an STA thread
            worker.Start();
            worker.Join();
            if (error != null || output == null) throw new IOException(error?.Message ?? "Conversion failed.");

            Util.MarkOfTheWeb(output, Url, Referer);
            lock (_lock)
            {
                FileName = Path.GetFileName(output);
                _size = new FileInfo(output).Length;
                _convertTo = null;
            }
            if (!keep) TryDelete(src);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                      or System.Runtime.InteropServices.COMException)
        {
            lock (_lock) _convertTo = null;                          // the original stays as downloaded
            _mgr.RaiseNotice("Couldn't convert " + name, e.Message + " The original picture was kept.", true);
        }
    }
}
