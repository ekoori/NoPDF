using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using DjvuNet;
using DjvuNet.Graphics;

namespace NoPdf.Core.Import;

/// <summary>
/// Decodes DjVu documents to PNG page images using the vendored, pure-managed DjvuNet
/// decoder — no external <c>ddjvu</c> tool and no native libraries, so it works the same on
/// every platform. Only the raw-pixel path is used (never DjvuNet's System.Drawing image
/// API), and pixels are PNG-encoded here so the rest of the pipeline (ImagesToPdf) is
/// unchanged.
/// </summary>
public static class DjvuDecoder
{
    /// <summary>Cap on a rendered page's longest side, in pixels. A DjVu page at full
    /// resolution is often 2000–4000px; ~2200 keeps it crisp without a giant PDF.</summary>
    private const int MaxSide = 2200;

    /// <summary>One PNG per page, in order.</summary>
    public static IReadOnlyList<byte[]> DecodeToPngPages(string path)
    {
        using var doc = new DjvuDocument(path);
        var pages = doc.Pages;
        if (pages is null || pages.Count == 0)
            throw new InvalidDataException("The DjVu document has no pages.");

        var result = new List<byte[]>(pages.Count);
        foreach (var page in pages)
        {
            int w = page.Width, h = page.Height;
            if (w <= 0 || h <= 0) continue;

            // Render at a subsample chosen to keep the longest side under the cap. DjvuNet's
            // subsample is an integer divisor (1 = full res); the target rectangle is given
            // in the SUBSAMPLED coordinate space, so scale it down too.
            int subsample = 1;
            while (Math.Max(w, h) / subsample > MaxSide && subsample < 12) subsample++;
            int sw = (w + subsample - 1) / subsample;
            int sh = (h + subsample - 1) / subsample;

            var rect = new Rectangle(0, 0, sw, sh);
            var pm = page.GetPixelMap(rect, subsample, 2.2, null);
            if (pm?.Data is null || pm.Width <= 0 || pm.Height <= 0) continue;

            result.Add(EncodePng(pm));
        }
        if (result.Count == 0) throw new InvalidDataException("No DjVu pages could be decoded.");
        return result;
    }

    /// <summary>
    /// Encodes a DjvuNet pixel map to a PNG. The raw map (from <see cref="DjvuPage.GetPixelMap"/>)
    /// is stored bottom-to-top with inverted intensity and BGR samples, one row every
    /// <c>Width * BytesPerPixel</c> bytes (its <c>GetRowSize</c> is in pixels, not bytes). So
    /// rows are read in reverse, samples swapped to RGB, and each value complemented — the
    /// exact transform was found by matching DjvuNet's own reference render pixel-for-pixel.
    /// </summary>
    private static byte[] EncodePng(IPixelMap pm)
    {
        int w = pm.Width, h = pm.Height, bpp = pm.BytesPerPixel;
        int stride = w * bpp;                    // bytes per row
        var data = pm.Data;

        // PNG wants each scanline prefixed with a filter byte (0 = none), pixels as RGB.
        var raw = new byte[h * (1 + w * 3)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (h - 1 - y) * stride;    // bottom-up -> top-down
            raw[o++] = 0;                         // filter: none
            for (int x = 0; x < w; x++)
            {
                int p = srcRow + x * bpp;
                byte b = (byte)(255 - (byte)data[p]);
                byte g = bpp >= 2 ? (byte)(255 - (byte)data[p + 1]) : b;
                byte r = bpp >= 3 ? (byte)(255 - (byte)data[p + 2]) : b;
                raw[o++] = r; raw[o++] = g; raw[o++] = b;
            }
        }

        using var ms = new MemoryStream();
        WritePngSignature(ms);
        WriteChunk(ms, "IHDR", Ihdr(w, h));
        WriteChunk(ms, "IDAT", ZlibCompress(raw));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] Ihdr(int w, int h)
    {
        var b = new byte[13];
        WriteBE(b, 0, w);
        WriteBE(b, 4, h);
        b[8] = 8;   // bit depth
        b[9] = 2;   // colour type: truecolour (RGB)
        b[10] = 0;  // deflate
        b[11] = 0;  // filter method 0
        b[12] = 0;  // no interlace
        return b;
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static void WritePngSignature(Stream s)
        => s.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length);
        s.Write(len, 0, 4);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc32(typeBytes, data);
        var crcB = new byte[4]; WriteBE(crcB, 0, (int)crc);
        s.Write(crcB, 0, 4);
    }

    private static void WriteBE(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static readonly uint[] _crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in type) c = _crcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in data) c = _crcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
