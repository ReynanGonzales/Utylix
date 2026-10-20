using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Two questions before a Save replaces the open file: has something else changed the file since it was opened here (its date and size are not what they were), and is the PDF digitally signed
/// (saving breaks the signature). Either one offers "Save as…" instead; the person can still overwrite on purpose.
/// </summary>
public sealed partial class PdfWindow
{
    private (DateTime WriteUtc, long Length)? _diskStamp;          // the file as it was when it was opened (or last saved) here
    private int _signedCount = -1;                                 // digital signatures the open document has (-1 = not looked at yet)

    private static (DateTime, long)? StampOf(string path)
    {
        try { var info = new FileInfo(path); return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private int SignatureCount()
    {
        if (_signedCount >= 0) return _signedCount;
        try { _signedCount = _pdf?.GetSignatures().Count ?? 0; }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { _signedCount = 0; }
        return _signedCount;
    }

    /// <summary>True when the save may go on (<paramref name="target"/> may have been changed to another file by the person's choice).</summary>
    private bool ConfirmSafeToWrite(ref string target)
    {
        if (_path == null || !string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase)) return true;   // (another file: nothing of the original is touched)
        bool changed = _diskStamp is { } before && StampOf(_path) is { } now && (now.Item1 != before.WriteUtc || now.Item2 != before.Length);
        bool vanished = _diskStamp != null && StampOf(_path) == null;
        int signatures = SignatureCount();
        if (!changed && !vanished && signatures == 0) return true;

        var reasons = new List<string>();
        if (changed) reasons.Add("Another program (or another person, if the file is on a shared folder) has changed this file since you opened it here. Saving would replace their version with yours.");
        if (vanished) reasons.Add("This file is not where it was any more (it was moved, renamed or deleted). Saving would put it back.");
        if (signatures > 0) reasons.Add($"This PDF has {signatures} digital signature{(signatures == 1 ? "" : "s")}. Any change breaks {(signatures == 1 ? "it" : "them")}: the file would no longer be the one that was signed.");
        var answer = UMessage.Ask(this, string.Join("\n\n", reasons) + "\n\nSave as a new file instead, so nothing is lost?", changed || vanished ? "The file has changed" : "Signed PDF", MessageBoxImage.Warning,
                                  MessageBoxResult.Yes, MessageBoxResult.Cancel, ("Save as…", MessageBoxResult.Yes), (changed || vanished ? "Replace it" : "Save anyway", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel));
        if (answer == MessageBoxResult.No) return true;
        if (answer != MessageBoxResult.Yes) return false;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the edited PDF as", Filter = "PDF|*.pdf",
            FileName = System.IO.Path.GetFileNameWithoutExtension(_path) + " (edited).pdf", InitialDirectory = System.IO.Path.GetDirectoryName(_path),
        };
        if (dlg.ShowDialog(this) != true) return false;
        if (string.Equals(System.IO.Path.GetFullPath(dlg.FileName), System.IO.Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase)) return ConfirmSafeToWrite(ref target) ;     // (the same file again: asked again)
        target = dlg.FileName;
        return true;
    }

    /// <summary>
    /// A changed line or paragraph of the PDF is written with the PDF's own font (or the same one from Windows): letters that font does not have (Chinese, emoji, Arabic ...) would save as empty boxes.
    /// Text added with the Text tool is not asked about: those letters are drawn as shapes. True when the save may go on.
    /// </summary>
    private bool ConfirmTextCoverage()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        void Check(string text, System.Windows.Media.FontFamily family)
        {
            new System.Windows.Media.Typeface(family, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal).TryGetGlyphTypeface(out var glyphs);
            foreach (var rune in text.EnumerateRunes())
            {
                int cp = rune.Value;
                if (cp is < 0x20 or (>= 0x20 and <= 0x7E) or (>= 0xA0 and <= 0xFF)) continue;
                bool shaped = cp is (>= 0x0590 and <= 0x0EFF) or (>= 0x1000 and <= 0x109F) or (>= 0x1780 and <= 0x17FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF);
                if (shaped || glyphs == null || !glyphs.CharacterToGlyphMap.ContainsKey(cp)) missing.Add(rune.ToString());
            }
        }
        foreach (var run in _items.OfType<RunEditItem>()) Check(run.NewText, run.EffFamily);
        foreach (var para in _items.OfType<ParagraphItem>()) if (para.Looks.Count > 0) Check(para.Text, RunEditItem.Family(para.Looks[0]));
        if (missing.Count == 0) return true;
        string list = string.Join(" ", missing.Take(14)) + (missing.Count > 14 ? " …" : "");
        var answer = UMessage.Ask(this, $"These letters are not in the font of the line you changed, so the saved PDF would show empty boxes instead:\n\n{list}\n\nUse the Text tool for them instead (letters like these are drawn as shapes there, so they always show), or save anyway.",
                                  "Letters the font does not have", MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxResult.Cancel, ("Save anyway", MessageBoxResult.Yes), ("Go back", MessageBoxResult.Cancel));
        return answer == MessageBoxResult.Yes;
    }
}
