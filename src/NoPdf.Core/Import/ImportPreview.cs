using System;
using System.Collections.Generic;

namespace NoPdf.Core.Import;

/// <summary>
/// Lets a conversion hand over pages as it produces them, so a viewer can start showing a long
/// document instead of waiting for the whole thing. Converting a big scan takes tens of
/// seconds, split about evenly between decoding pages and assembling them into a PDF, and none
/// of it is visible until the very end otherwise.
///
/// Callbacks come from worker threads and may overlap — a UI caller has to marshal them.
/// </summary>
public sealed class ImportPreview
{
    /// <summary>
    /// The page geometry in points, reported once as soon as it is known and before any page
    /// has been rendered. Lets the viewer lay out the whole document immediately, so the
    /// scrollbar and page count are right while the pages are still filling in.
    /// </summary>
    public Action<IReadOnlyList<(double WidthPt, double HeightPt)>>? Layout { get; init; }

    /// <summary>
    /// One page's encoded image (PNG or JPEG) as it becomes available, by page index. Indices
    /// refer to <see cref="Layout"/>; if a page later fails to convert, the finished document
    /// will be shorter and the viewer rebuilds from it.
    /// </summary>
    public Action<int, byte[]>? Page { get; init; }
}
