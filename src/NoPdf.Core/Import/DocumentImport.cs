using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace NoPdf.Core.Import;

/// <summary>
/// Opens non-PDF documents by converting them to an in-memory PDF, so the whole
/// viewer/editor pipeline works unchanged. Comic archives (CBZ/CBR/CB7/CBT) become
/// one image per page; DjVu is converted via DjVuLibre's <c>ddjvu</c> if available.
/// </summary>
public static class DocumentImport
{
    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff" };

    public static bool IsSupportedNonPdf(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return e is ".cbz" or ".cbr" or ".cb7" or ".cbt" or ".djvu" or ".djv";
    }

    /// <summary>PDF bytes for the file: comics/DjVu are converted; anything else is read as-is.</summary>
    public static byte[] ReadAsPdfBytes(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        if (e is ".cbz" or ".cbr" or ".cb7" or ".cbt") return ComicToPdf(path);
        if (e is ".djvu" or ".djv") return DjvuToPdf(path);
        return File.ReadAllBytes(path);
    }

    private static byte[] ComicToPdf(string path)
    {
        var images = new List<(string name, byte[] data)>();
        using (var archive = SharpCompress.Archives.ArchiveFactory.Open(path))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                string key = entry.Key ?? "";
                if (Array.IndexOf(ImageExt, Path.GetExtension(key).ToLowerInvariant()) < 0) continue;
                using var s = entry.OpenEntryStream();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                images.Add((key, ms.ToArray()));
            }
        }
        if (images.Count == 0) throw new InvalidDataException("No page images found in the archive.");
        images.Sort((a, b) => NaturalCompare(a.name, b.name));
        return ImagesToPdf(images.Select(i => i.data));
    }

    public static byte[] ImagesToPdf(IEnumerable<byte[]> images)
    {
        var doc = new PdfDocument();
        foreach (var bytes in images)
        {
            XImage xi;
            try { xi = XImage.FromStream(new MemoryStream(bytes)); } // stream kept alive by the XImage
            catch { continue; } // skip anything PdfSharp can't decode (e.g. webp)
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(xi.PixelWidth);
            page.Height = XUnit.FromPoint(xi.PixelHeight);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(xi, 0, 0, page.Width.Point, page.Height.Point);
        }
        if (doc.PageCount == 0) throw new InvalidDataException("No decodable page images.");
        using var outMs = new MemoryStream();
        doc.Save(outMs, false);
        return outMs.ToArray();
    }

    private static byte[] DjvuToPdf(string path)
    {
        string outPdf = Path.Combine(Path.GetTempPath(), "nopdf_" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ddjvu")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-format=pdf");
            psi.ArgumentList.Add("-quality=85");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add(outPdf);
            using var p = System.Diagnostics.Process.Start(psi)
                ?? throw new NotSupportedException("Could not start ddjvu.");
            p.WaitForExit(120_000);
            if (p.ExitCode != 0 || !File.Exists(outPdf))
                throw new InvalidDataException("ddjvu failed to convert the DjVu file.");
            return File.ReadAllBytes(outPdf);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new NotSupportedException("DjVu support needs DjVuLibre (the 'ddjvu' tool) installed and on PATH.");
        }
        finally { try { if (File.Exists(outPdf)) File.Delete(outPdf); } catch { } }
    }

    /// <summary>Orders names so page2 &lt; page10 (numeric runs compared as numbers).</summary>
    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;
                int cmp = na.SequenceCompareTo(nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i) - (b.Length - j);
    }
}
