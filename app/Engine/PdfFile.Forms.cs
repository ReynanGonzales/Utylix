using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace IdmClone.Engine;

public enum PdfFieldKind { Text, CheckBox, Radio, Combo, List, Button, Signature }

/// <summary>A fillable box of a PDF form (where it is, in points from the top-left of the page as shown, and what is in it).</summary>
public sealed record PdfField(int Page, int AnnotIndex, string Name, string Hint, PdfFieldKind Kind, Rect Box, string Value, bool Checked,
                              bool ReadOnly, bool Required, bool Multiline, bool Password, bool EditableCombo, IReadOnlyList<string> Options, int Selected, double FontSize);

/// <summary>The fillable forms of a PDF, through PDFium's form engine (the one in Chrome): values are kept in the document and saved with it.</summary>
public sealed partial class PdfFile
{
    private IntPtr _form, _formInfo;

    /// <summary>The PDF has fields to fill in (an ordinary AcroForm; XFA forms aren't supported).</summary>
    public bool HasForm => _form != IntPtr.Zero;

    /// <summary>The PDF is an XFA form (made with Adobe LiveCycle): its fields can't be filled here.</summary>
    public bool IsXfaForm { get; private set; }

    private void InitForm()
    {
        int type = Pdfium.FPDF_GetFormType(_doc);
        IsXfaForm = type is 2 or 3;
        if (type != 1) return;
        // FPDF_FORMFILLINFO, version 1, without any callbacks (Utylix asks PDFium directly instead): 17 pointers' worth of zeros
        _formInfo = Marshal.AllocHGlobal(256);
        for (int i = 0; i < 256; i += 8) Marshal.WriteInt64(_formInfo, i, 0);
        Marshal.WriteInt32(_formInfo, 0, 1);
        _form = Pdfium.FPDFDOC_InitFormFillEnvironment(_doc, _formInfo);
        if (_form == IntPtr.Zero) { Marshal.FreeHGlobal(_formInfo); _formInfo = IntPtr.Zero; return; }
        Pdfium.FPDF_SetFormFieldHighlightColor(_form, 0, 0xFFE0CF);           // (light blue, like other readers; PDFium wants 0xBBGGRR)
        Pdfium.FPDF_SetFormFieldHighlightAlpha(_form, Highlight);
    }

    private const byte Highlight = 60;               // (light: the text typed in a field stays dark and easy to read)

    private void ExitForm()
    {
        if (_form != IntPtr.Zero) { Pdfium.FPDFDOC_ExitFormFillEnvironment(_form); _form = IntPtr.Zero; }
        if (_formInfo != IntPtr.Zero) { Marshal.FreeHGlobal(_formInfo); _formInfo = IntPtr.Zero; }
    }

    /// <summary>Draws the form fields over a page just drawn (call under Pdfium.Sync).</summary>
    private void DrawForm(IntPtr page, IntPtr bmp, int width, int height, int rotate, bool forScreen)
    {
        if (_form == IntPtr.Zero) return;
        Pdfium.FORM_OnAfterLoadPage(page, _form);
        if (!forScreen) Pdfium.FPDF_SetFormFieldHighlightAlpha(_form, 0);              // (paper: no highlight)
        Pdfium.FPDF_FFLDraw(_form, bmp, page, 0, 0, width, height, rotate, Pdfium.RenderAnnotations | Pdfium.RenderLcdText);
        if (!forScreen) Pdfium.FPDF_SetFormFieldHighlightAlpha(_form, Highlight);
        Pdfium.FORM_OnBeforeClosePage(page, _form);
    }

