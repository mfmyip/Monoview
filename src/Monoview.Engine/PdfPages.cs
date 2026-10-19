using System.Runtime.InteropServices;
using PDFiumCore;

namespace Monoview.Engine;

/// <summary>Page operations: delete, move, rotate, insert from another PDF, extract to a new PDF.</summary>
public sealed partial class PdfDocument
{
    /// <summary>Deletes pages (0-based). At least one page must remain.</summary>
    public Task DeletePagesAsync(IReadOnlyList<int> pages) => PdfiumThread.Run(() =>
    {
        var distinct = Valid(pages).OrderDescending().ToList();
        if (distinct.Count == 0) return;
        if (distinct.Count >= PageCount) throw new PdfException("A PDF needs at least one page.");
        IntPtr doc = Doc.__Instance;
        _openPages.Clear(); // close pages before deleting them
        foreach (int index in distinct)
            FPDFPage_Delete(doc, index);
        PagesChanged();
    });

    /// <summary>
    /// Moves <paramref name="pages"/> (in the order given) so the first lands at <paramref name="destination"/>,
    /// counted in the document after the move.
    /// </summary>
    public Task MovePagesAsync(IReadOnlyList<int> pages, int destination) => PdfiumThread.Run(() =>
    {
        var ordered = Valid(pages).ToArray();
        if (ordered.Length == 0) return;
        destination = Math.Clamp(destination, 0, PageCount - ordered.Length);
        _openPages.Clear();
        if (FPDF_MovePages(Doc.__Instance, ordered, (ulong)ordered.Length, destination) == 0)
            throw new PdfException("PDFium could not move the pages.");
        PagesChanged();
    });

    /// <summary>Turns pages by quarter turns clockwise; saved into the file, unlike the view rotation.</summary>
    public Task RotatePagesAsync(IReadOnlyList<int> pages, int quarterTurns) => PdfiumThread.Run(() =>
    {
        foreach (int index in Valid(pages))
        {
            var page = GetPage(index).__Instance;
            FPDFPage_SetRotation(page, Rotation.Normalize(FPDFPage_GetRotation(page) + quarterTurns));
        }
        PagesChanged();
    });

    /// <summary>
    /// Inserts pages of <paramref name="source"/>, an open document such as another tab, a password-protected PDF or
    /// a picture, before page <paramref name="atIndex"/>: the given <paramref name="pages"/> in that order, or all of
    /// them when null. Returns how many were added. <paramref name="source"/> is unchanged.
    /// </summary>
    public Task<int> InsertPagesFromAsync(PdfDocument source, int atIndex, IReadOnlyList<int>? pages = null) => PdfiumThread.Run(() =>
    {
        if (source == this) throw new PdfException("A document can't insert pages from itself.");
        var indices = pages is null ? null : source.Valid(pages).ToArray();
        if (indices is { Length: 0 }) return 0;
        atIndex = Math.Clamp(atIndex, 0, PageCount);
        _openPages.Clear();
        if (FPDF_ImportPagesByIndex(Doc.__Instance, source.Doc.__Instance, indices, (ulong)(indices?.Length ?? 0), atIndex) == 0)
            throw new PdfException($"Could not insert the pages of {System.IO.Path.GetFileName(source.Path)}.");
        PagesChanged();
        return indices?.Length ?? source.PageCount;
    });

    /// <summary>
    /// Crops a page to <paramref name="rect"/> (unrotated normalized page coordinates): its crop box, the area
    /// readers show and print, becomes that part of the page. The content outside stays in the file.
    /// </summary>
    public Task CropPageAsync(int pageIndex, NormRect rect) => PdfiumThread.Run(() =>
    {
        var bounds = PlaceRect(pageIndex, rect, out _);
        if (bounds.Right - bounds.Left < 1 || bounds.Top - bounds.Bottom < 1) throw new PdfException("The crop area is too small.");
        FPDFPage_SetCropBox(GetPage(pageIndex).__Instance, bounds.Left, bounds.Bottom, bounds.Right, bounds.Top);
        PagesChanged();
    });

    /// <summary>Writes the given pages, in order, to a new PDF at <paramref name="path"/>. This document is unchanged.</summary>
    public Task ExtractPagesAsync(IReadOnlyList<int> pages, string path) => PdfiumThread.Run(() =>
    {
        var indices = Valid(pages).ToArray();
        if (indices.Length == 0) return;
        IntPtr target = FPDF_CreateNewDocument();
        try
        {
            if (FPDF_ImportPagesByIndex(target, Doc.__Instance, indices, (ulong)indices.Length, 0) == 0)
                throw new PdfException("Could not copy the pages.");
            using var file = File.Create(path);
            WriteTo(target, file);
        }
        finally
        {
            FPDF_CloseDocument(target);
        }
    });

    /// <summary>Page indices in range, without repeats, in the order given.</summary>
    private IEnumerable<int> Valid(IEnumerable<int> pages) => pages.Where(p => p >= 0 && p < PageCount).Distinct();

    private void PagesChanged()
    {
        _openPages.Clear();
        _texts.Clear();
        ForgetRecognizedText();
        _links.Clear();
        ReadPageSizes();
        IsModified = true;
    }

    [DllImport("pdfium")] private static extern void FPDFPage_Delete(IntPtr doc, int index);
    [DllImport("pdfium")] private static extern void FPDFPage_SetCropBox(IntPtr page, float left, float bottom, float right, float top);
    [DllImport("pdfium")] private static extern int FPDF_MovePages(IntPtr doc, int[] pageIndices, ulong length, int destination);
    [DllImport("pdfium")] private static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport("pdfium")] private static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
    [DllImport("pdfium")] private static extern int FPDF_ImportPagesByIndex(IntPtr dest, IntPtr source, int[]? indices, ulong length, int index);
    [DllImport("pdfium")] private static extern IntPtr FPDF_CreateNewDocument();
    [DllImport("pdfium")] private static extern void FPDF_CloseDocument(IntPtr doc);
}
