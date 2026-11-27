using SuperBar.Core.Shell;
using SuperBar.Infrastructure.Persistence;

namespace SuperBar.Tests.Infrastructure;

public sealed class BadgeStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SuperBar.Tests", Guid.NewGuid().ToString("N"));

    public BadgeStoreTests() => Directory.CreateDirectory(_directory);

    private string FilePath => Path.Combine(_directory, "badges.json");

    [Fact]
    public void Saved_badges_load_back_with_their_pixels()
    {
        var pixels = new byte[16 * 16 * 4];
        Random.Shared.NextBytes(pixels);
        var badge = new SavedBadge(0x640D28, 4242, new IconPixels(16, 16, pixels));

        new JsonBadgeStore(FilePath).Save([badge]);
        var loaded = Assert.Single(new JsonBadgeStore(FilePath).Load());

        Assert.Equal(badge.Window, loaded.Window);
        Assert.Equal(badge.ProcessId, loaded.ProcessId);
        Assert.Equal(16, loaded.Icon.Width);
        Assert.Equal(pixels, loaded.Icon.Bgra);
    }

    [Fact]
    public void Missing_file_means_no_badges() => Assert.Empty(new JsonBadgeStore(FilePath).Load());

    [Fact]
    public void Damaged_file_means_no_badges()
    {
        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(new JsonBadgeStore(FilePath).Load());
    }

    [Fact]
    public void Saving_an_empty_list_clears_the_badges()
    {
        var store = new JsonBadgeStore(FilePath);
        store.Save([new SavedBadge(1, 2, new IconPixels(1, 1, [1, 2, 3, 4]))]);
        store.Save([]);
        Assert.Empty(store.Load());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
