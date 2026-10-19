using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Auto-save for safety: while a PDF has changes that are not saved, a copy with those changes in it is kept in the data folder (<see cref="PdfRecovery"/>) every minute.
/// If Utylix or the PC stops before the person saves, opening that PDF again offers the changes back. The copy goes away when the file is saved or closed.
/// </summary>
public sealed partial class PdfWindow
{
    private DispatcherTimer? _recoveryTimer;
    private bool _recoveryBusy;

    /// <summary>Seconds between safety copies (a test can shorten it with UTYLIX_RECOVERY_SECONDS).</summary>
    private static int RecoverySeconds => int.TryParse(Environment.GetEnvironmentVariable("UTYLIX_RECOVERY_SECONDS"), out int s) && s > 0 ? s : 60;

    private void StartRecovery()
    {
        _recoveryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(RecoverySeconds) };
        _recoveryTimer.Tick += (_, _) => SaveRecoveryCopy();
        _recoveryTimer.Start();
    }

    private void SaveRecoveryCopy()
    {
        if (_recoveryBusy || !_dirty || _pdf == null || _path == null || _pdf.IsProtected || _pdf.Length > 80_000_000) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed || _drag != DragMode.None) return;           // (not in the middle of dragging something)
        _recoveryBusy = true;
        try
        {
            // the document as it is, with what was added on the pages written into a COPY of it (the open document and its undo steps are not touched)
            var marks = _items.SelectMany(i => i.Marks()).Where(m => m is not (PdfRedactMark or PdfFieldMark)).ToList();
            byte[] bytes = _pdf.SaveToBytes();
            if (marks.Count > 0)
            {
                using var copy = PdfFile.OpenBytes(bytes, _path);
                PdfMarkWriter.Apply(copy, marks);
                bytes = copy.SaveToBytes();
            }
            string path = _path;
            _ = Task.Run(() =>
            {
                try { PdfRecovery.Write(path, bytes); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }).ContinueWith(_ => _recoveryBusy = false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OutOfMemoryException or ObjectDisposedException or PdfSharp.PdfSharpException)
        {
            _recoveryBusy = false;                                   // (a safety copy that could not be made is not worth a message)
        }
    }

    /// <summary>After a file was opened: when changes of it were not saved last time, offers them back.</summary>
    private void OfferRecovery(string path)
    {
        var found = PdfRecovery.Find(path);
        if (found == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_pdf == null || !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return;
            string when = found.SavedUtc.ToLocalTime().ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
            var answer = UMessage.Ask(this,
                $"Utylix kept a copy of changes to {Path.GetFileName(path)} that were not saved ({when}). That usually means the program or the PC stopped before you saved.\n\nBring those changes back? Undo then goes back to the file as it is on disk.",
                "Unsaved changes found", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel,
                ("Bring them back", MessageBoxResult.Yes), ("Throw them away", MessageBoxResult.No), ("Ask me later", MessageBoxResult.Cancel));
            if (answer == MessageBoxResult.No) { PdfRecovery.Delete(path); return; }
            if (answer != MessageBoxResult.Yes) return;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(found.File); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast("Couldn't read the saved copy: " + e.Message); return; }
            PageOp(p => p.Restore(bytes), new[] { Math.Clamp(_current, 0, Math.Max(0, (_pdf?.PageCount ?? 1) - 1)) },
                   "Your unsaved changes are back. Save to keep them; Undo goes back to the file as it was", keepView: true);
        }, DispatcherPriority.ApplicationIdle);
    }
}
