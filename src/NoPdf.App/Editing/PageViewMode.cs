namespace NoPdf.App.ViewModels;

/// <summary>How pages are laid out in the viewport.</summary>
public enum PageViewMode
{
    /// <summary>Continuous vertical scrolling, N pages across.</summary>
    Scroll,
    /// <summary>N whole pages fill the viewport; scrolls a full screen at a time.</summary>
    Full,
    /// <summary>Continuous horizontal scrolling, N rows of pages.</summary>
    ScrollH,
}
