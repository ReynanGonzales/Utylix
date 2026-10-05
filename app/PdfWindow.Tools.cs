using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>The bigger tools of the "…" menu of the page tools: OCR, page numbers / watermark, saving as Word or Excel.</summary>
public sealed partial class PdfWindow
{
    // (the menu itself is built in ShowPagesMenu: grouped, with headings)
    private void ExportToOffice()
    {
        if (_pdf != null && _path != null && CommitItems()) new PdfExportDialog(this, _pdf, _path, SelectedPages()).ShowDialog();
    }

    // ---------- page numbers, header / footer, watermark ----------
    /// <param name="focus">"line" (page numbers, header, footer) or "mark" (watermark): which half the dialog starts on; "" = as the dialog starts</param>
    private void AddPageMarks(string focus = "")
    {
        if (_pdf == null || _path == null) return;
        var dialog = new PdfPageMarksDialog(this, _pdf, _path, _current, focus);
        if (dialog.ShowDialog() != true) return;
        var pdf = _pdf;
        string stem = Path.GetFileNameWithoutExtension(_path);
        int first = dialog.FirstPage, last = dialog.LastPage;
        var marks = PdfPageMarks.Build(pdf.PageCount, pdf.PageSize, stem, first, last, dialog.Line, dialog.Mark);
        if (marks.Count == 0) { Toast("Nothing to add (check the pages and the text)"); return; }
        int pages = last - first + 1;
        string what = dialog.Line != null && dialog.Mark != null ? "Text and watermark" : dialog.Mark != null ? "Watermark" : "Text";
        PageOp(p => PdfMarkWriter.Apply(p, marks), new[] { Math.Clamp(_current, 0, pdf.PageCount - 1) },
               $"{what} added to {(pages == 1 ? "page " + (first + 1) : pages == pdf.PageCount ? "all pages" : $"pages {first + 1} to {last + 1}")}. Undo takes it away until you save");
    }

    // ---------- passwords ----------
    /// <summary>A new copy with a password to open it and / or limits (no printing, copying, changing). The open PDF stays as it is.</summary>
    private void AddPassword()
    {
        if (_pdf == null || _path == null) return;
        if (!_pdf.CanEdit && !AskOwnerPassword()) return;                      // (a PDF that is locked against changes: its owner password first)
        if (!CommitItems()) return;
        var dialog = new PdfProtectDialog(this);
        if (dialog.ShowDialog() != true) return;
        MakeProtectedCopy(" (protected)", "Save the PDF with a password as", bytes => PdfSecurity.Protect(bytes, _pdf!.Password, dialog.OpenPassword, dialog.LimitPassword, dialog.Limits),
                          dialog.OpenPassword.Length > 0 ? "now asks for its password to open" : "now has the limits you chose");
    }

    /// <summary>A new copy without any password or limit (it needs the owner password if the PDF limits changes).</summary>
    private void RemovePassword()
    {
        if (_pdf == null || _path == null) return;
        if (!_pdf.IsProtected) { Toast("This PDF has no password or limits"); return; }
        if (!_pdf.CanEdit && !AskOwnerPassword()) return;
        if (!CommitItems()) return;
        var answer = UMessage.Ask(this, "Make a copy of this PDF without any password or limits?\n\nAnyone will be able to open, print, copy and change that copy. This PDF stays as it is.",
            "Remove the password", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel, ("Choose where to save…", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel));
        if (answer != MessageBoxResult.Yes) return;
        MakeProtectedCopy(" (no password)", "Save the PDF without its password as", bytes => PdfSecurity.Unprotect(bytes, _pdf!.Password ?? ""), "is now free of passwords and limits");
    }

