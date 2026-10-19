using System.Text;

namespace Monoview.Engine.Tests;

/// <summary>
/// Writes a one-page PDF with a form: a text field "name", a check box "agree" and a drop-down "color",
/// in that tab order. Rects are in points on a 612 x 792 page.
/// </summary>
internal static class FormPdf
{
    private static readonly Lazy<string> SharedPath = new(() => Write(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"monoview-form-{Guid.NewGuid():N}.pdf")));

    /// <summary>Path to a form file shared by all tests in the run.</summary>
    public static string Path => SharedPath.Value;

    public static readonly NormRect NameField = Norm(100, 600, 400, 630);
    public static readonly NormRect AgreeBox = Norm(100, 550, 120, 570);
    public static readonly NormRect ColorList = Norm(100, 480, 300, 505);

    private static NormRect Norm(float left, float bottom, float right, float top) =>
        new(left / 612, (792 - top) / 792, right / 612, (792 - bottom) / 792);

    public static string Write(string path)
    {
        var objects = new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
            [2] = "<< /Type /Pages /Kids [10 0 R] /Count 1 >>",
            [3] = "<< /Fields [30 0 R 31 0 R 33 0 R] /NeedAppearances true /DA (/Helv 0 Tf 0 g) " +
                  "/DR << /Font << /Helv 4 0 R /ZaDb 5 0 R >> >> >>",
            [4] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            [5] = "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>",
            [10] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [30 0 R 31 0 R 33 0 R] " +
                   "/Resources << /Font << /F1 4 0 R >> >> /Contents 20 0 R >>",
            [20] = Stream("BT /F1 18 Tf 100 700 Td (Application form) Tj ET\n"),
            [30] = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [100 600 400 630] /P 10 0 R /F 4 " +
                   "/DA (/Helv 14 Tf 0 g) /MK << /BC [0 0 0] >> >>",
            [31] = "<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /Rect [100 550 120 570] /P 10 0 R /F 4 " +
                   "/V /Off /AS /Off /MK << /BC [0 0 0] /CA (4) >> /DA (/ZaDb 0 Tf 0 g) /AP << /N << /Yes 34 0 R /Off 35 0 R >> >> >>",
            [33] = "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (color) /Rect [100 480 300 505] /P 10 0 R /F 4 " +
                   "/Opt [(Red) (Green) (Blue)] /V (Red) /DA (/Helv 12 Tf 0 g) /MK << /BC [0 0 0] >> >>",
            [34] = Stream("0 g 3 3 14 14 re f\n", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]"),
            [35] = Stream("0 G 0.5 0.5 19 19 re S\n", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]"),
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

        static string Stream(string content, string extra = "") =>
            $"<< {extra} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream";
    }
}
