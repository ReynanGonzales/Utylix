using System;
using System.IO;

namespace IdmClone.Engine;

/// <summary>What a person with only the open password may do with a protected PDF.</summary>
public sealed record PdfLimits(bool Print = true, bool Copy = true, bool Edit = true)
{
    public bool Any => !Print || !Copy || !Edit;
}

/// <summary>Putting a password on a PDF (AES-256) and taking it off again, with PDFsharp (PDFium can't encrypt).</summary>
public static class PdfSecurity
{
    /// <summary>
    /// The PDF with new passwords. <paramref name="openWith"/> = the password the bytes need to be read (null when they are not protected).
    /// <paramref name="openPassword"/> is asked when the file is opened (empty = anyone can open it); <paramref name="limitPassword"/> lifts the limits
    /// (empty = the open password does that too).
    /// </summary>
    public static byte[] Protect(byte[] pdf, string? openWith, string openPassword, string limitPassword, PdfLimits limits)
    {
        if (openPassword.Length == 0 && !limits.Any) throw new InvalidOperationException("Nothing to protect: give a password or limit something.");
        if (limits.Any && limitPassword.Length == 0 && openPassword.Length == 0) throw new InvalidOperationException("A limit needs a password that can lift it.");
        using var input = new MemoryStream(pdf, writable: false);
        try
        {
            using var doc = Open(input, openWith);
            var security = doc.SecuritySettings;
            security.UserPassword = openPassword;
            security.OwnerPassword = limitPassword.Length > 0 ? limitPassword : openPassword;
            security.PermitPrint = limits.Print;
            security.PermitFullQualityPrint = limits.Print;
            security.PermitExtractContent = limits.Copy;
            security.PermitModifyDocument = limits.Edit;
            security.PermitAssembleDocument = limits.Edit;
            security.PermitAnnotations = limits.Edit;
            security.PermitFormsFill = true;
            doc.SecurityHandler.SetEncryptionToV5();                                 // AES-256
            using var output = new MemoryStream();
            doc.Save(output, false);
            return output.ToArray();
        }
        catch (Exception e) when (e is PdfSharp.Pdf.IO.PdfReaderException or NotImplementedException or NotSupportedException or InvalidCastException or ArgumentException or NullReferenceException)
        {
            throw new IOException("This PDF can't be given a password: " + e.Message);
        }
    }

    /// <summary>The PDF without any password or limit (<paramref name="password"/> = the owner password, or the one password it has).</summary>
    public static byte[] Unprotect(byte[] pdf, string password)
    {
        using var input = new MemoryStream(pdf, writable: false);
        try
        {
            using var doc = Open(input, password);
            doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
            using var output = new MemoryStream();
            doc.Save(output, false);
            return output.ToArray();
        }
        catch (Exception e) when (e is PdfSharp.Pdf.IO.PdfReaderException or NotImplementedException or NotSupportedException or InvalidCastException or ArgumentException or NullReferenceException)
        {
            throw new IOException("The password couldn't be taken off this PDF: " + e.Message);
        }
    }

    /// <summary>The message of the <see cref="PdfProtectedException"/> thrown when the password given only opens the PDF (the owner password is needed to change it).</summary>
    public const string OwnerPasswordNeeded = "owner password needed";

    private static PdfSharp.Pdf.PdfDocument Open(Stream input, string? password)
    {
        PdfSharp.Pdf.PdfDocument doc;
        try
        {
            doc = string.IsNullOrEmpty(password)
                ? PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify)
                : PdfSharp.Pdf.IO.PdfReader.Open(input, password, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        }
        catch (PdfSharp.Pdf.IO.PdfReaderException) when (!string.IsNullOrEmpty(password))
        {
            throw new PdfProtectedException(OwnerPasswordNeeded);          // (it opened with PDFium: PDFsharp refuses to CHANGE it without the owner password)
        }
        return doc;
    }
}
