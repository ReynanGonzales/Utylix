using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Right-click -> Convert to JPG / MP4 / MP3 ...: converts in the background, next to the original, and reports once
/// ("3 files converted to MP3") when the queue is empty - Explorer starts one process per selected file, so the requests
/// arrive one by one. Pictures use Windows' own picture code; videos and music use ffmpeg.
/// </summary>
public sealed class QuickConverter
{
    private readonly BlockingCollection<(string File, string Target)> _queue = new();
    private readonly Action<string, string, bool, string?> _report;   // title, text, isError, file to show when the notice is clicked
    private readonly Thread _worker;

    public QuickConverter(Action<string, string, bool, string?> report)
    {
        _report = report;
        _worker = new Thread(Run) { IsBackground = true, Name = "quick-convert" };
        _worker.SetApartmentState(ApartmentState.STA);       // Windows' imaging code wants an STA thread
        _worker.Start();
    }

    public void Enqueue(IEnumerable<string> files, string target)
    {
        foreach (var f in files) _queue.Add((f, target));
    }

    private void Run()
    {
        int ok = 0;
        var failed = new List<string>();
        string last = "jpg";
        string? lastOutput = null;
        bool announced = false;
        foreach (var (file, target) in _queue.GetConsumingEnumerable())
        {
            last = target;
            try
            {
                var kind = MediaConverter.KindOf(file);
                if (kind == MediaKind.Image && MediaConverter.ImageTargets.Contains(target) && ImageConverter.TryParseFormat(target, out var format))
                {
                    lastOutput = ImageConverter.Convert(file, new ConvertOptions(format, Quality: format == ImgFormat.Jpg ? 92 : 90));
                }
                else if (kind != null)
                {
                    if (!Tools.HasFfmpeg) throw new IOException("Video and music need ffmpeg. Open Utylix > Multi Convert and click Install ffmpeg.");
                    if (!announced)                              // a video can take minutes: say that it has started
                    {
                        announced = true;
                        _report("Converting…", Path.GetFileName(file) + "\nYou will get a notice when it is done.", false, null);
                    }
                    lastOutput = MediaConverter.ConvertAsync(file, target, new MediaOptions(), null, null, CancellationToken.None).GetAwaiter().GetResult();
                }
                else throw new IOException("Not a file type this converter reads.");
                ok++;
            }
            catch (Exception e)            // any failure: report it, never let the worker thread die
            {
                failed.Add($"{Path.GetFileName(file)}: {e.Message}");
            }

            if (_queue.Count > 0) continue;
            Thread.Sleep(1200);                              // more files of the same selection may still be arriving
            if (_queue.Count > 0) continue;

            string label = MediaConverter.Label(last);
            if (failed.Count == 0)
                _report("Converted", ok == 1 ? $"1 file converted to {label}." : $"{ok} files converted to {label}.", false, lastOutput);
            else
                _report(ok > 0 ? $"Converted {ok}, {failed.Count} failed" : "Couldn't convert",
                        string.Join("\n", failed.Take(3)) + (failed.Count > 3 ? $"\n...and {failed.Count - 3} more" : ""), true, null);
            ok = 0;
            failed.Clear();
            announced = false;
        }
    }
}
