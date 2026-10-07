using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace IdmClone.Engine;

public enum FieldFormatKind { None, Number, Date, Time }

/// <summary>
/// How a text box shows what is typed in it. Number: <see cref="Decimals"/> places, a decimal point or comma (<see cref="CommaDecimal"/>), no thousands separator on purpose
/// (readers parse the stored text again to calculate, and "1,234.50" does not survive that). Date / Time: <see cref="Pattern"/> in Acrobat's letters (dd mm yyyy, HH MM ss tt).
/// </summary>
public sealed record FieldFormat(FieldFormatKind Kind, int Decimals = 2, bool CommaDecimal = false, string Pattern = "");

public enum FieldCalcOp { Sum, Product, Average, Min, Max }

/// <summary>A box whose text is worked out from other boxes (by their names).</summary>
public sealed record FieldCalc(FieldCalcOp Op, IReadOnlyList<string> Fields);

/// <summary>
/// Number / date / time formats and calculated fields. They are written into the PDF as the standard Acrobat form scripts (AFNumber_Format, AFDate_FormatEx, AFSimple_Calculate ...), so Acrobat,
/// Edge and Chrome run them, and read back from those scripts (Utylix's own and other makers' standard ones), so Utylix's own form filling applies them too: PDFium has no script engine.
/// </summary>
public static class PdfFormLogic
{
    // ---------- writing the scripts ----------
    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string FormatScript(FieldFormat f) => f.Kind switch
    {
        FieldFormatKind.Number => $"AFNumber_Format({f.Decimals}, {(f.CommaDecimal ? 3 : 1)}, 0, 0, \"\", true);",
        FieldFormatKind.Date => $"AFDate_FormatEx({Q(f.Pattern)});",
        FieldFormatKind.Time => $"AFTime_FormatEx({Q(f.Pattern)});",
        _ => "",
    };

    public static string KeystrokeScript(FieldFormat f) => f.Kind switch
    {
        FieldFormatKind.Number => $"AFNumber_Keystroke({f.Decimals}, {(f.CommaDecimal ? 3 : 1)}, 0, 0, \"\", true);",
        FieldFormatKind.Date => $"AFDate_KeystrokeEx({Q(f.Pattern)});",
        FieldFormatKind.Time => $"AFTime_Keystroke(0);",
        _ => "",
    };

    private static string OpName(FieldCalcOp op) => op switch { FieldCalcOp.Product => "PRD", FieldCalcOp.Average => "AVG", FieldCalcOp.Min => "MIN", FieldCalcOp.Max => "MAX", _ => "SUM" };

    public static string CalcScript(FieldCalc c) => $"AFSimple_Calculate({Q(OpName(c.Op))}, new Array({string.Join(", ", c.Fields.Select(Q))}));";

    // ---------- reading the scripts ----------
    private static readonly string[] OldDatePatterns =
        { "m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy", "yy-mm-dd", "mmm-yy", "mmmm-yy", "mmm d, yyyy", "mmmm d, yyyy", "m/d/yy h:MM tt", "m/d/yy HH:MM" };
    private static readonly string[] OldTimePatterns = { "HH:MM", "h:MM tt", "HH:MM:ss", "h:MM:ss tt" };

