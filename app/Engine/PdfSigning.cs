using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using PdfSharp.Drawing;
using PdfSharp.Pdf.Signatures;

namespace IdmClone.Engine;

/// <summary>One signature found in a PDF, as the file stores it.</summary>
public sealed record PdfSignatureInfo(byte[] Contents, int[] ByteRange, string SubFilter, string Reason, string Time);

/// <summary>What the check of one signature found.</summary>
public sealed record PdfSignatureCheck(string Signer, string Issuer, string When, string Reason, bool SignatureOk, bool WholeFile, bool Trusted, string Problem);

/// <summary>Where the visible signature goes.</summary>
public enum PdfSignSpot { None, BottomRight, BottomLeft, TopRight, TopLeft }

public sealed record PdfSignOptions(X509Certificate2 Certificate, string Reason, string Location, string ContactInfo, PdfSignSpot Spot, int PageIndex);

/// <summary>
/// Signing a PDF with a certificate (a PKCS#7 "adbe.pkcs7.detached" signature, which Acrobat and every other reader understand) and checking signatures.
/// A signature proves the file has not been changed since it was signed; WHO signed shows as trusted only when the reader trusts the certificate.
/// </summary>
public static class PdfSigning
{
    // ---------- certificates ----------
    /// <summary>The certificates of this Windows user that can sign (a private key, not expired, allowed for signing), newest first.</summary>
    public static List<X509Certificate2> UsableCertificates()
    {
        var list = new List<X509Certificate2>();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        foreach (var c in store.Certificates)
        {
            if (!c.HasPrivateKey || c.NotAfter < now || c.NotBefore > now) continue;
            if (!CanSign(c)) continue;
            list.Add(c);
        }
        return list.OrderByDescending(c => c.NotBefore).ToList();
    }

    private static readonly string[] SigningUses =
    {
        "1.3.6.1.5.5.7.3.4",            // e-mail protection (what most personal / work certificates carry)
        "1.3.6.1.5.5.7.3.3",            // code signing
        "1.3.6.1.4.1.311.10.3.12",      // document signing (Microsoft)
        "1.2.840.113583.1.1.5",         // Adobe PDF signing
        "2.5.29.37.0",                  // any use
    };

    private static bool CanSign(X509Certificate2 c)
    {
        foreach (var ext in c.Extensions)
        {
            if (ext is X509KeyUsageExtension ku && (ku.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0) return false;
            // a certificate that says what it is for (a web server's, say) must say signing documents / e-mail is among them
            if (ext is X509EnhancedKeyUsageExtension eku && eku.EnhancedKeyUsages.Count > 0 && !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => SigningUses.Contains(o.Value))) return false;
        }
        return true;
    }

    /// <summary>A certificate from a .pfx / .p12 file. Throws <see cref="CryptographicException"/> when the password is wrong.</summary>
    public static X509Certificate2 LoadFile(string path, string password)
    {
        var c = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        if (!c.HasPrivateKey) throw new CryptographicException("This file has no private key, so it can't sign.");
        if (c.NotAfter < DateTime.Now) throw new CryptographicException("This certificate expired on " + c.NotAfter.ToString("d") + ".");
        return c;
    }

    /// <summary>A new certificate made by the person for themselves (valid 5 years), kept in the Windows certificate store of this user.</summary>
    public static X509Certificate2 CreateOwn(string name, string email)
    {
        string subject = "CN=" + name.Replace(",", " ").Replace("=", " ").Trim() + (email.Trim().Length > 0 ? ", E=" + email.Trim().Replace(",", " ") : "");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        // (a copy that keeps its private key in this user's key store, so it is still there next time)
        byte[] pfx = made.Export(X509ContentType.Pfx, "utylix");
        var kept = X509CertificateLoader.LoadPkcs12(pfx, "utylix", X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(kept);
        return kept;
    }

    public static string Name(X509Certificate2 c) => c.GetNameInfo(X509NameType.SimpleName, false);
    public static string IssuerName(X509Certificate2 c) => c.GetNameInfo(X509NameType.SimpleName, true);
    public static bool SelfSigned(X509Certificate2 c) => c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData);

