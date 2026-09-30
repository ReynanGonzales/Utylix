using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The archive commands in Explorer's right-click menu that need no window: "Extract here", "Extract to folder" and
/// "Add to ZIP". Explorer starts one process per selected file, so requests arrive one by one: several "Add to ZIP" that
/// arrive together become ONE zip (named after the file, or after the folder when there are several).
/// </summary>
public sealed class QuickArchive
{
    private readonly BlockingCollection<(string Op, string File, string? Dest)> _queue = new();
    private readonly Action<string, string, bool, string?> _report;    // title, text, isError, file/folder to show when the notice is clicked
    private readonly Action<string> _needsPassword;
    private readonly Thread _worker;

    public QuickArchive(Action<string, string, bool, string?> report, Action<string> needsPassword)
    {
        _report = report;
        _needsPassword = needsPassword;
        _worker = new Thread(Run) { IsBackground = true, Name = "quick-archive" };
        _worker.Start();
    }

    /// <param name="dest">For "extract-to-folder": exactly where the files go.</param>
    public void Enqueue(string op, IEnumerable<string> files, string? dest = null)
    {
        foreach (var f in files) _queue.Add((op, f, dest));
    }

    private void Run()
    {
        var zipItems = new List<string>();
        while (true)
        {
            (string Op, string File, string? Dest) item;
            if (!_queue.TryTake(out item, zipItems.Count > 0 ? 1200 : Timeout.Infinite))    // quiet for a moment: the selection is complete
            {
                MakeZip(zipItems);
                zipItems.Clear();
                continue;
            }
            if (item.Op == "zip-add") zipItems.Add(item.File);
            else Extract(item.Op, item.File, item.Dest);
        }
    }

    private void Extract(string op, string archive, string? chosenDest)
    {
        try
        {
            string dir = Path.GetDirectoryName(archive)!;
            string dest = chosenDest ?? (op == "extract-here" ? dir : Path.Combine(dir, Util.Sanitize(ArchiveService.BaseName(archive))));
            var result = ArchiveService.ExtractAsync(archive, dest, null, null, OverwriteMode.Rename, null, CancellationToken.None).GetAwaiter().GetResult();
            _report("Extracted", $"{result.Files:N0} file{(result.Files == 1 ? "" : "s")} from {Path.GetFileName(archive)}", false, dest);
        }
        catch (ArchivePasswordException)
        {
            _report("Password needed", Path.GetFileName(archive) + " is protected: opening it in Utylix.", false, null);
            _needsPassword(archive);
        }
        catch (Exception e)            // whatever went wrong, the worker thread must live on (and the app with it)
        {
            _report("Couldn't extract " + Path.GetFileName(archive), e.Message, true, null);
        }
    }

    private void MakeZip(List<string> items)
    {
        if (items.Count == 0) return;
        try
        {
            string first = items[0].TrimEnd(Path.DirectorySeparatorChar);
            string dir = Path.GetDirectoryName(first) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string name = items.Count == 1
                ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
                : Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (name.Length == 0) name = "Archive";
            string output = ArchiveService.UniquePath(dir, name, ".zip");
            ArchiveService.CreateAsync(items, output, ArchiveFormat.Zip, CompressionLevel.Optimal, null, CancellationToken.None).GetAwaiter().GetResult();
            _report("ZIP created", $"{Path.GetFileName(output)} ({Format.Bytes(new FileInfo(output).Length)})", false, output);
        }
        catch (Exception e)
        {
            _report("Couldn't create the ZIP", e.Message, true, null);
        }
    }
}
