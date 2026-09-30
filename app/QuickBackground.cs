using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Right-click a picture -> Remove background: works in the background, saves "name (no background).png" next to the original
/// and reports once when the queue is empty. Explorer starts one process per selected file, so the requests arrive one by one.
/// The first time, it asks before downloading the AI model.
/// </summary>
public sealed class QuickBackground
{
    private readonly BlockingCollection<string> _queue = new();
    private readonly Action<string, string, bool, string?> _report;     // title, text, isError, file to show when the notice is clicked
    private readonly Func<bool> _ensureModel;                           // asks (on the UI thread) and downloads the model; false = not now
    private readonly Thread _worker;

    public QuickBackground(Action<string, string, bool, string?> report, Func<bool> ensureModel)
    {
        _report = report;
        _ensureModel = ensureModel;
        _worker = new Thread(Run) { IsBackground = true, Name = "quick-background" };
        _worker.SetApartmentState(ApartmentState.STA);                 // Windows' imaging code wants an STA thread
        _worker.Start();
    }

    public void Enqueue(IEnumerable<string> files)
    {
        foreach (var f in files) _queue.Add(f);
    }

    private void Run()
    {
        int ok = 0;
        var failed = new List<string>();
        string? lastOutput = null;
        bool announced = false, declined = false;
        foreach (var file in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (declined) throw new IOException("The AI model was not downloaded.");
                if (!BackgroundRemover.HasModel && !_ensureModel()) { declined = true; throw new IOException("The AI model was not downloaded."); }
                if (!announced)
                {
                    announced = true;
                    _report("Removing the background…", Path.GetFileName(file) + "\nThis takes a few seconds. You will get a notice when it is done.", false, null);
                }
                lastOutput = BackgroundRemover.Remove(file);
                ok++;
            }
            catch (Exception e)            // any failure: report it, never let the worker thread die
            {
                failed.Add($"{Path.GetFileName(file)}: {e.Message}");
            }

            if (_queue.Count > 0) continue;
            Thread.Sleep(1200);                                         // more files of the same selection may still be arriving
            if (_queue.Count > 0) continue;

            if (failed.Count == 0)
                _report("Background removed", ok == 1 ? Path.GetFileName(lastOutput) ?? "" : $"{ok} pictures saved as PNG with a transparent background.", false, lastOutput);
            else
                _report(ok > 0 ? $"Done {ok}, {failed.Count} failed" : "Couldn't remove the background",
                        string.Join("\n", failed.Take(3)) + (failed.Count > 3 ? $"\n...and {failed.Count - 3} more" : ""), true, null);
            ok = 0;
            failed.Clear();
            announced = false;
            declined = false;
        }
    }
}
