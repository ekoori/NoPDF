using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NoPdf.App.Editing;
using NoPdf.App.ViewModels;

namespace NoPdf.App.Views;

public partial class DocumentView : UserControl
{
    private ScrollViewer? _scroll;
    // True while a document's view is being restored, so the scroll churn of the relayout
    // isn't mistaken for the user scrolling.
    private bool _applyingView;
    private bool _panning;
    private Point _panStartPointer;
    private Vector _panStartOffset;
    private DocumentViewModel? _vm;

    public DocumentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        PageList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        PageList.AddHandler(PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);

        // A resize changes the fit, so re-lay-out — unless the user has taken zoom into
        // their own hands (a :zoom command), in which case leave their zoom alone. This is
        // what stops Full view snapping back to fit when the command bar opens.
        PageList.SizeChanged += (_, _) =>
        {
            if (_vm is null || _vm.ManualZoom) return;
            if (_vm.ViewMode != PageViewMode.Scroll || _vm.PagesPerRow > 1) OnViewModeChanged();
        };
    }

    private void OnScrollBy(double dx, double dy)
    {
        var sv = Scroll;
        if (sv is null) return;
        double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

        // Full view shows whole page(s): with nothing to scroll to, scrolling turns pages.
        if (_vm is { ViewMode: PageViewMode.Full } && dy != 0 && maxY <= 1)
        {
            _vm.GoToPage(_vm.CurrentPage + (dy > 0 ? 1 : -1) * Math.Max(1, _vm.PagesPerRow));
            return;
        }

        sv.Offset = new Vector(
            Math.Clamp(sv.Offset.X + dx, 0, maxX),
            Math.Clamp(sv.Offset.Y + dy, 0, maxY));
    }

    private void OnScrollPage(int dir)
    {
        // In Full view a "page" of scrolling is the next/previous spread.
        if (_vm is { ViewMode: PageViewMode.Full })
        {
            _vm.GoToPage(_vm.CurrentPage + dir * Math.Max(1, _vm.PagesPerRow));
            return;
        }
        var sv = Scroll;
        if (sv is null) return;
        OnScrollBy(0, dir * sv.Viewport.Height * 0.9);
    }

    private void HookScroll()
    {
        var sv = Scroll;
        if (sv is null) return;
        sv.ScrollChanged -= OnScrollChanged;
        sv.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // While a document is being laid out the offset passes through 0. Reacting to that
        // would both reset its current page and overwrite the position we're restoring.
        if (_vm is null || _applyingView) return;
        // Full view shows only the focused page(s); its page is driven by navigation.
        if (_vm.ViewMode == PageViewMode.Full) return;
        var sv = Scroll;
        if (sv is null) return;
        int best = _vm.ViewMode == PageViewMode.ScrollH
            ? LeadingPageHorizontal(sv)
            : LeadingPageVertical(sv);
        if (best >= 0) _vm.SetCurrentPageSilent(best + 1);

        // Always keep the in-memory position current, so switching tabs returns exactly
        // here; only the disk persistence is throttled.
        _vm.SetLiveView(_vm.ZoomPercent / 100.0, sv.Offset.X, sv.Offset.Y);
        var now = DateTime.UtcNow;
        if ((now - _lastViewSave).TotalMilliseconds > 400)
        {
            _lastViewSave = now;
            SaveViewState();
        }
    }

    /// <summary>The last page whose top has passed the viewport top — the one being read
    /// in a vertically-scrolling view.</summary>
    private int LeadingPageVertical(ScrollViewer sv)
    {
        int best = -1;
        double bestTop = double.NegativeInfinity;
        foreach (var c in PageList.GetRealizedContainers())
        {
            if (c.DataContext is not PageViewModel pvm) continue;
            double top = c.Bounds.Y - sv.Offset.Y;
            if (top <= 8 && top > bestTop) { bestTop = top; best = pvm.PageIndex; }
        }
        return best;
    }

    /// <summary>The first page of the leftmost visible column — the one being read in the
    /// horizontal-scroll view. Tracking this by Y (as the vertical view does) would never
    /// change as you scroll sideways, which is why the current page used to stick.</summary>
    private int LeadingPageHorizontal(ScrollViewer sv)
    {
        int best = -1;
        double bestX = double.PositiveInfinity, bestY = double.PositiveInfinity;
        foreach (var c in PageList.GetRealizedContainers())
        {
            if (c.DataContext is not PageViewModel pvm) continue;
            if (c.Bounds.X - sv.Offset.X < -8) continue;  // scrolled off to the left
            if (c.Bounds.X < bestX - 1 ||
                (Math.Abs(c.Bounds.X - bestX) <= 1 && c.Bounds.Y < bestY))
            { bestX = c.Bounds.X; bestY = c.Bounds.Y; best = pvm.PageIndex; }
        }
        return best;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            // Persist the outgoing document's position (LastView is already current from
            // scrolling; this also writes it to the on-disk store).
            SaveViewState();
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.ScrollToPageRequested -= OnScrollToPage;
            _vm.FitWidthRequested -= OnFitWidth;
            _vm.FitPageRequested -= OnFitPage;
            _vm.ScrollByRequested -= OnScrollBy;
            _vm.ScrollPageRequested -= OnScrollPage;
            _vm.ViewModeChanged -= OnViewModeChanged;
            _vm.AnchoredZoom = null;
        }
        _vm = DataContext as DocumentViewModel;
        if (_vm is not null)
        {
            // Guard synchronously, before the ItemsSource rebind resets the shared scroll
            // viewer to 0 — otherwise that reset fires OnScrollChanged and overwrites this
            // document's remembered position with (0,0), sending it back to page 1.
            _applyingView = true;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.ScrollToPageRequested += OnScrollToPage;
            _vm.FitWidthRequested += OnFitWidth;
            _vm.FitPageRequested += OnFitPage;
            _vm.ScrollByRequested += OnScrollBy;
            _vm.ScrollPageRequested += OnScrollPage;
            _vm.ViewModeChanged += OnViewModeChanged;
            _vm.AnchoredZoom = apply => ApplyZoomAnchored(apply, null);
            ApplyDpi();
            // The view is recycled when the tab changes, so a document swapped in here has
            // to re-apply its own mode and position — OnAttachedToVisualTree won't fire again.
            if (this.IsAttachedToVisualTree())
                Dispatcher.UIThread.Post(ApplyViewForDoc, DispatcherPriority.Loaded);
        }
        UpdateCursor();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyDpi();
        Dispatcher.UIThread.Post(() => { HookScroll(); PageList.Focus(); ApplyViewForDoc(); }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        SaveViewState();
    }

    /// <summary>
    /// Shows the current document with ITS OWN view mode and position. Runs every time a
    /// document is put in this view, not just the first time: the view (and its panels and
    /// scroll viewer) is recycled between tabs, so without this a tab would keep whatever
    /// mode and offset the previously shown tab left behind — which made a `:view` command
    /// look like it applied to every tab.
    /// </summary>
    private void ApplyViewForDoc()
    {
        if (_vm is null) { _applyingView = false; return; }

        // Nothing can be laid out until the document has pages. The tab is shown before its
        // content finishes loading (so a slow open is visible), so this can run first against
        // an empty document — and sizing the wrap panel to no pages is exactly what left every
        // page squished into a one-pixel line until the next resize. Wait; OnVmPropertyChanged
        // re-runs this the moment the pages arrive. Stay guarded meanwhile so the ItemsSource
        // rebind's scroll reset doesn't overwrite the remembered position.
        if (_vm.Pages.Count == 0) { _applyingView = true; return; }

        _applyingView = true;   // ignore the scroll churn the relayout is about to cause
        bool first = _vm.PendingInitialView;
        _vm.PendingInitialView = false;

        // First display: the remembered-from-disk position. Later: where this tab was when
        // you last left it. Either way we ALWAYS position the document — never leave it at
        // whatever offset the previously shown tab left in the shared scroll viewer.
        var saved = first ? _vm.InitialView : _vm.LastView;
        if (first) _vm.InitialView = null;
        int keep = _vm.CurrentPage;

        // Fix the zoom BEFORE building the panels. RelayoutPanels and UpdateWrapPanel size the
        // page slot from the current zoom, so if the zoom is set afterwards the slot is built
        // at the wrong size and corrected a frame later — the visible "squished then snaps"
        // flash. The viewport is already measured here (the tab has been shown), so the fit is
        // valid now.
        var sv = Scroll;
        bool modeOwnsZoom = _vm.ViewMode != PageViewMode.Scroll || _vm.PagesPerRow > 1;
        if (saved is { } v) _vm.SetZoom(v.Zoom);
        else if (modeOwnsZoom && sv is { Viewport.Width: > 0, Viewport.Height: > 0 } && !_vm.ManualZoom)
            _vm.FitForView(sv.Viewport.Width, sv.Viewport.Height, keep);
        // else: plain scroll with no saved position — keep the zoom, go to the top.


        RelayoutPanels();   // this document's mode and now-correct zoom
        UpdateWrapPanel();  // size the wrap panel to the same
        RestoreOffset(saved is { } v2 ? new Vector(v2.OffsetX, v2.OffsetY) : default);
    }

    /// <summary>
    /// Puts the scroll offset back, once the panel has actually been laid out far enough to
    /// hold it. Setting it any earlier silently clamps to zero — the relayout has thrown the
    /// containers away and the extent isn't there yet — which sent every tab switch back to
    /// page 1. Retries for a few frames, then takes what it can get.
    /// </summary>
    private void RestoreOffset(Vector want)
    {
        int tries = 0;
        void Attempt()
        {
            var sv = Scroll;
            if (sv is null) { _applyingView = false; return; }
            double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
            double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            if (++tries < 10 && (want.X > maxX + 0.5 || want.Y > maxY + 0.5))
            {
                Dispatcher.UIThread.Post(Attempt, DispatcherPriority.Background);
                return;
            }
            sv.Offset = new Vector(Math.Clamp(want.X, 0, maxX), Math.Clamp(want.Y, 0, maxY));
            // Let the scroll handler track the page again, from the position we just set.
            Dispatcher.UIThread.Post(() => _applyingView = false, DispatcherPriority.Background);
        }
        Dispatcher.UIThread.Post(Attempt, DispatcherPriority.Loaded);
    }

    private DateTime _lastViewSave = DateTime.MinValue;

    private void SaveViewState()
    {
        var sv = Scroll;
        if (sv is null || _vm is null) return;
        _vm.ReportViewState(_vm.ZoomPercent / 100.0, sv.Offset.X, sv.Offset.Y);
    }

    private void ApplyDpi()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        _vm?.SetDpiScale(scaling);
    }

    private static readonly Avalonia.Controls.Templates.FuncTemplate<Panel?> StackPanelTemplate =
        new(() => new VirtualizingStackPanel());

    /// <summary>Pages flow down a column then wrap rightwards, giving horizontal scroll.
    /// The explicit height is what pins it to N rows: vertical scrolling otherwise offers
    /// the panel infinite height and it lays every page out in one endless column. Sizing
    /// that height off the current zoom (rather than the viewport) is what lets a zoomed-in
    /// page overflow the viewport and become reachable by scrolling down.</summary>
    private static Avalonia.Controls.Templates.FuncTemplate<Panel?> ColumnTemplate(double itemH, double panelH)
        // Centred: when the rows are shorter than the viewport they sit in the middle;
        // once zoomed past it the centring is a no-op and it scrolls from the top edge.
        => new(() => new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            ItemHeight = itemH > 0 ? itemH : double.NaN,
            Height = panelH > 0 ? panelH : double.NaN,
        });

    /// <summary>Pages flow across a row then wrap downwards, giving vertical scroll. The
    /// mirror of <see cref="ColumnTemplate"/>: the explicit width pins it to N columns
    /// sized off the current zoom, so a page never overlaps its neighbour (the old fixed
    /// slot width let a zoomed-in page spill over) and a zoomed-in row overflows into a
    /// horizontal scroll.</summary>
    private static Avalonia.Controls.Templates.FuncTemplate<Panel?> ColumnsTemplate(double itemW, double panelW)
        // Centred: when the columns are narrower than the viewport they sit in the middle;
        // once zoomed past it the centring is a no-op and it scrolls from the left edge.
        => new(() => new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            ItemWidth = itemW > 0 ? itemW : double.NaN,
            Width = panelW > 0 ? panelW : double.NaN,
        });

    /// <summary>Re-sizes the wrap panel to N rows/columns at the current zoom, so zooming
    /// grows the pages and scrolls rather than overlapping them (scroll N-across and
    /// horizontal-scroll modes).</summary>
    private void UpdateWrapPanel()
    {
        // Sizing the slot from RowHeight()/ColWidth() with no page to measure collapses every
        // page to a one-pixel line. Never do it; the real sizing happens once pages exist.
        if (_vm is null || _vm.Pages.Count == 0) return;
        var panel = PageList.GetVisualDescendants().OfType<WrapPanel>().FirstOrDefault();
        if (panel is null) return;
        int n = Math.Max(1, _vm.PagesPerRow);
        if (_vm.ViewMode == PageViewMode.ScrollH)
        {
            double rowH = _vm.RowHeight();
            panel.ItemHeight = rowH;
            panel.Height = rowH * n;
        }
        else if (_vm.ViewMode == PageViewMode.Scroll && n > 1)
        {
            double colW = _vm.ColWidth();
            panel.ItemWidth = colW;
            panel.Width = colW * n;
        }
    }

    /// <summary>Exactly the focused page(s) in one row, sized to their content so the
    /// scroll viewer can pan a zoomed-in page (a UniformGrid capped them at the viewport,
    /// which clipped any zoom). VisiblePages already holds only the N in focus, so nothing
    /// else can appear.</summary>
    private static readonly Avalonia.Controls.Templates.FuncTemplate<Panel?> FullRowTemplate =
        new(() => new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        });

    private void OnViewModeChanged()
    {
        if (_vm is null) return;
        // The mode is restored (SetView) before the document's pages have loaded, so this can
        // run with nothing to measure — RowHeight()/ColWidth() fall back to 1 and the panel
        // collapses every page to a one-pixel line. Skip; ApplyViewForDoc lays the mode out
        // once the pages arrive.
        if (_vm.Pages.Count == 0) return;
        // The relayout below resets the scroll offset, which would otherwise drag the
        // current page back to 1 before the refit reads it.
        int keep = _vm.CurrentPage;
        RelayoutPanels();

        Dispatcher.UIThread.Post(() =>
        {
            var sv = Scroll;
            if (sv is null || _vm is null) return;
            var vp = sv.Viewport;
            // A hand-set zoom (a :zoom command) must survive relayouts; only auto-fit when
            // the user hasn't overridden it.
            if (vp.Width > 0 && vp.Height > 0 && !_vm.ManualZoom) _vm.FitForView(vp.Width, vp.Height, keep);
            UpdateWrapPanel(); // the refit may not change the zoom, so size the panel anyway
        }, DispatcherPriority.Background);
    }

    /// <summary>Rebuilds the item panel and scrollbars for the current document's view mode.
    /// The view is recycled between tabs, so its panels still describe whichever document
    /// was shown last — every tab has to re-assert its own layout when it comes forward.</summary>
    private void RelayoutPanels()
    {
        if (_vm is null) return;
        var sv0 = Scroll;
        var vp0 = sv0?.Viewport ?? default;
        int n = Math.Max(1, _vm.PagesPerRow);

        switch (_vm.ViewMode)
        {
            case PageViewMode.ScrollH:
                // Rows are sized off the zoom, not the viewport, so a zoomed-in page
                // overflows downwards and can be panned to.
                PageList.ItemsPanel = ColumnTemplate(_vm.RowHeight(), _vm.RowHeight() * n);
                ScrollViewer.SetVerticalScrollBarVisibility(PageList, ScrollBarVisibility.Auto);
                ScrollViewer.SetHorizontalScrollBarVisibility(PageList, ScrollBarVisibility.Auto);
                break;
            case PageViewMode.Full:
                // Only the focused page(s) are in the list, laid out at their content size
                // so a zoomed-in page can be panned. Scrollbars available for that panning.
                PageList.ItemsPanel = FullRowTemplate;
                ScrollViewer.SetHorizontalScrollBarVisibility(PageList, ScrollBarVisibility.Auto);
                ScrollViewer.SetVerticalScrollBarVisibility(PageList, ScrollBarVisibility.Auto);
                break;
            case PageViewMode.Scroll when n > 1:
                // N columns sized off the zoom; overflow scrolls rather than overlapping.
                PageList.ItemsPanel = ColumnsTemplate(_vm.ColWidth(), _vm.ColWidth() * n);
                ScrollViewer.SetHorizontalScrollBarVisibility(PageList, ScrollBarVisibility.Auto);
                ScrollViewer.SetVerticalScrollBarVisibility(PageList, ScrollBarVisibility.Auto);
                break;
            default:
                PageList.ItemsPanel = StackPanelTemplate;
                ScrollViewer.SetHorizontalScrollBarVisibility(PageList, ScrollBarVisibility.Auto);
                ScrollViewer.SetVerticalScrollBarVisibility(PageList, ScrollBarVisibility.Auto);
                break;
        }
    }

    /// <summary>Puts keyboard focus back on the page in view. Page-level keys (Delete on a
    /// selected annotation, Escape, Ctrl+C) are handled by PageView, so they stop working
    /// as soon as anything else — the command bar, a toolbar button — takes focus.</summary>
    public void FocusPage()
    {
        var pages = PageList.GetVisualDescendants().OfType<PageView>().ToList();
        var wanted = pages.FirstOrDefault(v => v.DataContext is PageViewModel p
                                               && p.PageNumber == _vm?.CurrentPage);
        if ((wanted ?? pages.FirstOrDefault()) is { } pv) pv.Focus();
        else PageList.Focus();
    }

    private void OnFitWidth() => Dispatcher.UIThread.Post(() => Fit(true));
    private void OnFitPage() => Dispatcher.UIThread.Post(() => Fit(false));

    private void Fit(bool widthOnly)
    {
        var sv = Scroll;
        if (sv is null || _vm is null) return;
        var vp = sv.Viewport;
        if (vp.Width > 0 && vp.Height > 0)
            _vm.FitToViewport(vp.Width, vp.Height, widthOnly);
    }

    private void OnScrollToPage(int index)
    {
        if (_vm is null || index < 0 || index >= _vm.Pages.Count) return;
        var target = _vm.Pages[index];
        Dispatcher.UIThread.Post(() =>
        {
            PageList.ScrollIntoView(target);
            // A pending reveal will scroll to the annotation itself — landing on the
            // page's top would undo it.
            if (target.PendingRevealRect is not null) return;
            // Align the page's leading edge with the viewport (ScrollIntoView alone can
            // leave the page at the far edge when moving backwards).
            Dispatcher.UIThread.Post(() =>
            {
                var sv = Scroll;
                int viewIdx = _vm.VisiblePages.IndexOf(target);
                var c = viewIdx >= 0 ? PageList.ContainerFromIndex(viewIdx) : null;
                if (sv is null || c is null) return;
                var pt = c.TranslatePoint(new Point(0, 0), sv);
                if (pt is not { } p) return;
                // Horizontal-scroll view flows in columns, so a page is reached by moving
                // sideways: bring its LEFT edge to the viewport. Everything else scrolls
                // vertically and wants the page's top.
                double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
                double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                sv.Offset = _vm.ViewMode == PageViewMode.ScrollH
                    ? new Vector(Math.Clamp(sv.Offset.X + p.X - 12, 0, maxX), sv.Offset.Y)
                    : new Vector(sv.Offset.X, Math.Clamp(sv.Offset.Y + p.Y - 24, 0, maxY));
            }, DispatcherPriority.Background);
        });
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.CurrentTool))
            UpdateCursor();
        // The N-across and horizontal-scroll panels are sized off the zoom, so the panel
        // has to grow with it — that overflow is what makes zooming scroll instead of
        // overlapping the pages.
        else if (e.PropertyName == nameof(DocumentViewModel.ZoomPercent))
            Dispatcher.UIThread.Post(UpdateWrapPanel, DispatcherPriority.Background);
        // The pages have just appeared (the tab was shown before its content loaded). The
        // initial layout couldn't run against an empty document, so run it now that there is
        // something to fit and size the panel to.
        else if (e.PropertyName == nameof(DocumentViewModel.PageCount))
        {
            if (_vm is { PendingInitialView: true } && _vm.Pages.Count > 0)
                Dispatcher.UIThread.Post(ApplyViewForDoc, DispatcherPriority.Loaded);
        }
    }

    private void UpdateCursor()
    {
        // Base cursor per tool; PageView refines it while hovering a page.
        Cursor = _vm?.CurrentTool switch
        {
            EditorTool.Hand => PanCursors.Open,
            EditorTool.Zoom => new Cursor(StandardCursorType.Cross),
            _ => Cursor.Default,
        };
    }

    private ScrollViewer? Scroll =>
        _scroll ??= PageList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Pan with the Hand tool (left button) or the middle mouse button (any tool).
        var props = e.GetCurrentPoint(this).Properties;
        bool hand = _vm?.CurrentTool == EditorTool.Hand && props.IsLeftButtonPressed;
        if (!hand && !props.IsMiddleButtonPressed) return;
        // In view mode a click on a link follows it, and a click in a form field lands in
        // the field, instead of starting a pan. This runs in the tunnel phase — before
        // PageView sees the click — so without these the page would just pan away.
        if (hand && (e.Source as Visual)?.FindAncestorOfType<PageView>() is { } pv
                 && (pv.HasLinkAt(e) || pv.HasFormFieldAt(e))) return;
        var sv = Scroll;
        if (sv is null) return;

        _panning = true;
        _panStartPointer = e.GetPosition(this);
        _panStartOffset = sv.Offset;
        e.Pointer.Capture(PageList);
        Cursor = PanCursors.Grab; // closed hand while dragging
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;
        var sv = Scroll;
        if (sv is null) return;

        var delta = e.GetPosition(this) - _panStartPointer;
        sv.Offset = new Vector(_panStartOffset.X - delta.X, _panStartOffset.Y - delta.Y);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
        UpdateCursor(); // back to the open hand (or tool default)
        e.Handled = true;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl + wheel = zoom; plain wheel scrolls normally.
        if (_vm is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var sv = Scroll;
        if (sv is null) return;

        double factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        double target = _vm.ZoomPercent / 100.0 * factor;
        // Anchor to the point under the cursor so the same spot stays put.
        ApplyZoomAnchored(() => _vm.SetZoom(target), e.GetPosition(sv));
        e.Handled = true;
    }

    /// <summary>
    /// Applies a zoom, then re-anchors the scroll offset in the SAME layout pass so the
    /// pages don't resize and then visibly jump a frame later (the jitter). The anchor is a
    /// point in viewport space — the cursor for a wheel zoom, the viewport centre otherwise
    /// — that is kept over the same document position across the zoom.
    /// </summary>
    private void ApplyZoomAnchored(Action applyZoom, Point? anchor)
    {
        var sv = Scroll;
        if (sv is null) { applyZoom(); return; }
        var vp = sv.Viewport;
        var a = anchor ?? new Point(vp.Width / 2, vp.Height / 2);

        double exW = sv.Extent.Width, exH = sv.Extent.Height;
        double rx = exW > 0 ? (sv.Offset.X + a.X) / exW : 0;
        double ry = exH > 0 ? (sv.Offset.Y + a.Y) / exH : 0;

        applyZoom();
        UpdateWrapPanel();     // size the wrap panels for the new zoom first
        sv.UpdateLayout();     // force the resize now, so the new extent is available

        double nW = sv.Extent.Width, nH = sv.Extent.Height;
        sv.Offset = new Vector(
            Math.Clamp(rx * nW - a.X, 0, Math.Max(0, nW - vp.Width)),
            Math.Clamp(ry * nH - a.Y, 0, Math.Max(0, nH - vp.Height)));
    }
}
