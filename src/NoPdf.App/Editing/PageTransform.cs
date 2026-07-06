using Avalonia;
using NoPdf.Core.Annotations;
using NoPdf.Core.Text;

namespace NoPdf.App.Editing;

/// <summary>
/// Maps between PDF page space (points, origin bottom-left, unrotated) and the
/// displayed device-independent pixels (origin top-left), accounting for the
/// page's /Rotate and the current zoom scale.
/// </summary>
public readonly struct PageTransform
{
    private readonly int _rot;      // 0, 90, 180, 270
    private readonly double _uW;    // unrotated width (points)
    private readonly double _uH;    // unrotated height (points)
    private readonly double _scale; // DIP per point

    public PageTransform(int rotation, double unrotWidth, double unrotHeight, double scale)
    {
        _rot = ((rotation % 360) + 360) % 360;
        _uW = unrotWidth;
        _uH = unrotHeight;
        _scale = scale;
    }

    /// <summary>Page point → display point (top-left origin), before scaling.</summary>
    private (double x, double y) MapDisplay(double px, double py) => _rot switch
    {
        90 => (py, px),
        180 => (_uW - px, py),
        270 => (_uH - py, _uW - px),
        _ => (px, _uH - py),
    };

    private (double px, double py) MapPage(double dx, double dy) => _rot switch
    {
        90 => (dy, dx),
        180 => (_uW - dx, dy),
        270 => (_uW - dy, _uH - dx),
        _ => (dx, _uH - dy),
    };

    public Point ToDip(PdfPoint p)
    {
        var (x, y) = MapDisplay(p.X, p.Y);
        return new Point(x * _scale, y * _scale);
    }

    public PdfPoint ToPage(Point dip)
    {
        var (px, py) = MapPage(dip.X / _scale, dip.Y / _scale);
        return new PdfPoint(px, py);
    }

    /// <summary>Maps a page-space rectangle to a display rectangle (bounding box).</summary>
    public Rect ToDip(TextRect r)
    {
        var a = ToDip(new PdfPoint(r.Left, r.Bottom));
        var b = ToDip(new PdfPoint(r.Right, r.Top));
        double x = System.Math.Min(a.X, b.X), y = System.Math.Min(a.Y, b.Y);
        return new Rect(x, y, System.Math.Abs(a.X - b.X), System.Math.Abs(a.Y - b.Y));
    }
}
