using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Monoview.Engine;

public enum FormFieldKind { Unknown, PushButton, CheckBox, RadioButton, ComboBox, ListBox, Text, Signature }

/// <summary>
/// A form field on a page. Rect is in unrotated normalized page coordinates. A drop-down list (ComboBox) has its
/// choices in Options, and Editable when the document lets you type your own value.
/// </summary>
public sealed record FormField(int PageIndex, string Name, FormFieldKind Kind, string Value, bool IsChecked, NormRect Rect,
    int AnnotIndex = -1, IReadOnlyList<string>? Options = null, bool Editable = false);

/// <summary>Pointer events passed to form fields.</summary>
public enum FormPointer { Move, Down, Up, DoubleClick }

/// <summary>
/// Form filling through PDFium's form engine: it lays out fields, draws the text cursor, toggles check boxes,
/// opens drop-down lists and moves between fields with Tab. Form JavaScript stays off: no script platform is given.
/// </summary>
public sealed partial class PdfDocument
{
    private const int FormTypeNone = 0;
    private const int ModifierShift = 1, ModifierControl = 2, ModifierAlt = 4; // FWL_EVENTFLAG_*
    private const int ChoiceEditFlag = 1 << 18; // FPDF_FORMFLAG_CHOICE_EDIT

    private IntPtr _form;       // FPDF_FORMHANDLE, zero when the document has no form
    private IntPtr _formInfo;   // our FPDF_FORMFILLINFO, which PDFium keeps a pointer to
    private readonly Dictionary<IntPtr, int> _pageIndexByHandle = new();

    // Callbacks find their document by the FPDF_FORMFILLINFO pointer PDFium passes back.
    private static readonly ConcurrentDictionary<IntPtr, PdfDocument> FormOwners = new();

    /// <summary>True when the document has a fillable form.</summary>
    public bool HasForm => _form != IntPtr.Zero;

    /// <summary>A form changed what a page shows (typing, the text cursor, a check box). Raised on the PDFium thread.</summary>
    public event EventHandler<int>? FormPageInvalidated;

    /// <summary>The user changed a field's value. Raised on the PDFium thread.</summary>
    public event EventHandler? FormValueChanged;

    /// <summary>Starts the form engine for documents that have a form; runs on the PDFium thread from the constructor.</summary>
    private void InitForm()
    {
        if (FPDF_GetFormType(_doc!.__Instance) == FormTypeNone) return;
        var info = new FormFillInfo
        {
            Version = 1,
            Invalidate = Marshal.GetFunctionPointerForDelegate(InvalidateCallback),
            SetTimer = Marshal.GetFunctionPointerForDelegate(SetTimerCallback),
            KillTimer = Marshal.GetFunctionPointerForDelegate(KillTimerCallback),
            OnChange = Marshal.GetFunctionPointerForDelegate(OnChangeCallback),
            GetPage = Marshal.GetFunctionPointerForDelegate(GetPageCallback),
            GetCurrentPage = Marshal.GetFunctionPointerForDelegate(GetCurrentPageCallback),
            GetRotation = Marshal.GetFunctionPointerForDelegate(GetRotationCallback),
        };
        _formInfo = Marshal.AllocHGlobal(Marshal.SizeOf<FormFillInfo>());
        Marshal.StructureToPtr(info, _formInfo, false);
        FormOwners[_formInfo] = this;
        _form = FPDFDOC_InitFormFillEnvironment(_doc.__Instance, _formInfo);
        if (_form == IntPtr.Zero)
        {
            FormOwners.TryRemove(_formInfo, out _);
            Marshal.FreeHGlobal(_formInfo);
            _formInfo = IntPtr.Zero;
            return;
        }
        // Fields show a light tint, like Acrobat, so they're easy to find.
        FPDF_SetFormFieldHighlightColor(_form, 0, 0xFFE4DD);
        FPDF_SetFormFieldHighlightAlpha(_form, 70);
    }

