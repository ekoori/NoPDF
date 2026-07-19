using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DjvuNet;
using DjvuNet.Graphics;

namespace NoPdf.Core.Import;

/// <summary>
/// Decodes DjVu documents to page images using the vendored, pure-managed DjvuNet decoder —
/// no external <c>ddjvu</c> tool and no native libraries, so it works the same on every
/// platform. Only the raw-pixel path is used (never DjvuNet's System.Drawing image API), and
/// pixels are PNG-encoded here so the rest of the pipeline is unchanged.
/// </summary>
public static class DjvuDecoder
{
    /// <summary>A rendered page: the image, plus the size its PDF page should be. The two are
    /// deliberately independent — render resolution varies per page (see
    /// <see cref="ChooseSubsample"/>), but page geometry must follow the DjVu's own
    /// dimensions or a double-page spread ends up no wider than a single leaf.</summary>
    public readonly record struct DjvuPageImage(byte[] Png, double WidthPt, double HeightPt);

    /// <summary>Cap on a rendered page's longest side, in pixels. A DjVu page at full
    /// resolution is often 2000–9000px; ~2200 keeps it crisp without a giant PDF.</summary>
    private const int MaxSide = 2200;

    /// <summary>DjvuNet accepts subsample factors 1..12 (DjvuNet.Util.Verify).</summary>
    private const int MaxSubsample = 12;

    /// <summary>Assumed scan resolution when a page doesn't declare one.</summary>
    private const int DefaultDpi = 300;

    /// <summary>JPEG quality for photographic pages. High enough to be visually lossless on
    /// scans, which matters because this is the second lossy step after DjVu's own wavelet.</summary>
    private const int JpegQuality = 88;

    /// <summary>One image per page, in order.</summary>
    /// <param name="onProgress">Called as pages finish, with (completed, total). Invoked from
    /// worker threads, so it must be safe to call concurrently.</param>
    public static IReadOnlyList<DjvuPageImage> DecodeToPages(string path,
        Action<int, int>? onProgress = null, ImportPreview? preview = null)
    {
        // Read once, sequentially. Libraries often live on network/cloud mounts where the
        // decoder's seek-heavy access pattern is painfully slow, and the parallel decode below
        // needs a separate document per thread — re-reading the file per worker would multiply
        // that cost.
        byte[] file = File.ReadAllBytes(path);

        int pageCount;
        using (var probe = Open(file, path))
        {
            pageCount = probe.Pages?.Count ?? 0;
            if (pageCount == 0) throw new InvalidDataException("The DjVu document has no pages.");

            // Page geometry is in the header, so the whole document can be laid out before a
            // single page is decoded — the viewer gets its page count and scrollbar straight
            // away and fills the pages in behind that.
            if (preview?.Layout is not null)
            {
                var probePages = probe.Pages!;
                var layout = new (double, double)[pageCount];
                for (int i = 0; i < pageCount; i++)
                {
                    var p = probePages[i];
                    int dpi = p.Info?.DPI ?? 0;
                    if (dpi <= 0) dpi = DefaultDpi;
                    layout[i] = (p.Width * 72.0 / dpi, p.Height * 72.0 / dpi);
                }
                preview.Layout(layout);
            }
        }

        // Decoding is CPU-bound and pages are independent, but a DjvuDocument is not safe to
        // share across threads — so each worker gets its own, over the same bytes.
        var results = new DjvuPageImage[pageCount];
        int done = 0;
        onProgress?.Invoke(0, pageCount);

        void Completed()
        {
            int n = Interlocked.Increment(ref done);
            // Pages finish out of order across workers, and a big book would otherwise post
            // hundreds of updates; report at most once per percent (and always the last one).
            if (onProgress is null) return;
            if (n == pageCount || pageCount <= 100 || n * 100 / pageCount != (n - 1) * 100 / pageCount)
                onProgress(n, pageCount);
        }

        void Publish(int index)
        {
            if (preview?.Page is not null && results[index].Png is { } png) preview.Page(index, png);
        }

        int workers = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        if (workers == 1 || pageCount == 1)
        {
            using var doc = Open(file, path);
            for (int i = 0; i < pageCount; i++)
            {
                results[i] = RenderPage(doc.Pages[i]);
                Publish(i);
                Completed();
            }
        }
        else
        {
            Parallel.For(0, workers, wk =>
            {
                using var doc = Open(file, path);
                for (int i = wk; i < pageCount; i += workers)
                {
                    try { results[i] = RenderPage(doc.Pages[i]); Publish(i); }
                    catch { /* drop this page rather than failing the whole document */ }
                    Completed();
                }
            });
        }

        var pages = results.Where(r => r.Png is not null).ToList();
        if (pages.Count == 0) throw new InvalidDataException("No DjVu pages could be decoded.");
        return pages;
    }

