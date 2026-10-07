using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace IdmClone.Engine;

[Flags]
public enum PdfPersonalKind { None = 0, Email = 1, Phone = 2, Card = 4, IdNumber = 8, Date = 16, Web = 32, Custom = 64, All = 127 }

/// <param name="Start">where it begins in the page's text (<see cref="PdfPageText.Text"/>), End = just after it</param>
public sealed record PersonalHit(int Page, PdfPersonalKind Kind, string Text, int Start, int End);

/// <summary>
/// Finds what is usually private in the text of a PDF: e-mail addresses, phone numbers, card numbers (checked with the card checksum), ID numbers (US Social Security, Philippine TIN / SSS style,
/// IBAN, passport style), dates, web addresses, and the words or patterns the person types in. A list to look through: nothing is hidden until the person ticks and redacts.
/// </summary>
public static class PdfPersonalData
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    private static Regex Make(string pattern) => new(pattern, Opt, Limit);

    private static readonly Regex Email = Make(@"[A-Z0-9._%+\-]+@[A-Z0-9\-]+(?:\.[A-Z0-9\-]+)*\.[A-Z]{2,}");
    private static readonly Regex Web = Make(@"(?:https?://|www\.)[^\s<>""]+");
    private static readonly Regex Card = Make(@"(?<!\d)(?:\d[ \-]?){12,18}\d(?!\d)");
    private static readonly Regex Phone = Make(@"(?<![\w.])(?:\+\d{1,3}[ .\-]?)?(?:\(\d{1,4}\)[ .\-]?)?\d{2,4}(?:[ .\-]\d{2,4}){1,4}(?![\w])");
    private static readonly Regex[] IdNumbers =
    {
        Make(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)"),                       // US Social Security
        Make(@"(?<!\d)\d{3}-\d{3}-\d{3}(?:-\d{3,5})?(?!\d)"),         // tax number style (Philippines TIN)
        Make(@"(?<!\d)\d{2}-\d{7}-\d(?!\d)"),                          // social security style (Philippines SSS)
        Make(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b"),                     // IBAN
        Make(@"\b[A-Z]{1,2}\d{7,8}[A-Z]?\b"),                          // passport style
    };
    private static readonly Regex[] Dates =
    {
        Make(@"(?<!\d)\d{1,2}[/\-.]\d{1,2}[/\-.](?:\d{4}|\d{2})(?!\d)"),
        Make(@"(?<!\d)\d{4}-\d{2}-\d{2}(?!\d)"),
        Make(@"\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}\b"),
        Make(@"\b\d{1,2}(?:st|nd|rd|th)?\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?,?\s+\d{4}\b"),
    };

    public static string Describe(PdfPersonalKind k) => k switch
    {
        PdfPersonalKind.Email => "E-mail", PdfPersonalKind.Phone => "Phone", PdfPersonalKind.Card => "Card number", PdfPersonalKind.IdNumber => "ID number",
        PdfPersonalKind.Date => "Date", PdfPersonalKind.Web => "Web address", _ => "Your words",
    };

    /// <summary>The card checksum (Luhn): a number that only looks like a card number mostly fails it.</summary>
    public static bool LuhnOk(string digits)
    {
        int sum = 0; bool second = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (second) { d *= 2; if (d > 9) d -= 9; }
            sum += d; second = !second;
        }
        return digits.Length >= 13 && sum % 10 == 0;
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>The matches of one page's text.</summary>
    public static List<PersonalHit> FindIn(string text, int page, PdfPersonalKind kinds, IReadOnlyList<string>? custom)
    {
        var hits = new List<PersonalHit>();
        var taken = new List<(int Start, int End)>();                       // (a number is reported once: as a card, else an ID, else a phone, else a date)
        bool Free(int s, int e) => !taken.Any(t => s < t.End && e > t.Start);
        void Add(PdfPersonalKind kind, Match m, string? shown = null)
        {
            string value = shown ?? m.Value;
            int start = m.Index, end = m.Index + value.Length;
            if (value.Length == 0 || !Free(start, end)) return;
            taken.Add((start, end));
            hits.Add(new PersonalHit(page, kind, value, start, end));
        }
        try
        {
            if ((kinds & PdfPersonalKind.Email) != 0) foreach (Match m in Email.Matches(text)) Add(PdfPersonalKind.Email, m);
            if ((kinds & PdfPersonalKind.Web) != 0)
                foreach (Match m in Web.Matches(text)) Add(PdfPersonalKind.Web, m, m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '!', '?'));
            if ((kinds & PdfPersonalKind.Card) != 0) foreach (Match m in Card.Matches(text)) { string d = Digits(m.Value); if (d.Length is >= 13 and <= 19 && LuhnOk(d)) Add(PdfPersonalKind.Card, m); }
            if ((kinds & PdfPersonalKind.IdNumber) != 0) foreach (var rx in IdNumbers) foreach (Match m in rx.Matches(text)) Add(PdfPersonalKind.IdNumber, m);
            if ((kinds & PdfPersonalKind.Phone) != 0)
                foreach (Match m in Phone.Matches(text))
                {
                    int n = Digits(m.Value).Length;
                    if (n >= 9 && n <= 15 && !Regex.IsMatch(m.Value, @"^\d{4}-\d{2}-\d{2}$")) Add(PdfPersonalKind.Phone, m);
                }
            if ((kinds & PdfPersonalKind.Date) != 0) foreach (var rx in Dates) foreach (Match m in rx.Matches(text)) Add(PdfPersonalKind.Date, m);
            if ((kinds & PdfPersonalKind.Custom) != 0 && custom != null)
                foreach (string raw in custom)
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    Regex rx;
                    try { rx = line.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? new Regex(line[3..].Trim(), Opt, Limit) : new Regex(Regex.Escape(line), Opt, Limit); }
                    catch (ArgumentException) { continue; }                  // (a pattern that isn't one: skipped)
                    foreach (Match m in rx.Matches(text)) if (m.Length > 0) Add(PdfPersonalKind.Custom, m);
                }
        }
        catch (RegexMatchTimeoutException) { /* a page that is too odd for a pattern: what was found stays */ }
        return hits.OrderBy(h => h.Start).ToList();
    }

    /// <summary>Every page of the PDF.</summary>
    public static List<PersonalHit> Scan(PdfFile pdf, PdfPersonalKind kinds, IReadOnlyList<string>? custom, IProgress<int>? progress, CancellationToken cancel)
    {
        var all = new List<PersonalHit>();
        for (int page = 0; page < pdf.PageCount; page++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(page);
            all.AddRange(FindIn(pdf.GetText(page).Text, page, kinds, custom));
        }
        return all;
    }
}
