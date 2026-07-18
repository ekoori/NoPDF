using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace NoPdf.Core.Editing;

/// <summary>
/// Document-level page manipulation via PDFsharp. Operations work on in-memory
/// byte buffers so the viewer can edit structure live and reload.
/// </summary>
public static class PageOps
{
    /// <summary>Appends a top-level outline (bookmark) entry pointing at a page.</summary>
    public static byte[] AddOutlineEntry(byte[] source, string title, int pageIndex)
    {
        using var input = new MemoryStream(source);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        if (pageIndex < 0 || pageIndex >= doc.PageCount) return source;
        doc.Outlines.Add(title, doc.Pages[pageIndex], true);
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }

    /// <summary>Removes the first top-level outline entry with this title. Returns the
    /// source unchanged when there is no match.</summary>
    public static byte[] RemoveOutlineEntry(byte[] source, string title)
    {
        using var input = new MemoryStream(source);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        PdfSharp.Pdf.PdfOutline? match = null;
        foreach (var o in doc.Outlines)
            if (string.Equals(o.Title, title, StringComparison.OrdinalIgnoreCase)) { match = o; break; }
        if (match is null) return source;
        doc.Outlines.Remove(match);
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }

    /// <summary>Builds a new PDF from the given zero-based page indices, in order.</summary>
    public static byte[] Compose(byte[] source, IReadOnlyList<int> pageOrder)
    {
        using var input = new MemoryStream(source);
        using var src = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        using var dst = new PdfDocument();
        foreach (int i in pageOrder)
            if (i >= 0 && i < src.PageCount) dst.AddPage(src.Pages[i]);
        if (dst.PageCount == 0) throw new InvalidOperationException("Resulting document has no pages.");
        return ToBytes(dst);
    }

    /// <summary>Removes the given pages.</summary>
    public static byte[] Delete(byte[] source, IEnumerable<int> pages)
    {
        var drop = new HashSet<int>(pages);
        using var input = new MemoryStream(source);
        using var src = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var keep = Enumerable.Range(0, src.PageCount).Where(i => !drop.Contains(i)).ToList();
        if (keep.Count == 0) throw new InvalidOperationException("Cannot delete every page.");
        return Compose(source, keep);
    }

    /// <summary>Reorders pages given a full permutation of the current indices.</summary>
    public static byte[] Reorder(byte[] source, IReadOnlyList<int> permutation)
        => Compose(source, permutation);

    /// <summary>Rotates the given pages by <paramref name="deltaDegrees"/> (multiple of 90).</summary>
    public static byte[] Rotate(byte[] source, IEnumerable<int> pages, int deltaDegrees)
    {
        using var input = new MemoryStream(source);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (int i in pages)
        {
            if (i < 0 || i >= doc.PageCount) continue;
            var p = doc.Pages[i];
            p.Rotate = ((p.Rotate + deltaDegrees) % 360 + 360) % 360;
        }
        return ToBytes(doc);
    }

    /// <summary>Inserts all pages of <paramref name="other"/> before <paramref name="atIndex"/>.</summary>
    /// <summary>A new document of blank pages at the given size, in points.</summary>
    public static byte[] CreateBlank(double widthPt, double heightPt, int pageCount = 1)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < Math.Max(1, pageCount); i++)
        {
            var page = doc.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(widthPt);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(heightPt);
        }
        return ToBytes(doc);
    }

    /// <summary>Inserts one blank page at the given index.</summary>
    public static byte[] InsertBlank(byte[] source, int atIndex, double widthPt, double heightPt)
        => Insert(source, CreateBlank(widthPt, heightPt), atIndex);

    public static byte[] Insert(byte[] source, byte[] other, int atIndex)
    {
        using var s1 = new MemoryStream(source);
        using var d1 = PdfReader.Open(s1, PdfDocumentOpenMode.Import);
        using var s2 = new MemoryStream(other);
        using var d2 = PdfReader.Open(s2, PdfDocumentOpenMode.Import);
        using var dst = new PdfDocument();
        int at = Math.Clamp(atIndex, 0, d1.PageCount);
        for (int i = 0; i < at; i++) dst.AddPage(d1.Pages[i]);
        for (int j = 0; j < d2.PageCount; j++) dst.AddPage(d2.Pages[j]);
        for (int i = at; i < d1.PageCount; i++) dst.AddPage(d1.Pages[i]);
        return ToBytes(dst);
    }

    /// <summary>Concatenates several documents into one.</summary>
    public static byte[] Merge(IEnumerable<byte[]> documents)
    {
        using var dst = new PdfDocument();
        foreach (var bytes in documents)
        {
            using var s = new MemoryStream(bytes);
            using var d = PdfReader.Open(s, PdfDocumentOpenMode.Import);
            for (int i = 0; i < d.PageCount; i++) dst.AddPage(d.Pages[i]);
        }
        if (dst.PageCount == 0) throw new InvalidOperationException("Nothing to merge.");
        return ToBytes(dst);
    }

    /// <summary>Extracts pages (by 1-based range spec) to a new PDF file on disk.</summary>
    public static void ExtractRangeToFile(byte[] source, string rangeSpec, string destPath, int pageCount)
    {
        var idx = ParseRange(rangeSpec, pageCount);
        if (idx.Count == 0) throw new InvalidOperationException($"No pages in range: {rangeSpec}");
        File.WriteAllBytes(destPath, Compose(source, idx));
    }

    /// <summary>
    /// Parses a page range like "2-3,5,8-10" (1-based, inclusive) into zero-based
    /// indices, clamped to <paramref name="pageCount"/>. Order and duplicates preserved.
    /// </summary>
    public static IReadOnlyList<int> ParseRange(string spec, int pageCount)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(spec)) return result;

        foreach (var partRaw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = partRaw.Trim();
            int dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part[..dash].Trim(), out int a) &&
                    int.TryParse(part[(dash + 1)..].Trim(), out int b))
                {
                    int step = a <= b ? 1 : -1;
                    for (int p = a; ; p += step)
                    {
                        if (p >= 1 && p <= pageCount) result.Add(p - 1);
                        if (p == b) break;
                    }
                }
            }
            else if (int.TryParse(part, out int single))
            {
                if (single >= 1 && single <= pageCount) result.Add(single - 1);
            }
        }
        return result;
    }

    private static byte[] ToBytes(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }
}
