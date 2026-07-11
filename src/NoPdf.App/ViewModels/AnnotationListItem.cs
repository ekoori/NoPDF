using NoPdf.Core.Annotations;

namespace NoPdf.App.ViewModels;

/// <summary>A row in the annotations panel: one annotation, its page and a label.</summary>
public sealed class AnnotationListItem
{
    public PdfAnnotationModel Model { get; }
    public int PageNumber { get; }
    public bool IsSelected { get; }
    public string TypeName { get; }
    public string Preview { get; }

    public string Display
        => Preview.Length > 0 ? $"{TypeName} — {Preview}" : TypeName;
    public string PageLabel => $"p{PageNumber}";

    public AnnotationListItem(PdfAnnotationModel m, int pageNumber, bool selected)
    {
        Model = m;
        PageNumber = pageNumber;
        IsSelected = selected;
        TypeName = m switch
        {
            HighlightAnnotation => "Highlight",
            SquareAnnotation => "Rectangle",
            ImageAnnotation => "Image",
            LineAnnotation { Arrow: true } => "Arrow",
            LineAnnotation => "Line",
            PolylineAnnotation { Closed: true } => "Polygon",
            PolylineAnnotation => "Polyline",
            SignatureAnnotation => "Signature",
            CalloutAnnotation => "Callout",
            FreeTextAnnotation => "Text box",
            StickyNoteAnnotation => "Note",
            _ => "Annotation",
        };
        var c = (m.Contents ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        Preview = c.Length > 40 ? c[..40] + "…" : c;
    }
}
