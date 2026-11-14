using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace IdmClone.Engine;

/// <summary>
/// Moves, resizes or takes away things already on a page by changing the page's OWN drawing instructions in place, the way Acrobat and Nitro do, instead of letting PDFium write
/// the whole page again (which loses what it has no place for: soft shadows, gradient masks, blends, so those parts come out black). Only a few bytes are put in around the
/// instructions that draw the chosen thing; everything else stays byte for byte as it was.
/// </summary>
/// <remarks>
/// PDFium lists the objects of a page in drawing order, one for each painting instruction (a path painted, a text shown, an image / form / shading drawn). The same count and order
/// is found here by reading the instructions; when they do not agree, or something is not understood, <see cref="Apply"/> says null and the caller does it the old way.
/// A thing alone in its q ... Q block is moved with the whole block (its clip with it); otherwise the instructions of the thing are wrapped in q ... Q on their own.
/// A line of text that shares its BT ... ET with other lines is moved with a Td before it and the opposite Td after it (when it starts a line and the next text is placed by its own
/// instruction), so the lines after it stay where they are.
/// </remarks>
internal static class PdfContentEditor
{
    /// <summary>Why the last <see cref="Apply"/> said no (for the log).</summary>
    public static string LastReason { get; private set; } = "";

    private sealed class Op { public string Name = ""; public int First, Start, End; public List<double> Nums = new(); }

    private sealed class Unit
    {
        public int FirstOp, Op;                       // the first instruction of it (a path's first segment) and its painting instruction
        public bool Text, Unsafe;
        public int ClipOuter = -1;                    // the q (its instruction number) of the outermost frame round it that has a clipping path of its own; -2 = a clipping path outside any q; -1 = none
        public double[] Ctm = Identity();             // the drawing matrix where it starts
        public int BlockOpen = -1, BlockClose = -1;   // the q and Q round it, when it is the only thing in them
        public double[] BlockCtm = Identity();
        public int BtOp = -1, EtOp = -1, BtIndex = -1;            // the BT ... ET it is shown in, when that shows nothing else
        public double[] BtCtm = Identity();
        public bool InBt;                             // a text shown in a BT ... ET (that may show others too)
        public double[] ShowTlm = Identity();         // the line matrix where it is shown
        public bool FirstOnLine, NextPositioned;      // it starts at the start of its line / what is shown after it is placed by its own instruction (or nothing follows)
    }

    private sealed record Edit(int Pos, int Remove, string Insert);
    private sealed record Span(int Start, int End, List<Edit> Edits);

    private static double[] Identity() => new double[] { 1, 0, 0, 1, 0, 0 };

