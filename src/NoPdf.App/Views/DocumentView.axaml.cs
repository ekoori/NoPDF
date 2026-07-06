using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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
    }

    private void OnScrollBy(double dx, double dy)
    {
        var sv = Scroll;
        if (sv is null) return;
        double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        sv.Offset = new Vector(
            Math.Clamp(sv.Offset.X + dx, 0, maxX),
            Math.Clamp(sv.Offset.Y + dy, 0, maxY));
    }

    private void OnScrollPage(int dir)
    {
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
            ApplyDpi();
        }
        UpdateCursor();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyDpi();
        Dispatcher.UIThread.Post(() => { HookScroll(); PageList.Focus(); }, DispatcherPriority.Loaded);
    }

    private void ApplyDpi()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        _vm?.SetDpiScale(scaling);
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
        Dispatcher.UIThread.Post(() => PageList.ScrollIntoView(_vm.Pages[index]));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.CurrentTool))
            UpdateCursor();
    }

    private void UpdateCursor()
    {
        Cursor = _vm?.CurrentTool switch
        {
            EditorTool.Hand => new Cursor(StandardCursorType.Hand),
            EditorTool.Select => new Cursor(StandardCursorType.Ibeam),
            EditorTool.Zoom => new Cursor(StandardCursorType.Cross),
            _ => Cursor.Default,
        };
    }

    private ScrollViewer? Scroll =>
        _scroll ??= PageList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm?.CurrentTool != EditorTool.Hand) return;
        var sv = Scroll;
        if (sv is null) return;

        _panning = true;
        _panStartPointer = e.GetPosition(this);
        _panStartOffset = sv.Offset;
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
        e.Handled = true;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl + wheel = zoom; plain wheel scrolls normally.
        if (_vm is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        if (e.Delta.Y > 0) _vm.SetZoom(ZoomFrom(_vm, 1.1));
        else if (e.Delta.Y < 0) _vm.SetZoom(ZoomFrom(_vm, 1 / 1.1));
        e.Handled = true;
    }

    private static double ZoomFrom(DocumentViewModel vm, double factor)
        => vm.ZoomPercent / 100.0 * factor;
}
