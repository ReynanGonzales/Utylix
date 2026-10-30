using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

/// <summary>The PDF needs a password (or the one given is wrong).</summary>
public sealed class PdfPasswordException : Exception
{
    public PdfPasswordException() : base("This PDF needs a password.") { }
}

/// <summary>The PDF is protected against changes: changing it needs its owner (permissions) password.</summary>
public sealed class PdfProtectedException : Exception
{
    public PdfProtectedException(string message) : base(message) { }
}

/// <summary>
/// One open PDF, read through PDFium. The file is read into memory, so it is not locked and can be overwritten while it is shown.
/// Every method may be called from any thread (PDFium itself is used one call at a time).
/// </summary>
public sealed partial class PdfFile : IDisposable
{
    private IntPtr _doc, _data;
    public string Path { get; }
    public string? Password { get; }
    public long Length { get; }
    public int PageCount { get; private set; }
    /// <summary>The file is encrypted (it has an open password, or an owner password that limits changes).</summary>
    public bool IsProtected { get; }
    /// <summary>The permission bits of a protected file (print = 4, change = 8, copy = 16, comment = 32, fill forms = 256, assemble = 1024, high-quality print = 2048).</summary>
    public uint Permissions { get; }

    private PdfFile(string path, string? password, IntPtr doc, IntPtr data, long length)
    {
        Path = path; Password = password; _doc = doc; _data = data; Length = length;
        lock (Pdfium.Sync)
        {
            PageCount = Pdfium.FPDF_GetPageCount(doc);
            IsProtected = Pdfium.FPDF_GetSecurityHandlerRevision(doc) >= 0;
            Permissions = IsProtected ? Pdfium.FPDF_GetDocUserPermissions(doc) : 0xFFFFFFFF;
            InitForm();
        }
    }

    /// <summary>Opens a PDF. Throws <see cref="PdfPasswordException"/> when it needs a (different) password, IOException when it can't be read.</summary>
    public static PdfFile Open(string path, string? password = null)
    {
        Pdfium.Init();
        byte[] bytes = ReadShared(path);
        var file = TryLoad(path, password, bytes, out uint error);
        if (file != null) return file;
        if (error == Pdfium.ErrPassword) throw new PdfPasswordException();

        // A file from a shared folder / server is sometimes not quite a clean PDF (a few bytes in front of the header, a file that was cut short or rewritten by
        // some server software): the same file is tried again after the usual repairs, before it is given up.
        foreach (byte[] repaired in Repairs(bytes, password))
        {
            file = TryLoad(path, password, repaired, out uint again);
            if (file != null) { LogOpen(path, bytes, error, "opened after a repair"); return file; }
            if (again == Pdfium.ErrPassword) throw new PdfPasswordException();
        }
        LogOpen(path, bytes, error, "not opened");
        throw new IOException(error switch
        {
            2 => "The file can't be found or opened.",
            3 => "This doesn't look like a PDF, or it is damaged (Utylix also tried to repair it).",
            5 => "This PDF uses a kind of protection Utylix can't open.",
            _ => "This PDF can't be opened.",
        } + $" (size {bytes.Length:N0} bytes, code {error})");
    }

    /// <summary>Opens a PDF that is only in memory (<paramref name="name"/> is what it is called).</summary>
    public static PdfFile OpenBytes(byte[] bytes, string name)
    {
        Pdfium.Init();
        return TryLoad(name, null, bytes, out uint error) ?? throw new IOException("This PDF can't be opened (code " + error + ").");
    }

