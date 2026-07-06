using System.Collections.Generic;

namespace NoPdf.Core.Rendering;

/// <summary>A node in a document's bookmark/outline tree.</summary>
public sealed class OutlineItem
{
    public required string Title { get; init; }
    /// <summary>Zero-based destination page, or -1 if the bookmark has no page target.</summary>
    public required int PageIndex { get; init; }
    public List<OutlineItem> Children { get; } = new();
}
