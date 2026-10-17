using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;

namespace Monoview.Engine;

/// <summary>
/// A clickable area on a page: a jump to a place in the document, or a web address.
/// Rects and the target point are in unrotated normalized page coordinates.
/// </summary>
public sealed record PageLink(IReadOnlyList<NormRect> Rects, int? TargetPage, double? TargetY, string? Uri);

public sealed partial class PdfDocument
{
    private readonly LruCache<int, IReadOnlyList<PageLink>> _links = new(48);

    /// <summary>Link annotations on a page, plus web addresses written in its text.</summary>
    public Task<IReadOnlyList<PageLink>> GetLinksAsync(int pageIndex, WorkPriority priority = WorkPriority.Low, CancellationToken ct = default) =>
        PdfiumThread.Run(() =>
        {
            if (_links.TryGet(pageIndex, out var cached)) return cached;
            var links = new List<PageLink>();
            ReadLinkAnnotations(pageIndex, links);
            ReadWebLinks(pageIndex, links);
            _links.Add(pageIndex, links);
            return (IReadOnlyList<PageLink>)links;
        }, priority, ct);

    private void ReadLinkAnnotations(int pageIndex, List<PageLink> links)
    {
        var page = GetPage(pageIndex);
        var transform = GetTransform(pageIndex);
        IntPtr doc = Doc.__Instance;
        int pos = 0;
        while (FPDFLink_Enumerate(page.__Instance, ref pos, out IntPtr link) != 0)
        {
            if (FPDFLink_GetAnnotRect(link, out var r) == 0) continue;
            var rect = transform.ApplyToBox(r.Left, Math.Min(r.Top, r.Bottom), r.Right, Math.Max(r.Top, r.Bottom));

            IntPtr dest = FPDFLink_GetDest(doc, link);
            string? uri = null;
            if (dest == IntPtr.Zero && FPDFLink_GetAction(link) is var action && action != IntPtr.Zero)
            {
                switch (FPDFAction_GetType(action))
                {
                    case 1: // PDFACTION_GOTO
                        dest = FPDFAction_GetDest(doc, action);
                        break;
                    case 3: // PDFACTION_URI
                        uri = ReadUri(doc, action);
                        break;
                }
            }

            int? target = null;
            double? targetY = null;
            if (dest != IntPtr.Zero)
            {
                int index = FPDFDest_GetDestPageIndex(doc, dest);
                if (index >= 0 && index < PageCount)
                {
                    target = index;
                    if (FPDFDest_GetLocationInPage(dest, out _, out int hasY, out _, out float x, out float y, out _) != 0 && hasY != 0)
                        targetY = Math.Clamp(GetTransform(index).Apply(x, y).Y, 0, 1);
                }
            }
            if (target is not null || uri is not null)
                links.Add(new PageLink([rect], target, targetY, uri));
        }
    }

    private void ReadWebLinks(int pageIndex, List<PageLink> links)
    {
        var page = GetPage(pageIndex);
        var transform = GetTransform(pageIndex);
        var textPage = fpdf_text.FPDFTextLoadPage(page);
        var webLinks = fpdf_text.FPDFLinkLoadWebLinks(textPage);
        try
        {
            int count = fpdf_text.FPDFLinkCountWebLinks(webLinks);
            for (int i = 0; i < count; i++)
            {
                int length = fpdf_text.FPDFLinkGetURL(webLinks, i, ref Unsafe.NullRef<ushort>(), 0);
                if (length <= 1) continue;
                var buffer = new ushort[length];
                fpdf_text.FPDFLinkGetURL(webLinks, i, ref buffer[0], length);
                string url = new string(buffer.Take(length - 1).Select(c => (char)c).ToArray());

                var rects = new List<NormRect>();
                int rectCount = fpdf_text.FPDFLinkCountRects(webLinks, i);
                for (int j = 0; j < rectCount; j++)
                {
                    double l = 0, t = 0, r = 0, b = 0;
                    if (fpdf_text.FPDFLinkGetRect(webLinks, i, j, ref l, ref t, ref r, ref b) != 0)
                        rects.Add(transform.ApplyToBox(l, Math.Min(t, b), r, Math.Max(t, b)));
                }
                // Skip text URLs that an annotation already covers.
                if (rects.Count > 0 && !links.Any(link => link.Rects.Any(a => Overlaps(a, rects[0]))))
                    links.Add(new PageLink(rects, null, null, url));
            }
        }
        finally
        {
            fpdf_text.FPDFLinkCloseWebLinks(webLinks);
            fpdf_text.FPDFTextClosePage(textPage);
        }
    }

    private static bool Overlaps(NormRect a, NormRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static string? ReadUri(IntPtr doc, IntPtr action)
    {
        ulong length = FPDFAction_GetURIPath(doc, action, null, 0);
        if (length <= 1) return null;
        var buffer = new byte[length];
        FPDFAction_GetURIPath(doc, action, buffer, length);
        return Encoding.ASCII.GetString(buffer, 0, (int)length - 1); // 7-bit ASCII, NUL-terminated
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FS_RECTF
    {
        public float Left, Top, Right, Bottom;
    }

    // The bindings model these out-handles awkwardly, so call PDFium directly with raw handles.
    [DllImport("pdfium")] private static extern int FPDFLink_Enumerate(IntPtr page, ref int startPos, out IntPtr link);
    [DllImport("pdfium")] private static extern int FPDFLink_GetAnnotRect(IntPtr link, out FS_RECTF rect);
    [DllImport("pdfium")] private static extern IntPtr FPDFLink_GetDest(IntPtr doc, IntPtr link);
    [DllImport("pdfium")] private static extern IntPtr FPDFLink_GetAction(IntPtr link);
    [DllImport("pdfium")] private static extern ulong FPDFAction_GetType(IntPtr action);
    [DllImport("pdfium")] private static extern IntPtr FPDFAction_GetDest(IntPtr doc, IntPtr action);
    [DllImport("pdfium")] private static extern ulong FPDFAction_GetURIPath(IntPtr doc, IntPtr action, byte[]? buffer, ulong length);
    [DllImport("pdfium")] private static extern int FPDFDest_GetDestPageIndex(IntPtr doc, IntPtr dest);
    [DllImport("pdfium")]
    private static extern int FPDFDest_GetLocationInPage(IntPtr dest, out int hasX, out int hasY, out int hasZoom,
        out float x, out float y, out float zoom);
}
