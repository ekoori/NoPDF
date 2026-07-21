using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NoPdf.Core.Signing;

/// <summary>
/// Appends an incremental update to a PDF, leaving the original bytes untouched.
///
/// This is what makes multi-party signing work. A signature covers a byte range of the file as
/// it stood when it was signed; re-saving the document through a PDF library rewrites those
/// bytes and every earlier signature stops verifying. An incremental update instead appends new
/// objects, a new cross-reference section and a new trailer whose <c>/Prev</c> points at the
/// previous one — so earlier signatures still hash the bytes they signed.
///
/// Deliberately limited: only classic cross-reference tables are supported, and objects must be
/// addressable in the file (not packed into object streams). Anything else is rejected up front
/// so the caller can fall back rather than emit a file that readers disagree about. That covers
/// what noPDF itself writes, which is what it signs.
/// </summary>
public sealed class PdfIncrementalWriter
{
    private readonly byte[] _original;
    private readonly List<(int Number, string Body)> _objects = new();

    /// <summary>Object number of the document catalog (<c>/Root</c>).</summary>
    public int RootObject { get; }

    /// <summary>Offset of the cross-reference section this update chains back to.</summary>
    public long PreviousStartXref { get; }

    private int _nextObject;

    private PdfIncrementalWriter(byte[] original, int rootObject, int size, long previousStartXref)
    {
        _original = original;
        RootObject = rootObject;
        PreviousStartXref = previousStartXref;
        _nextObject = size;                 // /Size is one past the highest object number
    }

    /// <summary>
    /// Prepares an update for this document, or returns null with a reason if the file is not
    /// one this writer can safely extend.
    /// </summary>
    public static PdfIncrementalWriter? TryCreate(byte[] pdf, out string? reason)
    {
        reason = null;
        try
        {
            string text = Latin1(pdf);

            int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
            if (sx < 0) { reason = "no startxref"; return null; }
            string digits = new(text[(sx + 9)..].TrimStart().TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0 || !long.TryParse(digits, out long start)) { reason = "unreadable startxref"; return null; }
            if (start <= 0 || start >= pdf.Length) { reason = "startxref out of range"; return null; }

            if (!text[(int)start..].TrimStart().StartsWith("xref", StringComparison.Ordinal))
            { reason = "cross-reference streams are not supported"; return null; }

            // Objects inside an object stream have no byte offset of their own, so they cannot
            // be superseded by appending.
            if (text.Contains("/ObjStm", StringComparison.Ordinal))
            { reason = "compressed object streams are not supported"; return null; }

            int tr = text.IndexOf("trailer", (int)start, StringComparison.Ordinal);
            if (tr < 0) { reason = "no trailer"; return null; }
            string trailer = text[tr..];

            int root = RefNumber(trailer, "/Root");
            if (root <= 0) { reason = "no /Root in trailer"; return null; }

            int size = IntValue(trailer, "/Size");
            if (size <= 0) { reason = "no /Size in trailer"; return null; }

            return new PdfIncrementalWriter(pdf, root, size, start);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    /// <summary>Reserves the next unused object number.</summary>
    public int AllocateObject() => _nextObject++;

    /// <summary>
    /// Adds an object to the update. Using a number that already exists supersedes that object
    /// — which is how an existing page or catalog gets an added entry.
    /// </summary>
    public void AddObject(int number, string body) => _objects.Add((number, body));

    /// <summary>
    /// The raw text of an existing object — the newest definition of it.
    ///
    /// Deliberately the LAST occurrence, not the first: in a file that has already been updated
    /// incrementally the same object appears once per revision, and only the most recent one is
    /// live. Reading the first would resurrect a superseded version — which, for a page, means
    /// silently dropping the annotations an earlier revision added to it.
    /// </summary>
    public string? GetObjectText(int number)
    {
        string text = Latin1(_original);
        int best = -1, bestLen = 0;
        for (int gen = 0; gen <= 1; gen++)
        {
            foreach (char lead in new[] { '\n', '\r' })
            {
                string needle = $"{lead}{number} {gen} obj";
                int i = text.LastIndexOf(needle, StringComparison.Ordinal);
                if (i > best) { best = i; bestLen = needle.Length; }
            }
        }
        if (best < 0) return null;
        int start = best + bestLen;
        int end = text.IndexOf("endobj", start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end].Trim();
    }

    /// <summary>
    /// Builds the updated file: the original bytes, then the new objects, a cross-reference
    /// table covering only what changed, and a trailer chaining to the previous one.
    /// </summary>
    public byte[] Build()
    {
        if (_objects.Count == 0) return (byte[])_original.Clone();

        var body = new MemoryStream();
        body.Write(_original, 0, _original.Length);
        // A new section must start on its own line, or the first object runs onto whatever
        // ended the original file.
        if (_original.Length > 0 && _original[^1] is not ((byte)'\n' or (byte)'\r'))
            Write(body, "\n");

        var offsets = new Dictionary<int, long>();
        foreach (var (number, objBody) in _objects.OrderBy(o => o.Number))
        {
            offsets[number] = body.Length;
            Write(body, $"{number} 0 obj\n{objBody}\nendobj\n");
        }

        long xrefOffset = body.Length;
        Write(body, "xref\n");
        // One subsection per run of consecutive object numbers.
        foreach (var run in ConsecutiveRuns(offsets.Keys.OrderBy(n => n).ToList()))
        {
            Write(body, $"{run[0]} {run.Count}\n");
            foreach (int n in run)
                Write(body, $"{offsets[n],10:D10} {0,5:D5} n \n");   // exactly 20 bytes per entry
        }

        int size = _objects.Count == 0 ? _nextObject : Math.Max(_nextObject, _objects.Max(o => o.Number) + 1);
        Write(body, "trailer\n");
        Write(body, $"<</Size {size}/Root {RootObject} 0 R/Prev {PreviousStartXref}>>\n");
        Write(body, $"startxref\n{xrefOffset}\n%%EOF\n");
        return body.ToArray();
    }

    /// <summary>Object numbers grouped into consecutive runs, for xref subsections.</summary>
    private static List<List<int>> ConsecutiveRuns(List<int> sorted)
    {
        var runs = new List<List<int>>();
        foreach (int n in sorted)
        {
            if (runs.Count > 0 && runs[^1][^1] == n - 1) runs[^1].Add(n);
            else runs.Add(new List<int> { n });
        }
        return runs;
    }

    // ---------- small parsing helpers ----------

    /// <summary>The object number in an indirect reference like <c>/Root 2 0 R</c>.</summary>
    private static int RefNumber(string dict, string key)
    {
        int i = dict.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return -1;
        string rest = dict[(i + key.Length)..].TrimStart();
        string digits = new(rest.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : -1;
    }

    private static int IntValue(string dict, string key)
    {
        int i = dict.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return -1;
        string rest = dict[(i + key.Length)..].TrimStart();
        string digits = new(rest.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : -1;
    }

    internal static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    private static void Write(Stream s, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }
}