    // ---------- signing ----------
    /// <summary>The visible box: who signed, when, and why.</summary>
    private sealed class SignatureBox : PdfSharp.Pdf.Annotations.IAnnotationAppearanceHandler
    {
        private readonly string _who, _reason, _where;
        public SignatureBox(string who, string reason, string where) { _who = who; _reason = reason; _where = where; }
        public void DrawAppearance(XGraphics gfx, XRect rect)
        {
            var area = new XRect(0, 0, rect.Width, rect.Height);
            gfx.DrawRoundedRectangle(new XPen(XColor.FromArgb(255, 60, 60, 60), 0.8), area.X + 0.5, area.Y + 0.5, area.Width - 1, area.Height - 1, 6, 6);
            var bold = new XFont("Arial", 9, XFontStyleEx.Bold);
            var plain = new XFont("Arial", 8, XFontStyleEx.Regular);
            double y = 5;
            void Line(string text, XFont font)
            {
                if (text.Length == 0) return;
                gfx.DrawString(text, font, XBrushes.Black, new XRect(7, y, area.Width - 14, 12), XStringFormats.TopLeft);
                y += font.Size + 3.5;
            }
            Line("Digitally signed by " + _who, bold);
            Line("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), plain);
            if (_reason.Length > 0) Line("Reason: " + _reason, plain);
            if (_where.Length > 0) Line("Location: " + _where, plain);
        }
    }

    private sealed class CertificateSigner : IDigitalSigner
    {
        private readonly X509Certificate2 _cert;
        public CertificateSigner(X509Certificate2 cert) { _cert = cert; }
        public string CertificateName => Name(_cert);

        private byte[] Cms(byte[] data)
        {
            var cms = new SignedCms(new ContentInfo(data), detached: true);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _cert) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.WholeChain };
            signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
            cms.ComputeSignature(signer, silent: true);
            return cms.Encode();
        }

        // (the room kept for the signature in the file: the size of a signature over a dummy, plus some for the time attribute and the chain)
        public Task<int> GetSignatureSizeAsync() => Task.FromResult(Cms(new byte[] { 1 }).Length + 512);

