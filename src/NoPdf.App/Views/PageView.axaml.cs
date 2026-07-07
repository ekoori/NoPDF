using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NoPdf.App.Editing;
using NoPdf.App.ViewModels;
using NoPdf.Core.Annotations;
using NoPdf.Core.Text;

namespace NoPdf.App.Views;

public partial class PageView : UserControl
{
    private enum Mode { None, TextSelect, DrawDrag, DrawPoly, MoveAnn, ResizeAnn }

    private PageViewModel? _current;
    private Mode _mode;
    private bool _dragged;
    private PdfPoint _startPage;
    private PdfPoint _lastPage;
    private int _resizeId = -1;
    private bool _gestureChanged;
    private PdfAnnotationModel? _active;
    private PdfAnnotationModel? _editing;
    private string _editingOriginal = "";

    public PageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        TextEditor.LostFocus += (_, _) => CommitEditor();
        TextEditor.KeyDown += OnEditorKeyDown;
    }

    private DocumentViewModel? Owner => _current?.Owner;
    private EditorTool Tool => _current?.Owner.CurrentTool ?? EditorTool.Hand;
    private double Scale => _current?.Owner.Scale ?? 1;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_current is not null && this.IsAttachedToVisualTree())
            _current.SetRealized(false);
        _current = DataContext as PageViewModel;
        if (_current is not null && this.IsAttachedToVisualTree())
            _current.SetRealized(true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _current?.SetRealized(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _current?.SetRealized(false);
    }

    // ---------- coordinate helpers ----------

    private PdfPoint ToPage(Point p) => _current!.Transform.ToPage(p);
    private Point ToDip(PdfPoint p) => _current!.Transform.ToDip(p);
    private Point AreaPos(PointerEventArgs e) => e.GetPosition(PageArea);

    // ---------- pointer ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_current is null) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        Focus();
        var pos = AreaPos(e);
        var page = ToPage(pos);
        var tool = Tool;

        // Discard an in-progress polyline if the tool changed.
        if (_mode == Mode.DrawPoly && tool != EditorTool.Polyline) { _current.SetDraft(null); _mode = Mode.None; }

        switch (tool)
        {
            case EditorTool.Hand:
            case EditorTool.Zoom:
                return; // pan handled by DocumentView

            case EditorTool.Select:
                SelectPress(pos, page, e);
                break;

            case EditorTool.Highlight:
                BeginTextSelect(page, e);
                break;

            case EditorTool.Line:
                BeginDraw(new LineAnnotation { PageIndex = _current.PageIndex, Start = page, End = page }, page, e);
                break;
            case EditorTool.Arrow:
                BeginDraw(new LineAnnotation { PageIndex = _current.PageIndex, Start = page, End = page, Arrow = true }, page, e);
                break;
            case EditorTool.Rectangle:
                BeginDraw(new SquareAnnotation { PageIndex = _current.PageIndex, Rect = new TextRect(page.X, page.Y, page.X, page.Y) }, page, e);
                break;
            case EditorTool.TextBox:
                BeginDraw(new FreeTextAnnotation
                {
                    PageIndex = _current.PageIndex,
                    Rect = new TextRect(page.X, page.Y, page.X, page.Y),
                    Color = _current.Owner.TextboxFrameColor,
                    FontSize = _current.Owner.TextboxFontSize,
                    BorderOpacity = _current.Owner.TextboxFrameOpacity,
                }, page, e);
                break;
            case EditorTool.Callout:
                BeginDraw(new CalloutAnnotation
                {
                    PageIndex = _current.PageIndex,
                    Tip = page,
                    Rect = DefaultRectAt(page),
                    Color = _current.Owner.TextboxFrameColor,
                    FontSize = _current.Owner.TextboxFontSize,
                    BorderOpacity = _current.Owner.TextboxFrameOpacity,
                }, page, e);
                break;
            case EditorTool.Signature:
                BeginDraw(new SignatureAnnotation
                {
                    PageIndex = _current.PageIndex,
                    Rect = new TextRect(page.X, page.Y, page.X, page.Y),
                    SignerName = string.IsNullOrWhiteSpace(_current.Owner.SignerName) ? "Signature" : _current.Owner.SignerName,
                    Signed = System.DateTime.Now,
                    Color = _current.Owner.TextboxFrameColor,
                }, page, e);
                break;
            case EditorTool.Note:
                CreateNote(page, e);
                break;
            case EditorTool.Polyline:
                PolyPress(page, e);
                break;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_current is null) return;
        if (_mode == Mode.None) { UpdateHoverCursor(AreaPos(e)); return; }
        var page = ToPage(AreaPos(e));

        switch (_mode)
        {
            case Mode.TextSelect:
                _dragged = true;
                _current.ExtendSelection(page.X, page.Y);
                break;
            case Mode.DrawDrag:
                _dragged = true;
                UpdateDraft(page);
                break;
            case Mode.DrawPoly when _current.Draft is PolylineAnnotation poly && poly.Points.Count > 0:
                poly.Points[^1] = page;
                _current.SetDraft(poly);
                break;
            case Mode.MoveAnn when _active is not null:
                EnsureGestureUndo();
                AnnotationGeometry.Translate(_active, page.X - _lastPage.X, page.Y - _lastPage.Y);
                _lastPage = page;
                TouchActive();
                break;
            case Mode.ResizeAnn when _active is not null:
                EnsureGestureUndo();
                AnnotationGeometry.MoveHandle(_active, _resizeId, page);
                TouchActive();
                break;
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_current is null) return;

        switch (_mode)
        {
            case Mode.TextSelect:
                if (Tool == EditorTool.Highlight && _current.HasSelection)
                    _current.HighlightSelection(AnnotColor.Yellow);
                else if (!_dragged)
                    _current.ClearSelection();
                break;
            case Mode.DrawDrag:
                FinishDraw();
                break;
            // DrawPoly is finished by double-click / Enter, not release.
            case Mode.MoveAnn:
            case Mode.ResizeAnn:
                _active = null;
                break;
        }

        if (_mode != Mode.DrawPoly) { _mode = Mode.None; e.Pointer.Capture(null); }
        e.Handled = true;
    }

    // ---------- Select tool ----------

    private void SelectPress(Point pos, PdfPoint page, PointerPressedEventArgs e)
    {
        var sel = Owner!.SelectedAnnotation;
        if (sel is not null && _current!.Annotations.Contains(sel))
        {
            int id = HandleAt(sel, pos);
            if (id != int.MinValue)
            {
                _mode = Mode.ResizeAnn; _active = sel; _resizeId = id; _gestureChanged = false;
                e.Pointer.Capture(this); e.Handled = true; return;
            }
        }

        var hit = _current!.HitTestAnnotation(page.X, page.Y, 5);
        if (hit is not null)
        {
            Owner.SelectAnnotation(_current, hit);
            if (e.ClickCount == 2 && hit is FreeTextAnnotation ft) { OpenEditor(ft); return; }
            _mode = Mode.MoveAnn; _active = hit; _lastPage = page; _gestureChanged = false;
            e.Pointer.Capture(this); e.Handled = true; return;
        }

        Owner.SelectAnnotation(null, null);
        BeginTextSelect(page, e);
    }

    private void BeginTextSelect(PdfPoint page, PointerPressedEventArgs e)
    {
        _mode = Mode.TextSelect; _dragged = false;
        _current!.BeginSelection(page.X, page.Y);
        e.Pointer.Capture(this); e.Handled = true;
    }

    // ---------- drawing ----------

    private void BeginDraw(PdfAnnotationModel draft, PdfPoint page, PointerPressedEventArgs e)
    {
        _mode = Mode.DrawDrag; _startPage = page; _dragged = false;
        _current!.SetDraft(draft);
        e.Pointer.Capture(this); e.Handled = true;
    }

    private void UpdateDraft(PdfPoint page)
    {
        switch (_current!.Draft)
        {
            case LineAnnotation l: l.End = page; break;
            case SquareAnnotation s: s.Rect = RectFrom(_startPage, page); break;
            case CalloutAnnotation c: c.Rect = DefaultRectAt(page); break;
            case FreeTextAnnotation f: f.Rect = RectFrom(_startPage, page); break;
        }
        _current.SetDraft(_current.Draft);
    }

    private void FinishDraw()
    {
        var draft = _current!.Draft;
        if (draft is null) return;

        // Discard degenerate shapes; give text boxes a default size on a plain click.
        switch (draft)
        {
            case LineAnnotation l when Dist(l.Start, l.End) < 3:
                _current.SetDraft(null); return;
            case SquareAnnotation s when Area(s.Rect) < 9:
                _current.SetDraft(null); return;
            case SignatureAnnotation sig when Area(sig.Rect) < 400:
                sig.Rect = new TextRect(sig.Rect.Left, sig.Rect.Top - 72, sig.Rect.Left + 240, sig.Rect.Top);
                break;
            case FreeTextAnnotation f when Area(f.Rect) < 100:
                f.Rect = DefaultRectAt(new PdfPoint(f.Rect.Left, f.Rect.Top)); break;
        }

        var ann = _current.CommitDraft();
        if (ann is null) return;
        Owner!.SelectAnnotation(_current, ann);
        // Switch to Select so the new annotation can be moved/resized immediately.
        Owner.SelectTool(EditorTool.Select);
        // Signatures get their note from the properties panel, not an inline editor.
        if (ann is FreeTextAnnotation ft and not SignatureAnnotation) OpenEditor(ft, isNew: true);
    }

    private void CreateNote(PdfPoint page, PointerPressedEventArgs e)
    {
        var note = new StickyNoteAnnotation { PageIndex = _current!.PageIndex, Position = page, Contents = "" };
        _current.AddAnnotation(note);
        Owner!.SelectAnnotation(_current, note);
        Owner.SelectTool(EditorTool.Select);
        OpenNoteEditor(note);
        e.Handled = true;
    }

    // ---------- polyline ----------

    private void PolyPress(PdfPoint page, PointerPressedEventArgs e)
    {
        if (e.ClickCount >= 2) { FinishPoly(); e.Handled = true; return; }

        if (_current!.Draft is not PolylineAnnotation poly)
        {
            poly = new PolylineAnnotation { PageIndex = _current.PageIndex, Color = AnnotColor.Blue };
            poly.Points.Add(page);
            poly.Points.Add(page); // live cursor point
            _mode = Mode.DrawPoly;
            _current.SetDraft(poly);
        }
        else
        {
            poly.Points[^1] = page;
            poly.Points.Add(page); // new live point
            _current.SetDraft(poly);
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void FinishPoly()
    {
        if (_current!.Draft is PolylineAnnotation poly)
        {
            if (poly.Points.Count > 0) poly.Points.RemoveAt(poly.Points.Count - 1); // drop live point
            if (poly.Points.Count >= 2)
            {
                var ann = _current.CommitDraft();
                if (ann is not null) { Owner!.SelectAnnotation(_current, ann); Owner.SelectTool(EditorTool.Select); }
            }
            else _current.SetDraft(null);
        }
        _mode = Mode.None;
    }

    // ---------- keyboard ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Delete or Key.Back when Owner?.SelectedAnnotation is not null:
                Owner.DeleteSelectedAnnotation(); e.Handled = true; break;
            case Key.Enter when _mode == Mode.DrawPoly:
                FinishPoly(); e.Handled = true; break;
            case Key.Escape when _mode == Mode.DrawPoly:
                _current?.SetDraft(null); _mode = Mode.None; e.Handled = true; break;
            case Key.Escape when Owner?.SelectedAnnotation is not null:
                Owner.SelectAnnotation(null, null); e.Handled = true; break;
        }
    }

    // ---------- inline text editing ----------

    private bool _editingIsNew;

    private void OpenEditor(FreeTextAnnotation f, bool isNew = false)
    {
        _editing = f;
        _editingIsNew = isNew;
        _editingOriginal = f.Contents ?? "";
        var r = ToDipRect(f.Rect);
        PlaceEditor(r.X, r.Y, Math.Max(60, r.Width), Math.Max(24, r.Height));
        TextEditor.Text = f.Contents ?? "";
        ShowEditor();
    }

    private void OpenNoteEditor(StickyNoteAnnotation n, bool isNew = true)
    {
        _editing = n;
        _editingIsNew = isNew;
        _editingOriginal = n.Contents ?? "";
        var icon = ToDip(new PdfPoint(n.Position.X + StickyNoteAnnotation.IconSize, n.Position.Y));
        PlaceEditor(icon.X + 4, icon.Y, 170, 70);
        TextEditor.Text = n.Contents ?? "";
        ShowEditor();
    }

    private void PlaceEditor(double x, double y, double w, double h)
    {
        Canvas.SetLeft(TextEditor, x);
        Canvas.SetTop(TextEditor, y);
        TextEditor.Width = w;
        TextEditor.Height = h;
    }

    private void ShowEditor()
    {
        TextEditor.IsVisible = true;
        TextEditor.Focus();
        TextEditor.SelectAll();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { CommitEditor(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CommitEditor(); e.Handled = true; }
    }

    private void CommitEditor()
    {
        if (_editing is null || !TextEditor.IsVisible) return;
        var ann = _editing;
        _editing = null;
        TextEditor.IsVisible = false;

        string text = TextEditor.Text ?? "";

        // Editing an existing annotation's text is separately undoable.
        if (!_editingIsNew && text != _editingOriginal)
            Owner?.BeginChange();
        ann.Contents = text;

        // Remove empty free-text/callout boxes (nothing to show).
        if (string.IsNullOrWhiteSpace(text) && ann is FreeTextAnnotation)
            _current?.RemoveAnnotation(ann);
        else
        {
            Owner?.MarkDirty();
            _current?.NotifyAnnotationChanged();
        }
        Focus();
    }

    // ---------- geometry helpers ----------

    private int HandleAt(PdfAnnotationModel sel, Point pos)
    {
        foreach (var (id, pt) in AnnotationGeometry.Handles(sel))
        {
            var d = ToDip(pt);
            if (Math.Abs(d.X - pos.X) <= 6 && Math.Abs(d.Y - pos.Y) <= 6) return id;
        }
        return int.MinValue;
    }

    private Rect ToDipRect(TextRect r) => _current!.Transform.ToDip(r);

    private static TextRect RectFrom(PdfPoint a, PdfPoint b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private static TextRect DefaultRectAt(PdfPoint topLeft)
        => new(topLeft.X, topLeft.Y - 44, topLeft.X + 150, topLeft.Y);

    private static double Dist(PdfPoint a, PdfPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Area(TextRect r) => Math.Abs(r.Width * r.Height);

    // ---------- hover cursor ----------

    private static readonly Cursor CArrow = new(StandardCursorType.Arrow);
    private static readonly Cursor CIbeam = new(StandardCursorType.Ibeam);
    private static readonly Cursor CCross = new(StandardCursorType.Cross);
    private static readonly Cursor CMove = new(StandardCursorType.SizeAll);
    private static readonly Cursor CNS = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor CWE = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor CTL = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor CTR = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor CBL = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor CBR = new(StandardCursorType.BottomRightCorner);

    private void UpdateHoverCursor(Point pos)
    {
        if (_current is null) return;
        var page = ToPage(pos);
        Cursor cursor = CArrow;
        switch (_current.Owner.CurrentTool)
        {
            case EditorTool.Hand: return; // DocumentView sets the hand cursor
            case EditorTool.Zoom:
            case EditorTool.Line or EditorTool.Rectangle or EditorTool.Arrow
                or EditorTool.Polyline or EditorTool.Note or EditorTool.TextBox
                or EditorTool.Callout or EditorTool.Signature:
                cursor = CCross; break;
            case EditorTool.Highlight:
                cursor = CIbeam; break;
            case EditorTool.Select:
                var sel = Owner!.SelectedAnnotation;
                if (sel is not null && _current.Annotations.Contains(sel))
                {
                    int id = HandleAt(sel, pos);
                    if (id != int.MinValue) { cursor = CursorForHandle(sel, id); break; }
                }
                if (_current.HitTestAnnotation(page.X, page.Y, 5) is not null) cursor = CMove;
                else if (_current.IsOverText(page.X, page.Y)) cursor = CIbeam;
                else cursor = CArrow;
                break;
        }
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
    }

    private static Cursor CursorForHandle(PdfAnnotationModel a, int id)
    {
        if (a is LineAnnotation or PolylineAnnotation) return CCross;
        if (a is CalloutAnnotation && id == AnnotationGeometry.TipHandle) return CCross;
        return id switch
        {
            0 => CBL, 1 => CNS, 2 => CBR, 3 => CWE,
            4 => CTR, 5 => CNS, 6 => CTL, 7 => CWE,
            _ => CMove,
        };
    }

    private void EnsureGestureUndo()
    {
        if (_gestureChanged) return;
        _gestureChanged = true;
        Owner?.BeginChange();
    }

    private void TouchActive()
    {
        Owner?.MarkDirty();
        _current?.NotifyAnnotationChanged();
    }
}
