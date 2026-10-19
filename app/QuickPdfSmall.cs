using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Right-click a PDF > Utylix Editor > "Small copy for email / chat": makes "name (small).pdf" next to the original, as small as the chosen size needs (pictures are made lighter, text stays text),
/// in the background, and reports once when the queue is empty. The original is never changed. Explorer starts one process per selected file, so the requests arrive one by one.
/// </summary>
public sealed class QuickPdfSmall
{
    private readonly BlockingCollection<(string File, long Target)> _queue = new();
    private readonly Action<string, string, bool, string?> _report;     // title, text, isError, file to show when the notice is clicked
    private static int _waiting;

    /// <summary>A copy is being made (or files are waiting): a program that was started only for this must stay until it is done.</summary>
    public static bool IsBusy => Volatile.Read(ref _waiting) > 0;
    public static event Action? Idle;

    public QuickPdfSmall(Action<string, string, bool, string?> report)
    {
        _report = report;
        var worker = new Thread(Run) { IsBackground = true, Name = "quick-pdf-small" };
        worker.SetApartmentState(ApartmentState.STA);                  // (pictures are read and written with Windows' imaging code)
        worker.Start();
    }

    public void Enqueue(IEnumerable<string> files, long targetBytes)
    {
        foreach (var f in files) { Interlocked.Increment(ref _waiting); _queue.Add((f, targetBytes)); }
    }

    private void Run()
    {
        int ok = 0, already = 0;
        var failed = new List<string>();
        var notes = new List<string>();
        string? lastOutput = null;
        bool announced = false;
        foreach (var (file, target) in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (!announced)
                {
                    announced = true;
                    _report("Making a small copy…", Path.GetFileName(file) + "\nThis can take a little while. You will get a notice when it is done.", false, null);
                }
                var result = PdfCompressor.ReduceToSize(file, null, null, target, null, CancellationToken.None);
                if (result.Output == null)
                {
                    if (result.Before <= target) { already++; notes.Add($"{Path.GetFileName(file)} is already {PdfReduceWindow.Bytes(result.Before)}"); }
                    else failed.Add($"{Path.GetFileName(file)}: it can't be made smaller without turning its pages into pictures (use Reduce file size… to choose that)");
                }
                else
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
                    string output = ImageConverter.ReserveName(dir, Path.GetFileNameWithoutExtension(file) + " (small)", "pdf");
                    string temp = output + ".utylix-tmp";
                    File.WriteAllBytes(temp, result.Output);
                    File.Move(temp, output, overwrite: true);
                    lastOutput = output; ok++;
                    notes.Add(result.Fits ? $"{Path.GetFileName(output)}: {PdfReduceWindow.Bytes(result.Before)} → {PdfReduceWindow.Bytes(result.After)}"
                                          : $"{Path.GetFileName(output)}: {PdfReduceWindow.Bytes(result.Before)} → {PdfReduceWindow.Bytes(result.After)} (the closest it could get; not under {PdfReduceWindow.Bytes(target)})");
                }
            }
            catch (PdfPasswordException) { failed.Add($"{Path.GetFileName(file)}: it needs a password (open it in Utylix Editor and use Reduce file size…)"); }
            catch (Exception e)             // any failure: report it, never let the worker thread die
            {
                failed.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
            finally { Interlocked.Decrement(ref _waiting); }

            if (_queue.Count > 0) continue;
            Thread.Sleep(1200);                                         // more files of the same selection may still be arriving
            if (_queue.Count > 0) continue;

            if (failed.Count == 0)
                _report(ok > 0 ? "Small copy ready" : "Nothing to do", string.Join("\n", notes.Take(4)) + (notes.Count > 4 ? $"\n...and {notes.Count - 4} more" : ""), false, lastOutput);
            else
                _report(ok > 0 ? $"Done {ok}, {failed.Count} not made" : "Couldn't make a small copy",
                        string.Join("\n", failed.Take(3)) + (failed.Count > 3 ? $"\n...and {failed.Count - 3} more" : ""), true, null);
            ok = already = 0; failed.Clear(); notes.Clear(); announced = false; lastOutput = null;
            if (!IsBusy) Idle?.Invoke();
        }
    }
}
