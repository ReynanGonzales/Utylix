using System;
using System.Collections.Generic;
using System.Linq;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Number / date / time formats and calculated boxes while a form is filled in here. PDFium has no script engine, so Utylix reads the standard Acrobat scripts of the fields
/// (<see cref="PdfFormLogic"/>) and does what they say itself: a typed value is checked and shown in the field's form, and every calculated box is worked out again after each change.
/// </summary>
public sealed partial class PdfWindow
{
    private bool? _formHasCalc;                              // null = not looked at yet for this document

    /// <summary>False when what was typed doesn't fit the field's format (a message says so); otherwise <paramref name="text"/> is now in the format's form.</summary>
    private bool AcceptFieldText(PdfField field, ref string text)
    {
        if (field.Format is not { Kind: not FieldFormatKind.None } f) return true;
        string? shown = PdfFormLogic.Format(f, text);
        if (shown == null) { Toast($"\"{text.Trim()}\" doesn't fit: this box takes {PdfFormLogic.Describe(f)}"); return false; }
        text = shown;
        return true;
    }

    /// <summary>
    /// Works every calculated box out again (a box can depend on another calculated box, so it repeats until nothing changes). Cheap when the form has none: the pages are only looked through once per document.
    /// </summary>
    private void RecalculateForm()
    {
        if (_pdf == null || !_pdf.HasForm || _formHasCalc == false) return;
        var pages = new HashSet<int>();
        try
        {
            for (int pass = 0; pass < 6; pass++)
            {
                var all = Enumerable.Range(0, _pdf.PageCount).SelectMany(_pdf.GetFields).ToList();
                if (pass == 0) { _formHasCalc = all.Any(f => f.Calc != null); if (_formHasCalc == false) return; }
                var byName = new Dictionary<string, PdfField>(StringComparer.Ordinal);
                foreach (var f in all) if (f.Name.Length > 0 && f.Kind == PdfFieldKind.Text) byName.TryAdd(f.Name, f);
                bool changed = false;
                foreach (var f in all.Where(f => f.Calc != null && f.Kind == PdfFieldKind.Text))
                {
                    string result = PdfFormLogic.Calculate(f.Calc!, f.Format, n => byName.TryGetValue(n, out var s) ? (s.Value, s.Format) : null);
                    if (result == f.Value) continue;
                    _pdf.SetFieldText(f, result);
                    pages.Add(f.Page); changed = true;
                }
                if (!changed) break;
            }
        }
        catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException) { Toast("Couldn't work the form out: " + e.Message); }
        foreach (int page in pages) { _fields.Remove(page); RefreshPage(page); }
        if (pages.Count > 0) { _dirty = true; UpdateTitle(); }
    }
}
