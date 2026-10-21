using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The clock button next to Open: the PDFs opened lately, and favourite folders (a server folder, the drawings folder) that open the Open dialog right there
/// and are also listed on the left of that dialog. Nothing on the disk or the server is looked at until something is clicked (a server can be slow).
/// </summary>
public sealed partial class PdfWindow
{
    private static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string FolderLabel(string folder)
    {
        string name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? folder : name + "   (" + Short(System.IO.Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? "", 40) + ")";
    }

    private void RecentMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu();
        void Item(string text, Action action, string? tip = null, bool enabled = true, string? id = null, ItemsControl? into = null)
        {
            var m = new MenuItem { Header = text, IsEnabled = enabled, ToolTip = tip };
            if (id != null) System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) => action();
            (into ?? menu).Items.Add(m);
        }
        void Heading(string text) => menu.Items.Add(new MenuItem { Header = text, IsEnabled = false, FontWeight = FontWeights.SemiBold });

        Heading("Recent PDFs");
        if (PdfRecent.Files.Count == 0) Item("(none yet)", () => { }, enabled: false);
        int n = 0;
        foreach (string file in PdfRecent.Files.ToList())
            Item(Short(System.IO.Path.GetFileName(file), 60), () => OpenRecent(file), file, id: "PdfRecentFile" + n++);
        if (PdfRecent.Files.Count > 0) Item("Clear this list", PdfRecent.ClearFiles, id: "PdfRecentClear");

        menu.Items.Add(new Separator());
        Heading("Favourite folders");
        if (PdfRecent.Folders.Count == 0) Item("(none yet)", () => { }, enabled: false);
        n = 0;
        foreach (string folder in PdfRecent.Folders.ToList())
            Item(FolderLabel(folder), () => BrowseIn(folder), folder, id: "PdfFavourite" + n++);
        string? here = _path == null ? null : System.IO.Path.GetDirectoryName(_path);
        if (here != null)
        {
            if (PdfRecent.IsFavourite(here)) Item("Remove this PDF's folder from favourites", () => PdfRecent.RemoveFolder(here), here, id: "PdfFavouriteRemoveHere");
            else Item("Add this PDF's folder to favourites", () => { PdfRecent.AddFolder(here); Toast("Added to your favourite folders"); }, here, id: "PdfFavouriteAddHere");
        }
        if (PdfRecent.Folders.Count > 0)
        {
            var remove = new MenuItem { Header = "Remove a favourite folder" };
            foreach (string folder in PdfRecent.Folders.ToList()) Item(FolderLabel(folder), () => PdfRecent.RemoveFolder(folder), folder, into: remove);
            menu.Items.Add(remove);
        }
        Themed(menu);
        menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void OpenRecent(string file)
    {
        if (!File.Exists(file))
        {
            PdfRecent.Forget(file);
            UMessage.Show(this, "This PDF is not there any more (moved, renamed or deleted), or this PC can't reach its folder right now:\n\n" + file + "\n\nIt was taken off the recent list.", "Can't find the PDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Open(new[] { file });
    }

    private void BrowseIn(string folder)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open a PDF", Filter = "PDF|*.pdf|All files|*.*", Multiselect = true, InitialDirectory = folder };
        AddFavouritePlaces(dlg);
        if (dlg.ShowDialog(this) == true) Open(dlg.FileNames);
    }

    /// <summary>The favourite folders as places on the left of an Open dialog.</summary>
    private static void AddFavouritePlaces(Microsoft.Win32.FileDialog dlg)
    {
        foreach (string folder in PdfRecent.Folders.Take(10))
            try { dlg.CustomPlaces.Add(new Microsoft.Win32.FileDialogCustomPlace(folder)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
    }
}
