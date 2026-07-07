using System;
using System.Collections.Generic;
using NoPdf.Core.Annotations;
using NoPdf.Core.Text;

namespace NoPdf.App.Editing;

/// <summary>
/// Type-aware geometry operations on annotation models used for editing:
/// selection handles, hit-testing, moving and resizing. All coordinates are in
/// PDF page space (points, origin bottom-left).
/// </summary>
public static class AnnotationGeometry
{
    public const int TipHandle = 100;

    /// <summary>Editable handles for an annotation, as (id, page-space point).</summary>
    public static IReadOnlyList<(int id, PdfPoint pt)> Handles(PdfAnnotationModel a)
    {
        var list = new List<(int, PdfPoint)>();
        switch (a)
        {
            case LineAnnotation l:
                list.Add((0, l.Start));
                list.Add((1, l.End));
                break;
            case PolylineAnnotation p:
                for (int i = 0; i < p.Points.Count; i++) list.Add((i, p.Points[i]));
                break;
            case StickyNoteAnnotation:
                break; // move only
            case CalloutAnnotation c:
                AddRectHandles(list, c.Rect);
                list.Add((TipHandle, c.Tip));
                break;
            case SquareAnnotation s:
                AddRectHandles(list, s.Rect);
                break;
            case FreeTextAnnotation f:
                AddRectHandles(list, f.Rect);
                break;
            case HighlightAnnotation:
                break; // not resizable
        }
        return list;
    }

    private static void AddRectHandles(List<(int, PdfPoint)> list, TextRect r)
    {
        double cx = (r.Left + r.Right) / 2, cy = (r.Bottom + r.Top) / 2;
        list.Add((0, new PdfPoint(r.Left, r.Bottom)));
        list.Add((1, new PdfPoint(cx, r.Bottom)));
        list.Add((2, new PdfPoint(r.Right, r.Bottom)));
        list.Add((3, new PdfPoint(r.Right, cy)));
        list.Add((4, new PdfPoint(r.Right, r.Top)));
        list.Add((5, new PdfPoint(cx, r.Top)));
        list.Add((6, new PdfPoint(r.Left, r.Top)));
        list.Add((7, new PdfPoint(r.Left, cy)));
    }

    /// <summary>Hit-tests the annotation body within <paramref name="tol"/> page units.</summary>
    public static bool HitTest(PdfAnnotationModel a, PdfPoint p, double tol)
    {
        switch (a)
        {
            case LineAnnotation l:
                return DistToSegment(p, l.Start, l.End) <= tol + l.StrokeWidth;
            case PolylineAnnotation poly:
                for (int i = 0; i + 1 < poly.Points.Count; i++)
                    if (DistToSegment(p, poly.Points[i], poly.Points[i + 1]) <= tol + poly.StrokeWidth) return true;
                return false;
            case HighlightAnnotation h:
                foreach (var q in h.Quads) if (Inside(q, p, 0)) return true;
                return false;
            default:
                return Inside(a.Bounds, p, tol);
        }
    }

    /// <summary>Moves the whole annotation by (dx, dy) page units.</summary>
    public static void Translate(PdfAnnotationModel a, double dx, double dy)
    {
        switch (a)
        {
            case LineAnnotation l:
                l.Start = new PdfPoint(l.Start.X + dx, l.Start.Y + dy);
                l.End = new PdfPoint(l.End.X + dx, l.End.Y + dy);
                break;
            case PolylineAnnotation p:
                for (int i = 0; i < p.Points.Count; i++)
                    p.Points[i] = new PdfPoint(p.Points[i].X + dx, p.Points[i].Y + dy);
                break;
            case StickyNoteAnnotation n:
                n.Position = new PdfPoint(n.Position.X + dx, n.Position.Y + dy);
                break;
            case CalloutAnnotation c:
                // Move the text box (and its bend); the arrow tip stays anchored
                // to whatever it points at.
                c.Rect = Offset(c.Rect, dx, dy);
                if (c.Knee is { } k) c.Knee = new PdfPoint(k.X + dx, k.Y + dy);
                break;
            case SquareAnnotation s:
                s.Rect = Offset(s.Rect, dx, dy);
                break;
            case FreeTextAnnotation f:
                f.Rect = Offset(f.Rect, dx, dy);
                break;
            case HighlightAnnotation h:
                for (int i = 0; i < h.Quads.Count; i++) { }
                // Highlights are anchored to text; not translated.
                break;
        }
    }

    /// <summary>Drags a specific handle to a new page-space point.</summary>
    public static void MoveHandle(PdfAnnotationModel a, int id, PdfPoint p)
    {
        switch (a)
        {
            case LineAnnotation l:
                if (id == 0) l.Start = p; else l.End = p;
                break;
            case PolylineAnnotation poly:
                if (id >= 0 && id < poly.Points.Count) poly.Points[id] = p;
                break;
            case CalloutAnnotation c:
                if (id == TipHandle) c.Tip = p;
                else c.Rect = ResizeRect(c.Rect, id, p);
                break;
            case SquareAnnotation s:
                s.Rect = ResizeRect(s.Rect, id, p);
                break;
            case FreeTextAnnotation f:
                f.Rect = ResizeRect(f.Rect, id, p);
                break;
        }
    }

    private static TextRect ResizeRect(TextRect r, int id, PdfPoint p)
    {
        double l = r.Left, b = r.Bottom, rt = r.Right, t = r.Top;
        switch (id)
        {
            case 0: l = p.X; b = p.Y; break;   // bottom-left
            case 1: b = p.Y; break;            // bottom
            case 2: rt = p.X; b = p.Y; break;  // bottom-right
            case 3: rt = p.X; break;           // right
            case 4: rt = p.X; t = p.Y; break;  // top-right
            case 5: t = p.Y; break;            // top
            case 6: l = p.X; t = p.Y; break;   // top-left
            case 7: l = p.X; break;            // left
        }
        if (l > rt) (l, rt) = (rt, l);
        if (b > t) (b, t) = (t, b);
        return new TextRect(l, b, rt, t);
    }

    private static TextRect Offset(TextRect r, double dx, double dy)
        => new(r.Left + dx, r.Bottom + dy, r.Right + dx, r.Top + dy);

    private static bool Inside(TextRect r, PdfPoint p, double tol)
        => p.X >= r.Left - tol && p.X <= r.Right + tol && p.Y >= r.Bottom - tol && p.Y <= r.Top + tol;

    private static double DistToSegment(PdfPoint p, PdfPoint a, PdfPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double tt = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        tt = Math.Clamp(tt, 0, 1);
        double qx = a.X + tt * dx, qy = a.Y + tt * dy;
        return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
    }
}
