using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using NoPdf.App.Editing;
using NoPdf.App.ViewModels;
using NoPdf.Core.Annotations;
using NoPdf.Core.Text;

namespace NoPdf.App.Views;

/// <summary>
/// Transparent layer drawn on top of a page bitmap. Renders text selection,
/// annotations, the in-progress draft, and selection handles — mapping PDF page
/// space (points, origin bottom-left) to device-independent pixels (top-left).
/// </summary>
public sealed class PageOverlay : Control
{
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 51, 133, 255));
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1.5);

    private PageViewModel? _vm;
    private double _scale = 1;
    private PageTransform _t;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.OverlayInvalidated -= OnInvalidated;
        _vm = DataContext as PageViewModel;
        if (_vm is not null) _vm.OverlayInvalidated += OnInvalidated;
        InvalidateVisual();
    }

    private void OnInvalidated()
    {
        if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
        else Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public override void Render(DrawingContext context)
    {
        var vm = _vm;
        if (vm is null) return;
        _scale = vm.Owner.Scale;
        _t = vm.Transform;

        foreach (var a in vm.Annotations) DrawAnnotation(context, a);
        if (vm.Draft is not null) DrawAnnotation(context, vm.Draft);

        foreach (var q in vm.SelectionRects)
            context.FillRectangle(SelectionBrush, ToDip(q));

        var sel = vm.Owner.SelectedAnnotation;
        if (sel is not null && vm.Annotations.Contains(sel))
            DrawHandles(context, sel);
    }

    private void DrawAnnotation(DrawingContext ctx, PdfAnnotationModel a)
    {
        var color = Color.FromRgb(a.Color.R, a.Color.G, a.Color.B);
        var pen = new Pen(new SolidColorBrush(color), Math.Max(1, a.StrokeWidth * _scale));

        switch (a)
        {
            case HighlightAnnotation h:
            {
                var brush = new SolidColorBrush(Color.FromArgb(110, h.Color.R, h.Color.G, h.Color.B));
                foreach (var q in h.Quads) ctx.FillRectangle(brush, ToDip(q));
                break;
            }
            case SquareAnnotation s:
            {
                var rect = ToDip(s.Rect);
                IBrush? fill = s.Interior is { } ic
                    ? new SolidColorBrush(Color.FromRgb(ic.R, ic.G, ic.B)) : null;
                ctx.DrawRectangle(fill, pen, rect);
                break;
            }
            case CalloutAnnotation c:
            {
                var tip = P(c.Tip);
                var attach = P(ClosestPointOnRect(c.Rect, c.Knee ?? c.Tip));
                if (c.Knee is { } k) { ctx.DrawLine(pen, tip, P(k)); ctx.DrawLine(pen, P(k), attach); }
                else ctx.DrawLine(pen, tip, attach);
                DrawArrowhead(ctx, color, c.Knee is { } kk ? P(kk) : attach, tip, a.StrokeWidth);
                var rr = ToDip(c.Rect);
                ctx.DrawRectangle(null, pen, rr);
                DrawText(ctx, c, rr);
                break;
            }
            case FreeTextAnnotation f:
            {
                var rr = ToDip(f.Rect);
                if (f.Border) ctx.DrawRectangle(null, pen, rr);
                DrawText(ctx, f, rr);
                break;
            }
            case LineAnnotation l:
            {
                var p1 = P(l.Start); var p2 = P(l.End);
                ctx.DrawLine(pen, p1, p2);
                if (l.Arrow) DrawArrowhead(ctx, color, p1, p2, l.StrokeWidth);
                break;
            }
            case PolylineAnnotation poly:
            {
                for (int i = 0; i + 1 < poly.Points.Count; i++)
                    ctx.DrawLine(pen, P(poly.Points[i]), P(poly.Points[i + 1]));
                if (poly.Closed && poly.Points.Count > 2)
                    ctx.DrawLine(pen, P(poly.Points[^1]), P(poly.Points[0]));
                break;
            }
            case StickyNoteAnnotation n:
            {
                DrawStickyNote(ctx, n);
                break;
            }
        }
    }

    private void DrawText(DrawingContext ctx, FreeTextAnnotation f, Rect rect)
    {
        if (string.IsNullOrEmpty(f.Contents)) return;
        var ft = new FormattedText(f.Contents, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, f.FontSize * _scale,
            new SolidColorBrush(Color.FromRgb(f.TextColor.R, f.TextColor.G, f.TextColor.B)))
        {
            MaxTextWidth = Math.Max(1, rect.Width - 6),
            MaxTextHeight = Math.Max(1, rect.Height - 4),
        };
        ctx.DrawText(ft, new Point(rect.X + 3, rect.Y + 2));
    }

    private void DrawStickyNote(DrawingContext ctx, StickyNoteAnnotation n)
    {
        var r = ToDip(new TextRect(n.Position.X, n.Position.Y - StickyNoteAnnotation.IconSize,
            n.Position.X + StickyNoteAnnotation.IconSize, n.Position.Y));
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(255, 214, 92)), r, 3);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(120, 90, 0)), 1), r, 3, 3);
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(120, 90, 0)), 1);
        for (int i = 0; i < 3; i++)
        {
            double y = r.Y + 5 + i * 4 * _scale / (96.0 / 72.0);
            if (y < r.Bottom - 2)
                ctx.DrawLine(linePen, new Point(r.X + 4, y), new Point(r.Right - 4, y));
        }
    }

    private void DrawArrowhead(DrawingContext ctx, Color color, Point from, Point to, double strokeW)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.5) return;
        double ux = dx / len, uy = dy / len, px = -uy, py = ux;
        double head = (8 + strokeW * 2) * _scale, half = (3 + strokeW) * _scale;
        var bas = new Point(to.X - ux * head, to.Y - uy * head);
        var g = new StreamGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(to, true);
            gc.LineTo(new Point(bas.X + px * half, bas.Y + py * half));
            gc.LineTo(new Point(bas.X - px * half, bas.Y - py * half));
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(color), null, g);
    }

    private void DrawHandles(DrawingContext ctx, PdfAnnotationModel a)
    {
        const double s = 4;
        foreach (var (_, pt) in AnnotationGeometry.Handles(a))
        {
            var c = P(pt);
            ctx.DrawRectangle(HandleFill, HandlePen, new Rect(c.X - s, c.Y - s, s * 2, s * 2));
        }
    }

    private static PdfPoint ClosestPointOnRect(TextRect rc, PdfPoint p)
    {
        double x = Math.Clamp(p.X, rc.Left, rc.Right);
        double y = Math.Clamp(p.Y, rc.Bottom, rc.Top);
        double dl = Math.Abs(x - rc.Left), dr = Math.Abs(x - rc.Right);
        double db = Math.Abs(y - rc.Bottom), dt = Math.Abs(y - rc.Top);
        double min = Math.Min(Math.Min(dl, dr), Math.Min(db, dt));
        if (min == dl) x = rc.Left; else if (min == dr) x = rc.Right;
        else if (min == db) y = rc.Bottom; else y = rc.Top;
        return new PdfPoint(x, y);
    }

    private Point P(PdfPoint p) => _t.ToDip(p);

    private Rect ToDip(TextRect q) => _t.ToDip(q);
}
