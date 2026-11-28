using SuperBar.Core.Shell;
using SuperBar.Infrastructure.Persistence;
using SuperBar.Windows.Apps;

namespace SuperBar.Tests.Windows;

public sealed class TaskbandFavoritesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SuperBar.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>An ID list of one item with <paramref name="payload"/> bytes, then the terminator.</summary>
    private static byte[] IdList(int payload)
    {
        var item = new byte[2 + payload + 2];
        BitConverter.GetBytes((ushort)(2 + payload)).CopyTo(item, 0);
        for (var i = 0; i < payload; i++)
        {
            item[2 + i] = (byte)(i + 1);
        }

        return item;
    }

    /// <summary>The Favorites layout: version byte, then per pin a uint32 size, the ID list and a 0x00, then 0xFF.</summary>
    private static byte[] Favorites(params byte[][] idLists)
    {
        var bytes = new List<byte> { 0x00 };
        foreach (var list in idLists)
        {
            bytes.AddRange(BitConverter.GetBytes((uint)list.Length));
            bytes.AddRange(list);
            bytes.Add(0x00);
        }

        bytes[^1] = 0xFF; // the last separator is the end marker
        return bytes.ToArray();
    }

    [Fact]
    public void Splits_every_entry_in_order()
    {
        var a = IdList(18);
        var b = IdList(40);
        var c = IdList(3);

        var entries = TaskbandFavorites.Split(Favorites(a, b, c), out var wellFormed);

        Assert.True(wellFormed);
        Assert.Equal(3, entries.Count);
        Assert.Equal(a, entries[0]);
        Assert.Equal(b, entries[1]);
        Assert.Equal(c, entries[2]);
    }

    [Fact]
    public void A_truncated_value_keeps_the_entries_before_the_damage()
    {
        var value = Favorites(IdList(10), IdList(10));

        var entries = TaskbandFavorites.Split(value.AsSpan(0, value.Length - 8), out var wellFormed);

        Assert.False(wellFormed);
        Assert.Single(entries);
    }

    [Fact]
    public void An_entry_whose_items_overrun_it_is_rejected()
    {
        var broken = IdList(10);
        broken[0] = 0xF0; // cb larger than the entry

        Assert.Empty(TaskbandFavorites.Split(Favorites(broken), out var wellFormed));
        Assert.False(wellFormed);
    }

    [Fact]
    public void An_empty_taskband_is_well_formed() =>
        Assert.Empty(TaskbandFavorites.Split([0x00, 0xFF], out _));

    [Fact]
    public void Pin_icons_round_trip_and_unchanged_icons_are_not_rewritten()
    {
        var store = new FilePinIconStore(_directory);
        var pixels = new byte[8 * 8 * 4];
        Random.Shared.NextBytes(pixels);
        var icon = new IconPixels(8, 8, pixels);

        Assert.True(store.Save("aumid:app", icon));
        Assert.False(store.Save("aumid:app", icon));
        var loaded = store.Load("aumid:app");

        Assert.NotNull(loaded);
        Assert.Equal(pixels, loaded.Bgra);
        Assert.Null(store.Load("other"));
    }

    [Fact]
    public void A_damaged_icon_file_is_ignored()
    {
        var store = new FilePinIconStore(_directory);
        store.Save("key", new IconPixels(2, 2, new byte[16]));
        foreach (var file in Directory.GetFiles(_directory))
        {
            File.WriteAllBytes(file, [1, 2, 3]);
        }

        Assert.Null(store.Load("key"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
