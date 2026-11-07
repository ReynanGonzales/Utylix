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
/// </remarks>
internal static class PdfContentEditor
{
    private sealed class Op { public string Name = ""; public int First, Start, End; public List<double> Nums = new(); }

    private sealed class Unit
    {
        public int FirstOp, Op;                       // the first instruction of it (a path's first segment) and its painting instruction
        public bool Text, Unsafe;
        public double[] Ctm = Identity();             // the drawing matrix where it starts
        public int BlockOpen = -1, BlockClose = -1;   // the q and Q round it, when it is the only thing in them
        public double[] BlockCtm = Identity();
        public int BtOp = -1, EtOp = -1;              // the BT ... ET it is shown in, when that shows nothing else
        public double[] BtCtm = Identity();
    }

    private sealed record Span(int Start, int End, int InsideStart, bool Block, double[] Ctm);

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

    /// <param name="content">the page's instructions (all its content streams one after the other)</param>
    /// <param name="objectCount">how many objects PDFium found on the page</param>
    /// <param name="indices">the objects (PDFium's numbers) to change</param>
    /// <param name="matrix">where things go, in page coordinates, as a PDF matrix a b c d e f; null = take them away</param>
    /// <returns>the new instructions, or null when it can't be done safely (nothing is changed then)</returns>
    public static byte[]? Apply(byte[] content, int objectCount, IReadOnlyList<int> indices, double[]? matrix)
    {
        var toks = new List<PdfSpacingBake.Tok>();
        int pos = 0;
        if (!PdfSpacingBake.Tokenize(content, ref pos, toks, inArray: false)) return null;

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
                        if (units.Count - f.Units == 1 && units[f.Units].BlockOpen < 0) { var u = units[f.Units]; u.BlockOpen = f.Open; u.BlockClose = i; u.BlockCtm = f.Ctm; }
                    }
                    break;
                case "cm":
                    if (op.Nums.Count != 6) return null;
                    ctm = Mul(op.Nums.ToArray(), ctm);
                    break;
                case "m": case "l": case "c": case "v": case "y": case "h": case "re":
                    if (pathStart < 0) pathStart = i;
                    break;
                case "W": case "W*": clip = true; break;
                case "n": pathStart = -1; clip = false; break;
                case "BT": inText = true; btOp = i; unitsAtBt = units.Count; btCtm = ctm; break;
                case "ET":
                    if (inText)
                    {
                        int shown = units.Count - unitsAtBt;
                        for (int k = unitsAtBt; k < units.Count; k++)
                        {
                            if (shown == 1) { units[k].BtOp = btOp; units[k].EtOp = i; units[k].BtCtm = btCtm; }
                            else units[k].Unsafe = true;                               // (more than one thing shown in this BT ... ET: not moved on its own)
                        }
                    }
                    inText = false;
                    break;
                case "Tj": case "TJ": case "'": case "\"":
                    if (inText) units.Add(new Unit { FirstOp = i, Op = i, Text = true, Ctm = ctm });
                    break;
                case "Do": case "sh": case "BI":
                    units.Add(new Unit { FirstOp = i, Op = i, Ctm = ctm });
                    break;
                default:
                    if (IsPaint(op.Name))
                    {
                        units.Add(new Unit { FirstOp = pathStart < 0 ? i : pathStart, Op = i, Ctm = ctm, Unsafe = clip });
                        pathStart = -1; clip = false;
                    }
                    break;
            }
        }
        if (units.Count != objectCount) return null;

        // what to change: the block, or the text block, or the instructions of the thing itself
        var spans = new List<Span>();
        foreach (int index in indices.Distinct())
        {
            if (index < 0 || index >= units.Count) return null;
            var u = units[index];
            if (u.Unsafe) return null;
            Span s;
            if (u.BlockOpen >= 0) s = new Span(ops[u.BlockOpen].First, ops[u.BlockClose].End, ops[u.BlockOpen].End, true, u.BlockCtm);
            else if (u.Text)
            {
                if (u.BtOp < 0) return null;
                s = new Span(ops[u.BtOp].First, ops[u.EtOp].End, ops[u.BtOp].First, false, u.BtCtm);
            }
            else s = new Span(ops[u.FirstOp].First, ops[u.Op].End, ops[u.FirstOp].First, false, u.Ctm);
            if (!spans.Any(x => x.Start == s.Start && x.End == s.End)) spans.Add(s);
        }
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var kept = new List<Span>();
        foreach (var s in spans)                                                       // (a span inside another one goes with it)
        {
            if (kept.Count > 0 && s.Start >= kept[^1].Start && s.End <= kept[^1].End) continue;
            if (kept.Count > 0 && s.Start < kept[^1].End) return null;               // (overlapping without being inside: not understood)
            kept.Add(s);
        }

        // write it: from the end, so the places of the earlier ones stay
        var bytes = new List<byte>(content);
        foreach (var s in Enumerable.Reverse(kept))
        {
            if (matrix == null) { bytes.RemoveRange(s.Start, s.End - s.Start); continue; }
            var inverse = Invert(s.Ctm);
            if (inverse == null) return null;
            var u = Mul(Mul(s.Ctm, matrix), inverse);                                   // (in the space of that place: it ends up where the page matrix says)
            string cm = Num(u[0]) + " " + Num(u[1]) + " " + Num(u[2]) + " " + Num(u[3]) + " " + Num(u[4]) + " " + Num(u[5]) + " cm";
            if (s.Block) bytes.InsertRange(s.InsideStart, Encoding.ASCII.GetBytes(" " + cm + " "));
            else
            {
                bytes.InsertRange(s.End, Encoding.ASCII.GetBytes(" Q"));
                bytes.InsertRange(s.Start, Encoding.ASCII.GetBytes("q " + cm + " "));
            }
        }
        return bytes.ToArray();
    }
}
