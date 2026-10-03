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
    private void ShowPagesMenu()
    {
        if (_pdf == null) return;
        var menu = new ContextMenu();
        void Item(string text, Action action, string id = "")
        {
            var m = new MenuItem { Header = text };
            if (id.Length > 0) System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) => action();
            menu.Items.Add(m);
        }
        Item("Add pages from a PDF or pictures…", InsertPagesFromFiles, "PdfMenuInsert");
        Item("Take the chosen page(s) out as a new PDF…", ExtractSelectedPages, "PdfMenuExtract");
        Item("Split into several PDFs…", () => OpenPagesDialog(split: true), "PdfMenuSplit");
        Item("Save pages as pictures (PNG / JPG)…", () => OpenPagesDialog(split: false), "PdfMenuPictures");
        AddMoreToolsToMenu(menu, Item);
        Themed(menu);
        menu.PlacementTarget = _morePages; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
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
