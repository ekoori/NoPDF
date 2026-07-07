namespace NoPdf.App.Editing;

/// <summary>The active pointer tool in the page viewer.</summary>
public enum EditorTool
{
    Hand,       // drag to pan
    Select,     // select text
    Zoom,       // marquee / click zoom
    Highlight,  // highlight selected text
    Note,       // sticky note
    TextBox,    // free text
    Callout,    // free text with leader line
    Line,
    Rectangle,
    Arrow,
    Polyline,
    Signature, // place a signature stamp
}
