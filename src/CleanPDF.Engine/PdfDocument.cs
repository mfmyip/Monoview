using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;

namespace CleanPDF.Engine;

/// <summary>
/// An open PDF. All PDFium calls run on <see cref="PdfiumThread"/>; every public method is
/// async and safe to call from any thread.
/// </summary>
public sealed partial class PdfDocument : IDisposable
{
    private const int BitmapFormatBgra = 4; // FPDFBitmap_BGRA
    private const int RenderFlagAnnot = 0x01; // FPDF_ANNOT
    private const int RenderFlagLcdText = 0x02; // FPDF_LCD_TEXT
    private const int OpenPageCacheSize = 8;
    private const int TextCacheSize = 64;

    private static bool _libraryInitialized;

    private FpdfDocumentT? _doc;
    private readonly PageSize[] _pageSizes;
    private readonly PageTransform?[] _transforms;
    private readonly SearchText?[] _searchTexts;
    private readonly LruCache<int, FpdfPageT> _openPages;
    private readonly LruCache<int, PageText> _texts = new(TextCacheSize);
    private volatile bool _disposed;

    /// <summary>Version of the bundled PDFium build (the PDFiumCore package version).</summary>
    public static string PdfiumVersion => typeof(fpdfview).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public string Path { get; }

    /// <summary>The password the file was opened with, so Reload can reopen it without asking again.</summary>
    public string? Password { get; }
    public int PageCount => _pageSizes.Length;
    public IReadOnlyList<PageSize> PageSizes => _pageSizes;

    private PdfDocument(string path, string? password, FpdfDocumentT doc, PageSize[] sizes)
    {
        Path = path;
        Password = password;
        _doc = doc;
        _pageSizes = sizes;
        _transforms = new PageTransform?[sizes.Length];
        _searchTexts = new SearchText?[sizes.Length];
        _openPages = new LruCache<int, FpdfPageT>(OpenPageCacheSize, page => fpdfview.FPDF_ClosePage(page));
    }

    public static Task<PdfDocument> OpenAsync(string path, string? password = null) => PdfiumThread.Run(() =>
    {
        if (!_libraryInitialized)
        {
            fpdfview.FPDF_InitLibrary();
            _libraryInitialized = true;
        }

        var doc = fpdfview.FPDF_LoadDocument(path, password);
        if (IsNull(doc))
            {
            ulong error = fpdfview.FPDF_GetLastError();
            if (error == 4) // FPDF_ERR_PASSWORD: a password is needed, or the one given is wrong
                throw new PdfPasswordException(password is null
                    ? "This PDF is protected with a password."
                    : "That password is not correct.");
            throw new PdfException(DescribeError(error, path));
        }

        int count = fpdfview.FPDF_GetPageCount(doc);
        var sizes = new PageSize[count];
        for (int i = 0; i < count; i++)
        {
            double w = 0, h = 0;
            fpdfview.FPDF_GetPageSizeByIndex(doc, i, ref w, ref h);
            sizes[i] = new PageSize(w, h);
        }
        return new PdfDocument(path, password, doc, sizes);
    });

    /// <summary>
    /// Renders part of a page. The page is scaled to <paramref name="fullWidth"/> x
    /// <paramref name="fullHeight"/> pixels and <paramref name="region"/> is cut out of that,
    /// so tiles of one page line up exactly. Pixels are premultiplied BGRA.
    /// </summary>
    public Task<RenderedBitmap> RenderAsync(int pageIndex, int fullWidth, int fullHeight, PixelRect region,
        WorkPriority priority = WorkPriority.High, CancellationToken ct = default) =>
        RenderAsync(pageIndex, fullWidth, fullHeight, region, 0, priority, ct);