        public Task<byte[]> GetSignatureAsync(Stream stream)
        {
            var data = new byte[stream.Length];                    // (ask for the length first: the stream sets itself up with it)
            int have = 0, read;
            while (have < data.Length && (read = stream.Read(data, have, data.Length - have)) > 0) have += read;
            if (have != data.Length) throw new IOException("The part of the file to sign couldn't be read.");
            return Task.FromResult(Cms(data));
        }
    }

    /// <summary>The signed copy of an (unprotected) PDF. Signing is the last thing: any change afterwards makes the signature say "changed since signing".</summary>
    public static byte[] Sign(byte[] pdf, PdfSignOptions o)
    {
        using var input = new MemoryStream(pdf, writable: false);
        try
        {
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
            int pageIndex = Math.Clamp(o.PageIndex, 0, doc.PageCount - 1);
            var page = doc.Pages[pageIndex];
            double w = page.Width.Point, h = page.Height.Point;
            const double bw = 210, bh = 56, edge = 28;
            // (PDFsharp's signature box is measured from the BOTTOM-left corner of the page, like PDF itself)
            var rect = o.Spot switch
            {
                PdfSignSpot.BottomLeft => new XRect(edge, edge, bw, bh),
                PdfSignSpot.TopRight => new XRect(w - edge - bw, h - edge - bh, bw, bh),
                PdfSignSpot.TopLeft => new XRect(edge, h - edge - bh, bw, bh),
                PdfSignSpot.BottomRight => new XRect(w - edge - bw, edge, bw, bh),
                _ => new XRect(0, 0, 0, 0),
            };
            try { PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true; } catch (InvalidOperationException) { }      // (the box is written in Arial)
            var options = new DigitalSignatureOptions
            {
                ContactInfo = o.ContactInfo, Location = o.Location, Reason = o.Reason, PageIndex = pageIndex, Rectangle = rect, AppName = "Utylix",
                AppearanceHandler = o.Spot != PdfSignSpot.None ? new SignatureBox(Name(o.Certificate), o.Reason, o.Location) : null,
            };
            DigitalSignatureHandler.ForDocument(doc, new CertificateSigner(o.Certificate), options);
            doc.Options.CompressContentStreams = true;
            using var output = new MemoryStream();
            doc.Save(output, false);
            return output.ToArray();
        }
        catch (Exception e) when (e is PdfSharp.Pdf.IO.PdfReaderException or NotImplementedException or NotSupportedException or InvalidCastException or ArgumentException or NullReferenceException or CryptographicException or InvalidOperationException)
        {
            throw new IOException("The PDF couldn't be signed: " + e.Message, e);
        }
    }

    // ---------- checking ----------
    /// <summary>Checks every signature of a PDF file (its bytes as they are on disk).</summary>
    public static List<PdfSignatureCheck> Check(byte[] file, IReadOnlyList<PdfSignatureInfo> signatures)
    {
        var results = new List<PdfSignatureCheck>();
        foreach (var s in signatures)
        {
            string reason = s.Reason, when = PdfTime(s.Time);
            try
            {
                var ranges = s.ByteRange;
                if (ranges.Length < 4) throw new CryptographicException("The signature doesn't say what it covers.");
                using var covered = new MemoryStream();
                for (int i = 0; i + 1 < ranges.Length; i += 2)
                {
                    if (ranges[i] < 0 || ranges[i + 1] < 0 || (long)ranges[i] + ranges[i + 1] > file.Length) throw new CryptographicException("The signature covers more than the file holds.");
                    covered.Write(file, ranges[i], ranges[i + 1]);
                }
                bool whole = (long)ranges[^2] + ranges[^1] == file.Length;
                var cms = new SignedCms(new ContentInfo(covered.ToArray()), detached: true);
                cms.Decode(TrimDer(s.Contents));
                string signer = "Unknown", issuer = "";
                bool trusted = false, ok;
                try { cms.CheckSignature(verifySignatureOnly: true); ok = true; }
                catch (CryptographicException) { ok = false; }
                var info = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
                var cert = info?.Certificate;
                if (cert != null)
                {
                    signer = Name(cert); issuer = IssuerName(cert);
                    using var chain = new X509Chain { ChainPolicy = { RevocationMode = X509RevocationMode.NoCheck } };
                    foreach (var extra in cms.Certificates) chain.ChainPolicy.ExtraStore.Add(extra);
                    trusted = chain.Build(cert) && !SelfSigned(cert);
                }
                if (when.Length == 0 && info != null)
                    foreach (var attr in info.SignedAttributes)
                        if (attr.Oid?.Value == "1.2.840.113549.1.9.5")
                        {
                            var t = new Pkcs9SigningTime(attr.Values[0].RawData);
                            when = t.SigningTime.ToLocalTime().ToString("g");
                        }
                results.Add(new PdfSignatureCheck(signer, issuer, when, reason, ok, whole, trusted, ""));
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException or InvalidOperationException)
            {
                results.Add(new PdfSignatureCheck("Unknown", "", when, reason, false, false, false, e.Message));
            }
        }
        return results;
    }

    /// <summary>The signature bytes without the zeros the file pads them with (the DER length says where it ends).</summary>
    private static byte[] TrimDer(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0x30) return data;
        int lenByte = data[1], header = 2, length;
        if (lenByte < 0x80) length = lenByte;
        else
        {
            int n = lenByte & 0x7F; if (n == 0 || n > 4 || data.Length < 2 + n) return data;
            length = 0; for (int i = 0; i < n; i++) length = (length << 8) | data[2 + i];
            header = 2 + n;
        }
        int total = header + length;
        return total <= data.Length ? data[..total] : data;
    }

    /// <summary>"D:20261005143012+08'00'" -> a local date and time, or empty.</summary>
    private static string PdfTime(string t)
    {
        if (!t.StartsWith("D:") || t.Length < 16) return "";
        return DateTime.TryParseExact(t.Substring(2, 14), "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("g") : "";
    }
}
