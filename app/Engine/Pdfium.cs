using System;
using System.IO;
using System.Runtime.InteropServices;

namespace IdmClone.Engine;

/// <summary>
/// The few functions of PDFium (Chrome's PDF engine, pdfium.dll from the bblanchon.PDFium.Win32 package) that Utylix uses.
/// PDFium is not thread-safe: every call goes through <see cref="Sync"/>.
/// </summary>
internal static class Pdfium
{
    private const string Dll = "pdfium.dll";

    /// <summary>Hold this around every PDFium call.</summary>
    public static readonly object Sync = new();

    private static bool _initialized;

    public static void Init()
    {
        lock (Sync)
        {
            if (_initialized) return;
            FPDF_InitLibrary();
            _initialized = true;
        }
    }

    // ---------- library, documents, pages ----------
    [DllImport(Dll)] private static extern void FPDF_InitLibrary();
    [DllImport(Dll)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, UIntPtr size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Dll)] public static extern uint FPDF_GetLastError();
    [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Dll)] public static extern int FPDF_GetSecurityHandlerRevision(IntPtr doc);     // -1: not protected
    [DllImport(Dll)] public static extern uint FPDF_GetDocUserPermissions(IntPtr doc);         // what the file allows without the owner password
    [DllImport(Dll)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr doc, int index, out SizeF size);
    [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Dll)] public static extern IntPtr FPDFPage_New(IntPtr doc, int index, double width, double height);
    [DllImport(Dll)] public static extern int FPDFPage_GenerateContent(IntPtr page);
    [DllImport(Dll)] public static extern int FPDF_SaveAsCopy(IntPtr doc, ref FileWrite write, uint flags);

    public const uint ErrPassword = 4;
    public const uint NoIncremental = 2;

    [StructLayout(LayoutKind.Sequential)] public struct SizeF { public float Width, Height; }

    // ---------- drawing a page ----------
    [DllImport(Dll)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Dll)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFBitmap_GetFormat(IntPtr bitmap);
    [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);

    public const int BitmapGray = 1, BitmapBgr = 2, BitmapBgrx = 3, BitmapBgra = 4;
    public const int RenderAnnotations = 0x01, RenderLcdText = 0x02;

    // ---------- objects on a page (pictures) ----------
    [DllImport(Dll)] public static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Dll)] public static extern int FPDFFormObj_CountObjects(IntPtr form);
    [DllImport(Dll)] public static extern IntPtr FPDFFormObj_GetObject(IntPtr form, uint index);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetType(IntPtr obj);
    [DllImport(Dll)] public static extern int FPDFPageObj_HasTransparency(IntPtr obj);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetBounds(IntPtr obj, out float left, out float bottom, out float right, out float top);
    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr doc);
    [DllImport(Dll)] public static extern void FPDFPage_InsertObject(IntPtr page, IntPtr obj);
    [DllImport(Dll)] public static extern int FPDFImageObj_SetMatrix(IntPtr obj, double a, double b, double c, double d, double e, double f);
    [DllImport(Dll)] public static extern int FPDFImageObj_LoadJpegFileInline(IntPtr pages, int count, IntPtr obj, ref FileAccess file);
    [DllImport(Dll)] public static extern IntPtr FPDFImageObj_GetBitmap(IntPtr obj);
    [DllImport(Dll)] public static extern uint FPDFImageObj_GetImageDataRaw(IntPtr obj, byte[]? buffer, uint length);
    [DllImport(Dll)] public static extern int FPDFImageObj_GetImageMetadata(IntPtr obj, IntPtr page, out ImageMetadata metadata);

    public const int ObjImage = 3, ObjForm = 5;

    // ---------- editing: text, lines and shapes, pictures, pages ----------
    [DllImport(Dll)] public static extern uint FPDF_GetDocPermissions(IntPtr doc);              // what is allowed with the password used
    [DllImport(Dll)] public static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);
    [DllImport(Dll)] public static extern IntPtr FPDFText_LoadStandardFont(IntPtr doc, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(Dll)] public static extern IntPtr FPDFText_LoadFont(IntPtr doc, byte[] data, uint size, int fontType, int cid);
    [DllImport(Dll)] public static extern void FPDFFont_Close(IntPtr font);
    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr doc, IntPtr font, float size);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int FPDFText_SetText(IntPtr textObj, string text);     // (UTF-16, as PDFium wants)
    [DllImport(Dll)] public static extern int FPDFPageObj_SetFillColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetStrokeColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetStrokeWidth(IntPtr obj, float width);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetLineJoin(IntPtr obj, int join);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetLineCap(IntPtr obj, int cap);
    [DllImport(Dll)] public static extern void FPDFPageObj_SetBlendMode(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string mode);
    [DllImport(Dll)] public static extern void FPDFPageObj_Transform(IntPtr obj, double a, double b, double c, double d, double e, double f);
    [DllImport(Dll)] public static extern IntPtr FPDFPageObj_CreateNewPath(float x, float y);
    [DllImport(Dll)] public static extern int FPDFPath_MoveTo(IntPtr path, float x, float y);
    [DllImport(Dll)] public static extern int FPDFPath_LineTo(IntPtr path, float x, float y);
    [DllImport(Dll)] public static extern int FPDFPath_BezierTo(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3);
    [DllImport(Dll)] public static extern int FPDFPath_Close(IntPtr path);
    [DllImport(Dll)] public static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);
    [DllImport(Dll)] public static extern int FPDFImageObj_SetBitmap(IntPtr pages, int count, IntPtr imageObj, IntPtr bitmap);
    [DllImport(Dll)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Dll)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
    [DllImport(Dll)] public static extern void FPDFPage_Delete(IntPtr doc, int index);
    [DllImport(Dll)] public static extern int FPDF_ImportPagesByIndex(IntPtr dest, IntPtr src, int[]? indices, uint length, int index);
    [DllImport(Dll)] public static extern int FPDF_MovePages(IntPtr doc, int[] indices, uint length, int destIndex);
    // a page of another document as a picture-like object that can be placed, scaled and turned on a new page (PDFium's "N pages on one"
    // does nothing for 1 x 1, so pages are put on their new sheets one by one)
    [DllImport(Dll)] public static extern IntPtr FPDF_NewXObjectFromPage(IntPtr dest, IntPtr src, int pageIndex);
    [DllImport(Dll)] public static extern void FPDF_CloseXObject(IntPtr xobject);
    [DllImport(Dll)] public static extern IntPtr FPDF_NewFormObjectFromXObject(IntPtr xobject);
    [DllImport(Dll)] public static extern int FPDFPage_GetMediaBox(IntPtr page, out float left, out float bottom, out float right, out float top);
    [DllImport(Dll)] public static extern int FPDFPage_GetCropBox(IntPtr page, out float left, out float bottom, out float right, out float top);

    // ---------- the text on a page ----------
    [DllImport(Dll)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Dll)] public static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Dll)] public static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Dll)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Dll)] public static extern int FPDFText_GetLooseCharBox(IntPtr textPage, int index, out RectF box);
    [DllImport(Dll)] public static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);

    [StructLayout(LayoutKind.Sequential)] public struct RectF { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct QuadF { public float X1, Y1, X2, Y2, X3, Y3, X4, Y4; }

    // ---------- links and bookmarks ----------
    [DllImport(Dll)] public static extern int FPDFLink_Enumerate(IntPtr page, ref int start, out IntPtr link);
    [DllImport(Dll)] public static extern int FPDFLink_GetAnnotRect(IntPtr link, out RectF rect);
    [DllImport(Dll)] public static extern IntPtr FPDFLink_GetDest(IntPtr doc, IntPtr link);
    [DllImport(Dll)] public static extern IntPtr FPDFLink_GetAction(IntPtr link);
    [DllImport(Dll)] public static extern uint FPDFAction_GetType(IntPtr action);
    [DllImport(Dll)] public static extern IntPtr FPDFAction_GetDest(IntPtr doc, IntPtr action);
    [DllImport(Dll)] public static extern uint FPDFAction_GetURIPath(IntPtr doc, IntPtr action, byte[]? buffer, uint length);
    [DllImport(Dll)] public static extern int FPDFDest_GetDestPageIndex(IntPtr doc, IntPtr dest);
    [DllImport(Dll)] public static extern IntPtr FPDFLink_LoadWebLinks(IntPtr textPage);
    [DllImport(Dll)] public static extern int FPDFLink_CountWebLinks(IntPtr links);
    [DllImport(Dll)] public static extern int FPDFLink_GetURL(IntPtr links, int index, char[]? buffer, int count);
    [DllImport(Dll)] public static extern int FPDFLink_CountRects(IntPtr links, int index);
    [DllImport(Dll)] public static extern int FPDFLink_GetRect(IntPtr links, int index, int rect, out double left, out double top, out double right, out double bottom);
    [DllImport(Dll)] public static extern void FPDFLink_CloseWebLinks(IntPtr links);
    [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr doc, IntPtr bookmark);
    [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr doc, IntPtr bookmark);
    [DllImport(Dll)] public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint length);
    [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetDest(IntPtr doc, IntPtr bookmark);
    [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    public const uint ActionGoTo = 1, ActionUri = 3;

    // ---------- comments (annotations) ----------
    [DllImport(Dll)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Dll)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetRect(IntPtr annot, out RectF rect);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetRect(IntPtr annot, ref RectF rect);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Dll)] public static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, ref QuadF quad);
    [DllImport(Dll)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, byte[]? buffer, uint length);

    public const int AnnotText = 1, AnnotHighlight = 9, AnnotUnderline = 10, AnnotStrikeOut = 12, AnnotPopup = 16;

    // ---------- the text already on a page, piece by piece (for changing it) ----------
    [DllImport(Dll)] public static extern uint FPDFTextObj_GetText(IntPtr textObj, IntPtr textPage, byte[]? buffer, uint length);
    [DllImport(Dll)] public static extern IntPtr FPDFTextObj_GetFont(IntPtr textObj);
    [DllImport(Dll)] public static extern int FPDFTextObj_GetFontSize(IntPtr textObj, out float size);
    [DllImport(Dll)] public static extern int FPDFTextObj_GetTextRenderMode(IntPtr textObj);
    [DllImport(Dll)] public static extern UIntPtr FPDFFont_GetBaseFontName(IntPtr font, byte[]? buffer, UIntPtr length);
    [DllImport(Dll)] public static extern UIntPtr FPDFFont_GetFamilyName(IntPtr font, byte[]? buffer, UIntPtr length);
    [DllImport(Dll)] public static extern int FPDFFont_GetWeight(IntPtr font);
    [DllImport(Dll)] public static extern int FPDFFont_GetItalicAngle(IntPtr font, out int angle);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetMatrix(IntPtr obj, out Matrix matrix);
    [DllImport(Dll)] public static extern int FPDFPageObj_SetMatrix(IntPtr obj, ref Matrix matrix);
    [DllImport(Dll)] public static extern int FPDFPageObj_GetFillColor(IntPtr obj, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Dll)] public static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr obj);
    [DllImport(Dll)] public static extern void FPDFPageObj_Destroy(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)] public struct Matrix { public float A, B, C, D, E, F; }

    // ---------- fillable forms (the same form engine Chrome uses) ----------
    [DllImport(Dll)] public static extern int FPDF_GetFormType(IntPtr doc);                    // 0 none, 1 AcroForm, 2/3 XFA
    [DllImport(Dll)] public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr doc, IntPtr formInfo);
    [DllImport(Dll)] public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
    [DllImport(Dll)] public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);
    [DllImport(Dll)] public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);
    [DllImport(Dll)] public static extern void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
    [DllImport(Dll)] public static extern void FPDF_SetFormFieldHighlightColor(IntPtr form, int fieldType, uint color);
    [DllImport(Dll)] public static extern void FPDF_SetFormFieldHighlightAlpha(IntPtr form, byte alpha);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetFormFieldType(IntPtr form, IntPtr annot);
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetFormFieldName(IntPtr form, IntPtr annot, byte[]? buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetFormFieldAlternateName(IntPtr form, IntPtr annot, byte[]? buffer, uint length);
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetFormFieldValue(IntPtr form, IntPtr annot, byte[]? buffer, uint length);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetFormFieldFlags(IntPtr form, IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_IsChecked(IntPtr form, IntPtr annot);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetOptionCount(IntPtr form, IntPtr annot);
    [DllImport(Dll)] public static extern uint FPDFAnnot_GetOptionLabel(IntPtr form, IntPtr annot, int index, byte[]? buffer, uint length);
    [DllImport(Dll)] public static extern int FPDFAnnot_IsOptionSelected(IntPtr form, IntPtr annot, int index);
    [DllImport(Dll)] public static extern int FPDFAnnot_GetFontSize(IntPtr form, IntPtr annot, out float size);
    [DllImport(Dll)] public static extern int FORM_SetFocusedAnnot(IntPtr form, IntPtr annot);
    [DllImport(Dll)] public static extern int FORM_SelectAllText(IntPtr form, IntPtr page);
    [DllImport(Dll)] public static extern void FORM_ReplaceSelection(IntPtr form, IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Dll)] public static extern int FORM_SetIndexSelected(IntPtr form, IntPtr page, int index, int selected);
    [DllImport(Dll)] public static extern int FORM_ForceToKillFocus(IntPtr form);
    [DllImport(Dll)] public static extern int FORM_OnMouseMove(IntPtr form, IntPtr page, int modifier, double x, double y);
    [DllImport(Dll)] public static extern int FORM_OnLButtonDown(IntPtr form, IntPtr page, int modifier, double x, double y);
    [DllImport(Dll)] public static extern int FORM_OnLButtonUp(IntPtr form, IntPtr page, int modifier, double x, double y);

    public const int AnnotWidget = 20;
    public const int FieldPushButton = 1, FieldCheckBox = 2, FieldRadio = 3, FieldCombo = 4, FieldList = 5, FieldText = 6, FieldSignature = 7;
    public const int ObjText = 1, ObjPath = 2;

    // ---------- taking things out of a page for good (redaction) ----------
    [DllImport(Dll)] public static extern IntPtr FPDFText_GetTextObject(IntPtr textPage, int charIndex);
    [DllImport(Dll)] public static extern int FPDFText_GetCharOrigin(IntPtr textPage, int index, out double x, out double y);
    [DllImport(Dll)] public static extern int FPDFFormObj_RemoveObject(IntPtr form, IntPtr obj);
    [DllImport(Dll)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Dll)] public static extern int FPDFTextObj_SetTextRenderMode(IntPtr textObj, int mode);
    [DllImport(Dll)] public static extern int FPDFPage_InsertObjectAtIndex(IntPtr page, IntPtr obj, UIntPtr index);
    public const int TextInvisible = 3;

    public const int FillNone = 0, FillWinding = 2;
    public const int FontTrueType = 2;
    public const int PermModify = 8, PermAnnotate = 32;

    [StructLayout(LayoutKind.Sequential)]
    public struct ImageMetadata
    {
        public uint Width, Height;
        public float HorizontalDpi, VerticalDpi;
        public uint BitsPerPixel;
        public int ColorSpace;
        public int MarkedContentId;
    }

    // ---------- handing data to / taking data from PDFium ----------
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int WriteBlockFn(IntPtr self, IntPtr data, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetBlockFn(IntPtr param, uint position, IntPtr buffer, uint size);

    [StructLayout(LayoutKind.Sequential)] public struct FileWrite { public int Version; public IntPtr WriteBlock; }
    [StructLayout(LayoutKind.Sequential)] public struct FileAccess { public uint FileLength; public IntPtr GetBlock; public IntPtr Param; }

    // (only used under Sync, so one "current" target / source at a time is enough)
    private static Stream? _writeTarget;
    private static byte[]? _readSource;
    private static readonly WriteBlockFn WriteBlockKeep = WriteBlock;
    private static readonly GetBlockFn GetBlockKeep = GetBlock;

    private static int WriteBlock(IntPtr self, IntPtr data, uint size)
    {
        try
        {
            var chunk = new byte[size];
            Marshal.Copy(data, chunk, 0, (int)size);
            _writeTarget!.Write(chunk, 0, chunk.Length);
            return 1;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NullReferenceException) { return 0; }
    }

    private static int GetBlock(IntPtr param, uint position, IntPtr buffer, uint size)
    {
        var src = _readSource;
        if (src == null || (long)position + size > src.Length) return 0;
        Marshal.Copy(src, (int)position, buffer, (int)size);
        return 1;
    }

    /// <summary>Writes the whole document into the stream (call under Sync).</summary>
    public static bool Save(IntPtr doc, Stream target)
    {
        _writeTarget = target;
        try
        {
            var w = new FileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(WriteBlockKeep) };
            return FPDF_SaveAsCopy(doc, ref w, NoIncremental) != 0;
        }
        finally { _writeTarget = null; }
    }

    /// <summary>Puts JPEG bytes into a picture object (call under Sync).</summary>
    public static bool LoadJpeg(IntPtr imageObj, byte[] jpeg)
    {
        _readSource = jpeg;
        try
        {
            var a = new FileAccess { FileLength = (uint)jpeg.Length, GetBlock = Marshal.GetFunctionPointerForDelegate(GetBlockKeep), Param = IntPtr.Zero };
            return FPDFImageObj_LoadJpegFileInline(IntPtr.Zero, 0, imageObj, ref a) != 0;
        }
        finally { _readSource = null; }
    }
}
