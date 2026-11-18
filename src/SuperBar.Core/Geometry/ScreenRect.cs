namespace SuperBar.Core.Geometry;

/// <summary>
/// A rectangle in physical screen pixels (the coordinate space used by Win32 and the AppBar APIs).
/// </summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static ScreenRect FromSize(int left, int top, int width, int height) =>
        new(left, top, left + width, top + height);

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}
