using System.Text;

namespace CleanPDF.Engine.Tests;

/// <summary>
/// Writes a small PDF with known text, positions, a rotated page and an outline, so tests
/// don't depend on files that can't be committed.
/// </summary>
internal static class SamplePdf
{
    private static readonly Lazy<string> SharedPath = new(() => Write(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cleanpdf-sample-{Guid.NewGuid():N}.pdf")));

    /// <summary>Path to a sample file shared by all tests in the run.</summary>
    public static string Path => SharedPath.Value;

    // Page 1 (612 x 792): "Hello CleanPDF" at (72, 700), size 24, then three 12pt lines.
    public const string Page1Title = "Hello CleanPDF";
    public const double TitleX = 72, TitleY = 700;

    public static string Write(string path)
    {
        var objects = new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R /PageMode /UseOutlines >>",
            [2] = "<< /Type /Pages /Kids [10 0 R 11 0 R 12 0 R] /Count 3 >>",
            [3] = "<< /Type /Outlines /First 5 0 R /Last 6 0 R /Count 3 >>",
            [4] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            [5] = "<< /Title (Introduction) /Parent 3 0 R /Next 6 0 R /Dest [10 0 R /XYZ 0 792 0] >>",
            [6] = "<< /Title (Rotated) /Parent 3 0 R /Prev 5 0 R /First 7 0 R /Last 7 0 R /Count 1 /Dest [11 0 R /Fit] >>",
            [7] = "<< /Title (Small page) /Parent 6 0 R /Dest [12 0 R /Fit] >>",

            [10] = Page("612 792", 20),
            [11] = Page("612 792", 21, "/Rotate 90"),
            [12] = Page("300 400", 22),

            [20] = Stream(
                $"BT /F1 24 Tf {TitleX} {TitleY} Td ({Page1Title}) Tj ET\n" +
                "BT /F1 12 Tf 72 650 Td (The quick brown fox) Tj ET\n" +
                "BT /F1 12 Tf 72 635 Td (jumps over the) Tj ET\n" +
                "BT /F1 12 Tf 72 620 Td (lazy dog at the Caf\\351.) Tj ET\n"),
            [21] = Stream("BT /F1 24 Tf 72 700 Td (Rotated page) Tj ET\n"),
            [22] = Stream("BT /F1 18 Tf 40 300 Td (hello again) Tj ET\n"),
        };

        var latin1 = Encoding.Latin1;
        using var fs = File.Create(path);
        var offsets = new Dictionary<int, long>();
        void W(string s) { var b = latin1.GetBytes(s); fs.Write(b); }

        W("%PDF-1.7\n%âãÏÓ\n");
        foreach (var (id, body) in objects)
        {
            offsets[id] = fs.Position;
            W($"{id} 0 obj\n{body}\nendobj\n");
        }

        int size = objects.Keys.Max() + 1;
        long xref = fs.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int id = 1; id < size; id++)
            W(offsets.TryGetValue(id, out var off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return path;

        static string Page(string size, int contents, string extra = "") =>
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {size}] {extra} /Resources << /Font << /F1 4 0 R >> >> /Contents {contents} 0 R >>";

        static string Stream(string content) =>
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream";
    }
}
