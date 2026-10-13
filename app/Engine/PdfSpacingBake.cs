using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace IdmClone.Engine;

/// <summary>
/// PDFium writes a page's text out again whenever something on the page changes, and what it writes has no character spacing (Tc) and no word spacing (Tw):
/// justified text and letter-spaced words in the lines that were not even touched come out narrower or wider. A letter-by-letter position adjustment inside
/// a text-show array (TJ) is kept, though, and means exactly the same thing: so before a page is changed, the spacing operators are turned into those
/// adjustments, and the spacing operators are set to zero. What is drawn does not change by a hair; it just survives the rewrite.
/// </summary>
public static class PdfSpacingBake
{
    private enum K { Number, Name, String, Array, Operator, Other }

    private sealed class Tok
    {
        public K Kind;
        public int Start, End;
        public string Text = "";
        public double Number;
        public byte[] Bytes = Array.Empty<byte>();
        public List<Tok> Items = new();
    }

    private static bool Space(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool Delim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>
    /// The content with its spacing operators turned into adjustments, or null when there is nothing to do (no spacing is set) or something is not understood
    /// (then the page is left exactly as it is). <paramref name="bytesPerCode"/> says how many bytes make one character of a font resource (1 simple, 2 composite; 0 = not known, -1 = vertical).
    /// </summary>
    public static byte[]? Rewrite(byte[] s, Func<string, int> bytesPerCode)
    {
        var toks = new List<Tok>();
        int i = 0;
        if (!Tokenize(s, ref i, toks, inArray: false)) return null;

        var output = new MemoryStream(s.Length + s.Length / 4);
        int copied = 0;
        bool changed = false;
        void CopyTo(int pos) { if (pos > copied) { output.Write(s, copied, pos - copied); copied = pos; } }
        void Write(string text) { var b = Encoding.Latin1.GetBytes(text); output.Write(b, 0, b.Length); }

        double tc = 0, tw = 0, size = 0;
        string font = "";
        var stack = new Stack<(double Tc, double Tw, double Size, string Font)>();
        var operands = new List<Tok>();

        string Num(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        string Hex(IEnumerable<byte> b) => "<" + string.Concat(b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture))) + ">";

        // one string of a text-show operator: pieces with the adjustment after every character that has spacing
        bool Adjusted(byte[] str, List<string> parts)
        {
            int len = bytesPerCode(font);
            if (len <= 0 || size <= 0) return false;
            var group = new List<byte>();
            for (int p = 0; p + len <= str.Length; p += len)
            {
                for (int k = 0; k < len; k++) group.Add(str[p + k]);
                double adj = tc + (len == 1 && str[p] == 32 ? tw : 0);
                if (adj != 0)
                {
                    parts.Add(Hex(group)); group.Clear();
                    parts.Add(Num(-adj / size * 1000));
                }
            }
            if (group.Count > 0) parts.Add(Hex(group));
            return true;
        }

        foreach (var t in toks)
        {
            if (t.Kind != K.Operator) { operands.Add(t); continue; }
            switch (t.Text)
            {
                case "q": stack.Push((tc, tw, size, font)); break;
                case "Q": if (stack.Count > 0) (tc, tw, size, font) = stack.Pop(); break;
                case "Tf":
                    if (operands.Count >= 2 && operands[^2].Kind == K.Name && operands[^1].Kind == K.Number) { font = operands[^2].Text; size = Math.Abs(operands[^1].Number); }
                    break;
                case "Tc":
                case "Tw":
                {
                    if (operands.Count < 1 || operands[^1].Kind != K.Number) return null;
                    double v = operands[^1].Number;
                    if (t.Text == "Tc") tc = v; else tw = v;
                    if (v != 0)
                    {
                        CopyTo(operands[^1].Start);
                        Write("0");
                        copied = operands[^1].End;
                        changed = true;
                    }
                    break;
                }
                case "'":
                case "\"":
                    if (tc != 0 || tw != 0 || t.Text == "\"") return null;          // (not worth the trouble: the page is left as it is)
                    break;
                case "Tj":
                {
                    if (tc == 0 && tw == 0) break;
                    if (operands.Count < 1 || operands[^1].Kind != K.String) return null;
                    var parts = new List<string>();
                    if (!Adjusted(operands[^1].Bytes, parts)) return null;
                    CopyTo(operands[^1].Start);
                    Write("[" + string.Join(" ", parts) + "] TJ");
                    copied = t.End;
                    changed = true;
                    break;
                }
                case "TJ":
                {
                    if (tc == 0 && tw == 0) break;
                    if (operands.Count < 1 || operands[^1].Kind != K.Array) return null;
                    var parts = new List<string>();
                    foreach (var item in operands[^1].Items)
                    {
                        if (item.Kind == K.String) { if (!Adjusted(item.Bytes, parts)) return null; }
                        else if (item.Kind == K.Number) parts.Add(Num(item.Number));
                        else return null;
                    }
                    CopyTo(operands[^1].Start);
                    Write("[" + string.Join(" ", parts) + "] TJ");
                    copied = t.End;
                    changed = true;
                    break;
                }
            }
            operands.Clear();
        }
        if (!changed) return null;
        CopyTo(s.Length);
        return output.ToArray();
    }

