using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NoPdf.App.Editing;
using NoPdf.App.Rendering;
using NoPdf.Core.Annotations;
using NoPdf.Core.Rendering;
using NoPdf.Core.Text;

namespace NoPdf.App.ViewModels;

/// <summary>One page within a document. Renders itself lazily when realized in the UI.</summary>
public sealed partial class PageViewModel : ViewModelBase
{
    private readonly DocumentViewModel _owner;
    private readonly PageInfo _size;

    private double _renderedScale = double.NaN;
    private CancellationTokenSource? _renderCts;
    private bool _isRealized;

    private PdfTextPage? _textPage;
    private bool _textLoading;
    private int _rotation = -1; // degrees; -1 = not yet read

    private int _selAnchor = -1;
    private int _selCurrent = -1;

    public int PageIndex { get; }
    public int PageNumber => PageIndex + 1;
    public DocumentViewModel Owner => _owner;
    public bool IsRealized => _isRealized;

    /// <summary>Displayed page size in PDF points (rotation applied).</summary>
    public double PointWidth => _size.Width;
    public double PointHeight => _size.Height;

    /// <summary>Page rotation in degrees (0/90/180/270).</summary>
    public int Rotation => _rotation < 0 ? 0 : _rotation;
    private bool Swapped => Rotation is 90 or 270;
    public double UnrotWidth => Swapped ? _size.Height : _size.Width;
    public double UnrotHeight => Swapped ? _size.Width : _size.Height;

    /// <summary>Transform between page space and displayed DIPs at the current zoom.</summary>
    public PageTransform Transform => new(Rotation, UnrotWidth, UnrotHeight, _owner.Scale);

    [ObservableProperty] private Bitmap? _bitmap;

    /// <summary>Layout size in device-independent pixels at the current zoom.</summary>
    public double DisplayWidth => _size.Width * _owner.Scale;
    public double DisplayHeight => _size.Height * _owner.Scale;

    /// <summary>Current text selection as page-space rectangles (empty if none).</summary>
    public IReadOnlyList<TextRect> SelectionRects { get; private set; } = Array.Empty<TextRect>();
    public bool HasSelection => _selAnchor >= 0 && _selCurrent >= 0 && _textPage is not null;

    /// <summary>Annotations on this page (page-space), drawn by the overlay.</summary>
    public List<PdfAnnotationModel> Annotations { get; } = new();

    /// <summary>In-progress annotation being drawn (not yet committed).</summary>
    public PdfAnnotationModel? Draft { get; private set; }

    /// <summary>In-progress marquee (rubber-band) selection rect in page space, or null.</summary>
    public TextRect? MarqueeRect { get; private set; }
    public void SetMarquee(TextRect? rect) { MarqueeRect = rect; OverlayInvalidated?.Invoke(); }

    /// <summary>Raised when overlay-drawn content (selection/highlights) changes.</summary>
    public event Action? OverlayInvalidated;

    /// <summary>Raised when a find match selection is set, so the view can reveal it.</summary>
    public event Action? FindRevealRequested;

    /// <summary>Set when a find match awaits scrolling into view; the view clears it.</summary>
    public bool PendingFindReveal { get; set; }

    public PageViewModel(DocumentViewModel owner, int pageIndex, PageInfo size)
    {
        _owner = owner;
        PageIndex = pageIndex;
        _size = size;
    }

    public void SetRealized(bool realized)
    {
        _isRealized = realized;
        if (realized)
        {
            EnsureRotation();
            EnsureRendered();
            EnsureTextLoaded();
        }
        else
        {
            CancelRender();
        }
    }

