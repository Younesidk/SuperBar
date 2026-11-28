namespace SuperBar.Core.Geometry;

/// <summary>A size in physical pixels.</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
