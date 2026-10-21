using System.IO;
using System.Windows;

namespace IdmClone;

public sealed partial class PdfWindow
{
    /// <summary>Tools > "Show this PDF next to another one": asks for the other PDF and opens the window with both (from the saved files).</summary>
    private void SideBySide()
    {
        if (_pdf == null || _path == null) return;
        if (_dirty) Toast("The saved file is shown; your unsaved changes are not in it");
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Show next to this PDF", Filter = "PDF|*.pdf", InitialDirectory = Path.GetDirectoryName(_path) };
        AddFavouritePlaces(dlg);
        if (dlg.ShowDialog(this) != true) return;
        PdfSideBySideWindow.Start(this, _path, dlg.FileName);
    }
}