    /// <summary>
    /// Renders part of a page turned <paramref name="rotation"/> quarter turns clockwise (view rotation).
    /// <paramref name="fullWidth"/> x <paramref name="fullHeight"/> is the size of the rotated page.
    /// </summary>
    public Task<RenderedBitmap> RenderAsync(int pageIndex, int fullWidth, int fullHeight, PixelRect region, int rotation,
        WorkPriority priority = WorkPriority.High, CancellationToken ct = default)
    {
        int rotate = Rotation.Normalize(rotation);
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region), "Region must have a positive size.");

        return PdfiumThread.Run(() =>
        {
            var page = GetPage(pageIndex);
            var pixels = new byte[region.Width * 4 * region.Height];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var bitmap = fpdfview.FPDFBitmapCreateEx(region.Width, region.Height, BitmapFormatBgra,
                handle.AddrOfPinnedObject(), region.Width * 4);
            try
            {
                fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, region.Width, region.Height, 0xFFFFFFFF);
                fpdfview.FPDF_RenderPageBitmap(bitmap, page, -region.X, -region.Y, fullWidth, fullHeight, rotate,
                    RenderFlagAnnot | RenderFlagLcdText);
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
                handle.Free();
            }
            return new RenderedBitmap(region.Width, region.Height, pixels);
        }, priority, ct);
    }

    /// <summary>Renders a whole page to fit the given pixel size.</summary>
    public Task<RenderedBitmap> RenderAsync(int pageIndex, int width, int height,
        WorkPriority priority = WorkPriority.High, CancellationToken ct = default) =>
        RenderAsync(pageIndex, width, height, new PixelRect(0, 0, width, height), 0, priority, ct);

    /// <summary>File and metadata details for the Document properties dialog.</summary>
    public Task<DocumentProperties> GetPropertiesAsync() => PdfiumThread.Run(() =>
    {
        var doc = Doc;
        string? Meta(string tag)
        {
            ulong bytes = fpdf_doc.FPDF_GetMetaText(doc, tag, IntPtr.Zero, 0);
            if (bytes <= 2) return null;
            var buffer = new byte[bytes];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                fpdf_doc.FPDF_GetMetaText(doc, tag, handle.AddrOfPinnedObject(), bytes);
            }
            finally
            {
                handle.Free();
            }
            string value = Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2).Trim();
            return value.Length == 0 ? null : value;
        }

        int version = 0;
        string pdfVersion = fpdfview.FPDF_GetFileVersion(doc, ref version) != 0 ? $"{version / 10}.{version % 10}" : "unknown";
        long size = new FileInfo(Path).Length;
        return new DocumentProperties(
            Meta("Title"), Meta("Author"), Meta("Subject"), Meta("Keywords"), Meta("Creator"), Meta("Producer"),
            ParsePdfDate(Meta("CreationDate")), ParsePdfDate(Meta("ModDate")),
            pdfVersion, fpdfview.FPDF_GetSecurityHandlerRevision(doc) != -1, size, PageCount,
            PageCount > 0 ? _pageSizes[0] : new PageSize(0, 0));
    }, WorkPriority.Low);

    /// <summary>Parses a PDF date like "D:20230808142530+02'00'" (most parts optional).</summary>
    public static DateTimeOffset? ParsePdfDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        string digits = new(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 4) return null;
        int Part(int start, int length, int fallback) =>
            digits.Length >= start + length ? int.Parse(digits.Substring(start, length)) : fallback;
        try
        {
            var offset = TimeSpan.Zero;
            string rest = s[digits.Length..];
            if (rest.Length > 0 && (rest[0] == '+' || rest[0] == '-'))
            {
                var tz = new string(rest[1..].Where(char.IsDigit).ToArray());
                int hours = tz.Length >= 2 ? int.Parse(tz[..2]) : 0;
                int minutes = tz.Length >= 4 ? int.Parse(tz.Substring(2, 2)) : 0;
                offset = new TimeSpan(hours, minutes, 0) * (rest[0] == '-' ? -1 : 1);
            }
            return new DateTimeOffset(Part(0, 4, 1), Part(4, 2, 1), Part(6, 2, 1),
                Part(8, 2, 0), Part(10, 2, 0), Part(12, 2, 0), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Page handle for <see cref="PdfPrinter"/>; only valid on the PDFium thread.</summary>
    internal FpdfPageT GetPageForPrinting(int index) => GetPage(index);

    /// <summary>
    /// Loads and parses a page ahead of time (e.g. page 1 while the window is still being
    /// created), so its first render doesn't pay for parsing.
    /// </summary>
    public Task PrefetchPageAsync(int pageIndex) => PdfiumThread.Run(() => { GetPage(pageIndex); });

    public Task<PageText> GetTextAsync(int pageIndex, WorkPriority priority = WorkPriority.High, CancellationToken ct = default) =>
        PdfiumThread.Run(() => GetText(pageIndex), priority, ct);

    /// <summary>Converts a point in PDF user space (e.g. a link or bookmark target) to normalized page coordinates.</summary>
    public Task<(double X, double Y)> PageToNormalizedAsync(int pageIndex, double x, double y) =>
        PdfiumThread.Run(() => GetTransform(pageIndex).Apply(x, y));

    public Task<IReadOnlyList<OutlineItem>> GetOutlineAsync() => PdfiumThread.Run(() =>
    {
        var doc = Doc;
        int budget = 10_000; // guards against malformed files with cyclic outlines
        return (IReadOnlyList<OutlineItem>)ReadOutline(doc, null, 0, ref budget);
    }, WorkPriority.Low);

    /// <summary>
    /// Finds every match of <paramref name="query"/>, page by page. Case, accents and runs of
    /// whitespace (including line breaks) are ignored. <paramref name="onPageHits"/> is called on
    /// the caller's synchronization context once per page that has matches.
    /// </summary>
    public async Task SearchAsync(string query, Action<IReadOnlyList<SearchHit>> onPageHits, CancellationToken ct = default,
        SearchOptions options = default)
    {
        string needle = SearchText.Normalize(query, out _);
        if (needle.Length == 0) return;

        for (int i = 0; i < PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            int pageIndex = i;
            var text = _searchTexts[i] ?? await PdfiumThread.Run(() => GetSearchText(pageIndex), WorkPriority.Low, ct);
            var hits = text.Find(pageIndex, needle, options);
            if (hits.Count > 0)
                onPageHits(hits);
        }
    }

    /// <summary>Text of a range that may span pages, for copying a selection.</summary>
    public async Task<string> GetTextRangeAsync(int startPage, int startIndex, int endPage, int endIndex)
    {
        var sb = new StringBuilder();
        for (int p = startPage; p <= endPage; p++)
        {
            var text = await GetTextAsync(p);
            int from = p == startPage ? Math.Clamp(startIndex, 0, text.Length) : 0;
            int to = p == endPage ? Math.Clamp(endIndex, 0, text.Length) : text.Length;
            if (to > from) sb.Append(text.Text, from, to - from);
            if (p != endPage && sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) sb.AppendLine();
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = PdfiumThread.Run(() =>
        {
            _openPages.Clear();
            if (_doc is not null)
            {
                fpdfview.FPDF_CloseDocument(_doc);
                _doc = null;
            }
        });
    }

    // ---- Everything below runs on the PDFium thread. ----

    private FpdfDocumentT Doc
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed || _doc is null, this);
            return _doc!;
        }
    }

    private FpdfPageT GetPage(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PageCount);
        var doc = Doc;
        if (_openPages.TryGet(index, out var cached))
            return cached;

        var page = fpdfview.FPDF_LoadPage(doc, index);
        if (IsNull(page))
            throw new PdfException($"Could not load page {index + 1} (PDFium error {fpdfview.FPDF_GetLastError()}).");
        _openPages.Add(index, page);
        return page;
    }

    private PageTransform GetTransform(int index)
    {
        if (_transforms[index] is { } t) return t;

        // PDFium maps page space to device pixels (handling /Rotate and the crop box). Map three
        // points onto a large virtual device and derive the affine transform to 0..1 coordinates.
        var page = GetPage(index);
        const int scale = 100;
        int sx = Math.Max(1, (int)Math.Round(_pageSizes[index].Width * scale));
        int sy = Math.Max(1, (int)Math.Round(_pageSizes[index].Height * scale));
        (double X, double Y) Map(double x, double y)
        {
            int dx = 0, dy = 0;
            fpdfview.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, x, y, ref dx, ref dy);
            return ((double)dx / sx, (double)dy / sy);
        }
        const double d = 1000;
        var o = Map(0, 0);
        var ax = Map(d, 0);
        var ay = Map(0, d);
        t = new PageTransform((ax.X - o.X) / d, (ax.Y - o.Y) / d, (ay.X - o.X) / d, (ay.Y - o.Y) / d, o.X, o.Y);
        _transforms[index] = t;
        return t;
    }

    private PageText GetText(int index)
    {
        if (_texts.TryGet(index, out var cached))
            return cached;

        var page = GetPage(index);
        var transform = GetTransform(index);
        var textPage = fpdf_text.FPDFTextLoadPage(page);
        try
        {
            string text = ReadAllText(textPage);
            var boxes = new NormRect[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                double l = 0, r = 0, b = 0, t = 0;
                if (fpdf_text.FPDFTextGetCharBox(textPage, i, ref l, ref r, ref b, ref t) != 0 && r > l && t > b)
                    boxes[i] = transform.ApplyToBox(l, b, r, t);
            }
            var result = new PageText(index, text, boxes);
            _texts.Add(index, result);
            _searchTexts[index] ??= new SearchText(text);
            return result;
        }
        finally
        {
            fpdf_text.FPDFTextClosePage(textPage);
        }
    }

    private SearchText GetSearchText(int index)
    {
        if (_searchTexts[index] is { } st) return st;
        if (_texts.TryGet(index, out var pt))
            return _searchTexts[index] = new SearchText(pt.Text);

        // Text only (no boxes, no cache of the page handle) so searching a big file stays cheap.
        var page = fpdfview.FPDF_LoadPage(Doc, index);
        if (IsNull(page)) return _searchTexts[index] = new SearchText("");
        var textPage = fpdf_text.FPDFTextLoadPage(page);
        try
        {
            return _searchTexts[index] = new SearchText(ReadAllText(textPage));
        }
        finally
        {
            fpdf_text.FPDFTextClosePage(textPage);
            if (!_openPages.TryGet(index, out _))
                fpdfview.FPDF_ClosePage(page);
        }
    }

    private static string ReadAllText(FpdfTextpageT textPage)
    {
        int count = fpdf_text.FPDFTextCountChars(textPage);
        if (count <= 0) return "";
        var buffer = new ushort[count + 1];
        int written = fpdf_text.FPDFTextGetText(textPage, 0, count, ref buffer[0]);
        var chars = new char[count];
        if (written - 1 == count)
        {
            for (int i = 0; i < count; i++) chars[i] = (char)buffer[i];
        }
        else
        {
            // Fall back to one call per character so indices still line up with PDFium's.
            for (int i = 0; i < count; i++)
            {
                uint u = fpdf_text.FPDFTextGetUnicode(textPage, i);
                chars[i] = u <= 0xFFFF ? (char)u : '�';
            }
        }
        return new string(chars);
    }

    private List<OutlineItem> ReadOutline(FpdfDocumentT doc, FpdfBookmarkT? parent, int depth, ref int budget)
    {
        var items = new List<OutlineItem>();
        if (depth > 32) return items;

        var bm = fpdf_doc.FPDFBookmarkGetFirstChild(doc, parent);
        while (!IsNull(bm) && budget-- > 0)
        {
            string title = ReadBookmarkTitle(bm);
            var dest = fpdf_doc.FPDFBookmarkGetDest(doc, bm);
            if (IsNull(dest))
            {
                var action = fpdf_doc.FPDFBookmarkGetAction(bm);
                if (!IsNull(action)) dest = fpdf_doc.FPDFActionGetDest(doc, action);
            }

            int pageIndex = -1;
            double? x = null, y = null;
            if (!IsNull(dest))
            {
                pageIndex = fpdf_doc.FPDFDestGetDestPageIndex(doc, dest);
                int hasX = 0, hasY = 0, hasZoom = 0;
                float fx = 0, fy = 0, zoom = 0;
                if (fpdf_doc.FPDFDestGetLocationInPage(dest, ref hasX, ref hasY, ref hasZoom, ref fx, ref fy, ref zoom) != 0)
                {
                    if (hasX != 0) x = fx;
                    if (hasY != 0) y = fy;
                }
            }

            var children = ReadOutline(doc, bm, depth + 1, ref budget);
            items.Add(new OutlineItem(title, pageIndex, x, y, children));
            bm = fpdf_doc.FPDFBookmarkGetNextSibling(doc, bm);
        }
        return items;
    }

    private static string ReadBookmarkTitle(FpdfBookmarkT bm)
    {
        ulong bytes = fpdf_doc.FPDFBookmarkGetTitle(bm, IntPtr.Zero, 0);
        if (bytes <= 2) return "";
        var buffer = new byte[bytes];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(bm, handle.AddrOfPinnedObject(), bytes);
        }
        finally
        {
            handle.Free();
        }
        return Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2).Trim();
    }

    private static bool IsNull(object? handle) => handle switch
    {
        null => true,
        FpdfDocumentT d => d.__Instance == IntPtr.Zero,
        FpdfPageT p => p.__Instance == IntPtr.Zero,
        FpdfBookmarkT b => b.__Instance == IntPtr.Zero,
        FpdfDestT d => d.__Instance == IntPtr.Zero,
        FpdfActionT a => a.__Instance == IntPtr.Zero,
        _ => false,
    };

    private static string DescribeError(ulong code, string path) => code switch
    {
        2 => $"File not found or could not be opened: {path}",
        3 => "This file isn't a PDF, or it's damaged.",
        5 => "This PDF uses an unsupported security handler.",
        _ => $"Could not open the PDF (PDFium error {code}).",
    };
}