    /// <summary>The fields of a page, in the order the PDF lists them.</summary>
    public List<PdfField> GetFields(int index)
    {
        var fields = new List<PdfField>();
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            if (_form == IntPtr.Zero) return fields;
            Pdfium.FPDF_GetPageSizeByIndexF(_doc, index, out var size);
            IntPtr page = Pdfium.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) return fields;
            Pdfium.FORM_OnAfterLoadPage(page, _form);
            try
            {
                var map = new PageMapping(page, size.Width, size.Height);
                int count = Pdfium.FPDFPage_GetAnnotCount(page);
                for (int i = 0; i < count; i++)
                {
                    IntPtr a = Pdfium.FPDFPage_GetAnnot(page, i);
                    if (a == IntPtr.Zero) continue;
                    try
                    {
                        if (Pdfium.FPDFAnnot_GetSubtype(a) != Pdfium.AnnotWidget) continue;
                        int type = Pdfium.FPDFAnnot_GetFormFieldType(_form, a);
                        if (type <= 0 || Pdfium.FPDFAnnot_GetRect(a, out var r) == 0) continue;
                        var kind = type switch
                        {
                            Pdfium.FieldCheckBox => PdfFieldKind.CheckBox, Pdfium.FieldRadio => PdfFieldKind.Radio, Pdfium.FieldCombo => PdfFieldKind.Combo,
                            Pdfium.FieldList => PdfFieldKind.List, Pdfium.FieldText => PdfFieldKind.Text, Pdfium.FieldSignature => PdfFieldKind.Signature, _ => PdfFieldKind.Button,
                        };
                        int flags = Pdfium.FPDFAnnot_GetFormFieldFlags(_form, a);
                        var options = new List<string>();
                        int selected = -1;
                        if (kind is PdfFieldKind.Combo or PdfFieldKind.List)
                        {
                            int n = Pdfium.FPDFAnnot_GetOptionCount(_form, a);
                            for (int k = 0; k < n && k < 2000; k++)
                            {
                                options.Add(Utf16((b, l) => Pdfium.FPDFAnnot_GetOptionLabel(_form, a, k, b, l)));
                                if (selected < 0 && Pdfium.FPDFAnnot_IsOptionSelected(_form, a, k) != 0) selected = k;
                            }
                        }
                        Pdfium.FPDFAnnot_GetFontSize(_form, a, out float fs);
                        fields.Add(new PdfField(index, i,
                            Utf16((b, l) => Pdfium.FPDFAnnot_GetFormFieldName(_form, a, b, l)),
                            Utf16((b, l) => Pdfium.FPDFAnnot_GetFormFieldAlternateName(_form, a, b, l)),
                            kind, map.ToShown(r.Left, r.Bottom, r.Right, r.Top),
                            Utf16((b, l) => Pdfium.FPDFAnnot_GetFormFieldValue(_form, a, b, l)),
                            Pdfium.FPDFAnnot_IsChecked(_form, a) != 0,
                            ReadOnly: (flags & 1) != 0, Required: (flags & 2) != 0,
                            Multiline: kind == PdfFieldKind.Text && (flags & (1 << 12)) != 0, Password: kind == PdfFieldKind.Text && (flags & (1 << 13)) != 0,
                            EditableCombo: kind == PdfFieldKind.Combo && (flags & (1 << 18)) != 0,
                            options, selected, fs));
                    }
                    finally { Pdfium.FPDFPage_CloseAnnot(a); }
                }
            }
            finally
            {
                Pdfium.FORM_OnBeforeClosePage(page, _form);
                Pdfium.FPDF_ClosePage(page);
            }
        }
        return fields;
    }

    private static string Utf16(Func<byte[]?, uint, uint> get)
    {
        uint len = get(null, 0);
        if (len <= 2 || len > 1 << 20) return "";
        var buf = new byte[len];
        get(buf, len);
        return Encoding.Unicode.GetString(buf, 0, (int)len - 2);
    }

    /// <summary>Puts text into a text field (or an editable drop-down list).</summary>
    public void SetFieldText(PdfField field, string text) => WithField(field, (page, annot) =>
    {
        Pdfium.FORM_SelectAllText(_form, page);
        Pdfium.FORM_ReplaceSelection(_form, page, text);
    });

    /// <summary>Picks an entry of a drop-down list or list box.</summary>
    public void SelectFieldOption(PdfField field, int option) => WithField(field, (page, annot) => Pdfium.FORM_SetIndexSelected(_form, page, option, 1));

    /// <summary>Clicks a check box or a round option (ticks / unticks it, the way the PDF wants).</summary>
    public void ClickField(PdfField field) => WithField(field, (page, annot) =>
    {
        if (Pdfium.FPDFAnnot_GetRect(annot, out var r) == 0) return;
        double x = (r.Left + r.Right) / 2, y = (r.Bottom + r.Top) / 2;
        Pdfium.FORM_OnMouseMove(_form, page, 0, x, y);
        Pdfium.FORM_OnLButtonDown(_form, page, 0, x, y);
        Pdfium.FORM_OnLButtonUp(_form, page, 0, x, y);
    });

    private void WithField(PdfField field, Action<IntPtr, IntPtr> change)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            if (_form == IntPtr.Zero) return;
            IntPtr page = Pdfium.FPDF_LoadPage(_doc, field.Page);
            if (page == IntPtr.Zero) throw new IOException("Page " + (field.Page + 1) + " can't be read.");
            Pdfium.FORM_OnAfterLoadPage(page, _form);
            IntPtr annot = Pdfium.FPDFPage_GetAnnot(page, field.AnnotIndex);
            try
            {
                if (annot == IntPtr.Zero) throw new IOException("That field isn't there any more.");
                Pdfium.FORM_SetFocusedAnnot(_form, annot);
                change(page, annot);
                Pdfium.FORM_ForceToKillFocus(_form);                 // (this writes the value and its look into the document)
            }
            finally
            {
                if (annot != IntPtr.Zero) Pdfium.FPDFPage_CloseAnnot(annot);
                Pdfium.FORM_OnBeforeClosePage(page, _form);
                Pdfium.FPDF_ClosePage(page);
            }
            _texts.Remove(field.Page); _runs.Remove(field.Page);
        }
    }
}
