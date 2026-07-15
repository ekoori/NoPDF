using System.Collections.Generic;
using NoPdf.Core.Text;

namespace NoPdf.Core.Annotations;

/// <summary>An sRGB colour, components 0..255.</summary>
public readonly record struct AnnotColor(byte R, byte G, byte B)
{
    public static readonly AnnotColor Yellow = new(255, 235, 60);
    public static readonly AnnotColor Red = new(229, 57, 53);
    public static readonly AnnotColor Blue = new(30, 110, 220);
    public static readonly AnnotColor Black = new(0, 0, 0);
    public double Rf => R / 255.0;
    public double Gf => G / 255.0;
    public double Bf => B / 255.0;
}

/// <summary>A point in PDF page space (points, origin bottom-left).</summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>Base for all annotations. Geometry is in PDF page space (points, origin bottom-left).</summary>
public abstract class PdfAnnotationModel
{
    public int PageIndex { get; set; }
    public AnnotColor Color { get; set; } = AnnotColor.Red;
    public double StrokeWidth { get; set; } = 2.0;
    public string? Author { get; set; }
    public string? Contents { get; set; }

    /// <summary>App-level nested grouping, innermost first / outermost last. Annotations
    /// sharing an outermost id select and move together; grouping appends a new id,
    /// ungrouping pops the outermost. Saved into the PDF under
    /// <see cref="AnnotationWriter.GroupKey"/>; other viewers ignore it.</summary>
    public System.Collections.Generic.List<System.Guid> GroupPath { get; set; } = new();

    /// <summary>Axis-aligned bounds in page space, used for hit-testing and selection.</summary>
    public abstract TextRect Bounds { get; }

    /// <summary>Deep copy (used for undo/redo snapshots).</summary>
    public abstract PdfAnnotationModel Clone();

    protected void CopyBaseTo(PdfAnnotationModel c)
    {
        c.PageIndex = PageIndex;
        c.Color = Color;
        c.StrokeWidth = StrokeWidth;
        c.Author = Author;
        c.Contents = Contents;
        c.GroupPath = new System.Collections.Generic.List<System.Guid>(GroupPath);
    }
}

/// <summary>A text-markup highlight defined by one or more quad rectangles.</summary>
public sealed class HighlightAnnotation : PdfAnnotationModel
{
    public required IReadOnlyList<TextRect> Quads { get; init; }

    public override TextRect Bounds
    {
        get
        {
            double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
            foreach (var q in Quads)
            {
                if (q.Left < l) l = q.Left;
                if (q.Bottom < b) b = q.Bottom;
                if (q.Right > r) r = q.Right;
                if (q.Top > t) t = q.Top;
            }
            return new TextRect(l, b, r, t);
        }
    }

    public override PdfAnnotationModel Clone()
    {
        var c = new HighlightAnnotation { Quads = new List<TextRect>(Quads) };
        CopyBaseTo(c);
        return c;
    }
}

/// <summary>Straight line; when <see cref="Arrow"/> is set, an arrowhead is drawn at <see cref="End"/>.</summary>
public sealed class LineAnnotation : PdfAnnotationModel
{
    public PdfPoint Start { get; set; }
    public PdfPoint End { get; set; }
    public bool Arrow { get; set; }

    public override TextRect Bounds => new(
        System.Math.Min(Start.X, End.X), System.Math.Min(Start.Y, End.Y),
        System.Math.Max(Start.X, End.X), System.Math.Max(Start.Y, End.Y));

    public override PdfAnnotationModel Clone()
    {
        var c = new LineAnnotation { Start = Start, End = End, Arrow = Arrow };
        CopyBaseTo(c);
        return c;
    }
}

/// <summary>Rectangle (PDF <c>/Square</c>).</summary>
public sealed class SquareAnnotation : PdfAnnotationModel
{
    public TextRect Rect { get; set; }
    public AnnotColor? Interior { get; set; }
    public override TextRect Bounds => Rect;

    public override PdfAnnotationModel Clone()
    {
        var c = new SquareAnnotation { Rect = Rect, Interior = Interior };
        CopyBaseTo(c);
        return c;
    }
}

/// <summary>Open polyline or, when <see cref="Closed"/>, a polygon.</summary>
public sealed class PolylineAnnotation : PdfAnnotationModel
{
    public List<PdfPoint> Points { get; set; } = new();
    public bool Closed { get; set; }

    public override TextRect Bounds
    {
        get
        {
            double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
            foreach (var p in Points)
            {
                if (p.X < l) l = p.X; if (p.X > r) r = p.X;
                if (p.Y < b) b = p.Y; if (p.Y > t) t = p.Y;
            }
            return new TextRect(l, b, r, t);
        }
    }

