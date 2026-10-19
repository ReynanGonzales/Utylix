using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace IdmClone.Engine;

/// <summary>Turns text copied from a web page, a mail or a chat into plain default text: no odd spaces, no invisible characters, no stray blank lines.</summary>
public static class TextCleaner
{
    public static string Clean(string text, bool joinLines = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch ((int)c)
            {
                case 0xA0 or 0x2007 or 0x202F or 0x2002 or 0x2003 or 0x2009 or 0x3000 or 0x09: sb.Append(' '); break;           // (all the kinds of space: one plain space)
                case 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF or 0xAD or 0x200E or 0x200F: break;                          // (invisible: gone)
                case 0x2028 or 0x2029 or 0x0B or 0x0C or 0x85: sb.Append('\n'); break;
                case 0x2018 or 0x2019 or 0x201B: sb.Append('\''); break;                                                      // (curly quotes: straight, like typed text)
                case 0x201C or 0x201D or 0x201F: sb.Append('"'); break;
                case 0x2013 or 0x2014 or 0x2212: sb.Append('-'); break;
                case 0x2026: sb.Append("..."); break;
                case 0x2022 or 0x25CF or 0x25AA or 0x25E6: sb.Append('-'); break;
                default: if (!char.IsControl(c) || c is '\n' or '\r') sb.Append(c); break;
            }
        }
        string s = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = s.Split('\n').Select(l => Regex.Replace(l, " {2,}", " ").Trim()).ToList();
        if (joinLines)
        {
            // one paragraph per block of lines: a blank line still separates paragraphs
            var paragraphs = new List<string>(); var current = new StringBuilder();
            foreach (var l in lines)
            {
                if (l.Length == 0) { if (current.Length > 0) { paragraphs.Add(current.ToString()); current.Clear(); } continue; }
                if (current.Length > 0) current.Append(' ');
                current.Append(l);
            }
            if (current.Length > 0) paragraphs.Add(current.ToString());
            return string.Join("\n\n", paragraphs);
        }
        s = string.Join("\n", lines);
        s = Regex.Replace(s, "\n{3,}", "\n\n");
        return s.Trim('\n', ' ');
    }
}