    /// <summary>A then B (row vectors, as the PDF matrices work): the point goes through A first.</summary>
    private static double[] Mul(double[] a, double[] b) => new[]
    {
        a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3],
        a[2] * b[0] + a[3] * b[2], a[2] * b[1] + a[3] * b[3],
        a[4] * b[0] + a[5] * b[2] + b[4], a[4] * b[1] + a[5] * b[3] + b[5],
    };

    private static double[]? Invert(double[] m)
    {
        double det = m[0] * m[3] - m[1] * m[2];
        if (Math.Abs(det) < 1e-12) return null;
        return new[] { m[3] / det, -m[1] / det, -m[2] / det, m[0] / det, (m[2] * m[5] - m[3] * m[4]) / det, (m[1] * m[4] - m[0] * m[5]) / det };
    }

    private static string Num(double v) => Math.Round(v, 5).ToString("0.#####", CultureInfo.InvariantCulture);

    private static bool IsPaint(string op) => op is "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "S" or "s";

    private static byte[]? No(string why) { LastReason = why; return null; }

    /// <param name="content">the page's instructions (all its content streams one after the other)</param>
    /// <param name="objectCount">how many objects PDFium found on the page</param>
    /// <param name="indices">the objects (PDFium's numbers) to change</param>
    /// <param name="matrix">where things go, in page coordinates, as a PDF matrix a b c d e f; null = take them away</param>
    /// <returns>the new instructions, or null when it can't be done safely (nothing is changed then)</returns>
    public static byte[]? Apply(byte[] content, int objectCount, IReadOnlyList<int> indices, double[]? matrix, double pageArea = 0)
    {
        LastReason = "";
        var toks = new List<PdfSpacingBake.Tok>();
        int pos = 0;
        if (!PdfSpacingBake.Tokenize(content, ref pos, toks, inArray: false)) return No("the instructions could not be read");

        // instructions: the operands in front and the operator
        var ops = new List<Op>();
        int operandStart = -1; var nums = new List<double>();
        foreach (var t in toks)
        {
            if (t.Kind != PdfSpacingBake.K.Operator)
            {
                if (operandStart < 0) operandStart = t.Start;
                if (t.Kind == PdfSpacingBake.K.Number) nums.Add(t.Number);
                continue;
            }
            ops.Add(new Op { Name = t.Text == "BI-image" ? "BI" : t.Text, First = operandStart < 0 ? t.Start : operandStart, Start = t.Start, End = t.End, Nums = nums });
            operandStart = -1; nums = new List<double>();
        }

        // the things that are drawn, in order
        var units = new List<Unit>();
        var ctm = Identity();
        var stack = new Stack<(double[] Ctm, int Open, int Units)>();
        int pathStart = -1; bool clip = false, inText = false; int btOp = -1, unitsAtBt = 0; double[] btCtm = Identity();
        double[] tlm = Identity(); double leading = 0; bool positioned = false; Unit? lastShow = null;
        // clipping paths: a picture drawn as "q <box> re W n q <matrix> cm /Im Do Q Q" is clipped to its box; moving only the inner q would leave the box behind and cut the picture off.
        // Each frame (q) that has a clipping path of its own is remembered; a clip that covers (nearly) the whole page is not one that matters.
        var clipOpens = new List<int>(); bool topClip = false;
        double[]? pathRect = null; int pathOps = 0;
        int ClipNow() => clipOpens.Count > 0 ? clipOpens.Min() : topClip ? -2 : -1;
        void InstallClip()
        {
            if (pageArea > 0 && pathRect != null && pathOps == 1)
            {
                var a = new[] { pathRect[0], pathRect[1], 1.0 }; var b = new[] { pathRect[0] + pathRect[2], pathRect[1] + pathRect[3], 1.0 };
                double x0 = a[0] * ctm[0] + a[1] * ctm[2] + ctm[4], y0 = a[0] * ctm[1] + a[1] * ctm[3] + ctm[5];
                double x1 = b[0] * ctm[0] + b[1] * ctm[2] + ctm[4], y1 = b[0] * ctm[1] + b[1] * ctm[3] + ctm[5];
                if (Math.Abs((x1 - x0) * (y1 - y0)) >= pageArea * 0.8) return;                 // (a page-wide clip: nothing is cut off by it)
            }
            if (stack.Count > 0) { int open = stack.Peek().Open; if (!clipOpens.Contains(open)) clipOpens.Add(open); }
            else topClip = true;
        }
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            switch (op.Name)
            {
                case "q": stack.Push((ctm, i, units.Count)); break;
                case "Q":
                    if (stack.Count > 0)
                    {
                        var f = stack.Pop();
                        ctm = f.Ctm;
                        clipOpens.Remove(f.Open);
                        // (the outermost frame that holds only this thing wins: the clip of a picture sits in the frame round its own q)
                        if (units.Count - f.Units == 1) { var only = units[f.Units]; only.BlockOpen = f.Open; only.BlockClose = i; only.BlockCtm = f.Ctm; }
                    }
                    break;
                case "cm":
                    if (op.Nums.Count != 6) return No("a cm without six numbers");
                    ctm = Mul(op.Nums.ToArray(), ctm);
                    break;
                case "m": case "l": case "c": case "v": case "y": case "h": case "re":
                    if (pathStart < 0) pathStart = i;
                    pathOps++; pathRect = op.Name == "re" && op.Nums.Count == 4 && pathOps == 1 ? op.Nums.ToArray() : null;
                    break;
                case "W": case "W*": clip = true; break;
                case "n": if (clip) InstallClip(); pathStart = -1; clip = false; pathRect = null; pathOps = 0; break;
                case "BT": inText = true; btOp = i; unitsAtBt = units.Count; btCtm = ctm; tlm = Identity(); positioned = true; lastShow = null; break;
                case "Tm": if (op.Nums.Count == 6) tlm = op.Nums.ToArray(); positioned = true; break;
                case "Td": if (op.Nums.Count == 2) tlm = Mul(new[] { 1, 0, 0, 1, op.Nums[0], op.Nums[1] }, tlm); positioned = true; break;
                case "TD": if (op.Nums.Count == 2) { leading = -op.Nums[1]; tlm = Mul(new[] { 1, 0, 0, 1, op.Nums[0], op.Nums[1] }, tlm); } positioned = true; break;
                case "TL": if (op.Nums.Count == 1) leading = op.Nums[0]; break;
                case "T*": tlm = Mul(new[] { 1, 0, 0, 1, 0, -leading }, tlm); positioned = true; break;
                case "ET":
                    if (inText)
                    {
                        int shown = units.Count - unitsAtBt;
                        for (int k = unitsAtBt; k < units.Count; k++)
                        {
                            if (shown == 1) { units[k].BtOp = btOp; units[k].EtOp = i; units[k].BtCtm = btCtm; }
                        }
                        if (lastShow != null) lastShow.NextPositioned = true;               // (nothing is shown after the last one in the block)
                    }
                    inText = false; lastShow = null;
                    break;
                case "Tj": case "TJ": case "'": case "\"":
                    if (inText)
                    {
                        bool own = op.Name is "'" or "\"";                               // (these two move to the next line themselves)
                        if (own) tlm = Mul(new[] { 1, 0, 0, 1, 0, -leading }, tlm);
                        var unit = new Unit { FirstOp = i, Op = i, Text = true, Ctm = ctm, InBt = true, BtIndex = btOp, ShowTlm = tlm, FirstOnLine = own || positioned, ClipOuter = ClipNow() };
                        if (lastShow != null) lastShow.NextPositioned = own || positioned;
                        units.Add(unit); lastShow = unit; positioned = false;
                    }
                    break;
                case "Do": case "sh": case "BI":
                    units.Add(new Unit { FirstOp = i, Op = i, Ctm = ctm, ClipOuter = ClipNow() });
                    break;
                default:
                    if (IsPaint(op.Name))
                    {
                        units.Add(new Unit { FirstOp = pathStart < 0 ? i : pathStart, Op = i, Ctm = ctm, Unsafe = clip, ClipOuter = ClipNow() });
                        if (clip) InstallClip();
                        pathStart = -1; clip = false; pathRect = null; pathOps = 0;
                    }
                    break;
            }
        }
        if (units.Count != objectCount) return No($"the page's instructions show {units.Count} things but PDFium found {objectCount}");

        // what to change
        var spans = new List<Span>();
        var shared = new List<int>();
        foreach (int index in indices.Distinct())
        {
            if (index < 0 || index >= units.Count) return No("an object number out of range");
            var u = units[index];
            if (u.Unsafe) return No("a thing drawn together with a clipping path");
            if (u.ClipOuter == -2 || (u.ClipOuter >= 0 && !(u.BlockOpen >= 0 && u.BlockOpen <= u.ClipOuter))) return No("a clipping path that would stay where the thing was");
            var edits = new List<Edit>();
            int start, end;
            if (u.BlockOpen >= 0)
            {
                start = ops[u.BlockOpen].First; end = ops[u.BlockClose].End;
                if (matrix == null) edits.Add(new Edit(start, end - start, ""));
                else { var cm = CmFor(u.BlockCtm, matrix); if (cm == null) return No("a singular drawing matrix"); edits.Add(new Edit(ops[u.BlockOpen].End, 0, " " + cm + " ")); }
            }
            else if (u.Text && u.BtOp >= 0)
            {
                start = ops[u.BtOp].First; end = ops[u.EtOp].End;
                if (matrix == null) edits.Add(new Edit(start, end - start, ""));
                else { var cm = CmFor(u.BtCtm, matrix); if (cm == null) return No("a singular drawing matrix"); edits.Add(new Edit(start, 0, "q " + cm + " ")); edits.Add(new Edit(end, 0, " Q")); }
            }
            else if (u.Text) { shared.Add(index); continue; }                             // (a line of text among others in its BT ... ET: handled below)
            else
            {
                start = ops[u.FirstOp].First; end = ops[u.Op].End;
                if (matrix == null) edits.Add(new Edit(start, end - start, ""));
                else { var cm = CmFor(u.Ctm, matrix); if (cm == null) return No("a singular drawing matrix"); edits.Add(new Edit(start, 0, "q " + cm + " ")); edits.Add(new Edit(end, 0, " Q")); }
            }
            if (!spans.Any(x => x.Start == start && x.End == end)) spans.Add(new Span(start, end, edits));
        }
        // lines of text among others: the ones that follow each other in a BT ... ET are moved together (one Td before the first, the opposite Td after the last);
        // taken away, each show goes on its own. Either way nothing after them may depend on where they ended.
        shared.Sort();
        for (int g = 0; g < shared.Count;)
        {
            int h = g;
            while (h + 1 < shared.Count && shared[h + 1] == shared[h] + 1 && units[shared[h + 1]].BtIndex == units[shared[g]].BtIndex) h++;
            var first = units[shared[g]]; var last = units[shared[h]];
            if (!first.FirstOnLine || !last.NextPositioned) return No("a text that is not at the start of its line, or is followed by text that is not placed on its own");
            if (matrix == null)
            {
                for (int k = g; k <= h; k++) { int s0 = ops[units[shared[k]].Op].First, e0 = ops[units[shared[k]].Op].End; spans.Add(new Span(s0, e0, new List<Edit> { new Edit(s0, e0 - s0, "") })); }
            }
            else
            {
                if (Math.Abs(matrix[0] - 1) > 1e-9 || Math.Abs(matrix[3] - 1) > 1e-9 || Math.Abs(matrix[1]) > 1e-9 || Math.Abs(matrix[2]) > 1e-9) return No("a text line inside a shared text block can only be moved, not stretched");
                var lin = Mul(first.ShowTlm, first.Ctm);
                var inv = Invert(new[] { lin[0], lin[1], lin[2], lin[3], 0, 0 });
                if (inv == null) return No("a singular text matrix");
                double tx = matrix[4] * inv[0] + matrix[5] * inv[2], ty = matrix[4] * inv[1] + matrix[5] * inv[3];
                int s1 = ops[first.Op].First, e1 = ops[last.Op].End;                spans.Add(new Span(s1, e1, new List<Edit> { new Edit(s1, 0, Num(tx) + " " + Num(ty) + " Td "), new Edit(e1, 0, " " + Num(-tx) + " " + Num(-ty) + " Td") }));
            }
            g = h + 1;
        }
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var kept = new List<Span>();
        foreach (var s in spans)                                                       // (a span inside another one goes with it)
        {
            if (kept.Count > 0 && s.Start >= kept[^1].Start && s.End <= kept[^1].End) continue;
            if (kept.Count > 0 && s.Start < kept[^1].End) return No("two changes overlap");
            kept.Add(s);
        }

        // write it: from the end, so the places of the earlier ones stay
        var bytes = new List<byte>(content);
        foreach (var edit in kept.SelectMany(s => s.Edits).OrderByDescending(e => e.Pos).ThenByDescending(e => e.Remove))
        {
            if (edit.Remove > 0) bytes.RemoveRange(edit.Pos, edit.Remove);
            if (edit.Insert.Length > 0) bytes.InsertRange(edit.Pos, Encoding.ASCII.GetBytes(edit.Insert));
        }
        return bytes.ToArray();
    }

    /// <summary>The "a b c d e f cm" that does what the page matrix says to things drawn with matrix <paramref name="c"/>; null when that matrix can't be turned round.</summary>
    private static string? CmFor(double[] c, double[] m)
    {
        var inverse = Invert(c);
        if (inverse == null) return null;
        var u = Mul(Mul(c, m), inverse);                                                // (in the space of that place: it ends up where the page matrix says)
        return Num(u[0]) + " " + Num(u[1]) + " " + Num(u[2]) + " " + Num(u[3]) + " " + Num(u[4]) + " " + Num(u[5]) + " cm";
    }
}
