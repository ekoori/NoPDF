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

    private double _zoom = 1.0;
    private PageViewModel? _activeSelectionPage;

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
        SetZoom(z);
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

    public PdfAnnotationModel? SelectedAnnotation { get; private set; }
    private PageViewModel? _selectedAnnotationPage;

    public void SelectAnnotation(PageViewModel? page, PdfAnnotationModel? annotation)
    {
        if (ReferenceEquals(SelectedAnnotation, annotation)) return;
        var oldPage = _selectedAnnotationPage;
        SelectedAnnotation = annotation;
        _selectedAnnotationPage = annotation is null ? null : page;
        oldPage?.NotifyAnnotationChanged();
        page?.NotifyAnnotationChanged();
    }

    public void DeleteSelectedAnnotation()
    {
        if (SelectedAnnotation is null || _selectedAnnotationPage is null) return;
        BeginChange();
        var page = _selectedAnnotationPage;
        var ann = SelectedAnnotation;
        SelectAnnotation(null, null);
        page.RemoveAnnotation(ann);
    }

    public bool IsAnnotationSelected(PdfAnnotationModel a) => ReferenceEquals(SelectedAnnotation, a);

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

    // ----- Save -----

    public IEnumerable<PdfAnnotationModel> AllAnnotations() => Pages.SelectMany(p => p.Annotations);

    public Task SaveAsync(string? destPath = null)
    {
        string dest = destPath ?? FilePath;
        var annots = AllAnnotations().Select(a => a).ToList();
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

    public void InsertFile(string path, int atIndex)
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
        SelectedAnnotation = null;
        _selectedAnnotationPage = null;
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

        SelectedAnnotation = null;
        _selectedAnnotationPage = null;
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