public sealed record RenderedBitmap(int Width, int Height, byte[] Bgra);

public sealed record DocumentProperties(
    string? Title, string? Author, string? Subject, string? Keywords, string? Creator, string? Producer,
    DateTimeOffset? Created, DateTimeOffset? Modified,
    string PdfVersion, bool Encrypted, long FileSize, int PageCount, PageSize FirstPageSize);

public sealed record OutlineItem(string Title, int PageIndex, double? X, double? Y, IReadOnlyList<OutlineItem> Children);

/// <summary>A search match: characters [Start, Start + Length) on a page, with surrounding text for the results list.</summary>
public sealed record SearchHit(int PageIndex, int Start, int Length, string Snippet, int SnippetMatchStart, int SnippetMatchLength);

public class PdfException(string message) : Exception(message);

/// <summary>The PDF needs a password, or the password given is wrong.</summary>
public sealed class PdfPasswordException(string message) : PdfException(message);

public readonly record struct SearchOptions(bool MatchCase, bool WholeWord);

/// <summary>A page's text with whitespace runs collapsed, plus a map back to PDFium character indices.</summary>
internal sealed class SearchText
{
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    private readonly string _normalized;
    private readonly int[] _map;

    public SearchText(string text) => _normalized = Normalize(text, out _map);

