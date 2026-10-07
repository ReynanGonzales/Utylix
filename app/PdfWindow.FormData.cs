using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Form data in and out (Tools > FORMS): save what is typed in a PDF form as a CSV file (Field, Value: opens in Excel), and fill a form from such a file (the same layout). Text boxes, drop-down lists
/// and check boxes are filled; option buttons (round) and signature fields are only saved, not filled.
/// </summary>
public sealed partial class PdfWindow
{
    private static string CsvCell(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Reads CSV text (comma, or semicolon when that is what the first line uses; quotes allowed).</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        char sep = text.Split('\n').FirstOrDefault() is { } first && first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Any(x => x.Length > 0)) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); if (row.Any(x => x.Length > 0)) rows.Add(row); }
        return rows;
    }

    private static string ValueOf(PdfField f) => f.Kind switch
    {
        PdfFieldKind.CheckBox => f.Checked ? "Yes" : "Off",
        PdfFieldKind.Radio => f.Value.Length > 0 && !f.Value.Equals("Off", StringComparison.OrdinalIgnoreCase) ? f.Value : "Off",
        PdfFieldKind.Combo or PdfFieldKind.List => f.Selected >= 0 && f.Selected < f.Options.Count ? f.Options[f.Selected] : f.Value,
        _ => f.Value.Replace("\r\n", "\n").Replace('\r', '\n'),
    };

    private async void ExportFormData()
    {
        if (_pdf == null || _path == null) return;
        if (!_pdf.HasForm) { Toast(_pdf.IsXfaForm ? "This is an XFA form: its fields can't be read here" : "This PDF has no form fields"); return; }
        var pdf = _pdf;
        var fields = await Task.Run(() => Enumerable.Range(0, pdf.PageCount).SelectMany(pdf.GetFields).ToList());
        var rows = new List<(string Name, string Value)>();
        foreach (var f in fields.Where(f => f.Name.Length > 0 && f.Kind is not (PdfFieldKind.Button or PdfFieldKind.Signature)))
            if (!rows.Any(r => r.Name == f.Name)) rows.Add((f.Name, ValueOf(f)));
        if (rows.Count == 0) { Toast("This PDF has no fields with a name to save"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save the form data", Filter = "CSV (opens in Excel)|*.csv", FileName = System.IO.Path.GetFileNameWithoutExtension(_path) + " (form data).csv", InitialDirectory = System.IO.Path.GetDirectoryName(_path) };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder("Field,Value\r\n");
        foreach (var (name, value) in rows) sb.Append(CsvCell(name)).Append(',').Append(CsvCell(value)).Append("\r\n");
        try { File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); Toast($"Saved {rows.Count} field{(rows.Count == 1 ? "" : "s")} to {System.IO.Path.GetFileName(dlg.FileName)}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast("Couldn't save it: " + e.Message); }
    }

    private async void ImportFormData()
    {
        if (_pdf == null || _path == null) return;
        if (!_pdf.HasForm) { Toast(_pdf.IsXfaForm ? "This is an XFA form: its fields can't be filled here" : "This PDF has no form fields to fill"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "The form data to fill in (a CSV file with Field and Value)", Filter = "CSV|*.csv;*.txt|All files|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        List<List<string>> rows;
        try { rows = ParseCsv(File.ReadAllText(dlg.FileName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast("Couldn't read that file: " + e.Message); return; }
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows.Where(r => r.Count >= 2 && r[0].Length > 0)) values[r[0].Trim()] = r[1];
        values.Remove("Field");
        if (values.Count == 0) { Toast("That file has no \"Field, Value\" lines"); return; }
        var pdf = _pdf;
        var yes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "yes", "true", "1", "x", "on", "checked", "y" };
        int filled = 0; var pages = new HashSet<int>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.Run(() =>
            {
                for (int page = 0; page < pdf.PageCount; page++)
                {
                    foreach (var f in pdf.GetFields(page))
                    {
                        if (f.ReadOnly || !values.TryGetValue(f.Name, out string? value)) continue;
                        seen.Add(f.Name);
                        switch (f.Kind)
                        {
                            case PdfFieldKind.Text when value != f.Value: pdf.SetFieldText(f, value); filled++; pages.Add(page); break;
                            case PdfFieldKind.CheckBox when yes.Contains(value.Trim()) != f.Checked: pdf.ClickField(f); filled++; pages.Add(page); break;
                            case PdfFieldKind.Combo or PdfFieldKind.List:
                            {
                                int at = f.Options.ToList().FindIndex(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
                                if (at >= 0 && at != f.Selected) { pdf.SelectFieldOption(f, at); filled++; pages.Add(page); }
                                else if (at < 0 && f.EditableCombo && value != f.Value) { pdf.SetFieldText(f, value); filled++; pages.Add(page); }
                                break;
                            }
                        }
                    }
                }
            });
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { Toast("Couldn't fill it in: " + e.Message); return; }
        foreach (int page in pages) { _fields.Remove(page); RefreshPage(page); }
        if (filled > 0) { _dirty = true; UpdateTitle(); }
        int missing = values.Keys.Count(k => !seen.Contains(k));
        Toast(filled == 0 ? "Nothing needed changing" + (missing > 0 ? $" ({missing} name{(missing == 1 ? "" : "s")} in the file are not fields of this PDF)" : "") : $"Filled in {filled} field{(filled == 1 ? "" : "s")}" + (missing > 0 ? $"; {missing} name{(missing == 1 ? "" : "s")} in the file are not fields of this PDF" : "") + ". Save keeps them.");
    }
}
