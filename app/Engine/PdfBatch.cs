using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;

namespace IdmClone.Engine;

public enum PdfBatchKind { Smaller, PageNumbers, Watermark, AddPassword, RemovePassword, PageSize, ToWord }

/// <summary>What to do to every file of a batch, and with which settings (only the settings of <see cref="Kind"/> are read).</summary>
public sealed class PdfBatchJob
{
    public PdfBatchKind Kind;
    public PdfReduceLevel Level = PdfReduceLevel.Recommended;
    public string Text = "";
    public PdfSpot Spot = PdfSpot.BottomCenter;
    public double Size = 10;
    public bool Slanted = true;
    public string Password = "";
    public PaperSize Paper = PdfResizer.Papers[0];
    public bool KeepDirection = true;
}

/// <param name="Output">the new file's bytes (null: nothing to write)</param>
/// <param name="Extension">".pdf" or ".docx"</param>
/// <param name="Note">what to tell the person: why nothing was written, or something to know about the copy</param>
public sealed record PdfBatchResult(byte[]? Output, string Extension, string Note);

/// <summary>Does one job to one file (the batch window calls it for each file; every file is independent, so one that can't be done leaves the others alone).</summary>
public static class PdfBatch
{
    public static string Suffix(PdfBatchKind kind, PdfBatchJob job) => kind switch
    {
        PdfBatchKind.Smaller => " (smaller)",
        PdfBatchKind.PageNumbers => " (numbered)",
        PdfBatchKind.Watermark => " (watermarked)",
        PdfBatchKind.AddPassword => " (protected)",
        PdfBatchKind.RemovePassword => " (no password)",
        PdfBatchKind.PageSize => " (" + job.Paper.Name.Split(' ')[0] + ")",
        _ => "",
    };

    /// <summary>The file name to save to: never an existing one ("name (2).pdf").</summary>
    public static string Target(string folder, string stem, string suffix, string extension)
    {
        string path = Path.Combine(folder, stem + suffix + extension);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}{suffix} ({n}){extension}");
        return path;
    }

    public static PdfBatchResult Process(string path, PdfBatchJob job, CancellationToken cancel)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        switch (job.Kind)
        {
            case PdfBatchKind.Smaller:
            {
                var r = PdfCompressor.Reduce(path, null, null, job.Level, null, cancel);
                return r.Helped ? new PdfBatchResult(r.Output, ".pdf", $"{PdfReduceWindow.Bytes(r.Before)} → {PdfReduceWindow.Bytes(r.After)}") : new PdfBatchResult(null, ".pdf", "already small: left as it is");
            }
            case PdfBatchKind.PageNumbers or PdfBatchKind.Watermark:
            {
                using var pdf = PdfFile.Open(path, null);
                if (pdf.IsProtected && !pdf.CanEdit) return new PdfBatchResult(null, ".pdf", "protected against changes");
                if (pdf.PageCount == 0) return new PdfBatchResult(null, ".pdf", "no pages");
                var line = job.Kind == PdfBatchKind.PageNumbers ? new PdfHeaderFooter { Text = job.Text, Spot = job.Spot, Size = job.Size } : null;
                var mark = job.Kind == PdfBatchKind.Watermark ? new PdfWatermark { Text = job.Text, Angle = job.Slanted ? -45 : 0 } : null;
                PdfMarkWriter.Apply(pdf, PdfPageMarks.Build(pdf.PageCount, pdf.PageSize, stem, 0, pdf.PageCount - 1, line, mark));
                return new PdfBatchResult(pdf.SaveToBytes(), ".pdf", "");
            }
            case PdfBatchKind.AddPassword:
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (var probe = PdfFile.Open(path, null)) { if (probe.IsProtected) return new PdfBatchResult(null, ".pdf", "already has a password"); }
                return new PdfBatchResult(PdfSecurity.Protect(bytes, null, job.Password, "", new PdfLimits()), ".pdf", "");
            }
            case PdfBatchKind.RemovePassword:
            {
                using (var probe = PdfFile.Open(path, job.Password)) { if (!probe.IsProtected) return new PdfBatchResult(null, ".pdf", "has no password"); }
                return new PdfBatchResult(PdfSecurity.Unprotect(File.ReadAllBytes(path), job.Password), ".pdf", "");
            }
            case PdfBatchKind.PageSize:
            {
                using var pdf = PdfFile.Open(path, null);
                if (pdf.IsProtected) return new PdfBatchResult(null, ".pdf", "protected: not resized");
                string note = PdfResizer.HasForm(pdf) ? "form fields are not kept" : "";
                return new PdfBatchResult(PdfResizer.Resize(pdf, job.Paper.Width, job.Paper.Height, job.KeepDirection, null, cancel), ".pdf", note);
            }
            default:
            {
                using var pdf = PdfFile.Open(path, null);
                if (pdf.IsProtected && !pdf.CanEdit) return new PdfBatchResult(null, ".docx", "its owner doesn't allow copying text out");
                var pages = new List<ExportPage>();
                for (int p = 0; p < pdf.PageCount; p++) { cancel.ThrowIfCancellationRequested(); pages.Add(PdfExport.Read(pdf, p, null, (0, 0))); }
                int empty = pages.Count(c => c.Runs.Count == 0);
                return new PdfBatchResult(PdfExport.ToDocx(pages), ".docx", empty > 0 ? $"{empty} page{(empty == 1 ? " has" : "s have")} no text (scans)" : "");
            }
        }
    }
}
