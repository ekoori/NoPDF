using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoPdf.App.Editing;
using NoPdf.Core.Annotations;
using NoPdf.Core.Editing;
using NoPdf.Core.Rendering;
using NoPdf.Core.Text;

namespace NoPdf.App.ViewModels;

/// <summary>A single open document = one tab.</summary>
public sealed partial class DocumentViewModel : ViewModelBase, IDisposable
{
    private const double DipsPerPoint = 96.0 / 72.0;
    private const double MinZoom = 0.10;
    private const double MaxZoom = 8.0;

    public PdfDocument? Document { get; private set; }

    /// <summary>The file this tab IS. A save-as re-points it (see <see cref="RebindTo"/>),
    /// so :save, :copypath and the session all follow the new file.</summary>
    [ObservableProperty] private string _filePath;
    [ObservableProperty] private string _title;

    /// <summary>Re-points this tab at the file it was just written to.</summary>
    public void RebindTo(string newPath)
    {
        FilePath = newPath;
        Title = Path.GetFileName(newPath);
    }

    // In-memory PDF the viewer edits (known annotations stripped into the model).
    private byte[] _workingBytes;

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    /// <summary>What the viewport actually shows: every page, except in Full view where
    /// it is only the page(s) in focus (so there is nothing to scroll to).</summary>
    public ObservableCollection<PageViewModel> VisiblePages { get; } = new();
    public ObservableCollection<PageThumbnail> Thumbnails { get; } = new();
    /// <summary>The document's bookmarks, read from (and saved into) the PDF outline.</summary>
    public ObservableCollection<BookmarkNode> Outline { get; } = new();

    /// <summary>Flat list of every annotation in the document, for the annotations panel.</summary>
    public ObservableCollection<AnnotationListItem> AnnotationList { get; } = new();
    public bool HasOutline => Outline.Count > 0;

    [ObservableProperty] private EditorTool _currentTool = EditorTool.Select;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isActive; // is this the selected tab

    private double _zoom = 1.0;
    private PageViewModel? _activeSelectionPage;

    /// <summary>Zoom + scroll offset to restore on first layout (null = none).</summary>
    public (double Zoom, double OffsetX, double OffsetY)? InitialView { get; set; }

    /// <summary>True until the view has positioned this document for the first time.
    /// Without it a re-shown tab (the view is reused across tabs) would be re-homed to
    /// the top every time it is selected.</summary>
    public bool PendingInitialView { get; set; } = true;
    /// <summary>Set by the host to persist view position as it changes.</summary>
    public Action<double, double, double>? ViewStateSink { get; set; }

    /// <summary>Where this document was last looked at. The view and its scroll viewer are
    /// shared between tabs, so switching back has to put this document's own position back
    /// — otherwise it inherits whatever the other tab left behind.</summary>
    public (double Zoom, double OffsetX, double OffsetY)? LastView { get; private set; }

    /// <summary>Records the live position in memory (cheap, every scroll) so a tab switch
    /// can return here. The disk write is separate and throttled.</summary>
    public void SetLiveView(double zoom, double ox, double oy) => LastView = (zoom, ox, oy);

    public void ReportViewState(double zoom, double ox, double oy)
    {
        LastView = (zoom, ox, oy);
        ViewStateSink?.Invoke(zoom, ox, oy);
    }

    /// <summary>Set by the host to persist the `:view` mode as it changes. Assigned after
    /// any restore, so restoring a mode doesn't write it straight back.</summary>
    public Action<string, int>? ViewModeSink { get; set; }

    /// <summary>Raised when a certified signature stamp is committed, to run the signing flow.</summary>
    public Action<SignatureAnnotation>? CertifyRequested { get; set; }

    /// <summary>Set by the host so the document can report to the status bar.</summary>
    public Action<string>? StatusSink { get; set; }

    /// <summary>Raised after the document is written to disk, so anything derived from the
    /// file (e.g. signature verification) can be refreshed.</summary>
    public event Action? Saved;

    /// <summary>Name printed on new signatures (from config or active preset).</summary>
    public string SignerName { get; set; } = "";
    public AnnotColor SigColor { get; set; } = AnnotColor.Blue;
    public double SigThickness { get; set; } = 1.5;
    public double SigOpacity { get; set; } = 1.0;
    /// <summary>When set, new signatures are certified with the certificate below.</summary>
    public bool SigUseCertificate { get; set; }
    public string SigCertPath { get; set; } = "";
    public string SigCertPassword { get; set; } = "";

    // Text-box annotation defaults (from config).
    public double TextboxFontSize { get; set; } = 14;
    public AnnotColor TextboxFrameColor { get; set; } = AnnotColor.Blue;
    public double TextboxFrameOpacity { get; set; } = 1.0;

    /// <summary>Device pixel ratio for crisp rendering on HiDPI displays.</summary>
    public double DpiScale { get; private set; } = 1.0;

    public void SetDpiScale(double s)
    {
        if (s <= 0 || Math.Abs(s - DpiScale) < 1e-6) return;
        DpiScale = s;
        foreach (var p in Pages) p.ForceRerender();
    }

    private readonly List<(int page, int start, int end)> _findMatches = new();
    private int _findIndex = -1;
    public string? LastQuery { get; private set; }

    public double Scale => _zoom * DipsPerPoint;
    public double RenderScale => Scale * DpiScale;
    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    public event Action<int>? ScrollToPageRequested;
    public event Action? FitWidthRequested;
    public event Action? FitPageRequested;
    /// <summary>Scroll the viewport by (dx, dy) device-independent pixels.</summary>
    public event Action<double, double>? ScrollByRequested;
    /// <summary>Scroll by a viewport page: +1 down, -1 up.</summary>
    public event Action<int>? ScrollPageRequested;

    public void ScrollBy(double dx, double dy) => ScrollByRequested?.Invoke(dx, dy);
    public void ScrollPage(int dir) => ScrollPageRequested?.Invoke(dir);

    private DocumentViewModel(string filePath)
    {
        FilePath = filePath;
        Title = Path.GetFileName(filePath);
        _workingBytes = Array.Empty<byte>();
    }

    /// <summary>A tab that has not read its file yet — the content loads on first display.</summary>
    public static DocumentViewModel CreateDeferred(string filePath) => new(filePath);

    public static async Task<DocumentViewModel> LoadAsync(string filePath)
    {
        var vm = new DocumentViewModel(filePath);
        await vm.EnsureLoadedAsync();
        return vm;
    }

    /// <summary>False until the file has actually been read (deferred/restored tabs).</summary>
    public bool IsLoaded => Document is not null;

    /// <summary>Unsaved bytes to restore instead of reading the file (crash recovery).</summary>
    public byte[]? RecoverBytes { get; set; }

    /// <summary>Reads the file (or the recovered bytes) and builds the pages. Idempotent.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (IsLoaded) return;
        var recover = RecoverBytes;
        string path = FilePath;
        var (doc, cleaned, outline, models) = await Task.Run(() =>
        {
            var fileBytes = recover ?? NoPdf.Core.Import.DocumentImport.ReadAsPdfBytes(path);
            var (c, m) = AnnotationReader.LoadAndStrip(fileBytes);
            var d = PdfDocument.OpenBytes(c, path);
            return (d, c, d.GetOutline(), m);
        });

