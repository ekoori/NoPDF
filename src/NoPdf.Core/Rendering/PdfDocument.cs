using System.Runtime.InteropServices;
using System.Text;
using NoPdf.Core.Text;
using PDFiumCore;

namespace NoPdf.Core.Rendering;

/// <summary>
/// A single open PDF, backed by native PDFium. Provides page geometry and
/// rasterization. All native access is serialized through
/// <see cref="PdfiumLibrary.Sync"/>. Not intended to be used concurrently from
/// multiple threads on the same instance beyond that lock.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    // FPDFBitmap format constants.
    private const int FormatBGRA = 4;

    // Render flags (see fpdfview.h).
    private const int FPDF_ANNOT = 0x01;      // render existing annotations
    private const int FPDF_LCD_TEXT = 0x02;   // subpixel text
    private const int FPDF_NO_NATIVETEXT = 0; // keep native text rendering

    private FpdfDocumentT? _doc;
    private readonly PageInfo[] _pages;
    // The file bytes must stay pinned for the lifetime of the PDFium document
    // when loading from memory.
    private GCHandle _bufferHandle;
    private bool _disposed;

    // AcroForm (form-fill) environment. PDFium draws widget annotations — form field
    // contents and signature appearance stamps — only through this module; a plain
    // FPDF_RenderPageBitmap with FPDF_ANNOT skips them entirely. Created on first
    // render and torn down before the document closes.
    private FpdfFormHandleT? _form;
    // PDFium keeps a pointer to this struct, so it has to outlive the form handle.
    private FPDF_FORMFILLINFO? _formInfo;
    private bool _formTried;

    /// <summary>Absolute path the document was loaded from (may be null for in-memory).</summary>
    public string? FilePath { get; }

    public int PageCount => _pages.Length;

    private PdfDocument(FpdfDocumentT doc, string? filePath, PageInfo[] pages, GCHandle bufferHandle)
    {
        _doc = doc;
        FilePath = filePath;
        _pages = pages;
        _bufferHandle = bufferHandle;
    }

    /// <summary>
    /// Opens a PDF from disk. The file is read into memory and handed to PDFium so
    /// the underlying file is not kept locked (allowing save-in-place).
    /// Throws if the document cannot be loaded.
    /// </summary>
    public static PdfDocument Open(string filePath, string? password = null)
    {
        var bytes = File.ReadAllBytes(filePath);
        var doc = OpenBytes(bytes, filePath, password);
        return doc;
    }

    /// <summary>Opens a PDF from an in-memory buffer. The buffer is copied and pinned.</summary>
    public static PdfDocument OpenBytes(byte[] bytes, string? filePath = null, string? password = null)
    {
        PdfiumLibrary.EnsureInitialized();
        // Own a private pinned copy so callers can't mutate/collect it underneath PDFium.
        var owned = (byte[])bytes.Clone();
        var handle = GCHandle.Alloc(owned, GCHandleType.Pinned);
        try
        {
            lock (PdfiumLibrary.Sync)
            {
                var doc = fpdfview.FPDF_LoadMemDocument64(handle.AddrOfPinnedObject(),
                    (ulong)owned.LongLength, password);
                if (doc == null)
                    throw new PdfLoadException(
                        $"PDFium could not open the document. It may be corrupt or password-protected.");

                int count = fpdfview.FPDF_GetPageCount(doc);
                var pages = new PageInfo[count];
                for (int i = 0; i < count; i++)
                {
                    double w = 0, h = 0;
                    fpdfview.FPDF_GetPageSizeByIndex(doc, i, ref w, ref h);
                    pages[i] = new PageInfo(w, h);
                }
                return new PdfDocument(doc, filePath, pages, handle);
            }
        }
        catch
        {
            handle.Free();
            throw;
        }
    }

    /// <summary>Displayed page size in PDF points (rotation already applied).</summary>
    public PageInfo GetPageSize(int index) => _pages[index];

    /// <summary>True when the document has an AcroForm (or XFA) — i.e. form fields
    /// and/or signature widgets that need the form module to render.</summary>
    public bool HasForm
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (PdfiumLibrary.Sync)
                return fpdf_formfill.FPDF_GetFormType(_doc) != FORMTYPE_NONE;
        }
    }

    private const int FORMTYPE_NONE = 0;

    /// <summary>
    /// The form-fill environment, created on first use. Null when the document has no
    /// form or PDFium refused to create one — callers just skip the widget pass.
    /// Must be called under <see cref="PdfiumLibrary.Sync"/>.
    /// </summary>
    private FpdfFormHandleT? EnsureForm()
    {
        if (_form is not null) return _form;
        if (_formTried) return null;
        _formTried = true;
        if (fpdf_formfill.FPDF_GetFormType(_doc) == FORMTYPE_NONE) return null;

        // Interface version 2 is current; older builds only accept 1. The callbacks are
        // all optional for rendering — nothing here changes the form, so PDFium never
        // needs to call back for invalidation.
        foreach (int version in new[] { 2, 1 })
        {
            var info = new FPDF_FORMFILLINFO { Version = version };
            var handle = fpdf_formfill.FPDFDOC_InitFormFillEnvironment(_doc, info);
            if (handle is not null)
            {
                _formInfo = info;
                _form = handle;
                // Render fields as the document authored them: no viewer-added blue wash
                // over every widget.
                fpdf_formfill.FPDF_RemoveFormFieldHighlight(handle);
                return _form;
            }
            info.Dispose();
        }
        return null;
    }

    private int[]? _rotations;

    /// <summary>Page rotation in degrees (0/90/180/270). Read lazily and cached.</summary>
    public int GetPageRotationDegrees(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _rotations ??= CreateFilled(_pages.Length, -1);
        if (_rotations[index] >= 0) return _rotations[index];

        lock (PdfiumLibrary.Sync)
        {
            var page = fpdfview.FPDF_LoadPage(_doc, index);
            if (page == null) return _rotations[index] = 0;
            try { return _rotations[index] = fpdf_edit.FPDFPageGetRotation(page) * 90; }
            finally { fpdfview.FPDF_ClosePage(page); }
        }
    }

    private static int[] CreateFilled(int n, int value)
    {
        var a = new int[n];
        Array.Fill(a, value);
        return a;
    }

    /// <summary>
    /// Rasterizes a page. <paramref name="scale"/> maps points→pixels
    /// (e.g. 96/72 * zoom). Returns a BGRA buffer with a white background.
    /// </summary>
    public RenderedPage RenderPage(int index, double scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)_pages.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        var size = _pages[index];
        int width = Math.Max(1, (int)Math.Round(size.Width * scale));
        int height = Math.Max(1, (int)Math.Round(size.Height * scale));
        int stride = width * 4;
        var pixels = new byte[stride * height];

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            lock (PdfiumLibrary.Sync)
            {
                var page = fpdfview.FPDF_LoadPage(_doc, index);
                if (page == null)
                    throw new PdfLoadException($"PDFium could not load page {index}.");
                try
                {
                    var bmp = fpdfview.FPDFBitmapCreateEx(width, height, FormatBGRA,
                        handle.AddrOfPinnedObject(), stride);
                    try
                    {
                        const int flags = FPDF_ANNOT | FPDF_LCD_TEXT;
                        // Opaque white background (0xAARRGGBB -> 0xFFFFFFFF).
                        fpdfview.FPDFBitmapFillRect(bmp, 0, 0, width, height, 0xFFFFFFFF);
                        fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, width, height, 0, flags);
                        DrawFormWidgets(page, bmp, width, height, flags);
                    }
                    finally { fpdfview.FPDFBitmapDestroy(bmp); }
                }
                finally { fpdfview.FPDF_ClosePage(page); }
            }
        }
        finally { handle.Free(); }

        return new RenderedPage { Width = width, Height = height, Stride = stride, Pixels = pixels };
    }

    /// <summary>
    /// Draws the page's widget annotations (form field contents, signature stamps) over
    /// the already-rendered page. This is the second half of PDFium's standard render
    /// pass; without it widgets are simply absent. Must be called under
    /// <see cref="PdfiumLibrary.Sync"/> with <paramref name="page"/> loaded.
    /// </summary>
    private void DrawFormWidgets(FpdfPageT page, FpdfBitmapT bmp, int width, int height, int flags)
    {
        var form = EnsureForm();
        if (form is null) return;
        // The form module tracks pages it has been told about; FFLDraw on an unannounced
        // page draws nothing.
        fpdf_formfill.FORM_OnAfterLoadPage(page, form);
        try
        {
            fpdf_formfill.FPDF_FFLDraw(form, bmp, page, 0, 0, width, height, 0, flags);
        }
        finally { fpdf_formfill.FORM_OnBeforeClosePage(page, form); }
    }

    /// <summary>
    /// Extracts the text layout for a page (glyph boxes + full text) into a
    /// fully-managed <see cref="PdfTextPage"/>. Retains no native handles.
    /// </summary>
    public PdfTextPage GetTextPage(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)_pages.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        var size = _pages[index];
        lock (PdfiumLibrary.Sync)
        {
            var page = fpdfview.FPDF_LoadPage(_doc, index);
            if (page == null)
                throw new PdfLoadException($"PDFium could not load page {index}.");
            try
            {
                var tp = fpdf_text.FPDFTextLoadPage(page);
                if (tp == null)
                    return new PdfTextPage(size.Width, size.Height, Array.Empty<TextChar>(), string.Empty);
                try
                {
                    int n = fpdf_text.FPDFTextCountChars(tp);
                    if (n < 0) n = 0;
                    var chars = new TextChar[n];
                    var sb = new StringBuilder(n);
                    for (int i = 0; i < n; i++)
                    {
                        double l = 0, r = 0, b = 0, t = 0;
                        fpdf_text.FPDFTextGetCharBox(tp, i, ref l, ref r, ref b, ref t);
                        uint u = fpdf_text.FPDFTextGetUnicode(tp, i);
                        char ch = u <= 0xFFFF ? (char)u : ' ';
                        chars[i] = new TextChar(ch, l, b, r, t);
                        sb.Append(ch);
                    }
                    return new PdfTextPage(size.Width, size.Height, chars, sb.ToString());
                }
                finally { fpdf_text.FPDFTextClosePage(tp); }
            }
            finally { fpdfview.FPDF_ClosePage(page); }
        }
    }

    /// <summary>Number of embedded (cryptographic) signature fields in the document.</summary>
    public int GetSignatureCount()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (PdfiumLibrary.Sync)
            return fpdf_signature.FPDF_GetSignatureCount(_doc);
    }

    /// <summary>Reads the document's bookmark outline (table of contents).</summary>
    public IReadOnlyList<OutlineItem> GetOutline()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var roots = new List<OutlineItem>();
        lock (PdfiumLibrary.Sync)
        {
            var first = fpdf_doc.FPDFBookmarkGetFirstChild(_doc, null);
            ReadSiblings(first, roots, depth: 0);
        }
        return roots;
    }

    private void ReadSiblings(FpdfBookmarkT? bookmark, List<OutlineItem> into, int depth)
    {
        int guard = 0;
        while (bookmark != null && guard++ < 5000 && depth < 32)
        {
            string title = ReadBookmarkTitle(bookmark);
            int page = ReadBookmarkPage(bookmark);
            var item = new OutlineItem { Title = title, PageIndex = page };
            into.Add(item);

            var child = fpdf_doc.FPDFBookmarkGetFirstChild(_doc, bookmark);
            if (child != null) ReadSiblings(child, item.Children, depth + 1);

            bookmark = fpdf_doc.FPDFBookmarkGetNextSibling(_doc, bookmark);
        }
    }

    private static string ReadBookmarkTitle(FpdfBookmarkT bookmark)
    {
        ulong len = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
        if (len <= 2) return string.Empty;
        var buf = new byte[len];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { fpdf_doc.FPDFBookmarkGetTitle(bookmark, h.AddrOfPinnedObject(), len); }
        finally { h.Free(); }
        // UTF-16LE, minus the 2-byte null terminator.
        return Encoding.Unicode.GetString(buf, 0, (int)len - 2);
    }

    private int ReadBookmarkPage(FpdfBookmarkT bookmark)
    {
        var dest = fpdf_doc.FPDFBookmarkGetDest(_doc, bookmark);
        if (dest == null)
        {
            var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
            if (action != null) dest = fpdf_doc.FPDFActionGetDest(_doc, action);
        }
        return dest != null ? fpdf_doc.FPDFDestGetDestPageIndex(_doc, dest) : -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (PdfiumLibrary.Sync)
        {
            // The form environment holds the document; it has to go first.
            if (_form != null)
            {
                fpdf_formfill.FPDFDOC_ExitFormFillEnvironment(_form);
                _form = null;
            }
            _formInfo?.Dispose();
            _formInfo = null;

            if (_doc != null)
            {
                fpdfview.FPDF_CloseDocument(_doc);
                _doc = null;
            }
        }
        if (_bufferHandle.IsAllocated)
            _bufferHandle.Free();
    }
}

/// <summary>Page geometry in PDF points.</summary>
public readonly record struct PageInfo(double Width, double Height);

public sealed class PdfLoadException : Exception
{
    public PdfLoadException(string message) : base(message) { }
}