    /// <summary>A signed copy (a certificate signature, like Acrobat's "Sign with a certificate"). The open PDF stays as it is.</summary>
    private void SignWithCertificate()
    {
        if (_pdf == null || _path == null) return;
        if (_pdf.IsProtected) { UMessage.Show(this, "A password-protected PDF can't be signed here. Make a copy without its password first (Tools > Remove the password).", "Sign", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!CommitItems()) return;
        var dialog = new PdfSignDialog(this, _pdf.PageCount, _current);
        if (dialog.ShowDialog() != true || dialog.Result == null) return;
        var options = dialog.Result;
        MakeProtectedCopy(" (signed)", "Save the signed PDF as", bytes => PdfSigning.Sign(bytes, options), "is signed by " + PdfSigning.Name(options.Certificate) + ". Don't change it any more, or the signature will say it was changed");
    }

    /// <summary>Tells who signed the open PDF and whether it was changed since.</summary>
    private void CheckSignatures()
    {
        if (_pdf == null || _path == null) return;
        List<PdfSignatureInfo> found;
        byte[] file;
        try { found = _pdf.GetSignatures(); file = File.ReadAllBytes(_path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { Toast("Couldn't check: " + e.Message); return; }
        if (found.Count == 0) { UMessage.Show(this, "This PDF has no digital signature.", "Signatures", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var lines = new List<string>();
        int n = 0;
        foreach (var c in PdfSigning.Check(file, found))
        {
            n++;
            if (found.Count > 1) lines.Add($"Signature {n}");
            lines.Add("Signed by " + c.Signer + (c.When.Length > 0 ? " on " + c.When : ""));
            if (c.Reason.Length > 0) lines.Add("Reason: " + c.Reason);
            if (c.Problem.Length > 0) lines.Add("Couldn't be checked: " + c.Problem);
            else
            {
                lines.Add(!c.SignatureOk ? "✘ The PDF was CHANGED after it was signed (or the signature is damaged)."
                        : c.WholeFile ? "✔ The PDF has not been changed since it was signed."
                        : "✔ What was signed is unchanged, but something was added to the file after signing.");
                lines.Add(c.Trusted ? "✔ Windows trusts this certificate (" + c.Issuer + ")."
                        : "• Windows does not know this signer yet" + (c.Issuer.Length > 0 && c.Issuer != c.Signer ? " (certificate from " + c.Issuer + ")" : " (a certificate the signer made)") + ", so who signed is not verified.");
            }
            lines.Add("");
        }
        var text = string.Join("\n", lines);
        UMessage.Show(this, text.TrimEnd(), "Signatures", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MakeProtectedCopy(string suffix, string title, Func<byte[], byte[]> change, string result)
    {
        if (_pdf == null || _path == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = title, Filter = "PDF|*.pdf", FileName = Path.GetFileNameWithoutExtension(_path) + suffix + ".pdf", InitialDirectory = Path.GetDirectoryName(_path),
        };
        if (dlg.ShowDialog(this) != true) return;
        string target = dlg.FileName;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            UMessage.Show(this, "Choose a different name: the PDF you are looking at stays as it is, and the copy is a new file.", "Utylix Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            byte[] made = change(_pdf.SaveToBytes());
            string temp = target + ".utylix-tmp";
            File.WriteAllBytes(temp, made);
            File.Move(temp, target, overwrite: true);
        }
        catch (PdfProtectedException e) when (e.Message == PdfSecurity.OwnerPasswordNeeded)
        {
            Mouse.OverrideCursor = null;
            UMessage.Show(this, "This PDF only opened with its \"open\" password, which doesn't allow changing it. Close it and open it again with its owner (permissions) password.", "Utylix Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or OutOfMemoryException or ObjectDisposedException)
        {
            Mouse.OverrideCursor = null;
            UMessage.Show(this, "Couldn't make the copy: " + e.Message, "Utylix Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { Mouse.OverrideCursor = null; }
        Toast($"Saved as {Path.GetFileName(target)}, which {result}");
    }

    // ---------- OCR ----------
    /// <summary>Pages without any text (scans, photos) are read by Windows and get invisible text, so Search, select and copy work.</summary>
    private async void OcrPages()
    {
        if (_pdf == null || _path == null) return;
        var pdf = _pdf;
        var empty = await Task.Run(() => Enumerable.Range(0, pdf.PageCount).Where(i => pdf.GetText(i).Text.Trim().Length == 0).ToList());
        if (pdf != _pdf) return;
        if (empty.Count == 0) { Toast("Every page already has text: nothing to read"); return; }

        var engine = PdfOcr.CreateEngine(out string problem);
        if (engine == null) { UMessage.Show(this, problem, "Make searchable", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var answer = UMessage.Ask(this,
            $"{empty.Count} of {pdf.PageCount} page{(pdf.PageCount == 1 ? "" : "s")} {(empty.Count == 1 ? "has" : "have")} no text (a scan or a photo).\n\nWindows will read the words from {(empty.Count == 1 ? "it" : "them")} on this PC (nothing is sent anywhere). It takes a few seconds a page. The pictures stay as they are; the words are added as invisible text, so Search, select and copy work.\n\nYou can Undo it until you save.",
            "Make searchable", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel, ("Make searchable", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel));
        if (answer != MessageBoxResult.Yes) return;

        var found = new Dictionary<int, (List<OcrWord> Words, (int Width, int Height) Pixels)>();
        bool finished;
        try
        {
            finished = await BusyDialog.RunAsync(this, "Making pages searchable", "Starting…", async (report, cancel) =>
            {
                for (int k = 0; k < empty.Count; k++)
                {
                    cancel.ThrowIfCancellationRequested();
                    int page = empty[k];
                    report(k, empty.Count, $"Reading page {page + 1}  ({k + 1} of {empty.Count})…");
                    var pixels = PdfOcr.PixelSize(pdf.PageSize(page));
                    var picture = await Task.Run(() => pdf.Render(page, pixels.Width, pixels.Height, 0, forScreen: false));
                    found[page] = (await PdfOcr.ReadAsync(engine, picture), pixels);
                }
                report(empty.Count, empty.Count, "");
            });
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or IOException or OutOfMemoryException or ObjectDisposedException)
        {
            UMessage.Show(this, "Couldn't read the pages: " + e.Message, "Make searchable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!finished) { Toast("Stopped: nothing was changed"); return; }

        var marks = found.SelectMany(kv => PdfOcr.Marks(kv.Key, pdf.PageSize(kv.Key), kv.Value.Pixels, kv.Value.Words)).Cast<PdfMark>().ToList();
        int words = found.Values.Sum(v => v.Words.Count);
        if (marks.Count == 0) { Toast("No words were found. Is the page upright and sharp enough to read?"); return; }
        int pagesWithWords = found.Count(kv => kv.Value.Words.Count > 0);
        PageOp(p => PdfMarkWriter.Apply(p, marks), new[] { Math.Clamp(_current, 0, pdf.PageCount - 1) },
               $"{pagesWithWords} page{(pagesWithWords == 1 ? "" : "s")} made searchable ({words} words found). Try Ctrl+F; Undo goes back until you save");
    }
}
