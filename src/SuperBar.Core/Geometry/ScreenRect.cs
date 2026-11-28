namespace SuperBar.Core.Geometry;

/// <summary>
/// A rectangle in physical screen pixels (the coordinate space used by Win32 and the AppBar APIs).
/// </summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>The smallest rectangle containing both.</summary>
    public ScreenRect Union(ScreenRect other) =>
        new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public static ScreenRect FromSize(int left, int top, int width, int height) =>
        new(left, top, left + width, top + height);

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}