        Document = doc;
        _workingBytes = cleaned;
        BuildPages(models);
        foreach (var item in outline)
            Outline.Add(BookmarkNode.FromOutline(item));

        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(HasOutline));
        if (recover is not null) { RecoverBytes = null; IsDirty = true; } // recovered edits are unsaved
    }

    private void BuildPages(List<PdfAnnotationModel> annotations)
    {
        var byPage = annotations.GroupBy(a => a.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        for (int i = 0; i < Document!.PageCount; i++)
        {
            var pvm = new PageViewModel(this, i, Document.GetPageSize(i));
            if (byPage.TryGetValue(i, out var list))
                foreach (var a in list) pvm.Annotations.Add(a);
            Pages.Add(pvm);
            Thumbnails.Add(new PageThumbnail(this, i, Document.GetPageSize(i)));
        }
        RefreshVisiblePages();
    }

    /// <summary>Adds a bookmark to the PDF's own outline, so saving persists it in the file.</summary>
    public void AddUserBookmark(string name)
    {
        BeginChange();
        var nb = PageOps.AddOutlineEntry(_workingBytes, name, CurrentPage - 1);
        Rebuild(nb, Enumerable.Range(0, PageCount).ToList());
    }

    public bool RemoveUserBookmark(string name)
    {
        var nb = PageOps.RemoveOutlineEntry(_workingBytes, name);
        if (ReferenceEquals(nb, _workingBytes)) return false; // no such bookmark
        BeginChange();
        Rebuild(nb, Enumerable.Range(0, PageCount).ToList());
        return true;
    }

    // ----- Zoom / fit -----

    [RelayCommand] private void ZoomIn() => SetZoom(_zoom * 1.2);
    [RelayCommand] private void ZoomOut() => SetZoom(_zoom / 1.2);
    [RelayCommand] private void ZoomReset() => SetZoom(1.0);

    public void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 1e-6) return;
        _zoom = zoom;
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(ZoomPercent));
        // Resize every page's layout at once — the existing bitmap scales to fit instantly,
        // so the whole spread zooms together and smoothly. The crisp re-render is deferred:
        // re-rendering on every step made pages pop back in one at a time (the jitter).
        foreach (var p in Pages) p.OnScaleChanged();
        ScheduleRerender();
    }

    private Avalonia.Threading.DispatcherTimer? _rerenderTimer;

    /// <summary>Re-renders the pages at the new zoom once it stops changing, so a burst of
    /// zoom steps produces one crisp update for all pages together instead of a per-page
    /// flicker as each finishes.</summary>
    private void ScheduleRerender()
    {
        if (_rerenderTimer is null)
        {
            _rerenderTimer = new Avalonia.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(110) };
            _rerenderTimer.Tick += (_, _) =>
            {
                _rerenderTimer!.Stop();
                foreach (var p in Pages) p.RerenderForScale();
            };
        }
        _rerenderTimer.Stop();
        _rerenderTimer.Start();
    }

    public void SetZoomPercent(double percent) => SetZoom(percent / 100.0);

    /// <summary>Sets zoom so a page of the given point-size fits the viewport.</summary>
    public void FitToViewport(double viewportWidth, double viewportHeight, bool widthOnly)
    {
        var page = Pages.Count > CurrentPage - 1 && CurrentPage >= 1 ? Pages[CurrentPage - 1] : Pages.FirstOrDefault();
        if (page is null) return;
        double padding = 48;
        double zw = (viewportWidth - padding) / (page.PointWidth * DipsPerPoint);
        double z = widthOnly ? zw : Math.Min(zw, (viewportHeight - padding) / (page.PointHeight * DipsPerPoint));
        int keep = CurrentPage;
        SetZoom(z);
        // Keep the current page in view instead of jumping after the resize.
        ScrollToPageRequested?.Invoke(Math.Clamp(keep - 1, 0, Math.Max(0, PageCount - 1)));
    }

    public void RequestFitWidth() => FitWidthRequested?.Invoke();
    public void RequestFitPage() => FitPageRequested?.Invoke();

    // ----- View mode -----

    /// <summary>Pages across (scroll/full) or rows down (scrollh).</summary>
    [ObservableProperty] private int _pagesPerRow = 1;
    [ObservableProperty] private PageViewMode _viewMode = PageViewMode.Scroll;

    /// <summary>Raised when the view mode changes so the view can relayout + refit.</summary>
    public event Action? ViewModeChanged;

    partial void OnViewModeChanged(PageViewMode value)
    {
        OnPropertyChanged(nameof(ListPad));
        OnPropertyChanged(nameof(PageMargin));
    }

    /// <summary>The name `:view` uses for a mode; also what gets persisted per file.</summary>
    public static string ModeName(PageViewMode m) => m switch
    {
        PageViewMode.Full => "full",
        PageViewMode.ScrollH => "scrollh",
        _ => "scroll",
    };

    public string SetView(string mode, int? count)
    {
        var m = mode.ToLowerInvariant() switch
        {
            "full" or "book" => PageViewMode.Full,
            "scrollh" => PageViewMode.ScrollH,
            _ => PageViewMode.Scroll,
        };
        int n = Math.Clamp(count ?? 1, 1, 8);
        ViewMode = m;
        PagesPerRow = n;
        ManualZoom = false; // a fresh mode fits itself; the user hasn't overridden it yet
        RefreshVisiblePages();
        ViewModeChanged?.Invoke();
        ViewModeSink?.Invoke(ModeName(m), n);
        return m switch
        {
            PageViewMode.Full => $"Full view, {n} page(s) in the viewport",
            PageViewMode.ScrollH => $"Horizontal scroll, {n} row(s)",
            _ => n > 1 ? $"Scroll view, {n} across" : "Scroll view",
        };
    }

    /// <summary>Rebuilds <see cref="VisiblePages"/> for the current mode: every page,
    /// or in Full view just the page(s) in focus.</summary>
    public void RefreshVisiblePages()
    {
        if (ViewMode != PageViewMode.Full)
        {
            if (VisiblePages.Count == Pages.Count && VisiblePages.SequenceEqual(Pages)) return;
            VisiblePages.Clear();
            foreach (var p in Pages) VisiblePages.Add(p);
            return;
        }
        int n = Math.Max(1, PagesPerRow);
        int start = Math.Clamp(CurrentPage - 1, 0, Math.Max(0, Pages.Count - 1));
        var window = Pages.Skip(start).Take(n).ToList();
        if (VisiblePages.SequenceEqual(window)) return;
        VisiblePages.Clear();
        foreach (var p in window) VisiblePages.Add(p);
    }

    // Layout constants shared with DocumentView so the fitted zoom and the panel's
    // slot size agree — if they disagree the panel wraps and the next page bleeds in.
    public const double ListPadding = 48;      // ListBox Padding="24" both sides
    public const double ItemExtraW = 2;        // page border
    public const double ItemExtraH = 20;       // page border + 18px bottom margin
    public const double ScrollbarReserve = 18; // space the scrollbar will claim once shown
    public const double PageGapH = 12;         // gap between pages in horizontal-scroll view

    /// <summary>ListBox padding for the current mode. Horizontal scroll drops the gutter
    /// so the rows get the whole viewport height; the pages carry their own gap instead.</summary>
    public Avalonia.Thickness ListPad =>
        ViewMode == PageViewMode.ScrollH ? default : new Avalonia.Thickness(ListPadding / 2);

    /// <summary>The margin around each page, which is also the gap between pages.</summary>
    public Avalonia.Thickness PageMargin => ViewMode == PageViewMode.ScrollH
        ? new Avalonia.Thickness(0, 0, PageGapH, PageGapH)
        : new Avalonia.Thickness(0, 0, 0, ItemExtraH - 2);

    /// <summary>Width of one page slot (columns across).</summary>
    public double SlotWidth(double viewportWidth)
    {
        double avail = viewportWidth - ListPadding
            - (ViewMode == PageViewMode.Scroll && PagesPerRow > 1 ? ScrollbarReserve : 0);
        return Math.Max(1, avail / Math.Max(1, PagesPerRow));
    }

    /// <summary>Height of one page slot (rows down, horizontal-scroll mode). There is no
    /// list padding in this mode, so only the horizontal scrollbar is held back.</summary>
    public double SlotHeight(double viewportHeight)
        => Math.Max(1, (viewportHeight - ScrollbarReserve) / Math.Max(1, PagesPerRow));

    /// <summary>Height of one horizontal-scroll row at the CURRENT zoom — the panel is
    /// sized to N of these, so zooming in makes it taller than the viewport and the
    /// whole page height becomes reachable by scrolling.</summary>
    public double RowHeight()
    {
        var page = PageAt(CurrentPage);
        return page is null ? 1 : Math.Max(1, page.DisplayHeight + PageGapH);
    }

    /// <summary>Width of one N-across column at the CURRENT zoom — the scroll-view panel
    /// is sized to N of these, so zooming keeps exactly N columns and lets a zoomed-in row
    /// overflow into a horizontal scroll instead of overlapping its neighbour.</summary>
    public double ColWidth()
    {
        var page = PageAt(CurrentPage);
        return page is null ? 1 : Math.Max(1, page.DisplayWidth + PageGapH);
    }

    private PageViewModel? PageAt(int oneBased)
        => oneBased >= 1 && oneBased <= Pages.Count ? Pages[oneBased - 1] : Pages.FirstOrDefault();

    /// <summary>True once the user has zoomed by hand (via a command), so a later relayout —
    /// the command bar opening, a window resize — must not auto-fit their zoom away. Reset
    /// whenever the view mode changes or an explicit fit runs.</summary>
    public bool ManualZoom { get; private set; }

    public void ClearManualZoom() => ManualZoom = false;

    /// <summary>Set by the view: applies a zoom action anchored to the viewport centre, in a
    /// single synchronous layout pass so the content doesn't resize and then visibly jump.</summary>
    public Action<Action>? AnchoredZoom { get; set; }

    /// <summary>Applies a zoom requested from a command. Marks it user-driven (so it isn't
    /// re-fitted away) and keeps the view anchored across the resize.</summary>
    public void ZoomCommand(Action apply)
    {
        ManualZoom = true;
        // The view resizes the pages and re-anchors in one pass; without a view (tests) just
        // apply the zoom.
        if (AnchoredZoom is { } anchored) anchored(apply);
        else apply();
    }

    /// <summary>Zooms so the pages exactly fill their slots for the current view mode,
    /// keeping <paramref name="keepPage"/> in focus across the relayout.</summary>
    public void FitForView(double viewportWidth, double viewportHeight, int keepPage)
    {
        var page = PageAt(keepPage);
        if (page is null) return;
        double pw = page.PointWidth * DipsPerPoint, ph = page.PointHeight * DipsPerPoint;
        if (pw <= 0 || ph <= 0) return;

        double z = ViewMode switch
        {
            // N rows fill the height; pages flow down a column then to the next column.
            PageViewMode.ScrollH => (SlotHeight(viewportHeight) - PageGapH) / ph,
            // N pages across AND the row fully visible — a whole screen per "page".
            PageViewMode.Full => Math.Min(
                (SlotWidth(viewportWidth) - ItemExtraW) / pw,
                (viewportHeight - ListPadding - ItemExtraH) / ph),
            // N across, scrolling vertically.
            _ => (SlotWidth(viewportWidth) - ItemExtraW) / pw,
        };
        SetZoom(z);
        // The relayout resets the scroll offset, which drags the current page back to 1 —
        // put it back where it was.
        keepPage = Math.Clamp(keepPage, 1, Math.Max(1, PageCount));
        if (ViewMode == PageViewMode.Full) SetCurrentPageSilent(keepPage);
        else GoToPage(keepPage);
    }

    // ----- Navigation -----

    private int _currentPage = 1;

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (_currentPage == value) return;
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PageLabel));
            // Full view shows only the focused page(s), so navigating swaps them out.
            if (ViewMode == PageViewMode.Full) RefreshVisiblePages();
        }
    }

    /// <summary>"page / pages", shown in the status bar (blank until the file is read).</summary>
    public string PageLabel => IsLoaded ? $"{CurrentPage} / {PageCount}" : "";

    public bool GoToPage(int oneBased)
    {
        int idx = oneBased - 1;
        if (idx < 0 || idx >= Pages.Count) return false;
        CurrentPage = oneBased;
        ScrollToPageRequested?.Invoke(idx);
        return true;
    }

    public bool NextPage() => GoToPage(CurrentPage + 1);
    public bool PrevPage() => GoToPage(CurrentPage - 1);
    public bool FirstPage() => GoToPage(1);
    public bool LastPage() => GoToPage(PageCount);

    /// <summary>Updates the current page from scroll position without re-scrolling.</summary>
    public void SetCurrentPageSilent(int oneBased)
    {
        if (oneBased >= 1 && oneBased <= PageCount) CurrentPage = oneBased;
    }

    // ----- Tools / selection -----

    public void SelectTool(EditorTool tool) => CurrentTool = tool;

    public void SetActiveSelectionPage(PageViewModel page)
    {
        if (!ReferenceEquals(_activeSelectionPage, page)) _activeSelectionPage?.ClearSelection();
        _activeSelectionPage = page;
    }

    public string? GetActiveSelectionText() => _activeSelectionPage?.GetSelectedText();
    public bool HighlightActiveSelection(AnnotColor color) => _activeSelectionPage?.HighlightSelection(color) ?? false;
    public void ClearActiveSelection() => _activeSelectionPage?.ClearSelection();

    private readonly List<PdfAnnotationModel> _selected = new();

    /// <summary>All currently selected annotations (multi-select).</summary>
    public IReadOnlyList<PdfAnnotationModel> SelectedAnnotations => _selected;

    /// <summary>The primary (most recently added) selected annotation, or null.</summary>
    public PdfAnnotationModel? SelectedAnnotation => _selected.Count > 0 ? _selected[^1] : null;

    [ObservableProperty] private AnnotationEditorViewModel? _annotationEditor;

    /// <summary>The page a given annotation lives on.</summary>
    public PageViewModel? PageOf(PdfAnnotationModel a)
        => a.PageIndex >= 0 && a.PageIndex < Pages.Count ? Pages[a.PageIndex] : null;

    public bool IsAnnotationSelected(PdfAnnotationModel a) => _selected.Contains(a);

    /// <summary>All annotations in <paramref name="a"/>'s outermost group (itself if ungrouped).</summary>
    public IEnumerable<PdfAnnotationModel> GroupMembers(PdfAnnotationModel a)
    {
        if (a.GroupPath.Count == 0) return new[] { a };
        var outer = a.GroupPath[^1];
        return AllAnnotations().Where(x => x.GroupPath.Count > 0 && x.GroupPath[^1] == outer);
    }

    /// <summary>Single-select (clears any other selection). Selecting a grouped
    /// annotation selects its whole group. Null clears the selection.</summary>
    public void SelectAnnotation(PageViewModel? page, PdfAnnotationModel? annotation)
    {
        _selected.Clear();
        if (annotation is not null)
            foreach (var a in GroupMembers(annotation))
                if (!_selected.Contains(a)) _selected.Add(a);
        RefreshSelection();
    }

    /// <summary>Selects a set of annotations at once (each expanded to its group).</summary>
    public void SelectAnnotations(IEnumerable<PdfAnnotationModel> annotations)
    {
        _selected.Clear();
        foreach (var ann in annotations)
            foreach (var a in GroupMembers(ann))
                if (!_selected.Contains(a)) _selected.Add(a);
        RefreshSelection();
    }

    /// <summary>Adds or removes an annotation (and its group) from the selection.</summary>
    public void ToggleSelection(PdfAnnotationModel annotation)
    {
        var members = GroupMembers(annotation).ToList();
        if (members.Any(_selected.Contains))
            foreach (var a in members) _selected.Remove(a);
        else
            foreach (var a in members) if (!_selected.Contains(a)) _selected.Add(a);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var targets = _selected
            .Select(a => (page: PageOf(a), ann: a))
            .Where(t => t.page is not null)
            .Select(t => (t.page!, t.ann))
            .ToList();
        AnnotationEditor = targets.Count > 0 ? new AnnotationEditorViewModel(this, targets) : null;
        // Redraw every page's overlay (cheap: only realized overlays have subscribers).
        foreach (var p in Pages) p.NotifyAnnotationChanged();
        RefreshAnnotationList();
    }

    /// <summary>Rebuilds the annotation list (call when annotations are added/removed).
    /// Groups become a header row with their members nested underneath.</summary>
    public void RefreshAnnotationList()
    {
        AnnotationList.Clear();
        EmitAnnotationRows(AllAnnotations().ToList(), depth: 0);
    }

    /// <summary>The group id <paramref name="depth"/> levels in from the outside, or null
    /// when the annotation is not grouped that deeply (GroupPath is innermost-first).</summary>
    private static Guid? GroupIdAt(PdfAnnotationModel a, int depth)
    {
        int i = a.GroupPath.Count - 1 - depth;
        return i >= 0 ? a.GroupPath[i] : null;
    }

    private void EmitAnnotationRows(IReadOnlyList<PdfAnnotationModel> annotations, int depth)
    {
        // Bucket by group at this level, keeping document order: a group takes the place
        // of its first member, and loose annotations stay where they are.
        var buckets = new List<(Guid? Key, List<PdfAnnotationModel> Items)>();
        var seen = new Dictionary<Guid, int>();
        foreach (var a in annotations)
        {
            if (GroupIdAt(a, depth) is not { } key) { buckets.Add((null, new() { a })); continue; }
            if (seen.TryGetValue(key, out int at)) buckets[at].Items.Add(a);
            else { seen[key] = buckets.Count; buckets.Add((key, new() { a })); }
        }

        foreach (var (key, items) in buckets)
        {
            if (key is null)
            {
                var a = items[0];
                AnnotationList.Add(AnnotationListItem.Leaf(a, a.PageIndex + 1, _selected.Contains(a), depth));
                continue;
            }
            AnnotationList.Add(AnnotationListItem.Group(
                key.Value, items[0].PageIndex + 1, items.All(_selected.Contains), depth, items.Count));
            EmitAnnotationRows(items, depth + 1);
        }
    }

    [RelayCommand]
    private void SelectAnnotationItem(AnnotationListItem? item)
    {
        if (item is null) return;
        // A group header stands for its members; picking it picks the whole group.
        var target = item.IsGroupHeader
            ? AllAnnotations().FirstOrDefault(a => a.GroupPath.Contains(item.GroupId!.Value))
            : item.Model;
        if (target is null) return;

        var page = PageOf(target);
        SelectAnnotation(page, target);
        GoToPage(target.PageIndex + 1);     // brings the page into view (and realizes it)
        page?.RequestReveal(target.Bounds); // then focus the annotation itself, not just the page
    }

    public void DeleteSelectedAnnotation()
    {
        if (_selected.Count == 0) return;
        BeginChange();
        var toDelete = _selected.ToList();
        _selected.Clear();
        foreach (var a in toDelete) PageOf(a)?.RemoveAnnotation(a);
        RefreshSelection();
    }

    // ----- Grouping -----

    public void GroupSelected()
    {
        if (_selected.Count < 2) return;
        BeginChange();
        var g = Guid.NewGuid();
        foreach (var a in _selected) a.GroupPath.Add(g); // new outermost level
        MarkDirty();
    }

    /// <summary>Peels the outermost group level off the selection (leaving inner groups).</summary>
    public void UngroupSelected()
    {
        if (_selected.All(a => a.GroupPath.Count == 0)) return;
        BeginChange();
        foreach (var a in _selected)
            if (a.GroupPath.Count > 0) a.GroupPath.RemoveAt(a.GroupPath.Count - 1);
        MarkDirty();
    }

    // ----- Annotation clipboard (shared across tabs) -----

    private static List<PdfAnnotationModel> _annClipboard = new();
    public static bool HasCopiedAnnotations => _annClipboard.Count > 0;

    public void CopySelectedAnnotations()
    {
        if (_selected.Count == 0) return;
        _annClipboard = _selected.Select(a => a.Clone()).ToList();
    }

    /// <summary>Clones the current selection in place (same page/position) and selects the
    /// clones, for a Ctrl-drag copy. The caller pushes the undo snapshot.</summary>
    public IReadOnlyList<PdfAnnotationModel> DuplicateSelectionForDrag()
    {
        var groupMap = new Dictionary<Guid, Guid>();
        var clones = new List<PdfAnnotationModel>();
        foreach (var a in _selected)
        {
            var c = a.Clone();
            RemapGroups(c, groupMap);
            PageOf(c)?.Annotations.Add(c);
            clones.Add(c);
        }
        foreach (var p in clones.Select(PageOf).Where(p => p is not null).Distinct()) p!.NotifyAnnotationChanged();
        MarkDirty();
        _selected.Clear();
        _selected.AddRange(clones);
        RefreshSelection();
        return clones;
    }

    /// <summary>Adds a pasted raster image (PNG bytes) as an image annotation on the current page.</summary>
    public void PasteImage(byte[] png, int pxW, int pxH)
    {
        int target = Math.Clamp(CurrentPage - 1, 0, Math.Max(0, PageCount - 1));
        if (target >= Pages.Count || pxW <= 0 || pxH <= 0 || png.Length == 0) return;
        BeginChange();
        var pg = Pages[target];
        double pw = pg.UnrotWidth, ph = pg.UnrotHeight;
        double aspect = (double)pxW / pxH;
        double w = pw * 0.5, h = w / aspect;
        if (h > ph * 0.5) { h = ph * 0.5; w = h * aspect; }
        double left = (pw - w) / 2, bottom = (ph - h) / 2;
        var img = new ImageAnnotation
        {
            PageIndex = target,
            Rect = new TextRect(left, bottom, left + w, bottom + h),
            ImageData = png, PixelWidth = pxW, PixelHeight = pxH,
            Opacity = 1.0, Border = false, Color = AnnotColor.Black, StrokeWidth = 1,
        };
        pg.Annotations.Add(img);
        pg.NotifyAnnotationChanged();
        MarkDirty();
        _selected.Clear();
        _selected.Add(img);
        RefreshSelection();
        GoToPage(target + 1);
    }

    /// <summary>Pastes clipboard annotations onto the current page, offset and selected.</summary>
    public void PasteAnnotations()
    {
        if (_annClipboard.Count == 0) return;
        int target = Math.Clamp(CurrentPage - 1, 0, Math.Max(0, PageCount - 1));
        if (target >= Pages.Count) return;
        BeginChange();
        // Preserve grouping within the paste by remapping group ids.
        var groupMap = new Dictionary<Guid, Guid>();
        var clones = new List<PdfAnnotationModel>();
        foreach (var src in _annClipboard)
        {
            var c = src.Clone();
            c.PageIndex = target;
            RemapGroups(c, groupMap);
            AnnotationGeometry.Translate(c, 14, -14); // nudge so it's visibly offset
            Pages[target].Annotations.Add(c);
            clones.Add(c);
        }
        Pages[target].NotifyAnnotationChanged();
        MarkDirty();
        _selected.Clear();
        _selected.AddRange(clones);
        RefreshSelection();
    }

    // ----- Find -----

    public int Find(string query)
    {
        _findMatches.Clear();
        _findIndex = -1;
        LastQuery = query;
        if (string.IsNullOrEmpty(query) || Document is null) return 0;
        foreach (var p in Pages)
        {
            var tp = Document.GetTextPage(p.PageIndex);
            foreach (var m in tp.Find(query))
                _findMatches.Add((p.PageIndex, m.Start, m.EndInclusive));
        }
        if (_findMatches.Count > 0) FindNext();
        return _findMatches.Count;
    }

    public bool FindNext() => MoveFind(+1);
    public bool FindPrev() => MoveFind(-1);

    private bool MoveFind(int dir)
    {
        if (_findMatches.Count == 0) return false;
        _findIndex = (_findIndex + dir + _findMatches.Count) % _findMatches.Count;
        var (page, start, end) = _findMatches[_findIndex];
        Pages[page].SetSelectionRange(start, end);
        ScrollToPageRequested?.Invoke(page);
        return true;
    }

    public int FindMatchCount => _findMatches.Count;
    public int FindCurrentOrdinal => _findIndex + 1;

    // ----- Follow-links hint mode -----

    /// <summary>One on-screen link hint: a label over a link rect + its destination.</summary>
    public sealed class HintTarget
    {
        public int PageIndex;
        public TextRect Rect = default;
        public string Label = "";
        public int? TargetPage;
        public string? Uri;
        /// <summary>A form field rather than a link: following it focuses the field for typing.</summary>
        public bool IsFormField;
    }

    private Dictionary<int, List<PdfLink>>? _links;
    private readonly List<HintTarget> _hints = new();
    public IReadOnlyList<HintTarget> Hints => _hints;
    public bool IsHintMode { get; private set; }
    public string HintPrefix { get; private set; } = "";

    /// <summary>Raised to open an external URI when a link hint is followed.</summary>
    public event Action<string>? OpenUriRequested;

    private const string HintKeys = "fjdkslaghrueiwovncmbt";

    /// <summary>Links on a page (lazily read once per document).</summary>
    public IReadOnlyList<PdfLink> LinksOn(int pageIndex)
    {
        _links ??= LinkReader.ReadAll(_workingBytes);
        return _links.TryGetValue(pageIndex, out var l) ? l : Array.Empty<PdfLink>();
    }

    /// <summary>The link at a page-space point, or null.</summary>
    public PdfLink? LinkAt(int pageIndex, double x, double y)
    {
        foreach (var l in LinksOn(pageIndex))
            if (x >= l.Rect.Left && x <= l.Rect.Right && y >= l.Rect.Bottom && y <= l.Rect.Top) return l;
        return null;
    }

    /// <summary>Follows a link: internal pages jump, URIs open externally.</summary>
    public void FollowLink(PdfLink link)
    {
        if (link.TargetPage is { } tp) GoToPage(tp + 1);
        else if (!string.IsNullOrEmpty(link.Uri)) OpenUriRequested?.Invoke(link.Uri!);
    }

    // ----- Form filling (AcroForm) -----

    /// <summary>True when the document has fillable form fields.</summary>
    public bool HasForm => Document?.HasForm == true;

    /// <summary>True while a form field has the keyboard: typing goes into the PDF rather
    /// than running normal-mode key bindings. Escape drops it.</summary>
    [ObservableProperty] private bool _isFormFocused;

    private int _formPageIndex = -1;
    // Field values live inside PDFium, so the working bytes are stale until pulled back out.
    private bool _formDirty;

    /// <summary>Focuses the form field under a page-space point (view/hand mode click, or
    /// an `f` hint). False when there is no field there.</summary>
    public bool TryFocusFormField(int pageIndex, double pageX, double pageY)
    {
        if (Document is null || !Document.HasForm) return false;
        if (!Document.FormClick(pageIndex, pageX, pageY)) return false;
        _formPageIndex = pageIndex;
        IsFormFocused = true;
        // The click itself may have toggled a checkbox or radio button.
        _formDirty = true;
        MarkDirty();
        RerenderFormPage();
        StatusSink?.Invoke("Filling in the form — type to edit, Esc to leave the field");
        return true;
    }

    private Dictionary<int, IReadOnlyList<FormFieldInfo>>? _formFields;

    /// <summary>The form fields on a page, read once per document. Cached because the hover
    /// cursor asks on every mouse move, and each miss is a native page load.</summary>
    public IReadOnlyList<FormFieldInfo> FormFieldsOn(int pageIndex)
    {
        if (Document is not { HasForm: true } d) return Array.Empty<FormFieldInfo>();
        _formFields ??= new Dictionary<int, IReadOnlyList<FormFieldInfo>>();
        if (!_formFields.TryGetValue(pageIndex, out var list))
            _formFields[pageIndex] = list = d.GetFormFields(pageIndex);
        return list;
    }

    /// <summary>True if a fillable field sits under a page-space point (for the cursor).
    /// Tested against the cached rects — asking PDFium per mouse move would be both slow
    /// and disruptive to the field being edited.</summary>
    public bool HasFormFieldAt(int pageIndex, double pageX, double pageY)
    {
        foreach (var f in FormFieldsOn(pageIndex))
            if (f.IsFillable && pageX >= f.Left && pageX <= f.Right
                             && pageY >= f.Bottom && pageY <= f.Top) return true;
        return false;
    }

    /// <summary>Mouse press inside a field: focuses it and starts a drag-selection.</summary>
    public bool FormMouseDown(int pageIndex, double pageX, double pageY)
    {
        if (Document is null || !Document.HasForm) return false;
        if (!Document.FormMouseDown(pageIndex, pageX, pageY)) return false;
        _formPageIndex = pageIndex;
        IsFormFocused = true;
        _formDirty = true;   // the press may have toggled a checkbox or radio button
        MarkDirty();
        RerenderFormPage();
        StatusSink?.Invoke("Filling in the form — type to edit, Esc to leave the field");
        return true;
    }

    /// <summary>Drag inside the focused field (extends its text selection).</summary>
    public void FormMouseMove(int pageIndex, double pageX, double pageY)
    {
        if (!IsFormFocused || Document is null) return;
        Document.FormMouseMove(pageIndex, pageX, pageY);
        RerenderFormPage();
    }

    public void FormMouseUp(int pageIndex, double pageX, double pageY)
    {
        if (!IsFormFocused || Document is null) return;
        Document.FormMouseUp(pageIndex, pageX, pageY);
        RerenderFormPage();
    }

    /// <summary>Text selected inside the focused field, so :copy / Ctrl+C can take it.</summary>
    public string SelectedFormText() => Document?.SelectedFormText() ?? "";

    public void TypeIntoForm(char c)
    {
        if (!IsFormFocused || Document is null) return;
        if (!Document.FormChar(c)) return;
        _formDirty = true;
        MarkDirty();
        RerenderFormPage();
    }

    /// <summary>Delete/arrows/Home/End/Tab. Backspace and Enter go to <see cref="TypeIntoForm"/>.</summary>
    public void SendFormKey(int virtualKey)
    {
        if (!IsFormFocused || Document is null) return;
        if (!Document.FormKey(virtualKey)) return;
        _formDirty = true;
        MarkDirty();
        RerenderFormPage();
    }

    /// <summary>The text of the field being filled, for the status bar.</summary>
    public string FocusedFormText() => Document?.FocusedFormText() ?? "";

    /// <summary>Drops form focus (Escape), committing the field's value.</summary>
    public void ExitFormField()
    {
        if (!IsFormFocused) return;
        IsFormFocused = false;
        Document?.FormKillFocus();  // commits the edit into the document
        RerenderFormPage();
        _formPageIndex = -1;
    }

    private void RerenderFormPage()
    {
        if (_formPageIndex >= 0 && _formPageIndex < Pages.Count)
            Pages[_formPageIndex].ForceRerender();
    }

    /// <summary>Forgets form focus — for when the underlying document is swapped out and
    /// the focus (and any unflushed values) belong to the old one.</summary>
    private void ResetFormState()
    {
        IsFormFocused = false;
        _formPageIndex = -1;
        _formDirty = false;
        _formFields = null; // rects belong to the document being replaced
    }

    /// <summary>Pulls filled-in form values back out of PDFium into the working bytes.
    /// Field edits are held inside PDFium, so without this a save would write the document
    /// as it was before the form was filled.</summary>
    private void FlushFormValues()
    {
        if (!_formDirty || Document is null) return;
        try
        {
            Document.FormKillFocus();       // commit the field being edited
            _workingBytes = Document.SaveWithFormValues();
            _formDirty = false;
            IsFormFocused = false;
            _links = null;                  // link offsets are re-read from the new bytes
        }
        catch { /* keep the old bytes rather than lose the document */ }
    }

    /// <summary>Collects hints for the currently visible (realized) pages: links to follow
    /// and form fields to fill in.</summary>
    public bool EnterHintMode()
    {
        ExitHintMode();
        _links ??= LinkReader.ReadAll(_workingBytes);

        var targets = new List<HintTarget>();
        foreach (var p in Pages)
        {
            if (!p.IsRealized) continue;
            if (_links.TryGetValue(p.PageIndex, out var ls))
                foreach (var l in ls)
                    targets.Add(new HintTarget
                    {
                        PageIndex = p.PageIndex, Rect = l.Rect,
                        TargetPage = l.TargetPage, Uri = l.Uri,
                    });
            foreach (var f in FormFieldsOn(p.PageIndex))
            {
                if (!f.IsFillable) continue;  // signature fields are signed via :sign
                targets.Add(new HintTarget
                {
                    PageIndex = p.PageIndex,
                    Rect = new TextRect(f.Left, f.Bottom, f.Right, f.Top),
                    IsFormField = true,
                });
            }
        }
        if (targets.Count == 0) return false;

        var labels = HintLabels(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            targets[i].Label = labels[i];
            _hints.Add(targets[i]);
        }
        IsHintMode = true;
        HintPrefix = "";
        NotifyAllPages();
        return true;
    }

    /// <summary>Feeds a typed character; follows a link on a unique match, else narrows.</summary>
    public void FeedHintKey(char c)
    {
        if (!IsHintMode) return;
        string prefix = HintPrefix + char.ToLowerInvariant(c);
        var matches = _hints.Where(h => h.Label.StartsWith(prefix)).ToList();
        if (matches.Count == 0) { ExitHintMode(); return; }
        var exact = matches.FirstOrDefault(h => h.Label == prefix);
        if (exact is not null && matches.Count == 1) { Follow(exact); ExitHintMode(); return; }
        HintPrefix = prefix;
        NotifyAllPages();
    }

    public void ExitHintMode()
    {
        if (!IsHintMode && _hints.Count == 0) return;
        IsHintMode = false;
        HintPrefix = "";
        _hints.Clear();
        NotifyAllPages();
    }

    private void Follow(HintTarget h)
    {
        if (h.IsFormField)
        {
            // Land in the middle of the field, as a click there would.
            TryFocusFormField(h.PageIndex, (h.Rect.Left + h.Rect.Right) / 2,
                (h.Rect.Bottom + h.Rect.Top) / 2);
        }
        else if (h.TargetPage is { } tp) GoToPage(tp + 1);
        else if (!string.IsNullOrEmpty(h.Uri)) OpenUriRequested?.Invoke(h.Uri!);
    }

    private void NotifyAllPages() { foreach (var p in Pages) p.NotifyAnnotationChanged(); }

    /// <summary>Remaps a clone's group ids to fresh ones (kept consistent within one paste/duplicate).</summary>
    private static void RemapGroups(PdfAnnotationModel c, Dictionary<Guid, Guid> map)
    {
        for (int i = 0; i < c.GroupPath.Count; i++)
        {
            var g = c.GroupPath[i];
            c.GroupPath[i] = map.TryGetValue(g, out var ng) ? ng : (map[g] = Guid.NewGuid());
        }
    }

    private static List<string> HintLabels(int n)
    {
        var labels = new List<string>(n);
        if (n <= HintKeys.Length)
        {
            for (int i = 0; i < n; i++) labels.Add(HintKeys[i].ToString());
        }
        else
        {
            for (int i = 0; i < HintKeys.Length && labels.Count < n; i++)
                for (int j = 0; j < HintKeys.Length && labels.Count < n; j++)
                    labels.Add($"{HintKeys[i]}{HintKeys[j]}");
        }
        return labels;
    }

    // ----- Save -----

    public IEnumerable<PdfAnnotationModel> AllAnnotations() => Pages.SelectMany(p => p.Annotations);

    /// <summary>Annotations prepared for writing: image opacity baked into the pixels
    /// (PDF has no simple constant-alpha for images). Call on the UI thread.</summary>
    private List<PdfAnnotationModel> AnnotationsForSave()
    {
        var list = new List<PdfAnnotationModel>();
        foreach (var a in AllAnnotations())
        {
            if (a is ImageAnnotation img && img.Opacity < 0.999 && img.ImageData.Length > 0
                && BakeOpacityPng(img.ImageData, img.Opacity) is { } baked)
            {
                var c = (ImageAnnotation)img.Clone();
                c.ImageData = baked; c.Opacity = 1.0;
                list.Add(c);
            }
            else list.Add(a);
        }
        return list;
    }

    private static byte[]? BakeOpacityPng(byte[] png, double opacity)
    {
        try
        {
            using var ms = new MemoryStream(png);
            using var src = new Avalonia.Media.Imaging.Bitmap(ms);
            var size = src.PixelSize;
            var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            using (var dc = rtb.CreateDrawingContext())
            using (dc.PushOpacity(Math.Clamp(opacity, 0, 1)))
                dc.DrawImage(src, new Avalonia.Rect(0, 0, size.Width, size.Height));
            using var outMs = new MemoryStream();
            rtb.Save(outMs);
            return outMs.ToArray();
        }
        catch { return null; }
    }

    /// <summary>Current document bytes with all annotations baked in (for signing).</summary>
    public byte[] ExportWithAnnotations()
    {
        FlushFormValues();
        return AnnotationWriter.SaveToBytes(_workingBytes, AnnotationsForSave());
    }

    /// <summary>Writes the document (annotations and form values baked in). Throws if it
    /// can't — a save that fails quietly leaves the user believing their edits are on disk.</summary>
    public async Task SaveAsync(string? destPath = null)
    {
        FlushFormValues();          // filled-in fields live in PDFium until pulled back out
        string dest = destPath ?? FilePath;
        var annots = AnnotationsForSave();
        var bytes = _workingBytes;
        await Task.Run(() =>
        {
            var outBytes = AnnotationWriter.SaveToBytes(bytes, annots);
            File.WriteAllBytes(dest, outBytes);
        });
        IsDirty = false;
        Saved?.Invoke();
    }

    public void MarkDirty() => IsDirty = true;

    // ----- Page operations (M6) -----

    public int PageCount => Pages.Count;

    public void RotatePages(IReadOnlyList<int> pages, int deltaDegrees)
    {
        if (pages.Count == 0) return;
        BeginChange();
        var nb = PageOps.Rotate(_workingBytes, pages, deltaDegrees);
        Rebuild(nb, Enumerable.Range(0, PageCount).ToList());
    }

    public void DeletePages(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0 || pages.Count >= PageCount) return;
        BeginChange();
        var drop = new HashSet<int>(pages);
        var keep = Enumerable.Range(0, PageCount).Where(i => !drop.Contains(i)).ToList();
        var nb = PageOps.Compose(_workingBytes, keep);
        Rebuild(nb, keep);
    }

    public void MovePage(int from, int to)
    {
        if (from < 0 || from >= PageCount || to < 0 || to >= PageCount || from == to) return;
        BeginChange();
        var order = Enumerable.Range(0, PageCount).ToList();
        order.RemoveAt(from);
        order.Insert(to, from);
        var nb = PageOps.Compose(_workingBytes, order);
        Rebuild(nb, order);
    }

    /// <summary>Moves a set of pages up (dir&lt;0) or down (dir&gt;0) by one, together.</summary>
    public void MovePages(IReadOnlyList<int> indices, int dir)
    {
        if (indices.Count == 0 || dir == 0) return;
        var sel = new HashSet<int>(indices);
        var order = Enumerable.Range(0, PageCount).ToList();
        bool moved = false;
        if (dir < 0)
            for (int i = 1; i < order.Count; i++)
            {
                if (sel.Contains(order[i]) && !sel.Contains(order[i - 1]))
                { (order[i - 1], order[i]) = (order[i], order[i - 1]); moved = true; }
            }
        else
            for (int i = order.Count - 2; i >= 0; i--)
            {
                if (sel.Contains(order[i]) && !sel.Contains(order[i + 1]))
                { (order[i + 1], order[i]) = (order[i], order[i + 1]); moved = true; }
            }
        if (!moved) return;
        BeginChange();
        Rebuild(PageOps.Compose(_workingBytes, order), order);
    }

    /// <summary>Re-reads the original file from disk, discarding in-memory edits and any
    /// cached copy of them (the caller drops the autosave entry to match).</summary>
    public void ReloadFromDisk()
    {
        if (!File.Exists(FilePath)) return;
        RecoverBytes = null; // a deferred tab must not load the cached edits after this
        var (cleaned, models) = AnnotationReader.LoadAndStrip(NoPdf.Core.Import.DocumentImport.ReadAsPdfBytes(FilePath));
        var oldDoc = Document;
        var newDoc = PdfDocument.OpenBytes(cleaned, FilePath);
        _workingBytes = cleaned;
        Document = newDoc;
        _undo.Clear(); _redo.Clear();
        _selected.Clear(); AnnotationEditor = null;
        _activeSelectionPage = null; _findMatches.Clear(); _findIndex = -1;
        ResetFormState(); // the old focus belonged to the document just replaced

        Pages.Clear(); Thumbnails.Clear(); Outline.Clear();
        var byPage = models.GroupBy(a => a.PageIndex).ToDictionary(g => g.Key, g => g.ToList());
        for (int i = 0; i < newDoc.PageCount; i++)
        {
            var pvm = new PageViewModel(this, i, newDoc.GetPageSize(i));
            if (byPage.TryGetValue(i, out var list)) foreach (var a in list) pvm.Annotations.Add(a);
            Pages.Add(pvm);
            Thumbnails.Add(new PageThumbnail(this, i, newDoc.GetPageSize(i)));
        }
        foreach (var item in newDoc.GetOutline()) Outline.Add(BookmarkNode.FromOutline(item));
        oldDoc?.Dispose();
        IsDirty = false;
        OnPropertyChanged(nameof(PageCount)); OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(HasOutline));
    }

    public int InsertFile(string path, int atIndex)
    {
        BeginChange();
        var other = File.ReadAllBytes(path);
        var (cleanedOther, _) = AnnotationReader.LoadAndStrip(other);
        int otherCount = CountPages(cleanedOther);
        var nb = PageOps.Insert(_workingBytes, cleanedOther, atIndex);
        var order = new List<int>();
        for (int i = 0; i < atIndex && i < PageCount; i++) order.Add(i);
        for (int j = 0; j < otherCount; j++) order.Add(-1);
        for (int i = atIndex; i < PageCount; i++) order.Add(i);
        Rebuild(nb, order);
        return otherCount;
    }

    public void MergeFile(string path)
    {
        BeginChange();
        var other = File.ReadAllBytes(path);
        var (cleanedOther, _) = AnnotationReader.LoadAndStrip(other);
        int otherCount = CountPages(cleanedOther);
        var nb = PageOps.Merge(new[] { _workingBytes, cleanedOther });
        var order = Enumerable.Range(0, PageCount).ToList();
        for (int j = 0; j < otherCount; j++) order.Add(-1);
        Rebuild(nb, order);
    }

    public void ExtractRange(string rangeSpec, string destPath)
        => PageOps.ExtractRangeToFile(_workingBytes, rangeSpec, destPath, PageCount);

    private static int CountPages(byte[] bytes)
    {
        using var d = PdfDocument.OpenBytes(bytes);
        return d.PageCount;
    }

    /// <summary>
    /// Replaces the document with new bytes and rebuilds pages, carrying each old
    /// page's annotations to its new position (source index -1 = a fresh page).
    /// </summary>
    private void Rebuild(byte[] newBytes, IReadOnlyList<int> sourceOldIndex)
    {
        var oldAnnots = Pages.Select(p => p.Annotations.ToList()).ToList();
        var oldDoc = Document;

        var newDoc = PdfDocument.OpenBytes(newBytes, FilePath);
        _workingBytes = newBytes;
        Document = newDoc;
        _activeSelectionPage = null;
        _selected.Clear();
        _findMatches.Clear();
        _findIndex = -1;
        ResetFormState(); // BeginChange already folded any form values into newBytes

        Pages.Clear();
        Thumbnails.Clear();
        for (int newIdx = 0; newIdx < sourceOldIndex.Count; newIdx++)
        {
            var pvm = new PageViewModel(this, newIdx, newDoc.GetPageSize(newIdx));
            int src = sourceOldIndex[newIdx];
            if (src >= 0 && src < oldAnnots.Count)
                foreach (var a in oldAnnots[src]) { a.PageIndex = newIdx; pvm.Annotations.Add(a); }
            Pages.Add(pvm);
            Thumbnails.Add(new PageThumbnail(this, newIdx, newDoc.GetPageSize(newIdx)));
        }

        // The outline lives in the file, so re-read it after any structural change.
        RefreshOutline();
        RefreshVisiblePages();

        oldDoc?.Dispose();
        MarkDirty();
        OnPropertyChanged(nameof(PageCount)); OnPropertyChanged(nameof(PageLabel));
        CurrentPage = Math.Clamp(CurrentPage, 1, Math.Max(1, PageCount));
    }

    private void RefreshOutline()
    {
        Outline.Clear();
        try
        {
            foreach (var item in Document?.GetOutline() ?? Array.Empty<OutlineItem>())
                Outline.Add(BookmarkNode.FromOutline(item));
        }
        catch { }
        OnPropertyChanged(nameof(HasOutline));
    }

    // ----- Undo / redo (snapshot-based) -----

    // Bookmarks live in the PDF bytes, so Bytes covers them.
    private sealed record Snapshot(
        byte[] Bytes,
        List<List<PdfAnnotationModel>> AnnotsPerPage);

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private Snapshot Capture()
        => new(
            _workingBytes,
            Pages.Select(p => p.Annotations.Select(a => a.Clone()).ToList()).ToList());

    /// <summary>Call immediately before a mutating operation to make it undoable.</summary>
    public void BeginChange()
    {
        // Any filled-in form values are still inside PDFium; fold them into the working
        // bytes first, or the snapshot (and the operation about to run on those bytes)
        // would quietly drop them.
        FlushFormValues();
        _undo.Push(Capture());
        if (_undo.Count > 100) _undo.TryPop(out _);
        _redo.Clear();
    }

    public void Undo()
    {
        // While a field is focused, undo belongs to that field's own edit history —
        // PDFium keeps one per field, and the document snapshots don't cover typing.
        if (IsFormFocused && Document is { } d && d.FormCanUndo())
        {
            d.FormUndo();
            MarkDirty();
            RerenderFormPage();
            return;
        }
        if (_undo.Count == 0) return;
        _redo.Push(Capture());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (IsFormFocused && Document is { } d && d.FormCanRedo())
        {
            d.FormRedo();
            MarkDirty();
            RerenderFormPage();
            return;
        }
        if (_redo.Count == 0) return;
        _undo.Push(Capture());
        Restore(_redo.Pop());
    }

    private void Restore(Snapshot snap)
    {
        if (!ReferenceEquals(snap.Bytes, _workingBytes))
        {
            // Structure changed: reopen the document from the snapshot bytes.
            var oldDoc = Document;
            var newDoc = PdfDocument.OpenBytes(snap.Bytes, FilePath);
            _workingBytes = snap.Bytes;
            Document = newDoc;
            Pages.Clear();
            Thumbnails.Clear();
            for (int i = 0; i < snap.AnnotsPerPage.Count; i++)
            {
                var pvm = new PageViewModel(this, i, newDoc.GetPageSize(i));
                foreach (var a in snap.AnnotsPerPage[i]) { a.PageIndex = i; pvm.Annotations.Add(a); }
                Pages.Add(pvm);
                Thumbnails.Add(new PageThumbnail(this, i, newDoc.GetPageSize(i)));
            }
            oldDoc?.Dispose();
            RefreshOutline(); // bookmarks live in the bytes
            RefreshVisiblePages();
            OnPropertyChanged(nameof(PageCount)); OnPropertyChanged(nameof(PageLabel));
        }
        else
        {
            // Only annotations changed: swap them back in place.
            for (int i = 0; i < Pages.Count && i < snap.AnnotsPerPage.Count; i++)
            {
                Pages[i].Annotations.Clear();
                foreach (var a in snap.AnnotsPerPage[i]) { a.PageIndex = i; Pages[i].Annotations.Add(a); }
                Pages[i].NotifyAnnotationChanged();
            }
        }

        _selected.Clear();
        AnnotationEditor = null;
        MarkDirty();
    }

    // ----- Thumbnails -----

    public PdfDocument? RenderDocument => Document;

    public void Dispose()
    {
        _rerenderTimer?.Stop();
        _rerenderTimer = null;
        Document?.Dispose();
        Document = null;
    }
}
