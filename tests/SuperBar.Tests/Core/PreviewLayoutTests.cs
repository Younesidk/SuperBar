using SuperBar.Core.Geometry;
using SuperBar.Core.Layout;

namespace SuperBar.Tests.Core;

public class PreviewLayoutTests
{
    [Fact]
    public void Wide_window_is_limited_by_width_and_keeps_its_aspect()
    {
        var (w, h) = PreviewLayout.FitThumbnail(new PixelSize(1920, 1080), 200, 120);

        Assert.Equal(200, w, 3);
        Assert.Equal(112.5, h, 3);
    }

    [Fact]
    public void Tall_window_is_limited_by_height()
    {
        var (w, h) = PreviewLayout.FitThumbnail(new PixelSize(600, 1200), 200, 120);

        Assert.Equal(60, w, 3);
        Assert.Equal(120, h, 3);
    }

    [Fact]
    public void Unknown_size_gets_a_16_by_10_box() =>
        Assert.Equal((192.0, 120.0), PreviewLayout.FitThumbnail(default, 200, 120));

    [Fact]
    public void Narrow_thumbnails_still_get_room_for_the_title()
    {
        var slot = Assert.Single(PreviewLayout.Fit([new PixelSize(600, 1200)], 200, 120, 140, 16, 4, 2000));

        Assert.Equal(60, slot.ThumbnailWidth, 3);
        Assert.Equal(140, slot.ContentWidth, 3);
    }

    [Fact]
    public void Cards_shrink_together_when_they_do_not_fit()
    {
        var sources = Enumerable.Repeat(new PixelSize(1600, 1000), 10).ToList();

        var slots = PreviewLayout.Fit(sources, 200, 120, 140, 16, 4, 1000);

        var total = slots.Sum(s => s.ContentWidth) + 16 * 10 + 4 * 9;
        Assert.True(total <= 1000.001, $"total {total}");
        Assert.All(slots, s => Assert.Equal(slots[0].ThumbnailWidth, s.ThumbnailWidth, 3));
    }

    [Fact]
    public void Popup_is_centered_above_the_button()
    {
        var r = PreviewLayout.Place(500, 1028, 240, 180, 8, new ScreenRect(0, 0, 1920, 1080), 8);

        Assert.Equal(new ScreenRect(380, 840, 620, 1020), r);
    }

    [Fact]
    public void Popup_stays_on_screen_near_the_edges()
    {
        var monitor = new ScreenRect(0, 0, 1920, 1080);

        Assert.Equal(8, PreviewLayout.Place(30, 1028, 240, 180, 8, monitor, 8).Left);
        Assert.Equal(1920 - 8, PreviewLayout.Place(1900, 1028, 240, 180, 8, monitor, 8).Right);
    }
}
