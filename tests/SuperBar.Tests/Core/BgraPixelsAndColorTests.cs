using SuperBar.Core.Imaging;
using SuperBar.Core.Settings;

namespace SuperBar.Tests.Core;

public class BgraPixelsTests
{
    [Fact]
    public void Opaque_pixels_are_unchanged()
    {
        byte[] straight = [10, 20, 30, 255];
        Assert.Equal(straight, BgraPixels.Premultiply(straight));
    }

    [Fact]
    public void Transparent_pixels_lose_their_color()
    {
        // A white, fully transparent pixel must become transparent black, or it bleeds into edges when scaled.
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, BgraPixels.Premultiply([255, 255, 255, 0]));
    }

    [Fact]
    public void Translucent_pixels_are_premultiplied_with_rounding()
    {
        // 200 * 128 / 255 = 100.39 -> 100; 255 * 128 / 255 = 128; 1 * 128 / 255 = 0.5 -> 1 (rounded half up)
        Assert.Equal(new byte[] { 100, 128, 1, 128 }, BgraPixels.Premultiply([200, 255, 1, 128]));
    }

    [Fact]
    public void Trailing_partial_pixels_are_ignored()
    {
        Assert.Equal(4, BgraPixels.Premultiply([1, 2, 3, 255, 9, 9]).Length);
    }
}

public class ColorHexTests
{
    [Theory]
    [InlineData("#5B8CFF", 0xFF, 0x5B, 0x8C, 0xFF)]
    [InlineData("5b8cff", 0xFF, 0x5B, 0x8C, 0xFF)]
    [InlineData("#80102030", 0x80, 0x10, 0x20, 0x30)]
    [InlineData("#FA0", 0xFF, 0xFF, 0xAA, 0x00)]
    public void Valid_colors_are_parsed(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(ColorHex.TryParse(text, out var c));
        Assert.Equal(new ArgbColor(a, r, g, b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("blue")]
    public void Invalid_colors_fall_back(string? text)
    {
        Assert.False(ColorHex.TryParse(text, out _));
        Assert.Equal(new ArgbColor(1, 2, 3, 4), ColorHex.ParseOrDefault(text, new ArgbColor(1, 2, 3, 4)));
    }
}