    // ---------- reading the content ----------
    private static bool Tokenize(byte[] s, ref int i, List<Tok> toks, bool inArray)
    {
        int n = s.Length;
        while (i < n)
        {
            byte c = s[i];
            if (Space(c)) { i++; continue; }
            if (c == '%') { while (i < n && s[i] != 10 && s[i] != 13) i++; continue; }
            int start = i;
            if (c == ']') { if (!inArray) return false; return true; }
            if (c == '[')
            {
                i++;
                var arr = new Tok { Kind = K.Array, Start = start };
                if (!Tokenize(s, ref i, arr.Items, inArray: true) || i >= n || s[i] != ']') return false;
                i++;
                arr.End = i;
                toks.Add(arr);
                continue;
            }
            if (c == '(')
            {
                var bytes = new List<byte>();
                int depth = 1; i++;
                while (i < n && depth > 0)
                {
                    byte b = s[i];
                    if (b == '\\')
                    {
                        i++;
                        if (i >= n) return false;
                        byte e = s[i];
                        switch (e)
                        {
                            case (byte)'n': bytes.Add(10); i++; break;
                            case (byte)'r': bytes.Add(13); i++; break;
                            case (byte)'t': bytes.Add(9); i++; break;
                            case (byte)'b': bytes.Add(8); i++; break;
                            case (byte)'f': bytes.Add(12); i++; break;
                            case 10: i++; break;                                                     // (a line break after a backslash: nothing)
                            case 13: i++; if (i < n && s[i] == 10) i++; break;
                            default:
                                if (e >= '0' && e <= '7')
                                {
                                    int v = 0, k = 0;
                                    while (k < 3 && i < n && s[i] >= '0' && s[i] <= '7') { v = v * 8 + (s[i] - '0'); i++; k++; }
                                    bytes.Add((byte)(v & 0xFF));
                                }
                                else { bytes.Add(e); i++; }
                                break;
                        }
                        continue;
                    }
                    if (b == '(') depth++;
                    else if (b == ')') { depth--; if (depth == 0) { i++; break; } }
                    bytes.Add(b); i++;
                }
                if (depth != 0) return false;
                toks.Add(new Tok { Kind = K.String, Start = start, End = i, Bytes = bytes.ToArray() });
                continue;
            }
            if (c == '<')
            {
                if (i + 1 < n && s[i + 1] == '<')                                                    // a dictionary (properties of a marked-content operator): skipped as one thing
                {
                    int depth = 0;
                    while (i + 1 < n)
                    {
                        if (s[i] == '<' && s[i + 1] == '<') { depth++; i += 2; }
                        else if (s[i] == '>' && s[i + 1] == '>') { depth--; i += 2; if (depth == 0) break; }
                        else if (s[i] == '(') { int d = 1; i++; while (i < n && d > 0) { if (s[i] == '\\') i++; else if (s[i] == '(') d++; else if (s[i] == ')') d--; i++; } }
                        else i++;
                    }
                    if (depth != 0) return false;
                    toks.Add(new Tok { Kind = K.Other, Start = start, End = i });
                    continue;
                }
                var hex = new List<byte>();
                i++;
                int nibble = -1;
                while (i < n && s[i] != '>')
                {
                    int v = HexVal(s[i]);
                    if (v >= 0) { if (nibble < 0) nibble = v; else { hex.Add((byte)(nibble * 16 + v)); nibble = -1; } }
                    i++;
                }
                if (i >= n) return false;
                if (nibble >= 0) hex.Add((byte)(nibble * 16));
                i++;
                toks.Add(new Tok { Kind = K.String, Start = start, End = i, Bytes = hex.ToArray() });
                continue;
            }
            if (c == '/')
            {
                i++;
                while (i < n && !Space(s[i]) && !Delim(s[i])) i++;
                toks.Add(new Tok { Kind = K.Name, Start = start, End = i, Text = Encoding.Latin1.GetString(s, start, i - start) });
                continue;
            }
            if (c == '>' || c == ')' || c == '{' || c == '}') return false;
            // a number or an operator
            while (i < n && !Space(s[i]) && !Delim(s[i])) i++;
            string word = Encoding.Latin1.GetString(s, start, i - start);
            if (word.Length > 0 && (char.IsDigit(word[0]) || word[0] is '-' or '+' or '.') && double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                toks.Add(new Tok { Kind = K.Number, Start = start, End = i, Number = num, Text = word });
                continue;
            }
            if (inArray) return false;                                                               // (only strings and numbers belong in a text-show array)
            if (word == "BI")                                                                        // an inline image: its data is not content: on to its EI
            {
                int p = i;
                while (p + 3 <= n && !(Space(s[p]) && s[p + 1] == 'E' && s[p + 2] == 'I' && (p + 3 >= n || Space(s[p + 3])))) p++;
                if (p + 3 > n) return false;
                i = p + 3;
                toks.Add(new Tok { Kind = K.Operator, Start = start, End = i, Text = "BI-image" });
                continue;
            }
            toks.Add(new Tok { Kind = K.Operator, Start = start, End = i, Text = word });
        }
        return !inArray;
    }

    private static int HexVal(byte b) => b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
}
