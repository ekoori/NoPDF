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
    public string FilePath { get; }
    public string Title { get; }

    // In-memory PDF the viewer edits (known annotations stripped into the model).
    private byte[] _workingBytes;

    public ObservableCollection<PageViewModel> Pages { get; } = new();
    public ObservableCollection<PageThumbnail> Thumbnails { get; } = new();
    public ObservableCollection<BookmarkNode> Outline { get; } = new();
    public ObservableCollection<BookmarkNode> UserBookmarks { get; } = new();
    public bool HasOutline => Outline.Count > 0;

    [ObservableProperty] private EditorTool _currentTool = EditorTool.Hand;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isActive; // is this the selected tab

    private double _zoom = 1.0;
    private PageViewModel? _activeSelectionPage;

    /// <summary>Zoom + scroll offset to restore on first layout (null = none).</summary>
    public (double Zoom, double OffsetX, double OffsetY)? InitialView { get; set; }
    /// <summary>Set by the host to persist view position as it changes.</summary>
    public Action<double, double, double>? ViewStateSink { get; set; }
    public void ReportViewState(double zoom, double ox, double oy) => ViewStateSink?.Invoke(zoom, ox, oy);

    /// <summary>Raised when a certified signature stamp is committed, to run the signing flow.</summary>
    public Action<SignatureAnnotation>? CertifyRequested { get; set; }

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

    private DocumentViewModel(PdfDocument document, string filePath, byte[] workingBytes,
        IReadOnlyList<OutlineItem> outline, List<PdfAnnotationModel> annotations)
    {
        Document = document;
        FilePath = filePath;
        Title = Path.GetFileName(filePath);
        _workingBytes = workingBytes;
        BuildPages(annotations);
        foreach (var item in outline)
            Outline.Add(BookmarkNode.FromOutline(item));
    }

    public static Task<DocumentViewModel> LoadAsync(string filePath)
        => Task.Run(() =>
        {
            var fileBytes = File.ReadAllBytes(filePath);
            var (cleaned, models) = AnnotationReader.LoadAndStrip(fileBytes);
            var doc = PdfDocument.OpenBytes(cleaned, filePath);
            var outline = doc.GetOutline();
            return new DocumentViewModel(doc, filePath, cleaned, outline, models);
        });

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
    }

    public void AddUserBookmark(string name)
    {
        BeginChange();
        UserBookmarks.Add(new BookmarkNode { Title = name, PageIndex = CurrentPage - 1 });
        MarkDirty();
    }

    public bool RemoveUserBookmark(string name)
    {
        var node = UserBookmarks.FirstOrDefault(b => string.Equals(b.Title, name, StringComparison.OrdinalIgnoreCase));
        if (node is null) return false;
        BeginChange();
        UserBookmarks.Remove(node);
        MarkDirty();
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
        foreach (var p in Pages) p.OnScaleChanged();
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

    // ----- Navigation -----

    public int CurrentPage { get; private set; } = 1;

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

    /// <summary>All annotations grouped with <paramref name="a"/> (itself if ungrouped).</summary>
    public IEnumerable<PdfAnnotationModel> GroupMembers(PdfAnnotationModel a)
        => a.GroupId is { } g ? AllAnnotations().Where(x => x.GroupId == g) : new[] { a };

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
        foreach (var a in _selected) a.GroupId = g;
        MarkDirty();
    }

    public void UngroupSelected()
    {
        if (_selected.All(a => a.GroupId is null)) return;
        BeginChange();
        foreach (var a in _selected) a.GroupId = null;
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
            if (c.GroupId is { } g)
                c.GroupId = groupMap.TryGetValue(g, out var ng) ? ng : (groupMap[g] = Guid.NewGuid());
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
            if (c.GroupId is { } g)
                c.GroupId = groupMap.TryGetValue(g, out var ng) ? ng : (groupMap[g] = Guid.NewGuid());
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
    }

    private Dictionary<int, List<PdfLink>>? _links;
    private readonly List<HintTarget> _hints = new();
    public IReadOnlyList<HintTarget> Hints => _hints;
    public bool IsHintMode { get; private set; }
    public string HintPrefix { get; private set; } = "";

    /// <summary>Raised to open an external URI when a link hint is followed.</summary>
    public event Action<string>? OpenUriRequested;

    private const string HintKeys = "fjdkslaghrueiwovncmbt";

    /// <summary>Collects link hints for the currently visible (realized) pages.</summary>
    public bool EnterHintMode()
    {
        ExitHintMode();
        _links ??= LinkReader.ReadAll(_workingBytes);
        var targets = new List<(int page, PdfLink link)>();
        foreach (var p in Pages)
            if (p.IsRealized && _links.TryGetValue(p.PageIndex, out var ls))
                foreach (var l in ls) targets.Add((p.PageIndex, l));
        if (targets.Count == 0) return false;

        var labels = HintLabels(targets.Count);
        for (int i = 0; i < targets.Count; i++)
            _hints.Add(new HintTarget
            {
                PageIndex = targets[i].page, Rect = targets[i].link.Rect, Label = labels[i],
                TargetPage = targets[i].link.TargetPage, Uri = targets[i].link.Uri,
            });
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
        if (h.TargetPage is { } tp) GoToPage(tp + 1);
        else if (!string.IsNullOrEmpty(h.Uri)) OpenUriRequested?.Invoke(h.Uri!);
    }

    private void NotifyAllPages() { foreach (var p in Pages) p.NotifyAnnotationChanged(); }

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
    public byte[] ExportWithAnnotations() => AnnotationWriter.SaveToBytes(_workingBytes, AnnotationsForSave());

    public Task SaveAsync(string? destPath = null)
    {
        string dest = destPath ?? FilePath;
        var annots = AnnotationsForSave();
        var bytes = _workingBytes;
        return Task.Run(() =>
        {
            var outBytes = AnnotationWriter.SaveToBytes(bytes, annots);
            File.WriteAllBytes(dest, outBytes);
        }).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                Dispatcher.UIThread.Post(() => IsDirty = false);
        });
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

    /// <summary>Re-reads the file from disk, discarding in-memory edits.</summary>
    public void ReloadFromDisk()
    {
        if (!File.Exists(FilePath)) return;
        var (cleaned, models) = AnnotationReader.LoadAndStrip(File.ReadAllBytes(FilePath));
        var oldDoc = Document;
        var newDoc = PdfDocument.OpenBytes(cleaned, FilePath);
        _workingBytes = cleaned;
        Document = newDoc;
        _undo.Clear(); _redo.Clear();
        _selected.Clear(); AnnotationEditor = null;
        _activeSelectionPage = null; _findMatches.Clear(); _findIndex = -1;

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
        OnPropertyChanged(nameof(PageCount));
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

        oldDoc?.Dispose();
        MarkDirty();
        OnPropertyChanged(nameof(PageCount));
        CurrentPage = Math.Clamp(CurrentPage, 1, Math.Max(1, PageCount));
    }

    // ----- Undo / redo (snapshot-based) -----

    private sealed record Snapshot(
        byte[] Bytes,
        List<List<PdfAnnotationModel>> AnnotsPerPage,
        List<BookmarkNode> UserBookmarks);

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private Snapshot Capture()
        => new(
            _workingBytes,
            Pages.Select(p => p.Annotations.Select(a => a.Clone()).ToList()).ToList(),
            UserBookmarks.Select(b => new BookmarkNode { Title = b.Title, PageIndex = b.PageIndex }).ToList());

    /// <summary>Call immediately before a mutating operation to make it undoable.</summary>
    public void BeginChange()
    {
        _undo.Push(Capture());
        if (_undo.Count > 100) _undo.TryPop(out _);
        _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Capture());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
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
            OnPropertyChanged(nameof(PageCount));
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
        UserBookmarks.Clear();
        foreach (var b in snap.UserBookmarks)
            UserBookmarks.Add(new BookmarkNode { Title = b.Title, PageIndex = b.PageIndex });

        _selected.Clear();
        AnnotationEditor = null;
        MarkDirty();
    }

    // ----- Thumbnails -----

    public PdfDocument? RenderDocument => Document;

    public void Dispose()
    {
        Document?.Dispose();
        Document = null;
    }
}
