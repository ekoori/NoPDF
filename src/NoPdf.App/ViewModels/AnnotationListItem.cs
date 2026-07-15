using System;
using Avalonia;
using NoPdf.Core.Annotations;

namespace NoPdf.App.ViewModels;

/// <summary>
/// A row in the annotations panel. Most rows are one annotation; a row with a
/// <see cref="GroupId"/> and no <see cref="Model"/> is the header of a group, and the
/// rows under it (at a greater <see cref="Depth"/>) are its members.
/// </summary>
public sealed class AnnotationListItem
{
    /// <summary>The annotation on this row, or null when this is a group header.</summary>
    public PdfAnnotationModel? Model { get; }

    /// <summary>Set on a group header row; the id its members share.</summary>
    public Guid? GroupId { get; }

    public int PageNumber { get; }
    public bool IsSelected { get; }

    /// <summary>Nesting level: 0 = ungrouped or an outermost group.</summary>
    public int Depth { get; }

    public bool IsGroupHeader => GroupId is not null;

    /// <summary>Steps each nesting level in, so the structure reads at a glance.</summary>
    public Thickness Indent => new(Depth * 12, 0, 0, 0);

    public string TypeName { get; }
    public string Preview { get; }

    public string Display => IsGroupHeader
        ? TypeName
        : Preview.Length > 0 ? $"{TypeName} — {Preview}" : TypeName;

    public string PageLabel => $"p{PageNumber}";

    /// <summary>A row for one annotation.</summary>
    public static AnnotationListItem Leaf(PdfAnnotationModel m, int pageNumber, bool selected, int depth)
        => new(m, null, pageNumber, selected, depth, NameOf(m), PreviewOf(m));

    /// <summary>A header row for a group of <paramref name="count"/> annotations.</summary>
    public static AnnotationListItem Group(Guid id, int pageNumber, bool selected, int depth, int count)
        => new(null, id, pageNumber, selected, depth, $"Group ({count})", "");

    private AnnotationListItem(PdfAnnotationModel? model, Guid? groupId, int pageNumber,
        bool selected, int depth, string typeName, string preview)
    {
        Model = model;
        GroupId = groupId;
        PageNumber = pageNumber;
        IsSelected = selected;
        Depth = depth;
        TypeName = typeName;
        Preview = preview;
    }

    private static string NameOf(PdfAnnotationModel m) => m switch
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

    private static string PreviewOf(PdfAnnotationModel m)
    {
        var c = (m.Contents ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return c.Length > 40 ? c[..40] + "…" : c;
    }
}
