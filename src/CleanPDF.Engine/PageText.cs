namespace CleanPDF.Engine;

/// <summary>
/// The text of one page with a box per character, extracted once from PDFium so hit-testing,
/// selection and highlight geometry run in managed code without going back to the PDFium thread.
/// Character indices match PDFium's (one UTF-16 unit per character).
/// </summary>
public sealed class PageText
{
    private readonly NormRect[] _boxes;

    public int PageIndex { get; }
    public string Text { get; }
    public int Length => Text.Length;

    internal PageText(int pageIndex, string text, NormRect[] boxes)
    {
        PageIndex = pageIndex;
        Text = text;
        _boxes = boxes;
    }

    public NormRect GetCharBox(int index) => _boxes[index];

    /// <summary>
    /// Caret position (0..Length) nearest to a point: before the closest character, or after
    /// it when the point is past its horizontal middle.
    /// </summary>
    public int CaretAt(double x, double y)
    {
        int best = NearestChar(x, y);
        if (best < 0) return 0;
        var box = _boxes[best];
        return x > (box.Left + box.Right) / 2 ? best + 1 : best;
    }

    /// <summary>Index of the character nearest to a point, or -1 if the page has no text.</summary>
    public int NearestChar(double x, double y)
    {
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _boxes.Length; i++)
        {
            var b = _boxes[i];
            if (b.IsEmpty) continue;
            // Weight vertical distance more, so a point between lines picks the line it's on.
            double dx = Math.Max(Math.Max(b.Left - x, 0), x - b.Right);
            double dy = Math.Max(Math.Max(b.Top - y, 0), y - b.Bottom);
            double dist = dx + dy * 4;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
                if (dist == 0) break;
            }
        }
        return best;
    }

    /// <summary>Whether a point is on (or very near) a character, used to show the text cursor.</summary>
    public bool IsOverText(double x, double y, double tolerance = 0.004)
    {
        foreach (var b in _boxes)
            if (!b.IsEmpty && b.DistanceTo(x, y) <= tolerance)
                return true;
        return false;
    }

    /// <summary>Start and length of the word containing a character.</summary>
    public (int Start, int Length) WordAt(int index)
    {
        if (Length == 0) return (0, 0);
        index = Math.Clamp(index, 0, Length - 1);
        if (!IsWordChar(Text[index])) return (index, 1);
        int start = index, end = index + 1;
        while (start > 0 && IsWordChar(Text[start - 1])) start--;
        while (end < Length && IsWordChar(Text[end])) end++;
        return (start, end - start);
    }

    /// <summary>One rectangle per line run covering the characters in [start, start + count).</summary>
    public List<NormRect> GetRects(int start, int count)
    {
        var rects = new List<NormRect>();
        int end = Math.Min(Length, start + count);
        NormRect? line = null;
        for (int i = Math.Max(0, start); i < end; i++)
        {
            var b = _boxes[i];
            if (b.IsEmpty || char.IsWhiteSpace(Text[i])) continue;
            if (line is { } cur && SameLine(cur, b))
            {
                line = cur.Union(b);
            }
            else
            {
                if (line is { } done) rects.Add(done);
                line = b;
            }
        }
        if (line is { } last) rects.Add(last);
        return rects;
    }

    private static bool SameLine(NormRect line, NormRect c)
    {
        float overlap = Math.Min(line.Bottom, c.Bottom) - Math.Max(line.Top, c.Top);
        float minHeight = Math.Min(line.Height, c.Height);
        // Same line if they overlap vertically and the character doesn't jump back to the left
        // (a new column or a line that wraps).
        return overlap > minHeight * 0.5f && c.Left >= line.Left - minHeight;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '\'' || c == '’' || c == '_';
}
