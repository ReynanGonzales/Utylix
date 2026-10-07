using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>The "…" menu of the page tools: adding pages from files, taking pages out, splitting, saving as pictures (and the bigger tools that work on pages).</summary>
public sealed partial class PdfWindow
{
    private void ShowPagesMenu(UIElement? under = null)
    {
        if (_pdf == null) return;
        var menu = new ContextMenu();
        void Heading(string text) =>
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 0, 2), Opacity = 0.65 },
                IsHitTestVisible = false, Focusable = false,
            });
        void Item(string text, Action action, string id = "")
        {
            var m = new MenuItem { Header = text };
            if (id.Length > 0) System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) => action();
            menu.Items.Add(m);
        }
        Heading("PAGES");
        Item("Crop pages…", CropPages, "PdfMenuCrop");
        Item("Take the chosen page(s) out as a new PDF…", ExtractSelectedPages, "PdfMenuExtract");
        Item("Split into several PDFs…", () => OpenPagesDialog(split: true), "PdfMenuSplit");
        Item("Save pages as pictures (PNG / JPG)…", () => OpenPagesDialog(split: false), "PdfMenuPictures");
        Heading("PROTECT");
        Item("Add a password or limits…", AddPassword, "PdfMenuPassword");
        Item("Remove the password…", RemovePassword, "PdfMenuRemovePassword");
        Item("Sign with a certificate…", SignWithCertificate, "PdfMenuSign");
        Item("Check the signatures…", CheckSignatures, "PdfMenuCheckSign");
        Heading("DOCUMENT");
        Item("Properties (title, author, keywords)…", EditProperties, "PdfMenuProperties");
        Item("Page labels (i, ii, iii … then 1, 2, 3)…", EditPageLabels, "PdfMenuPageLabels");
        Item("Attached files…", EditAttachments, "PdfMenuAttachments");
        Heading("FORMS");
        Item("Make the fields permanent (flatten)…", FlattenForms, "PdfMenuFlatten");
        Item("Save what is filled in as a CSV file…", ExportFormData, "PdfMenuFormExport");
        Item("Fill the form from a CSV file…", ImportFormData, "PdfMenuFormImport");
        Heading("PICTURES");
        Item("Save all the pictures of this PDF…", SaveAllPictures, "PdfMenuSavePictures");
        Heading("PRIVACY");
        Item("Find personal details to black out…", FindPersonalDetails, "PdfMenuPersonal");
        Heading("CLEAN UP");
        Item("Remove a watermark…", RemoveWatermark, "PdfMenuWatermark");
        Item("Make scanned pages searchable (OCR)…", OcrPages, "PdfMenuOcr");
        Heading("COMPARE AND CONVERT");
        Item("Compare with another PDF…", ComparePdf, "PdfMenuCompare");
        Item("Save as Word or Excel…", () => ExportToOffice(), "PdfMenuExport");
        Heading("MANY FILES");
        Item("Do one job to many PDFs…", () => PdfBatchWindow.ShowFor(this, _path), "PdfMenuBatch");
        Themed(menu);
        menu.PlacementTarget = under ?? _morePages; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    /// <summary>Every page as a big picture: moving, turning, deleting and adding blank pages, applied in one go (one Undo).</summary>
    private void OpenPageGrid()
    {
        if (_pdf == null || _path == null) return;
        if (!PreparePageOp()) return;
        var grid = new PdfPageGridWindow(this, _pdf, _current);
        bool applied = grid.ShowDialog() == true && grid.Result != null;
        if (!applied) { if (grid.GoTo >= 0) GoTo(grid.GoTo); return; }
        var plan = grid.Result!;
        int firstChanged = Math.Max(0, plan.FindIndex(p => p.Source < 0 || p.Turns % 4 != 0));
        PageOp(p => PdfPageTools.Rearrange(p, plan), new[] { Math.Clamp(_current, 0, Math.Max(0, plan.Count - 1)) },
               $"Pages changed: now {plan.Count} page{(plan.Count == 1 ? "" : "s")}. Undo goes back until you save");
    }

    private void OpenPagesDialog(bool split)
    {
        if (_pdf == null || _path == null || !CommitItems()) return;
        new PdfPagesDialog(this, _pdf, _path, SelectedPages(), split).ShowDialog();
    }

    private void InsertPagesFromFiles()
    {
        if (_pdf == null || _path == null) return;
        var pictures = string.Join(";", PdfCombiner.PictureExtensions.Select(e => "*." + e));
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add pages from a PDF or pictures", Multiselect = true, CheckFileExists = true,
            Filter = $"PDFs and pictures|*.pdf;{pictures}|PDF|*.pdf|Pictures|{pictures}",
        };
        if (dlg.ShowDialog(this) != true) return;
        var files = dlg.FileNames.Where(f => PdfCombiner.IsPdf(f) || PdfCombiner.IsPicture(f)).ToList();
        if (files.Count == 0) return;
        var sel = SelectedPages();
        int at = sel[^1] + 1;                                         // (after the chosen page, or the last of the chosen pages)
        int total = 0;
        PageOp(p =>
        {
            int pos = at;
            foreach (string file in files)
            {
                if (PdfCombiner.IsPdf(file)) { int added = PdfPageTools.InsertPdf(p, file, pos); pos += added; total += added; }
                else
                {
                    var like = p.PageSize(Math.Clamp(pos - 1, 0, p.PageCount - 1));
                    PdfPageTools.InsertPicture(p, file, pos, (like.Width, like.Height));
                    pos++; total++;
                }
            }
        },
        () => Enumerable.Range(at, total).ToList(),
        () => (total == 1 ? "1 page" : total + " pages") + " added after page " + at);
    }

    /// <summary>The form fields become part of the page (what is typed and ticked stays, nothing can be changed any more). One undo step.</summary>
    private void FlattenForms()
    {
        if (_pdf == null || _path == null) return;
        if (!_pdf.HasForm) { Toast("This PDF has no form fields"); return; }
        var answer = UMessage.Ask(this, "Make every field of this form part of the page?\n\nWhat is typed or ticked stays exactly as it looks, but nobody can fill in or change the fields afterwards. Comments stay comments.\n\nUndo takes it back until you save. To keep a fillable copy, use Save as… first.",
                                  "Make the fields permanent", MessageBoxImage.Question, MessageBoxResult.Cancel, MessageBoxResult.Cancel, ("Make them permanent", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel));
        if (answer != MessageBoxResult.Yes) return;
        PageOp(p => p.FlattenForms(), new[] { Math.Clamp(_current, 0, _pdf.PageCount - 1) }, "The fields are part of the page now. Undo takes it back until you save", keepView: true);
    }

    /// <summary>Compare the open PDF (as saved on disk) with another one: what is different, page by page.</summary>
    private void ComparePdf()
    {
        if (_pdf == null || _path == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Compare " + Path.GetFileName(_path) + " with ...", Filter = "PDF|*.pdf", CheckFileExists = true, InitialDirectory = Path.GetDirectoryName(_path) };
        if (dlg.ShowDialog(this) != true) return;
        PdfFile? a = null, b = null;
        try
        {
            a = PdfFile.Open(_path, _pdf.Password);
            b = PdfFile.Open(dlg.FileName);
            var window = new PdfCompareWindow(this, a, Path.GetFileName(_path), b, Path.GetFileName(dlg.FileName));
            window.Closed += (_, _) => { a.Dispose(); b.Dispose(); };
            window.Show();
        }
        catch (PdfPasswordException)
        {
            a?.Dispose(); b?.Dispose();
            UMessage.Show(this, "That PDF needs a password. Remove its password first (Tools > Remove the password) and compare the copy.", "Compare", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            a?.Dispose(); b?.Dispose();
            UMessage.Show(this, "Couldn't open it: " + e.Message, "Compare", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Pages from a scanner (Windows' own scan window): each scan becomes a page after the chosen page; ask for as many as wanted.</summary>
    private void InsertFromScanner()
    {
        if (_pdf == null || _path == null) return;
        var sel = SelectedPages();
        int at = sel[^1] + 1;
        string folder = Path.Combine(Path.GetTempPath(), "UtylixScanner");
        var files = new List<string>();
        while (true)
        {
            var file = ScannerImport.ScanOne(folder, out string error);
            if (file == null)
            {
                if (error.Length > 0) UMessage.Show(this, error, "Scan", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            }
            files.Add(file);
            var more = UMessage.Ask(this, files.Count == 1 ? "1 page scanned." : files.Count + " pages scanned.", "Scan", MessageBoxImage.Information, MessageBoxResult.No, MessageBoxResult.No,
                                    ("Scan another page", MessageBoxResult.Yes), ("That's all", MessageBoxResult.No));
            if (more != MessageBoxResult.Yes) break;
        }
        if (files.Count == 0) return;
        int total = 0;
        PageOp(p =>
        {
            int pos = at;
            foreach (string file in files)
            {
                var like = p.PageSize(Math.Clamp(pos - 1, 0, p.PageCount - 1));
                PdfPageTools.InsertPicture(p, file, pos, (like.Width, like.Height));
                pos++; total++;
            }
        },
        () => Enumerable.Range(at, total).ToList(),
        () => (total == 1 ? "1 scanned page" : total + " scanned pages") + " added after page " + at);
        foreach (string file in files) { try { File.Delete(file); } catch (IOException) { } }
    }

    /// <summary>One empty page after the chosen page (or the last of the chosen pages), as big as that page.</summary>
    private void AddBlankPage()
    {
        if (_pdf == null || _path == null) return;
        var sel = SelectedPages();
        int after = sel[^1];
        var like = _pdf.PageSize(Math.Clamp(after, 0, _pdf.PageCount - 1));
        PageOp(p => PdfPageTools.InsertBlank(p, after + 1, (like.Width, like.Height)), new[] { after + 1 }, $"A blank page was added after page {after + 1}. You can write on it with Text, Sign and the other tools");
    }

    private void ExtractSelectedPages()
    {
        if (_pdf == null || _path == null || !CommitItems()) return;
        var sel = SelectedPages();
        string stem = Path.GetFileNameWithoutExtension(_path);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the chosen page(s) as a new PDF", Filter = "PDF|*.pdf", OverwritePrompt = true,
            FileName = PdfPageTools.Safe($"{stem} ({(sel.Count == 1 ? "page" : "pages")} {PdfPageTools.Label(sel)})") + ".pdf",
            InitialDirectory = Path.GetDirectoryName(_path),
        };
        if (dlg.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase)) { Toast("Choose another name: that is the open PDF"); return; }
        try
        {
            File.WriteAllBytes(dlg.FileName, PdfPageTools.Extract(_pdf, sel));
            Toast((sel.Count == 1 ? "Page " + (sel[0] + 1) : sel.Count + " pages") + " saved as " + Path.GetFileName(dlg.FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfProtectedException or OutOfMemoryException or ObjectDisposedException)
        {
            UMessage.Show(this, "Couldn't save: " + e.Message, "Utylix Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>The bigger tools are added to the menu by their own files.</summary>
    partial void AddMoreTools(ContextMenu menu, Action<string, Action, string> item);

    private void AddMoreToolsToMenu(ContextMenu menu, Action<string, Action, string> item)
    {
        menu.Items.Add(new Separator());
        AddMoreTools(menu, item);
    }
}
