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

    // ----- Form interaction (AcroForm filling) -----

    // The page currently being typed into. PDFium's focused field lives on its page view,
    // which FORM_OnBeforeClosePage destroys — so the page a user is filling has to stay
    // loaded across clicks, keystrokes and re-renders, or focus is lost after every one.
    private FpdfPageT? _formPage;
    private int _formPageIndex = -1;

    /// <summary>Loads and announces the page being interacted with, keeping it open.
    /// Call under <see cref="PdfiumLibrary.Sync"/>.</summary>
    private FpdfPageT? AcquireFormPage(int index)
    {
        var form = EnsureForm();
        if (form is null) return null;
        if (_formPageIndex == index && _formPage is not null) return _formPage;

        ReleaseFormPage();
        var page = fpdfview.FPDF_LoadPage(_doc, index);
        if (page == null) return null;
        fpdf_formfill.FORM_OnAfterLoadPage(page, form);
        _formPage = page;
        _formPageIndex = index;
        return page;
    }

    private void ReleaseFormPage()
    {
        if (_formPage is null) return;
        if (_form is not null)
        {
            fpdf_formfill.FORM_ForceToKillFocus(_form);
            fpdf_formfill.FORM_OnBeforeClosePage(_formPage, _form);
        }
        fpdfview.FPDF_ClosePage(_formPage);
        _formPage = null;
        _formPageIndex = -1;
    }

    /// <summary>
    /// The form field type at a page-space point, or -1 for none. A pure query: it must not
    /// take over the page held for editing, or merely asking about another page would drop
    /// the field the user is typing into.
    /// </summary>
    public int FormFieldTypeAt(int index, double pageX, double pageY)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (PdfiumLibrary.Sync)
        {
            var form = EnsureForm();
            if (form is null) return -1;
            bool held = _formPageIndex == index && _formPage is not null;
            var page = held ? _formPage! : fpdfview.FPDF_LoadPage(_doc, index);
            if (page == null) return -1;
            try { return fpdf_formfill.FPDFPageHasFormFieldAtPoint(form, page, pageX, pageY); }
            finally { if (!held) fpdfview.FPDF_ClosePage(page); }
        }
    }

    /// <summary>Clicks a form field at a page-space point, giving it keyboard focus.
    /// False when there's no field there.</summary>
    public bool FormClick(int index, double pageX, double pageY)
    {
        if (!FormMouseDown(index, pageX, pageY)) return false;
        FormMouseUp(index, pageX, pageY);
        return true;
    }

    /// <summary>Presses in a form field: focuses it and starts a selection. Follow with
    /// <see cref="FormMouseMove"/> while dragging and <see cref="FormMouseUp"/> on release.
    /// False when there's no field at the point.</summary>
    public bool FormMouseDown(int index, double pageX, double pageY)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (PdfiumLibrary.Sync)
        {
            var form = EnsureForm();
            if (form is null) return false;
            var page = AcquireFormPage(index);
            if (page is null) return false;
            if (fpdf_formfill.FPDFPageHasFormFieldAtPoint(form, page, pageX, pageY) < 0) return false;
            fpdf_formfill.FORM_OnLButtonDown(form, page, 0, pageX, pageY);
            return true;
        }
    }

    /// <summary>Drags within the focused field (extends the text selection).</summary>
    public void FormMouseMove(int index, double pageX, double pageY)
    {
        if (_disposed) return;
        lock (PdfiumLibrary.Sync)
        {
            if (_form is null || _formPage is null || _formPageIndex != index) return;
            fpdf_formfill.FORM_OnMouseMove(_form, _formPage, 0, pageX, pageY);
        }
    }

    public void FormMouseUp(int index, double pageX, double pageY)
    {
        if (_disposed) return;
        lock (PdfiumLibrary.Sync)
        {
            if (_form is null || _formPage is null || _formPageIndex != index) return;
            fpdf_formfill.FORM_OnLButtonUp(_form, _formPage, 0, pageX, pageY);
        }
    }

    /// <summary>The text selected inside the focused field (for :copy).</summary>
    public string SelectedFormText()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (PdfiumLibrary.Sync)
        {
            if (_form is null || _formPage is null) return "";
            ulong len = fpdf_formfill.FORM_GetSelectedText(_form, _formPage, IntPtr.Zero, 0);
            if (len <= 2) return "";
            var buf = new byte[len];
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { fpdf_formfill.FORM_GetSelectedText(_form, _formPage, h.AddrOfPinnedObject(), len); }
            finally { h.Free(); }
            return Encoding.Unicode.GetString(buf, 0, (int)len - 2);
        }
    }

    /// <summary>
    /// Types one character into the focused field. False if nothing took it.
    /// Backspace ('\b') and Enter ('\r') are character events in PDFium, not key-downs —
    /// send those here; <see cref="FormKey"/> ignores them.
    /// </summary>
    public bool FormChar(char c)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (PdfiumLibrary.Sync)
        {
            if (_form is null || _formPage is null) return false;
            return fpdf_formfill.FORM_OnChar(_form, _formPage, c, 0) != 0;
        }
    }

    /// <summary>
    /// Sends a virtual key to the focused field: Delete, the arrows, Home/End, Tab.
    /// NOT backspace or Enter — PDFium routes those through <see cref="FormChar"/>.
    /// </summary>
    public bool FormKey(int virtualKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (PdfiumLibrary.Sync)
        {
            if (_form is null || _formPage is null) return false;
            bool handled = fpdf_formfill.FORM_OnKeyDown(_form, _formPage, virtualKey, 0) != 0;
            fpdf_formfill.FORM_OnKeyUp(_form, _formPage, virtualKey, 0);
            return handled;
        }
    }

    /// <summary>The text of the focused field (what the user has typed so far).</summary>
    public string FocusedFormText()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (PdfiumLibrary.Sync)
        {
            if (_form is null || _formPage is null) return "";
            ulong len = fpdf_formfill.FORM_GetFocusedText(_form, _formPage, IntPtr.Zero, 0);
            if (len <= 2) return "";
            var buf = new byte[len];
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { fpdf_formfill.FORM_GetFocusedText(_form, _formPage, h.AddrOfPinnedObject(), len); }
            finally { h.Free(); }
            return Encoding.Unicode.GetString(buf, 0, (int)len - 2); // drop the UTF-16 terminator
        }
    }

    /// <summary>Drops keyboard focus and releases the interacted page.</summary>
    public void FormKillFocus()
    {
        if (_disposed) return;
        lock (PdfiumLibrary.Sync) ReleaseFormPage();
    }

    /// <summary>Every widget (form field) on a page, for hinting and hit-testing.</summary>
    public IReadOnlyList<FormFieldInfo> GetFormFields(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var list = new List<FormFieldInfo>();
        lock (PdfiumLibrary.Sync)
        {
            var form = EnsureForm();
            if (form is null) return list;
            var page = fpdfview.FPDF_LoadPage(_doc, index);
            if (page == null) return list;
            try
            {
                int n = fpdf_annot.FPDFPageGetAnnotCount(page);
                for (int i = 0; i < n; i++)
                {
                    var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                    if (annot == null) continue;
                    try
                    {
                        if (fpdf_annot.FPDFAnnotGetSubtype(annot) != ANNOT_WIDGET) continue;
                        int type = fpdf_annot.FPDFAnnotGetFormFieldType(form, annot);
                        if (type < 0) continue;
                        var r = new FS_RECTF_();
                        if (fpdf_annot.FPDFAnnotGetRect(annot, r) == 0) continue;
                        list.Add(new FormFieldInfo(
                            index, (FormFieldType)type,
                            Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top),
                            Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top),
                            ReadFieldName(form, annot)));
                    }
                    finally { fpdf_annot.FPDFPageCloseAnnot(annot); }
                }
            }
            finally { fpdfview.FPDF_ClosePage(page); }
        }
        return list;
    }

    private const int ANNOT_WIDGET = 20; // FPDF_ANNOT_WIDGET

    private static string ReadFieldName(FpdfFormHandleT form, FpdfAnnotationT annot)
    {
        ushort dummy = 0;
        ulong len = fpdf_annot.FPDFAnnotGetFormFieldName(form, annot, ref dummy, 0);
        if (len <= 2) return "";
        var buf = new ushort[len / 2];
        ulong got = fpdf_annot.FPDFAnnotGetFormFieldName(form, annot, ref buf[0], len);
        if (got <= 2) return "";
        var bytes = new byte[got];
        Buffer.BlockCopy(buf, 0, bytes, 0, (int)got);
        return Encoding.Unicode.GetString(bytes, 0, (int)got - 2);
    }

    /// <summary>
    /// The document's bytes including any form values the user has filled in. Form edits
    /// live inside PDFium, so they can only come back out through its own writer.
    /// </summary>
    public byte[] SaveWithFormValues()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (PdfiumLibrary.Sync)
        {
            // Committing focus flushes the field being edited into the document.
            if (_form is not null && _formPage is not null)
                fpdf_formfill.FORM_ForceToKillFocus(_form);

            using var ms = new MemoryStream();
            var writer = new FPDF_FILEWRITE_ { Version = 1 };
            writer.WriteBlock = (_, data, size) =>
            {
                if (size > 0 && data != IntPtr.Zero)
                {
                    var chunk = new byte[size];
                    Marshal.Copy(data, chunk, 0, (int)size);
                    ms.Write(chunk, 0, chunk.Length);
                }
                return 1; // non-zero = keep going
            };
            int ok = fpdf_save.FPDF_SaveAsCopy(_doc, writer, 0);
            GC.KeepAlive(writer);
            if (ok == 0) throw new PdfLoadException("PDFium could not write the document.");
            return ms.ToArray();
        }
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
                // Reuse the page held open for form editing — loading a second handle for
                // it (and closing it here) would tear down the focused field mid-typing.
                bool held = _formPageIndex == index && _formPage is not null;
                var page = held ? _formPage! : fpdfview.FPDF_LoadPage(_doc, index);
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
                        DrawFormWidgets(page, bmp, width, height, flags, alreadyAnnounced: held);
                    }
                    finally { fpdfview.FPDFBitmapDestroy(bmp); }
                }
                finally { if (!held) fpdfview.FPDF_ClosePage(page); }
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
    private void DrawFormWidgets(FpdfPageT page, FpdfBitmapT bmp, int width, int height, int flags,
        bool alreadyAnnounced = false)
    {
        var form = EnsureForm();
        if (form is null) return;
        // The form module tracks pages it has been told about; FFLDraw on an unannounced
        // page draws nothing. A page held open for editing is already announced — and
        // un-announcing it would drop the focused field.
        if (!alreadyAnnounced) fpdf_formfill.FORM_OnAfterLoadPage(page, form);
        try
        {
            fpdf_formfill.FPDF_FFLDraw(form, bmp, page, 0, 0, width, height, 0, flags);
        }
        finally { if (!alreadyAnnounced) fpdf_formfill.FORM_OnBeforeClosePage(page, form); }
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
            ReleaseFormPage(); // the page view outlives neither the env nor the document
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

/// <summary>AcroForm field kinds, matching PDFium's FPDF_FORMFIELD_* values.</summary>
public enum FormFieldType
{
    Unknown = 0, PushButton = 1, CheckBox = 2, RadioButton = 3,
    ComboBox = 4, ListBox = 5, TextField = 6, Signature = 7,
}

/// <summary>One widget (form field) on a page, in page space (points, origin bottom-left).</summary>
public readonly record struct FormFieldInfo(
    int PageIndex, FormFieldType Type,
    double Left, double Bottom, double Right, double Top, string Name)
{
    /// <summary>Signature fields are read-only here — noPDF signs via :sign, not by typing.</summary>
    public bool IsFillable => Type is FormFieldType.CheckBox or FormFieldType.RadioButton
        or FormFieldType.ComboBox or FormFieldType.ListBox or FormFieldType.TextField
        or FormFieldType.PushButton;

    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Bottom + Top) / 2;
}

public sealed class PdfLoadException : Exception
{
    public PdfLoadException(string message) : base(message) { }
}