    /// <summary>Stops the form engine before the document closes; runs on the PDFium thread.</summary>
    private void ExitForm()
    {
        if (_form == IntPtr.Zero) return;
        FPDFDOC_ExitFormFillEnvironment(_form);
        _form = IntPtr.Zero;
        FormOwners.TryRemove(_formInfo, out _);
        Marshal.FreeHGlobal(_formInfo);
        _formInfo = IntPtr.Zero;
    }

    private void OnPageLoaded(int index, IntPtr page)
    {
        _pageIndexByHandle[page] = index;
        if (_form != IntPtr.Zero) FORM_OnAfterLoadPage(page, _form);
    }

    private void OnPageClosing(IntPtr page)
    {
        _pageIndexByHandle.Remove(page);
        if (_form != IntPtr.Zero) FORM_OnBeforeClosePage(page, _form);
    }

    /// <summary>Draws the form layer, such as the text being typed and the cursor, over a rendered page; on the PDFium thread.</summary>
    private void DrawForm(IntPtr bitmap, IntPtr page, int startX, int startY, int width, int height, int rotate, int flags)
    {
        if (_form != IntPtr.Zero) FPDF_FFLDraw(_form, bitmap, page, startX, startY, width, height, rotate, flags);
    }

    /// <summary>The annotations, other than form fields, that come after the first form field on the page.</summary>
    private static List<int> ObjectsAboveFormFields(IntPtr page)
    {
        var above = new List<int>();
        bool fieldSeen = false;
        int count = FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            IntPtr annot = FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            bool field = FPDFAnnot_GetSubtype(annot) == AnnotWidget;
            FPDFPage_CloseAnnot(annot);
            if (field) fieldSeen = true;
            else if (fieldSeen) above.Add(i);
        }
        return above;
    }

    /// <summary>Commits the field being typed in, so saving and snapshots include it; on the PDFium thread.</summary>
    private void CommitFormFocus()
    {
        if (_form != IntPtr.Zero) FORM_ForceToKillFocus(_form);
    }

    public Task<IReadOnlyList<FormField>> GetFormFieldsAsync(int pageIndex) => PdfiumThread.Run(() =>
    {
        var fields = new List<FormField>();
        if (_form == IntPtr.Zero) return (IReadOnlyList<FormField>)fields;
        var page = GetPage(pageIndex).__Instance;
        var transform = GetTransform(pageIndex);
        int count = FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            IntPtr annot = FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            try
            {
                int type = FPDFAnnot_GetFormFieldType(_form, annot);
                if (type <= 0 || FPDFAnnot_GetRect(annot, out var r) == 0) continue;
                var kind = (FormFieldKind)Math.Clamp(type, 0, 7);
                string[]? options = null;
                if (kind == FormFieldKind.ComboBox)
                {
                    options = new string[Math.Max(0, FPDFAnnot_GetOptionCount(_form, annot))];
                    for (int o = 0; o < options.Length; o++)
                        options[o] = FormString((b, n) => FPDFAnnot_GetOptionLabel(_form, annot, o, b, n));
                }
                fields.Add(new FormField(pageIndex,
                    FormString((b, n) => FPDFAnnot_GetFormFieldName(_form, annot, b, n)),
                    kind,
                    FormString((b, n) => FPDFAnnot_GetFormFieldValue(_form, annot, b, n)),
                    FPDFAnnot_IsChecked(_form, annot) != 0,
                    transform.ApplyToBox(Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom), Math.Max(r.Left, r.Right), Math.Max(r.Top, r.Bottom)),
                    i, options, (FPDFAnnot_GetFormFieldFlags(_form, annot) & ChoiceEditFlag) != 0));
            }
            finally
            {
                FPDFPage_CloseAnnot(annot);
            }
        }
        return (IReadOnlyList<FormField>)fields;
    });

    /// <summary>The kind of field at a point (unrotated normalized coordinates), for the pointer's shape; null when none.</summary>
    public Task<FormFieldKind?> FormFieldAtAsync(int pageIndex, double x, double y) => PdfiumThread.Run(() =>
    {
        if (_form == IntPtr.Zero) return (FormFieldKind?)null;
        var (px, py) = GetTransform(pageIndex).Invert(x, y);
        int type = FPDFPage_HasFormFieldAtPoint(_form, GetPage(pageIndex).__Instance, px, py);
        return type < 0 ? null : (FormFieldKind)Math.Clamp(type, 0, 7);
    }, WorkPriority.High);

    /// <summary>Passes a pointer event at (unrotated normalized) x, y to the form. True when a field took it.</summary>
    public Task<bool> FormPointerAsync(int pageIndex, FormPointer kind, double x, double y, bool shift = false, bool control = false) => PdfiumThread.Run(() =>
    {
        if (_form == IntPtr.Zero) return false;
        var page = GetPage(pageIndex).__Instance;
        var (px, py) = GetTransform(pageIndex).Invert(x, y);
        int modifiers = (shift ? ModifierShift : 0) | (control ? ModifierControl : 0);
        // PDFium acts on the field the pointer last moved over, so a click always arrives with a move to its point.
        if (kind != FormPointer.Move) FORM_OnMouseMove(_form, page, modifiers, px, py);
        return kind switch
        {
            FormPointer.Move => FORM_OnMouseMove(_form, page, modifiers, px, py),
            FormPointer.Down => FORM_OnLButtonDown(_form, page, modifiers, px, py),
            FormPointer.Up => FORM_OnLButtonUp(_form, page, modifiers, px, py),
            _ => FORM_OnLButtonDoubleClick(_form, page, modifiers, px, py),
        } != 0;
    }, WorkPriority.High);

    /// <summary>Sets a drop-down list (<see cref="FormField.AnnotIndex"/> on its page) to its choice number <paramref name="option"/>.</summary>
    public Task FormChooseAsync(int pageIndex, int annotIndex, int option) => PdfiumThread.Run(() =>
    {
        if (_form == IntPtr.Zero) return;
        var page = GetPage(pageIndex).__Instance;
        IntPtr annot = FPDFPage_GetAnnot(page, annotIndex);
        if (annot == IntPtr.Zero) return;
        try
        {
            if (FORM_SetFocusedAnnot(_form, annot) != 0)
                FORM_SetIndexSelected(_form, page, option, 1);
            CommitFormFocus();
        }
        finally
        {
            FPDFPage_CloseAnnot(annot);
        }
    }, WorkPriority.High);

    /// <summary>Types one character into the focused field.</summary>
    public Task FormCharAsync(char c) => PdfiumThread.Run(() =>
    {
        if (FocusedFormPage() is { } page) FORM_OnChar(_form, page, c, 0);
    }, WorkPriority.High);

    /// <summary>
    /// Passes a key, as a Windows virtual-key code, to the focused field: Tab, arrows, Backspace, Delete, Enter, Home, End.
    /// True when a field has focus and took it.
    /// </summary>
    public Task<bool> FormKeyAsync(int virtualKey, bool shift = false, bool control = false) => PdfiumThread.Run(() =>
    {
        if (FocusedFormPage() is not { } page) return false;
        int modifiers = (shift ? ModifierShift : 0) | (control ? ModifierControl : 0);
        bool handled = FORM_OnKeyDown(_form, page, virtualKey, modifiers) != 0;
        FORM_OnKeyUp(_form, page, virtualKey, modifiers);
        return handled;
    }, WorkPriority.High);

    /// <summary>The page of the field with focus, or null. A field has focus after a click on it or a Tab.</summary>
    public Task<int?> FormFocusPageAsync() => PdfiumThread.Run(() =>
        FocusedFormPage() is { } page && _pageIndexByHandle.TryGetValue(page, out int index) ? index : (int?)null, WorkPriority.High);

    /// <summary>Leaves the focused field, keeping what was typed.</summary>
    public Task FormKillFocusAsync() => PdfiumThread.Run(CommitFormFocus, WorkPriority.High);

    public Task<string> FormSelectedTextAsync() => PdfiumThread.Run(() =>
        FocusedFormPage() is { } page ? FormString((b, n) => FORM_GetSelectedText(_form, page, b, n)) : "", WorkPriority.High);

    /// <summary>Puts <paramref name="text"/> in place of the selection in the focused field, as paste does.</summary>
    public Task FormReplaceSelectionAsync(string text) => PdfiumThread.Run(() =>
    {
        if (FocusedFormPage() is { } page) FORM_ReplaceSelection(_form, page, Encoding.Unicode.GetBytes(text + "\0"));
    }, WorkPriority.High);

    public Task FormSelectAllAsync() => PdfiumThread.Run(() =>
    {
        if (FocusedFormPage() is { } page) FORM_SelectAllText(_form, page);
    }, WorkPriority.High);

    private IntPtr? FocusedFormPage()
    {
        if (_form == IntPtr.Zero || FORM_GetFocusedAnnot(_form, out int index, out IntPtr annot) == 0 || annot == IntPtr.Zero) return null;
        FPDFPage_CloseAnnot(annot);
        return index >= 0 && index < PageCount ? GetPage(index).__Instance : null;
    }

    /// <summary>Reads a UTF-16 string from a PDFium call that fills a buffer and returns its length in bytes.</summary>
    private static string FormString(Func<byte[]?, uint, uint> read)
    {
        uint bytes = read(null, 0);
        if (bytes <= 2) return "";
        var buffer = new byte[bytes];
        read(buffer, bytes);
        return Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2);
    }

    // ---- Callbacks from PDFium, on the PDFium thread ----

    private static PdfDocument? Owner(IntPtr info) => FormOwners.TryGetValue(info, out var doc) ? doc : null;

    private static readonly InvalidateDelegate InvalidateCallback = (info, page, _, _, _, _) =>
    {
        if (Owner(info) is { } doc && doc._pageIndexByHandle.TryGetValue(page, out int index))
            doc.FormPageInvalidated?.Invoke(doc, index);
    };

    private static readonly OnChangeDelegate OnChangeCallback = info =>
    {
        if (Owner(info) is not { } doc) return;
        doc.IsModified = true;
        doc.FormValueChanged?.Invoke(doc, EventArgs.Empty);
    };

    private static readonly GetPageDelegate GetPageCallback = (info, _, index) =>
        Owner(info) is { } doc && index >= 0 && index < doc.PageCount ? doc.GetPage(index).__Instance : IntPtr.Zero;

    private static readonly GetCurrentPageDelegate GetCurrentPageCallback = (info, _) =>
        Owner(info) is { PageCount: > 0 } doc ? doc.GetPage(0).__Instance : IntPtr.Zero;

    private static readonly GetRotationDelegate GetRotationCallback = (_, _) => 0;

    // The text cursor blinks on a timer; a steady cursor is enough, so timers are handed out and never fire.
    private static int _nextTimer;
    private static readonly SetTimerDelegate SetTimerCallback = (_, _, _) => Interlocked.Increment(ref _nextTimer);
    private static readonly KillTimerDelegate KillTimerCallback = (_, _) => { };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InvalidateDelegate(IntPtr info, IntPtr page, double left, double top, double right, double bottom);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void OnChangeDelegate(IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetPageDelegate(IntPtr info, IntPtr document, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetCurrentPageDelegate(IntPtr info, IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetRotationDelegate(IntPtr info, IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetTimerDelegate(IntPtr info, int elapse, IntPtr callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void KillTimerDelegate(IntPtr info, int id);

    /// <summary>FPDF_FORMFILLINFO, version 1: the callbacks we don't need stay null, which PDFium allows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FormFillInfo
    {
        public int Version;
        public IntPtr Release, Invalidate, OutputSelectedRect, SetCursor, SetTimer, KillTimer, GetLocalTime, OnChange,
            GetPage, GetCurrentPage, GetRotation, ExecuteNamedAction, SetTextFieldFocus, DoURIAction, DoGoToAction, JsPlatform;
    }

    [DllImport("pdfium")] private static extern int FPDF_GetFormType(IntPtr doc);
    [DllImport("pdfium")] private static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr doc, IntPtr info);
    [DllImport("pdfium")] private static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
    [DllImport("pdfium")] private static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);
    [DllImport("pdfium")] private static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);
    [DllImport("pdfium")] private static extern void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int startX, int startY, int width, int height, int rotate, int flags);
    [DllImport("pdfium")] private static extern void FPDF_SetFormFieldHighlightColor(IntPtr form, int fieldType, uint color);
    [DllImport("pdfium")] private static extern void FPDF_SetFormFieldHighlightAlpha(IntPtr form, byte alpha);
    [DllImport("pdfium")] private static extern int FORM_OnMouseMove(IntPtr form, IntPtr page, int modifiers, double x, double y);
    [DllImport("pdfium")] private static extern int FORM_OnLButtonDown(IntPtr form, IntPtr page, int modifiers, double x, double y);
    [DllImport("pdfium")] private static extern int FORM_OnLButtonUp(IntPtr form, IntPtr page, int modifiers, double x, double y);
    [DllImport("pdfium")] private static extern int FORM_OnLButtonDoubleClick(IntPtr form, IntPtr page, int modifiers, double x, double y);
    [DllImport("pdfium")] private static extern int FORM_OnChar(IntPtr form, IntPtr page, int c, int modifiers);
    [DllImport("pdfium")] private static extern int FORM_OnKeyDown(IntPtr form, IntPtr page, int key, int modifiers);
    [DllImport("pdfium")] private static extern int FORM_OnKeyUp(IntPtr form, IntPtr page, int key, int modifiers);
    [DllImport("pdfium")] private static extern int FORM_ForceToKillFocus(IntPtr form);
    [DllImport("pdfium")] private static extern int FORM_GetFocusedAnnot(IntPtr form, out int pageIndex, out IntPtr annot);
    [DllImport("pdfium")] private static extern uint FORM_GetSelectedText(IntPtr form, IntPtr page, [In, Out] byte[]? buffer, uint length);
    [DllImport("pdfium")] private static extern void FORM_ReplaceSelection(IntPtr form, IntPtr page, byte[] text);
    [DllImport("pdfium")] private static extern int FORM_SelectAllText(IntPtr form, IntPtr page);
    [DllImport("pdfium")] private static extern int FPDFPage_HasFormFieldAtPoint(IntPtr form, IntPtr page, double x, double y);
    [DllImport("pdfium")] private static extern int FPDFAnnot_GetFormFieldType(IntPtr form, IntPtr annot);
    [DllImport("pdfium")] private static extern uint FPDFAnnot_GetFormFieldName(IntPtr form, IntPtr annot, [In, Out] byte[]? buffer, uint length);
    [DllImport("pdfium")] private static extern uint FPDFAnnot_GetFormFieldValue(IntPtr form, IntPtr annot, [In, Out] byte[]? buffer, uint length);
    [DllImport("pdfium")] private static extern int FPDFAnnot_IsChecked(IntPtr form, IntPtr annot);
    [DllImport("pdfium")] private static extern int FPDFAnnot_GetFormFieldFlags(IntPtr form, IntPtr annot);
    [DllImport("pdfium")] private static extern int FPDFAnnot_GetOptionCount(IntPtr form, IntPtr annot);
    [DllImport("pdfium")] private static extern uint FPDFAnnot_GetOptionLabel(IntPtr form, IntPtr annot, int index, [In, Out] byte[]? buffer, uint length);
    [DllImport("pdfium")] private static extern int FORM_SetFocusedAnnot(IntPtr form, IntPtr annot);
    [DllImport("pdfium")] private static extern int FORM_SetIndexSelected(IntPtr form, IntPtr page, int index, int selected);
}