    /// <summary>A document over an in-memory copy of the file (read-only, no array copy).</summary>
    private static DjvuDocument Open(byte[] file, string path)
    {
        var doc = new DjvuDocument();
        doc.Load(new MemoryStream(file, writable: false), path);
        return doc;
    }

    private static DjvuPageImage RenderPage(IDjvuPage page)
    {
        int w = page.Width, h = page.Height;
        if (w <= 0 || h <= 0) return default;

        // Page geometry comes from the DjVu's own size and resolution, independent of how
        // coarsely the pixels below happen to be rendered.
        int dpi = page.Info?.DPI ?? 0;
        if (dpi <= 0) dpi = DefaultDpi;
        double widthPt = w * 72.0 / dpi, heightPt = h * 72.0 / dpi;

        // Purely bitonal pages — a JB2 mask with no colour layers, which is most scanned text.
        // GetPixelMap returns null for these (it has nothing to stencil onto), so they take a
        // separate path entirely.
        if (page.BackgroundIWPixelMap is null && page.ForegroundIWPixelMap is null
            && page.ForegroundJB2Image is not null)
            return RenderBitonal(page, widthPt, heightPt);

        // Try the preferred subsample, then any other supported one, until pixels come back.
        foreach (int subsample in ChooseSubsample(page))
        {
            int sw = (w + subsample - 1) / subsample;
            int sh = (h + subsample - 1) / subsample;
            IPixelMap pm;
            try { pm = page.GetPixelMap(new Rectangle(0, 0, sw, sh), subsample, 2.2, null); }
            catch { continue; }
            if (pm?.Data is null || pm.Width <= 0 || pm.Height <= 0) continue;
            if (IsBlank(pm)) continue;

            // Photographic pages (those carrying an IW44 background) go to JPEG: the source
            // wavelet is already lossy, so it costs nothing real and saves an order of
            // magnitude over PNG. Bitonal text pages stay PNG, where JPEG would ring badly
            // around the glyphs.
            var rgb = ToRgb(pm);
            byte[] image = page.BackgroundIWPixelMap is not null
                ? JpegEncoder.Encode(rgb, pm.Width, pm.Height, JpegQuality)
                : EncodePng(rgb, pm.Width, pm.Height);
            return new DjvuPageImage(image, widthPt, heightPt);
        }
        return default;
    }

