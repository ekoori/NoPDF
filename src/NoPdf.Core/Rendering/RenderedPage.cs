namespace NoPdf.Core.Rendering;

/// <summary>A rasterized page as a tightly-packed BGRA8888 (straight-alpha) buffer.</summary>
public sealed class RenderedPage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    /// <summary>BGRA pixels, length == Stride * Height.</summary>
    public required byte[] Pixels { get; init; }
}