    public override PdfAnnotationModel Clone()
    {
        var c = new PolylineAnnotation { Points = new List<PdfPoint>(Points), Closed = Closed };
        CopyBaseTo(c);
        return c;
    }
}

/// <summary>Free-text box (PDF <c>/FreeText</c>). <see cref="PdfAnnotationModel.Contents"/> holds the text.</summary>
public class FreeTextAnnotation : PdfAnnotationModel
{
    public TextRect Rect { get; set; }
    public double FontSize { get; set; } = 12;
    public AnnotColor TextColor { get; set; } = AnnotColor.Black;
    public bool Border { get; set; } = true;
    /// <summary>Frame opacity, 0 (transparent) .. 1 (solid).</summary>
    public double BorderOpacity { get; set; } = 1.0;
    public override TextRect Bounds => Rect;

    protected void CopyFreeTextTo(FreeTextAnnotation c)
    {
        CopyBaseTo(c);
        c.Rect = Rect; c.FontSize = FontSize; c.TextColor = TextColor;
        c.Border = Border; c.BorderOpacity = BorderOpacity;
    }

    public override PdfAnnotationModel Clone()
    {
        var c = new FreeTextAnnotation();
        CopyFreeTextTo(c);
        return c;
    }
}

/// <summary>Free-text callout with a leader line ending in an arrow at <see cref="Tip"/>.</summary>
public sealed class CalloutAnnotation : FreeTextAnnotation
{
    public PdfPoint Tip { get; set; }
    public PdfPoint? Knee { get; set; }

    public override PdfAnnotationModel Clone()
    {
        var c = new CalloutAnnotation { Tip = Tip, Knee = Knee };
        CopyFreeTextTo(c);
        return c;
    }
}

/// <summary>
/// A visible signature stamp: signer name, optional note, timestamp, over a faint
/// noPDF watermark. Extends free text so it shares rect handles/hit-testing.
/// </summary>
public sealed class SignatureAnnotation : FreeTextAnnotation
{
    public string SignerName { get; set; } = "";
    public System.DateTime Signed { get; set; } = System.DateTime.Now;

    /// <summary>When true, committing this stamp triggers a cryptographic signature.</summary>
    public bool Certify { get; set; }
    public string? CertPath { get; set; }
    public string? CertPassword { get; set; }
    /// <summary>Set once the certify save flow has run, so it does not fire again.</summary>
    public bool Certified { get; set; }

    public override PdfAnnotationModel Clone()
    {
        var c = new SignatureAnnotation
        {
            SignerName = SignerName, Signed = Signed,
            Certify = Certify, CertPath = CertPath, CertPassword = CertPassword, Certified = Certified,
        };
        CopyFreeTextTo(c);
        return c;
    }
}

/// <summary>A raster image stamp (e.g. a pasted screenshot), with an optional frame
/// and adjustable opacity. <see cref="Color"/>/<see cref="PdfAnnotationModel.StrokeWidth"/> style the frame.</summary>
public sealed class ImageAnnotation : PdfAnnotationModel
{
    public TextRect Rect { get; set; }
    /// <summary>Encoded image bytes (PNG).</summary>
    public byte[] ImageData { get; set; } = System.Array.Empty<byte>();
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    /// <summary>Image opacity, 0 (transparent) .. 1 (opaque).</summary>
    public double Opacity { get; set; } = 1.0;
    /// <summary>Draw a frame around the image.</summary>
    public bool Border { get; set; }
    public override TextRect Bounds => Rect;

    public override PdfAnnotationModel Clone()
    {
        var c = new ImageAnnotation
        {
            Rect = Rect, ImageData = ImageData, PixelWidth = PixelWidth, PixelHeight = PixelHeight,
            Opacity = Opacity, Border = Border,
        };
        CopyBaseTo(c);
        return c;
    }
}

/// <summary>Sticky note icon (PDF <c>/Text</c>). <see cref="PdfAnnotationModel.Contents"/> holds the note.</summary>
public sealed class StickyNoteAnnotation : PdfAnnotationModel
{
    public const double IconSize = 20;
    public PdfPoint Position { get; set; } // top-left of the icon
    public override TextRect Bounds =>
        new(Position.X, Position.Y - IconSize, Position.X + IconSize, Position.Y);

    public override PdfAnnotationModel Clone()
    {
        var c = new StickyNoteAnnotation { Position = Position };
        CopyBaseTo(c);
        return c;
    }
}
