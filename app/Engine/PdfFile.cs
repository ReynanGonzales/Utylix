using System;
using System.Collections.Generic;
using System.IO;
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
        byte[] bytes = File.ReadAllBytes(path);
        IntPtr data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        lock (Pdfium.Sync)
        {
            IntPtr doc = Pdfium.FPDF_LoadMemDocument64(data, (UIntPtr)bytes.Length, password);
            if (doc != IntPtr.Zero) return new PdfFile(path, password, doc, data, bytes.Length);
            uint error = Pdfium.FPDF_GetLastError();
            Marshal.FreeHGlobal(data);
            if (error == Pdfium.ErrPassword) throw new PdfPasswordException();
            throw new IOException(error switch
            {
                2 => "The file can't be found or opened.",
                3 => "This doesn't look like a PDF, or it is damaged.",
                5 => "This PDF uses a kind of protection Utylix can't open.",
                _ => "This PDF can't be opened.",
            });
        }
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
