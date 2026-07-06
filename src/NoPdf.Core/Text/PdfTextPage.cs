using System.Collections.Generic;

namespace NoPdf.Core.Text;

/// <summary>A single glyph with its box in PDF page space (points, origin bottom-left).</summary>
public readonly record struct TextChar(char Ch, double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Bottom + Top) / 2;
}

/// <summary>A rectangle in PDF page space (points, origin bottom-left).</summary>
public readonly record struct TextRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

/// <summary>A search hit expressed as an inclusive char-index range.</summary>
public readonly record struct TextMatch(int Start, int EndInclusive);

/// <summary>
/// Fully-managed text layout for one page, extracted from PDFium. Holds no native
/// handles, so selection/search math runs freely off the render lock.
/// All geometry is in PDF page space (points, origin bottom-left).
/// </summary>
public sealed class PdfTextPage
{
    private readonly TextChar[] _chars;

    public double PageWidth { get; }
    public double PageHeight { get; }
    public string Text { get; }
    public int CharCount => _chars.Length;
    public IReadOnlyList<TextChar> Chars => _chars;

    internal PdfTextPage(double pageWidth, double pageHeight, TextChar[] chars, string text)
    {
        PageWidth = pageWidth;
        PageHeight = pageHeight;
        _chars = chars;
        Text = text;
    }

    /// <summary>
    /// Returns the char index nearest a page-space point, or -1 if there is no text.
    /// Used to anchor/extend a text selection.
    /// </summary>
    public int HitTest(double pageX, double pageY)
    {
        if (_chars.Length == 0) return -1;

        // Prefer a char whose box directly contains the point.
        for (int i = 0; i < _chars.Length; i++)
        {
            var c = _chars[i];
            if (pageX >= c.Left && pageX <= c.Right && pageY >= c.Bottom && pageY <= c.Top)
                return i;
        }

        // Otherwise pick the char on the nearest line, then nearest horizontally.
        int best = -1;
        double bestScore = double.MaxValue;
        for (int i = 0; i < _chars.Length; i++)
        {
            var c = _chars[i];
            double dy = pageY < c.Bottom ? c.Bottom - pageY : pageY > c.Top ? pageY - c.Top : 0;
            double dx = pageX < c.Left ? c.Left - pageX : pageX > c.Right ? pageX - c.Right : 0;
            // Weight vertical distance heavily so we stay on the same line.
            double score = dy * 3 + dx;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>Extracted text for an inclusive index range.</summary>
    public string GetText(int start, int endInclusive)
    {
        if (_chars.Length == 0) return string.Empty;
        (start, endInclusive) = Order(start, endInclusive);
        start = Clamp(start);
        endInclusive = Clamp(endInclusive);
        return Text.Substring(start, endInclusive - start + 1);
    }

    /// <summary>
    /// Merges the char boxes of an inclusive range into per-line rectangles
    /// suitable for drawing a selection or highlight quad set.
    /// </summary>
    public IReadOnlyList<TextRect> GetRangeRects(int start, int endInclusive)
    {
        var rects = new List<TextRect>();
        if (_chars.Length == 0) return rects;
        (start, endInclusive) = Order(start, endInclusive);
        start = Clamp(start);
        endInclusive = Clamp(endInclusive);

        bool have = false;
        double l = 0, b = 0, r = 0, t = 0;
        double lineCenter = 0, lineHeight = 0;

        for (int i = start; i <= endInclusive; i++)
        {
            var c = _chars[i];
            // Skip zero-size control chars (e.g. line breaks) but let them break lines.
            bool sameLine = have && System.Math.Abs(c.CenterY - lineCenter) <= System.Math.Max(lineHeight, c.Height) * 0.5;
            if (!have)
            {
                l = c.Left; b = c.Bottom; r = c.Right; t = c.Top;
                lineCenter = c.CenterY; lineHeight = c.Height; have = true;
            }
            else if (sameLine)
            {
                l = System.Math.Min(l, c.Left);
                b = System.Math.Min(b, c.Bottom);
                r = System.Math.Max(r, c.Right);
                t = System.Math.Max(t, c.Top);
                lineHeight = System.Math.Max(lineHeight, c.Height);
            }
            else
            {
                rects.Add(new TextRect(l, b, r, t));
                l = c.Left; b = c.Bottom; r = c.Right; t = c.Top;
                lineCenter = c.CenterY; lineHeight = c.Height;
            }
        }
        if (have) rects.Add(new TextRect(l, b, r, t));
        return rects;
    }

    /// <summary>Finds all occurrences of <paramref name="query"/> as inclusive ranges.</summary>
    public IReadOnlyList<TextMatch> Find(string query, bool matchCase = false)
    {
        var matches = new List<TextMatch>();
        if (string.IsNullOrEmpty(query) || Text.Length == 0) return matches;

        var comparison = matchCase
            ? System.StringComparison.Ordinal
            : System.StringComparison.OrdinalIgnoreCase;

        int from = 0;
        while (from <= Text.Length - query.Length)
        {
            int idx = Text.IndexOf(query, from, comparison);
            if (idx < 0) break;
            matches.Add(new TextMatch(idx, idx + query.Length - 1));
            from = idx + 1;
        }
        return matches;
    }

    private int Clamp(int i) => i < 0 ? 0 : i >= _chars.Length ? _chars.Length - 1 : i;

    private static (int, int) Order(int a, int b) => a <= b ? (a, b) : (b, a);
}
