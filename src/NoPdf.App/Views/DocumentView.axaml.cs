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
        if (_vm is null) return;
        // Full view shows only the focused page(s); its page is driven by navigation.
        if (_vm.ViewMode == PageViewMode.Full) return;
        var sv = Scroll;
        if (sv is null) return;
        double offset = sv.Offset.Y;
        int best = -1;
        double bestTop = double.NegativeInfinity;
        foreach (var c in PageList.GetRealizedContainers())
        {
            if (c.DataContext is not PageViewModel pvm) continue;
            double top = c.Bounds.Y - offset;
            if (top <= 8 && top > bestTop) { bestTop = top; best = pvm.PageIndex; }
        }
        if (best >= 0) _vm.SetCurrentPageSilent(best + 1);

        // Throttle persistence of the view position.
        var now = DateTime.UtcNow;
        if ((now - _lastViewSave).TotalMilliseconds > 400)
        {
            _lastViewSave = now;
            SaveViewState();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.ScrollToPageRequested -= OnScrollToPage;
            _vm.FitWidthRequested -= OnFitWidth;
            _vm.FitPageRequested -= OnFitPage;
            _vm.ScrollByRequested -= OnScrollBy;
            _vm.ScrollPageRequested -= OnScrollPage;
            _vm.ViewModeChanged -= OnViewModeChanged;
        }
        _vm = DataContext as DocumentViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.ScrollToPageRequested += OnScrollToPage;
            _vm.FitWidthRequested += OnFitWidth;
            _vm.FitPageRequested += OnFitPage;
            _vm.ScrollByRequested += OnScrollBy;
            _vm.ScrollPageRequested += OnScrollPage;
            _vm.ViewModeChanged += OnViewModeChanged;
            ApplyDpi();
            // The view is reused when the tab changes, so a document swapped in here still
            // needs positioning — OnAttachedToVisualTree won't fire again.
            if (this.IsAttachedToVisualTree())
                Dispatcher.UIThread.Post(ApplyInitialView, DispatcherPriority.Loaded);
        }
        UpdateCursor();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyDpi();
        Dispatcher.UIThread.Post(() => { HookScroll(); PageList.Focus(); ApplyInitialView(); }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        SaveViewState();
    }

    /// <summary>Positions a document the first time it is shown: at its remembered spot,
    /// or at the top of page 1. Without the explicit reset a fresh document inherits
    /// whatever offset the previous tab left in the (reused) ScrollViewer.</summary>
    private void ApplyInitialView()
    {
        if (_vm is null || !_vm.PendingInitialView) return;
        _vm.PendingInitialView = false;
        var iv = _vm.InitialView;
        _vm.InitialView = null;

        // A remembered view mode owns the layout: it picks its own zoom from the viewport
        // and positions itself, so the saved zoom/offset would only fight it.
        if (_vm.ViewMode != PageViewMode.Scroll || _vm.PagesPerRow > 1) { OnViewModeChanged(); return; }

        if (iv is { } v) _vm.SetZoom(v.Zoom);
        else _vm.GoToPage(1);

        Dispatcher.UIThread.Post(() =>
        {
            var sv = Scroll;
            if (sv is null) return;
            sv.Offset = iv is { } v2 ? new Vector(v2.OffsetX, v2.OffsetY) : default;
        }, DispatcherPriority.Background);
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
        => new(() => new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            ItemHeight = itemH > 0 ? itemH : double.NaN,
            Height = panelH > 0 ? panelH : double.NaN,
        });

    /// <summary>Pages flow across a row then wrap downwards, giving vertical scroll. The
    /// mirror of <see cref="ColumnTemplate"/>: the explicit width pins it to N columns
    /// sized off the current zoom, so a page never overlaps its neighbour (the old fixed
    /// slot width let a zoomed-in page spill over) and a zoomed-in row overflows into a
    /// horizontal scroll.</summary>
    private static Avalonia.Controls.Templates.FuncTemplate<Panel?> ColumnsTemplate(double itemW, double panelW)
        => new(() => new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            ItemWidth = itemW > 0 ? itemW : double.NaN,
            Width = panelW > 0 ? panelW : double.NaN,
        });

    /// <summary>Re-sizes the wrap panel to N rows/columns at the current zoom, so zooming
    /// grows the pages and scrolls rather than overlapping them (scroll N-across and
    /// horizontal-scroll modes).</summary>
    private void UpdateWrapPanel()
    {
        if (_vm is null) return;
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
        var sv0 = Scroll;
        var vp0 = sv0?.Viewport ?? default;
        int n = Math.Max(1, _vm.PagesPerRow);
        // The relayout below resets the scroll offset, which would otherwise drag the
        // current page back to 1 before the refit reads it.
        int keep = _vm.CurrentPage;

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
            // Align the page's TOP with the viewport top (ScrollIntoView alone can
            // leave the page at the bottom when scrolling upward).
            Dispatcher.UIThread.Post(() =>
            {
                var sv = Scroll;
                int viewIdx = _vm.VisiblePages.IndexOf(target);
                var c = viewIdx >= 0 ? PageList.ContainerFromIndex(viewIdx) : null;
                if (sv is null || c is null) return;
                var pt = c.TranslatePoint(new Point(0, 0), sv);
                if (pt is { } p)
                    sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + p.Y - 24));
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
        // In view mode, a click on a link follows it instead of starting a pan.
        if (hand && (e.Source as Visual)?.FindAncestorOfType<PageView>()?.HasLinkAt(e) == true) return;
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

        // Anchor the document point under the cursor so the same spot on the same
        // page stays put across the zoom (extent scales, so anchor by extent ratio).
        var mouse = e.GetPosition(sv);
        double exW = sv.Extent.Width, exH = sv.Extent.Height;
        double ratioX = exW > 0 ? (sv.Offset.X + mouse.X) / exW : 0;
        double ratioY = exH > 0 ? (sv.Offset.Y + mouse.Y) / exH : 0;

        double factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        _vm.SetZoom(_vm.ZoomPercent / 100.0 * factor);

        Dispatcher.UIThread.Post(() =>
        {
            var s = Scroll;
            if (s is null) return;
            double nW = s.Extent.Width, nH = s.Extent.Height;
            double maxX = Math.Max(0, nW - s.Viewport.Width);
            double maxY = Math.Max(0, nH - s.Viewport.Height);
            s.Offset = new Vector(
                Math.Clamp(ratioX * nW - mouse.X, 0, maxX),
                Math.Clamp(ratioY * nH - mouse.Y, 0, maxY));
        }, DispatcherPriority.Background);
        e.Handled = true;
    }
}
