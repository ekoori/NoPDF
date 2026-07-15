using System;
using System.Collections.Generic;

namespace NoPdf.Core.Annotations;

/// <summary>
/// Encodes an annotation's nested grouping for storage in the PDF: the group ids,
/// innermost first, as space-separated hex. The ids only have to agree with each
/// other — two annotations are grouped because they carry the same outermost id —
/// so round-tripping the literal ids is enough to rebuild the groups on load.
/// </summary>
public static class GroupPathCodec
{
    public static string Write(IReadOnlyList<Guid> path)
        => string.Join(" ", Map(path));

    private static IEnumerable<string> Map(IReadOnlyList<Guid> path)
    {
        foreach (var g in path) yield return g.ToString("N");
    }

    /// <summary>Parses what <see cref="Write"/> produced. Unparsable ids are skipped
    /// rather than throwing — a broken group is better than an unreadable document.</summary>
    public static List<Guid> Read(string? encoded)
    {
        var list = new List<Guid>();
        if (string.IsNullOrWhiteSpace(encoded)) return list;
        foreach (var tok in encoded.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Guid.TryParseExact(tok, "N", out var g)) list.Add(g);
        return list;
    }
}