    private void EnsureRotation()
    {
        if (_rotation >= 0) return;
        var doc = _owner.Document;
        if (doc is null) return;
        _ = Task.Run(() =>
        {
            try
            {
                int r = doc.GetPageRotationDegrees(PageIndex);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_rotation == r) return;
                    _rotation = r;
                    OverlayInvalidated?.Invoke();
                });
            }
            catch { }
        });
    }

    public void OnScaleChanged()
    {
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
        OverlayInvalidated?.Invoke();
        if (_isRealized && _renderedScale != _owner.Scale)
            EnsureRendered();
    }

    /// <summary>Forces a re-render (e.g. after a DPI change) at the current scale.</summary>
    public void ForceRerender()
    {
        _renderedScale = double.NaN;
        if (_isRealized) EnsureRendered();
    }

    // ----- Text loading -----

    private void EnsureTextLoaded()
    {
        if (_textPage is not null || _textLoading) return;
        var doc = _owner.Document;
        if (doc is null) return;
        _textLoading = true;
        _ = Task.Run(() =>
        {
            try
            {
                var tp = doc.GetTextPage(PageIndex);
                Dispatcher.UIThread.Post(() =>
                {
                    _textPage = tp; _textLoading = false;
                    // A find selection may have been set before the text loaded.
                    if (_selAnchor >= 0)
                    {
                        UpdateSelectionRects();
                        if (PendingFindReveal) FindRevealRequested?.Invoke();
                    }
                });
            }
            catch { _textLoading = false; }
        });
    }

    // ----- Selection (page-space coordinates) -----

    public void BeginSelection(double pageX, double pageY)
    {
        _owner.SetActiveSelectionPage(this);
        if (_textPage is null) { EnsureTextLoaded(); return; }
        _selAnchor = _selCurrent = _textPage.HitTest(pageX, pageY);
        UpdateSelectionRects();
    }

    public void ExtendSelection(double pageX, double pageY)
    {
        if (_textPage is null || _selAnchor < 0) return;
        _selCurrent = _textPage.HitTest(pageX, pageY);
        UpdateSelectionRects();
    }

    /// <summary>Sets the selection to an explicit char range (used by find).</summary>
    public void SetSelectionRange(int start, int endInclusive)
    {
        _owner.SetActiveSelectionPage(this);
        _selAnchor = start;
        _selCurrent = endInclusive;
        // Ask the view to scroll this match into view (page may not be realized yet).
        PendingFindReveal = true;
        if (_textPage is null) { EnsureTextLoaded(); FindRevealRequested?.Invoke(); return; }
        UpdateSelectionRects();
        FindRevealRequested?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selAnchor < 0 && _selCurrent < 0) return;
        _selAnchor = _selCurrent = -1;
        SelectionRects = Array.Empty<TextRect>();
        OverlayInvalidated?.Invoke();
    }

    private void UpdateSelectionRects()
    {
        SelectionRects = _textPage is not null && _selAnchor >= 0
            ? _textPage.GetRangeRects(_selAnchor, _selCurrent)
            : Array.Empty<TextRect>();
        OverlayInvalidated?.Invoke();
    }

    public string? GetSelectedText()
        => HasSelection ? _textPage!.GetText(_selAnchor, _selCurrent) : null;

    // ----- Annotations -----

    /// <summary>Turns the current selection into a highlight annotation.</summary>
    public bool HighlightSelection(AnnotColor color)
    {
        if (!HasSelection) return false;
        var quads = _textPage!.GetRangeRects(_selAnchor, _selCurrent);
        if (quads.Count == 0) return false;
        AddAnnotation(new HighlightAnnotation { PageIndex = PageIndex, Quads = quads, Color = color });
        ClearSelection();
        return true;
    }

    public void AddAnnotation(PdfAnnotationModel a)
    {
        _owner.BeginChange();
        Annotations.Add(a);
        _owner.MarkDirty();
        OverlayInvalidated?.Invoke();
    }

    public void RemoveAnnotation(PdfAnnotationModel a)
    {
        if (Annotations.Remove(a))
        {
            _owner.MarkDirty();
            OverlayInvalidated?.Invoke();
        }
    }

    public void SetDraft(PdfAnnotationModel? draft)
    {
        Draft = draft;
        OverlayInvalidated?.Invoke();
    }

    /// <summary>Commits the current draft as a real annotation and returns it.</summary>
    public PdfAnnotationModel? CommitDraft()
    {
        var d = Draft;
        Draft = null;
        if (d is not null) AddAnnotation(d);
        return d;
    }

    /// <summary>Topmost annotation hit by a page-space point, within a pixel tolerance.</summary>
    public PdfAnnotationModel? HitTestAnnotation(double pageX, double pageY, double pixelTol)
    {
        double tol = pixelTol / _owner.Scale;
        var p = new PdfPoint(pageX, pageY);
        for (int i = Annotations.Count - 1; i >= 0; i--)
            if (AnnotationGeometry.HitTest(Annotations[i], p, tol))
                return Annotations[i];
        return null;
    }

    public void NotifyAnnotationChanged() => OverlayInvalidated?.Invoke();

    /// <summary>True if a page-space point falls inside a text glyph (for the I-beam cursor).</summary>
    public bool IsOverText(double pageX, double pageY)
    {
        if (_textPage is null) return false;
        foreach (var c in _textPage.Chars)
            if (pageX >= c.Left && pageX <= c.Right && pageY >= c.Bottom && pageY <= c.Top)
                return true;
        return false;
    }

    // ----- Rendering -----

    private void EnsureRendered()
    {
        double scale = _owner.Scale;
        if (_renderedScale == scale && Bitmap != null)
            return;

        CancelRender();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        var token = cts.Token;
        double renderScale = _owner.RenderScale;

        _ = Task.Run(() =>
        {
            try
            {
                var doc = _owner.Document;
                if (doc is null || token.IsCancellationRequested) return;
                RenderedPage page = doc.RenderPage(PageIndex, renderScale);
                if (token.IsCancellationRequested) return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    var old = Bitmap;
                    Bitmap = BitmapConverter.ToWriteableBitmap(page);
                    _renderedScale = scale;
                    old?.Dispose();
                });
            }
            catch (Exception)
            {
                // Leave the placeholder; errors surface elsewhere.
            }
        }, token);
    }

    private void CancelRender()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
    }
}
