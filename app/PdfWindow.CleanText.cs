using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Paste text from another program (a web page, a mail, a chat) as plain default text: Ctrl+V while editing (when what is on the Windows clipboard did not come from a copy made here),
/// or right-click > Paste the text here. It is cleaned first (odd spaces and invisible characters gone, curly quotes straight, stray blank lines removed) and becomes a text you can move and change.
/// </summary>
public sealed partial class PdfWindow
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    /// <summary>The clipboard's change number right after the last copy made in this program (a later change came from somewhere else).</summary>
    private static uint _ownClipSeq;

    private static void MarkOwnClipboard() => _ownClipSeq = GetClipboardSequenceNumber();

    /// <summary>The clipboard holds text that was copied somewhere else (not by Utylix).</summary>
    private static bool ClipboardHasOtherText()
    {
        try { return GetClipboardSequenceNumber() != _ownClipSeq && Clipboard.ContainsText(); }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    private static bool ClipboardHasText()
    {
        try { return Clipboard.ContainsText(); }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    /// <summary>Puts the clipboard's text on the page as a clean text (at the pointer, or near the top of the page).</summary>
    private bool PasteCleanText(Point? at, int? onPage, bool join = false)
    {
        if (_pdf == null || _pages.Count == 0) return false;
        string raw;
        try { raw = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.ExternalException) { Toast("The clipboard is busy, try again"); return false; }
        string text = TextCleaner.Clean(raw, join);
        if (text.Length == 0) { Toast("There is no text on the clipboard"); return false; }
        if (!_editing) { EnterEditing(); if (!_editing) return false; }
        int page = Math.Clamp(onPage ?? _current, 0, _pages.Count - 1);
        var size = _sizes.Length > page ? _sizes[page] : new Size(595, 842);
        _pasteStep++;
        double wrap = text.Length > 70 || text.Contains('\n') ? Math.Min(Math.Max(120, size.Width - 108), 460) : 0;      // (long text wraps inside the page)
        var start = at ?? new Point(54 + 14 * ((_pasteStep - 1) % 6), 72 + 14 * ((_pasteStep - 1) % 6));
        start.X = Math.Clamp(start.X, 0, Math.Max(0, size.Width - Math.Max(wrap, 40)));
        var item = new TextItem { Page = page, TopLeft = start, Text = text, Font = _font, FontName = _fontName, Bold = _bold, Italic = _italic, Underline = false, Align = 0, FontSize = _textSize, Color = _toolColors[EditTool.Text], BoxWidth = wrap };
        Add(item, select: true);
        Toast("Pasted as plain text. Drag it where it should go; Ctrl+V pastes it again");
        return true;
    }
}
