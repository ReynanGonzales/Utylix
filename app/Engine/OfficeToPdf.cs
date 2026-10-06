using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace IdmClone.Engine;

public enum OfficeKind { Word, Excel, PowerPoint }

/// <summary>
/// "Convert to PDF" for Word, Excel and PowerPoint files, done by the Microsoft Office that is installed on this PC (Utylix has no Office engine of
/// its own, so without Office there is nothing to convert with, and the right-click entry is not shown). Word documents, spreadsheets and slides
/// all come out the way Office itself would print them. The PDF is saved next to the original; an existing PDF is never replaced.
/// </summary>
public static class OfficeToPdf
{
    private static readonly string[] WordExt = { "doc", "docx", "docm", "dot", "dotx", "rtf", "odt" };
    private static readonly string[] ExcelExt = { "xls", "xlsx", "xlsm", "xlsb", "ods" };
    private static readonly string[] PowerPointExt = { "ppt", "pptx", "pptm", "pps", "ppsx", "odp" };

    public static IEnumerable<string> Extensions(OfficeKind kind) => kind switch { OfficeKind.Word => WordExt, OfficeKind.Excel => ExcelExt, _ => PowerPointExt };
    public static IEnumerable<string> AllExtensions => WordExt.Concat(ExcelExt).Concat(PowerPointExt);

    public static OfficeKind? KindOf(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return WordExt.Contains(ext) ? OfficeKind.Word : ExcelExt.Contains(ext) ? OfficeKind.Excel : PowerPointExt.Contains(ext) ? OfficeKind.PowerPoint : null;
    }

    public static bool IsOffice(string path) => KindOf(path) != null;

    private static string ProgId(OfficeKind kind) => kind switch { OfficeKind.Word => "Word.Application", OfficeKind.Excel => "Excel.Application", _ => "PowerPoint.Application" };
    private static string ProcessName(OfficeKind kind) => kind switch { OfficeKind.Word => "WINWORD", OfficeKind.Excel => "EXCEL", _ => "POWERPNT" };
    public static string ProgramName(OfficeKind kind) => kind switch { OfficeKind.Word => "Microsoft Word", OfficeKind.Excel => "Microsoft Excel", _ => "Microsoft PowerPoint" };

    /// <summary>Is the Office program that reads this kind of file installed?</summary>
    public static bool IsInstalled(OfficeKind kind)
    {
        try { return Type.GetTypeFromProgID(ProgId(kind)) != null; }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException) { return false; }
    }

    /// <summary>Where the PDF goes: next to the file, "name.pdf", or "name (2).pdf" when that is already taken.</summary>
    public static string OutputFor(string input)
    {
        string dir = Path.GetDirectoryName(input) ?? "", stem = Path.GetFileNameWithoutExtension(input);
        string output = Path.Combine(dir, stem + ".pdf");
        for (int n = 2; File.Exists(output) && n < 1000; n++) output = Path.Combine(dir, $"{stem} ({n}).pdf");
        return output;
    }

    /// <summary>Converts one file (blocks until Office is done; call it on an STA thread). Returns the PDF's path.</summary>
    public static string Convert(string input)
    {
        var kind = KindOf(input) ?? throw new IOException("This kind of file can't be converted to PDF.");
        var type = Type.GetTypeFromProgID(ProgId(kind));
        if (type == null) throw new IOException($"{ProgramName(kind)} isn't installed on this PC. Utylix uses the Microsoft Office on the PC to turn these files into PDF.");
        string output = OutputFor(input);
        bool wasRunning = Process.GetProcessesByName(ProcessName(kind)).Length > 0;      // (the person's own Word / Excel is never hidden or closed)
        dynamic? app = null;
        try
        {
            app = Activator.CreateInstance(type) ?? throw new IOException($"{ProgramName(kind)} could not be started.");
            switch (kind)
            {
                case OfficeKind.Word:
                {
                    if (!wasRunning) { app.Visible = false; app.DisplayAlerts = 0; }
                    dynamic doc = app.Documents.Open(input, false, true, false);                 // (FileName, ConfirmConversions, ReadOnly, AddToRecentFiles)
                    try { doc.ExportAsFixedFormat(output, 17); }                                  // 17 = PDF
                    finally { doc.Close(false); }
                    break;
                }
                case OfficeKind.Excel:
                {
                    if (!wasRunning) { app.Visible = false; app.DisplayAlerts = false; }
                    dynamic book = app.Workbooks.Open(input, 0, true);                           // (FileName, UpdateLinks = never, ReadOnly)
                    try { book.ExportAsFixedFormat(0, output); }                                  // 0 = PDF
                    finally { book.Close(false); }
                    break;
                }
                default:
                {
                    dynamic slides = app.Presentations.Open(input, -1, 0, 0);                    // (FileName, ReadOnly, Untitled, WithWindow = none)
                    try { slides.SaveAs(output, 32); }                                            // 32 = PDF
                    finally { slides.Close(); }
                    break;
                }
            }
            if (!File.Exists(output)) throw new IOException($"{ProgramName(kind)} did not make the PDF.");
            return output;
        }
        catch (COMException e)
        {
            throw new IOException($"{ProgramName(kind)} could not convert this file: {e.Message.Trim()}");
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException e)
        {
            throw new IOException($"{ProgramName(kind)} did not understand the request: {e.Message}");
        }
        finally
        {
            if (app != null)
            {
                try { if (!wasRunning) app.Quit(); } catch (Exception) { /* it is going away anyway */ }
                try { Marshal.ReleaseComObject(app); } catch (Exception) { }
            }
        }
    }
}

/// <summary>
/// The files waiting to be converted, one at a time (Explorer starts one Utylix per selected file; Word and friends are not made to be started many
/// times at once). Each result is shown as a message by the clock.
/// </summary>
internal static class OfficePdfQueue
{
    private static readonly BlockingCollection<string> Waiting = new();
    private static Thread? _worker;
    private static int _busy;

    /// <summary>Raised on the worker thread when everything that was handed in has been done.</summary>
    public static event Action? Idle;

    public static bool IsBusy => Volatile.Read(ref _busy) > 0;

    public static void Enqueue(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            Interlocked.Increment(ref _busy);
            Waiting.Add(f);
        }
        if (_worker != null) return;
        _worker = new Thread(Work) { IsBackground = true, Name = "Utylix Office to PDF" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    private static void Work()
    {
        foreach (var file in Waiting.GetConsumingEnumerable())
        {
            string name = Path.GetFileName(file);
            try
            {
                string pdf = OfficeToPdf.Convert(file);
                App.Current.Dispatcher.BeginInvoke(() => App.Notify("Converted to PDF", Path.GetFileName(pdf), pdf));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                App.Current.Dispatcher.BeginInvoke(() => App.Notify("Couldn't convert " + name, e.Message, null));
            }
            if (Interlocked.Decrement(ref _busy) == 0) Idle?.Invoke();
        }
    }
}