    /// <summary>
    /// Renders a bitonal page from its JB2 mask. DjvuNet only decodes the mask correctly at
    /// full resolution — asking for a subsampled bitmap returns an empty one — so the mask is
    /// decoded at 1:1 and box-filtered down here. That is also better than a nearest-neighbour
    /// reduction would be: averaging the ink coverage antialiases the glyphs into grey rather
    /// than dropping strokes, which is what makes shrunken scanned text readable.
    /// </summary>
    private static DjvuPageImage RenderBitonal(IDjvuPage page, double widthPt, double heightPt)
    {
        int w = page.Width, h = page.Height;
        IBitmap bm;
        try { bm = page.GetBitmap(new Rectangle(0, 0, w, h), 1, 1, null); }
        catch { return default; }
        if (bm?.Data is null || bm.Width <= 0 || bm.Height <= 0) return default;

        int srcW = bm.Width, srcH = bm.Height;
        int factor = Math.Clamp((Math.Max(srcW, srcH) + MaxSide - 1) / MaxSide, 1, 16);
        int outW = Math.Max(1, srcW / factor), outH = Math.Max(1, srcH / factor);
        int levels = Math.Max(1, bm.Grays - 1);   // mask values run 0 (paper) .. Grays-1 (ink)
        var data = bm.Data;

        var rgb = new byte[outW * outH * 3];
        int o = 0;
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                int sum = 0, n = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int sy = y * factor + dy;
                    if (sy >= srcH) break;
                    int row = (srcH - 1 - sy) * srcW;      // bottom-up -> top-down
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int sx = x * factor + dx;
                        if (sx >= srcW) break;
                        sum += (byte)data[row + sx];
                        n++;
                    }
                }
                byte v = (byte)(255 - (n == 0 ? 0 : sum * 255 / (n * levels)));
                rgb[o++] = v; rgb[o++] = v; rgb[o++] = v;
            }
        }

        // Always PNG: this is text, and grey-on-white antialiased glyphs both compress well
        // losslessly and would ring under JPEG.
        return new DjvuPageImage(EncodePng(rgb, outW, outH), widthPt, heightPt);
    }

    /// <summary>
    /// Subsample factors to try, best first. This matters for correctness, not just size:
    /// DjvuNet renders a page's background layer only when the requested subsample lines up
    /// with the reduction the IW44 background is stored at (<c>red</c>, i.e. red, 4·red/3,
    /// 2·red, 4·red or 8·red — see DjvuPage.GetBgPixmap). Any other factor falls into a
    /// scaler branch that yields an all-zero (blank) map. The supported factors are also far
    /// faster, since they map onto the wavelet's own reduction levels.
    /// </summary>
    private static IEnumerable<int> ChooseSubsample(IDjvuPage page)
    {
        int w = page.Width, h = page.Height;
        int ideal = Math.Clamp((Math.Max(w, h) + MaxSide - 1) / MaxSide, 1, MaxSubsample);

        var supported = SupportedSubsamples(page);
        if (supported.Count == 0)
        {
            // No background layer (e.g. a purely bitonal page): any factor renders.
            yield return ideal;
            yield break;
        }

        // Smallest supported factor that keeps the page under the cap (the best quality that
        // fits); failing that the coarsest available, then the rest as fallbacks.
        var ordered = supported.Where(s => s >= ideal).OrderBy(s => s)
            .Concat(supported.Where(s => s < ideal).OrderByDescending(s => s));
        foreach (int s in ordered) yield return s;
    }

    private static IReadOnlyList<int> SupportedSubsamples(IDjvuPage page)
    {
        var bg = page.BackgroundIWPixelMap;
        if (bg is null || bg.Width <= 0 || bg.Height <= 0) return Array.Empty<int>();

        // The reduction the background is stored at, relative to the full page (this mirrors
        // DjvuPage.ComputeRed, which is internal to the library).
        int red = 0;
        for (int r = 1; r < 16; r++)
        {
            if ((page.Width + r - 1) / r == bg.Width && (page.Height + r - 1) / r == bg.Height)
            { red = r; break; }
        }
        if (red is 0 or > MaxSubsample) return Array.Empty<int>();

        var set = new SortedSet<int>();
        foreach (int s in new[] { red, 2 * red, 4 * red, 8 * red })
            if (s <= MaxSubsample) set.Add(s);
        if (red % 3 == 0 && red * 4 / 3 <= MaxSubsample) set.Add(red * 4 / 3); // the 4:3 path
        return set.ToArray();
    }

    /// <summary>An all-zero map means the decode silently produced nothing.</summary>
    private static bool IsBlank(IPixelMap pm)
    {
        var d = pm.Data;
        for (int i = 0; i < d.Length; i += 7) if (d[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// Flattens a DjvuNet pixel map to tightly packed, top-down RGB. The map is stored
    /// bottom-to-top with BGR samples, one row every <c>Width * BytesPerPixel</c> bytes (its
    /// <c>GetRowSize</c> is in pixels, not bytes), so rows are read in reverse and samples
    /// swapped.
    /// </summary>
    private static byte[] ToRgb(IPixelMap pm)
    {
        int w = pm.Width, h = pm.Height, bpp = pm.BytesPerPixel;
        int stride = w * bpp;                    // bytes per row
        var data = pm.Data;

        var rgb = new byte[w * h * 3];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (h - 1 - y) * stride;    // bottom-up -> top-down
            for (int x = 0; x < w; x++)
            {
                int p = srcRow + x * bpp;
                byte b = (byte)data[p];
                byte g = bpp >= 2 ? (byte)data[p + 1] : b;
                byte r = bpp >= 3 ? (byte)data[p + 2] : b;
                rgb[o++] = r; rgb[o++] = g; rgb[o++] = b;
            }
        }
        return rgb;
    }

    private static byte[] EncodePng(byte[] rgb, int w, int h)
    {
        // PNG wants each scanline prefixed with a filter byte (0 = none).
        var raw = new byte[h * (1 + w * 3)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (1 + w * 3)] = 0;             // filter: none
            Buffer.BlockCopy(rgb, y * w * 3, raw, y * (1 + w * 3) + 1, w * 3);
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
