using System.Collections.ObjectModel;
using NoPdf.Core.Rendering;

namespace NoPdf.App.ViewModels;

/// <summary>A bookmark entry (from the PDF outline or a user bookmark).</summary>
public sealed class BookmarkNode : ViewModelBase
{
    public string Title { get; init; } = "";
    /// <summary>Zero-based target page, or -1 if none.</summary>
    public int PageIndex { get; init; } = -1;
    public ObservableCollection<BookmarkNode> Children { get; } = new();
    public bool IsExpanded { get; set; } = true;

    public string PageLabel => PageIndex >= 0 ? (PageIndex + 1).ToString() : "";

    public static BookmarkNode FromOutline(OutlineItem item)
    {
        var node = new BookmarkNode { Title = item.Title, PageIndex = item.PageIndex };
        foreach (var child in item.Children)
            node.Children.Add(FromOutline(child));
        return node;
    }
}