    public static string Normalize(string text, out int[] map)
    {
        var sb = new StringBuilder(text.Length);
        var m = new List<int>(text.Length);
        bool lastSpace = true; // also trims leading whitespace
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == '\0')
            {
                if (lastSpace) continue;
                sb.Append(' ');
                m.Add(i);
                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                m.Add(i);
                lastSpace = false;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
            m.RemoveAt(m.Count - 1);
        }
        map = m.ToArray();
        return sb.ToString();
    }

    public List<SearchHit> Find(int pageIndex, string needle, SearchOptions options = default)
    {
        // Match case compares exactly (accents too); the default ignores case, accents and width.
        var compare = options.MatchCase ? CompareOptions.Ordinal : Loose;
        var hits = new List<SearchHit>();
        int from = 0;
        while (from < _normalized.Length)
        {
            int idx = Compare.IndexOf(_normalized.AsSpan(from), needle, compare, out int matchLength);
            if (idx < 0 || matchLength == 0) break;
            idx += from;
            if (options.WholeWord && !IsWholeWord(idx, matchLength))
            {
                from = idx + 1;
                continue;
            }

            int start = _map[idx];
            int end = _map[idx + matchLength - 1] + 1;

            int snipStart = Math.Max(0, idx - 30);
            int snipEnd = Math.Min(_normalized.Length, idx + matchLength + 50);
            string prefix = snipStart > 0 ? "…" : "";
            string snippet = prefix + _normalized[snipStart..snipEnd] + (snipEnd < _normalized.Length ? "…" : "");

            hits.Add(new SearchHit(pageIndex, start, end - start, snippet, prefix.Length + idx - snipStart, matchLength));
            from = idx + matchLength;
        }
        return hits;
    }

    private bool IsWholeWord(int start, int length) =>
        (start == 0 || !char.IsLetterOrDigit(_normalized[start - 1])) &&
        (start + length >= _normalized.Length || !char.IsLetterOrDigit(_normalized[start + length]));
}