    /// <summary>The file's bytes. Read so that a file somebody else has open (a server file in use, the same file open in another program) can still be read; a network hiccup gets two more tries.</summary>
    private static byte[] ReadShared(string path)
    {
        // a file on a server or a network drive that is still reconnecting (Recent list, a shortcut after the PC woke up) often answers only on the 2nd or 3rd try
        bool network = OpenPath.IsNetwork(path);
        int tries = network ? 5 : 2;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                var bytes = new byte[fs.Length];
                int got = 0;
                while (got < bytes.Length) { int n = fs.Read(bytes, got, bytes.Length - got); if (n <= 0) break; got += n; }
                if (got < bytes.Length) Array.Resize(ref bytes, got);
                return bytes;
            }
            catch (IOException) when (attempt < tries) { System.Threading.Thread.Sleep(network ? 600 * (attempt + 1) : 400); }
            catch (Exception e) when (network && e is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Utylix can't reach this file on the network (" + e.Message.TrimEnd('.') + "). Check that the server or drive is connected, then try again.", e);
            }
        }
    }

    private static PdfFile? TryLoad(string path, string? password, byte[] bytes, out uint error)
    {
        error = 0;
        if (bytes.Length == 0) { error = 3; return null; }
        IntPtr data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        lock (Pdfium.Sync)
        {
            IntPtr doc = Pdfium.FPDF_LoadMemDocument64(data, (UIntPtr)bytes.Length, password);
            if (doc != IntPtr.Zero) return new PdfFile(path, password, doc, data, bytes.Length);
            error = Pdfium.FPDF_GetLastError();
            Marshal.FreeHGlobal(data);
            return null;
        }
    }

    /// <summary>Copies of the bytes with the usual damage taken out, in the order to try them.</summary>
    private static IEnumerable<byte[]> Repairs(byte[] bytes, string? password)
    {
        // 1) something in front of "%PDF-" (PDFium only looks for the header at the very start)
        int header = IndexOf(bytes, "%PDF-"u8);
        if (header > 0) yield return bytes[header..];

        // 2) read and written again by PdfSharp, which rebuilds a broken cross-reference table
        byte[]? rewritten = null;
        try
        {
            using var input = new MemoryStream(header > 0 ? bytes[header..] : bytes);
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, password ?? "", PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            using var output = new MemoryStream();
            doc.Save(output, false);
            rewritten = output.ToArray();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { /* it is not repairable that way */ }
        if (rewritten != null) yield return rewritten;
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern)
    {
        int limit = Math.Min(data.Length, 1 << 20);                      // (the header is at the start: no need to look further than the first megabyte)
        return data.AsSpan(0, limit).IndexOf(pattern);
    }

    /// <summary>A line in pdf-open.log (data folder) for a file that gave trouble, so the cause can be found afterwards.</summary>
    private static void LogOpen(string path, byte[] bytes, uint error, string result)
    {
        try
        {
            string head = BitConverter.ToString(bytes, 0, Math.Min(16, bytes.Length));
            string tail = BitConverter.ToString(bytes, Math.Max(0, bytes.Length - 8), Math.Min(8, bytes.Length));
            File.AppendAllText(System.IO.Path.Combine(App.DataDir, "pdf-open.log"),
                $"{DateTime.Now:s}  {result}  code {error}  {bytes.Length} bytes  starts {head}  ends {tail}  {path}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The page's size in points (1/72 inch), already turned the way the PDF says.</summary>
    public Size PageSize(int index)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            return Pdfium.FPDF_GetPageSizeByIndexF(_doc, index, out var s) != 0 ? new Size(s.Width, s.Height) : new Size(612, 792);
        }
    }

    /// <summary>Draws a page into a picture of the given size in pixels (white paper). rotate: 0..3 quarter turns clockwise.</summary>
    /// <param name="forScreen">false for printing and for making pictures of pages: form fields without their highlight</param>
    public BitmapSource Render(int index, int width, int height, int rotate = 0, bool forScreen = true)
    {
        width = Math.Clamp(width, 1, 12000); height = Math.Clamp(height, 1, 12000);
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) throw new IOException("Page " + (index + 1) + " can't be read.");
            IntPtr bmp = Pdfium.FPDFBitmap_CreateEx(width, height, Pdfium.BitmapBgrx, IntPtr.Zero, 0);
            try
            {
                if (bmp == IntPtr.Zero) throw new OutOfMemoryException();
                Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, width, height, 0xFFFFFFFF);
                Pdfium.FPDF_RenderPageBitmap(bmp, page, 0, 0, width, height, rotate & 3, Pdfium.RenderAnnotations | Pdfium.RenderLcdText);
                DrawForm(page, bmp, width, height, rotate & 3, forScreen);
                int stride = Pdfium.FPDFBitmap_GetStride(bmp);
                var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, Pdfium.FPDFBitmap_GetBuffer(bmp), stride * height, stride);
                picture.Freeze();
                return picture;
            }
            finally
            {
                if (bmp != IntPtr.Zero) Pdfium.FPDFBitmap_Destroy(bmp);
                Pdfium.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// The page as it looks WITHOUT some of its text objects (their places in the page's list, as <see cref="PdfTextRun.Index"/>): what is behind them (a coloured cell, a watermark, a picture) stays.
    /// The objects are only taken out of the page being drawn; the document is not changed.
    /// </summary>
    public BitmapSource RenderWithout(int index, int width, int height, IReadOnlyCollection<int> objects)
    {
        width = Math.Clamp(width, 1, 12000); height = Math.Clamp(height, 1, 12000);
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) throw new IOException("Page " + (index + 1) + " can't be read.");
            IntPtr bmp = Pdfium.FPDFBitmap_CreateEx(width, height, Pdfium.BitmapBgrx, IntPtr.Zero, 0);
            try
            {
                if (bmp == IntPtr.Zero) throw new OutOfMemoryException();
                foreach (int i in objects.Distinct().OrderByDescending(x => x))            // (the highest first: taking one out moves the ones after it)
                {
                    IntPtr obj = Pdfium.FPDFPage_GetObject(page, i);
                    if (obj == IntPtr.Zero || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjText) continue;
                    if (Pdfium.FPDFPage_RemoveObject(page, obj) != 0) Pdfium.FPDFPageObj_Destroy(obj);
                }
                Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, width, height, 0xFFFFFFFF);
                Pdfium.FPDF_RenderPageBitmap(bmp, page, 0, 0, width, height, 0, Pdfium.RenderAnnotations | Pdfium.RenderLcdText);
                int stride = Pdfium.FPDFBitmap_GetStride(bmp);
                var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, Pdfium.FPDFBitmap_GetBuffer(bmp), stride * height, stride);
                picture.Freeze();
                return picture;
            }
            finally
            {
                if (bmp != IntPtr.Zero) Pdfium.FPDFBitmap_Destroy(bmp);
                Pdfium.FPDF_ClosePage(page);
            }
        }
    }

    private readonly Dictionary<int, PdfPageText> _texts = new();

    /// <summary>A page's text, letter by letter with positions, plus its links and notes (read once, then remembered).</summary>
    public PdfPageText GetText(int index)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            if (_texts.TryGetValue(index, out var t)) return t;
            t = PdfTextReader.Read(_doc, index);
            _texts[index] = t;
            return t;
        }
    }

    private readonly Dictionary<int, List<PdfTextRun>> _runs = new();

    /// <summary>The pieces of upright text of a page that can be changed (read once, then remembered).</summary>
    public List<PdfTextRun> GetTextRuns(int index)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            if (_runs.TryGetValue(index, out var r)) return r;
            r = PdfTextRuns.Group(PdfTextRuns.Read(_doc, index));        // (a line stored in bits is one piece for the person)
            _runs[index] = r;
            return r;
        }
    }

    /// <summary>The PDF's table of contents (empty when it has none).</summary>
    public List<PdfBookmark> GetBookmarks()
    {
        lock (Pdfium.Sync) { ThrowIfClosed(); return PdfTextReader.Bookmarks(_doc); }
    }

    /// <summary>Changes are allowed: not protected, or protected but opened with a password that allows changing or commenting.</summary>
    public bool CanEdit
    {
        get
        {
            if (!IsProtected) return true;
            lock (Pdfium.Sync) { ThrowIfClosed(); return (Pdfium.FPDF_GetDocPermissions(_doc) & (Pdfium.PermModify | Pdfium.PermAnnotate)) != 0; }
        }
    }

    /// <summary>The whole document as a PDF file (with the changes made to it; a protected one stays protected).</summary>
    public byte[] SaveToBytes()
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            if (_form != IntPtr.Zero) Pdfium.FORM_ForceToKillFocus(_form);        // (a field being typed in keeps its text)
            using var ms = new MemoryStream();
            if (!Pdfium.Save(_doc, ms)) throw new IOException("The PDF couldn't be written.");
            return ms.ToArray();
        }
    }

    /// <summary>The digital signatures the PDF carries (empty for most files).</summary>
    public List<PdfSignatureInfo> GetSignatures()
    {
        var list = new List<PdfSignatureInfo>();
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            int count = Math.Max(0, Pdfium.FPDF_GetSignatureCount(_doc));
            for (int i = 0; i < count; i++)
            {
                IntPtr sig = Pdfium.FPDF_GetSignatureObject(_doc, i);
                if (sig == IntPtr.Zero) continue;
                uint n = Pdfium.FPDFSignatureObj_GetContents(sig, null, 0);
                var contents = new byte[n];
                if (n > 0) Pdfium.FPDFSignatureObj_GetContents(sig, contents, n);
                uint rn = Pdfium.FPDFSignatureObj_GetByteRange(sig, null, 0);
                var ranges = new int[rn];
                if (rn > 0) Pdfium.FPDFSignatureObj_GetByteRange(sig, ranges, rn);
                string Ascii(Func<byte[]?, uint, uint> get)
                {
                    uint len = get(null, 0);
                    if (len <= 1 || len > 4096) return "";
                    var buf = new byte[len];
                    get(buf, len);
                    return System.Text.Encoding.ASCII.GetString(buf, 0, (int)len - 1);
                }
                string reason = "";
                uint reasonLen = Pdfium.FPDFSignatureObj_GetReason(sig, null, 0);
                if (reasonLen > 2 && reasonLen < 8192)
                {
                    var rb = new byte[reasonLen];
                    Pdfium.FPDFSignatureObj_GetReason(sig, rb, reasonLen);
                    reason = System.Text.Encoding.Unicode.GetString(rb, 0, (int)reasonLen - 2);
                }
                list.Add(new PdfSignatureInfo(contents, ranges, Ascii((b, l) => Pdfium.FPDFSignatureObj_GetSubFilter(sig, b, l)), reason, Ascii((b, l) => Pdfium.FPDFSignatureObj_GetTime(sig, b, l))));
            }
        }
        return list;
    }

    /// <summary>The raw PDFium document, for the engines in this folder (use under <see cref="Pdfium.Sync"/>).</summary>
    internal IntPtr Handle { get { ThrowIfClosed(); return _doc; } }

    private void ThrowIfClosed() { if (_doc == IntPtr.Zero) throw new ObjectDisposedException(nameof(PdfFile)); }

    public void Dispose()
    {
        lock (Pdfium.Sync)
        {
            ExitForm();
            if (_doc != IntPtr.Zero) { Pdfium.FPDF_CloseDocument(_doc); _doc = IntPtr.Zero; }
            if (_data != IntPtr.Zero) { Marshal.FreeHGlobal(_data); _data = IntPtr.Zero; }
        }
    }
}