    /// <summary>The format a field's "format" script stands for (null when it is some other script).</summary>
    public static FieldFormat? ParseFormat(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        var m = Regex.Match(script, @"AFNumber_Format\(\s*(\d+)\s*,\s*(\d+)");
        if (m.Success) return new FieldFormat(FieldFormatKind.Number, Math.Clamp(int.Parse(m.Groups[1].Value), 0, 10), m.Groups[2].Value is "2" or "3");
        m = Regex.Match(script, @"AFDate_FormatEx\(\s*(?:""([^""]*)""|'([^']*)')\s*\)");
        if (m.Success) return new FieldFormat(FieldFormatKind.Date, Pattern: m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        m = Regex.Match(script, @"AFDate_Format\(\s*(\d+)\s*\)");
        if (m.Success && int.Parse(m.Groups[1].Value) < OldDatePatterns.Length) return new FieldFormat(FieldFormatKind.Date, Pattern: OldDatePatterns[int.Parse(m.Groups[1].Value)]);
        m = Regex.Match(script, @"AFTime_FormatEx\(\s*(?:""([^""]*)""|'([^']*)')\s*\)");
        if (m.Success) return new FieldFormat(FieldFormatKind.Time, Pattern: m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        m = Regex.Match(script, @"AFTime_Format\(\s*(\d+)\s*\)");
        if (m.Success && int.Parse(m.Groups[1].Value) < OldTimePatterns.Length) return new FieldFormat(FieldFormatKind.Time, Pattern: OldTimePatterns[int.Parse(m.Groups[1].Value)]);
        return null;
    }

    /// <summary>The sum / product / average / smallest / largest a field's "calculate" script stands for (null when it is some other script).</summary>
    public static FieldCalc? ParseCalc(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        var m = Regex.Match(script, @"AFSimple_Calculate\(\s*[""'](SUM|PRD|AVG|MIN|MAX)[""']\s*,\s*(?:new\s+Array\s*\(|\[)(.*?)\)?\s*\]?\s*\)\s*;?\s*$", RegexOptions.Singleline);
        if (!m.Success) return null;
        var names = Regex.Matches(m.Groups[2].Value, @"""((?:[^""\\]|\\.)*)""|'((?:[^'\\]|\\.)*)'")
            .Select(x => Regex.Unescape(x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value)).Where(s => s.Length > 0).ToList();
        if (names.Count == 0) return null;
        var op = m.Groups[1].Value switch { "PRD" => FieldCalcOp.Product, "AVG" => FieldCalcOp.Average, "MIN" => FieldCalcOp.Min, "MAX" => FieldCalcOp.Max, _ => FieldCalcOp.Sum };
        return new FieldCalc(op, names);
    }

    // ---------- applying them ----------
    public static string Describe(FieldFormat f) => f.Kind switch
    {
        FieldFormatKind.Number => f.Decimals == 0 ? "a whole number" : $"a number with {f.Decimals} decimal place{(f.Decimals == 1 ? "" : "s")}" + (f.CommaDecimal ? " (decimal comma)" : ""),
        FieldFormatKind.Date => "a date like " + Format(f, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        FieldFormatKind.Time => "a time like " + Format(f, "14:35:20"),
        _ => "text",
    };

    public static bool TryNumber(string text, bool commaDecimal, out double value)
    {
        string s = new string((text ?? "").Where(c => !char.IsWhiteSpace(c) && c is not ('$' or '€' or '£' or '¥' or '₱')).ToArray());
        if (s.Length == 0) { value = 0; return false; }
        if (commaDecimal) s = s.Replace(".", "").Replace(',', '.');
        else s = s.Replace(",", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public static string NumberText(double v, FieldFormat? f)
    {
        string s = f is { Kind: FieldFormatKind.Number } ? v.ToString("F" + f.Decimals, CultureInfo.InvariantCulture) : v.ToString("0.##########", CultureInfo.InvariantCulture);
        if (s.StartsWith("-") && s.Trim('-', '0', '.') .Length == 0) s = s.TrimStart('-');             // (no "-0.00")
        return f is { Kind: FieldFormatKind.Number, CommaDecimal: true } ? s.Replace('.', ',') : s;
    }

    private static string DotNetDate(string pattern) =>
        Regex.Replace(pattern, "yyyy|yy|mmmm|mmm|mm|m|dd|d", t => t.Value switch { "mmmm" => "MMMM", "mmm" => "MMM", "mm" => "MM", "m" => "M", _ => t.Value });

    private static string DotNetTime(string pattern) =>
        Regex.Replace(pattern, "HH|h|MM|ss|tt", t => t.Value switch { "MM" => "mm", _ => t.Value });

    /// <summary>What the person typed, shown the way the format wants (null when it can't be read as that kind of value: the typed text is then not accepted).</summary>
    public static string? Format(FieldFormat f, string typed)
    {
        typed = typed?.Trim() ?? "";
        if (typed.Length == 0) return "";
        var inv = CultureInfo.InvariantCulture;
        switch (f.Kind)
        {
            case FieldFormatKind.Number:
                return TryNumber(typed, f.CommaDecimal, out double v) ? NumberText(v, f) : null;
            case FieldFormatKind.Date:
            {
                string net = DotNetDate(f.Pattern);
                if (DateTime.TryParseExact(typed, net, inv, DateTimeStyles.None, out var d)
                    || DateTime.TryParse(typed, CultureInfo.CurrentCulture, DateTimeStyles.None, out d) || DateTime.TryParse(typed, inv, DateTimeStyles.None, out d))
                    return d.ToString(net, inv);
                return null;
            }
            case FieldFormatKind.Time:
            {
                string net = DotNetTime(f.Pattern);
                if (DateTime.TryParseExact(typed, net, inv, DateTimeStyles.None, out var t)
                    || DateTime.TryParse(typed, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out t) || DateTime.TryParse(typed, inv, DateTimeStyles.NoCurrentDateDefault, out t))
                    return t.ToString(net, inv);
                return null;
            }
        }
        return typed;
    }

    /// <summary>The result of a calculation, as text (empty when there is nothing to work with). <paramref name="valueOf"/> gives the text of a field by its name (null = no such field).</summary>
    public static string Calculate(FieldCalc calc, FieldFormat? own, Func<string, (string Text, FieldFormat? Format)?> valueOf)
    {
        var numbers = new List<double>();
        foreach (string name in calc.Fields)
        {
            var got = valueOf(name);
            if (got == null) continue;
            if (TryNumber(got.Value.Text, got.Value.Format is { Kind: FieldFormatKind.Number, CommaDecimal: true }, out double v)) numbers.Add(v);
        }
        double result;
        switch (calc.Op)
        {
            case FieldCalcOp.Sum: result = numbers.Sum(); break;
            case FieldCalcOp.Product: if (numbers.Count == 0) return ""; result = numbers.Aggregate(1.0, (a, b) => a * b); break;
            case FieldCalcOp.Average: if (numbers.Count == 0) return ""; result = numbers.Average(); break;
            case FieldCalcOp.Min: if (numbers.Count == 0) return ""; result = numbers.Min(); break;
            default: if (numbers.Count == 0) return ""; result = numbers.Max(); break;
        }
        return NumberText(result, own is { Kind: FieldFormatKind.Number } ? own : null);
    }

    public static readonly string[] DatePatterns = { "dd/mm/yyyy", "mm/dd/yyyy", "yyyy-mm-dd", "d mmm yyyy", "mmmm d, yyyy" };
    public static readonly string[] TimePatterns = { "HH:MM", "h:MM tt", "HH:MM:ss", "h:MM:ss tt" };
}
