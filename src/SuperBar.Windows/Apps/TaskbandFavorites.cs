using System.Buffers.Binary;

namespace SuperBar.Windows.Apps;

/// <summary>
/// Splits Explorer's <c>HKCU\…\Explorer\Taskband\Favorites</c> value into the pinned items' ID lists, in taskbar order.
/// <para>
/// Layout (Windows 7 to 11): one version byte, then for each pin a little-endian <c>uint32</c> size, an absolute
/// ITEMIDLIST of that many bytes and one separator byte (<c>0x00</c>); the value ends with <c>0xFF</c>. A pin's list
/// starts with the AppsFolder item for packaged apps, or the "User Pinned" folder for the .lnk files of
/// <c>%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar</c>.
/// </para>
/// Undocumented, so parsing is defensive: a malformed value yields the entries read before the damage and
/// <c>wellFormed</c> false.
/// </summary>
internal static class TaskbandFavorites
{
    private const byte EndMarker = 0xFF;

    public static List<byte[]> Split(ReadOnlySpan<byte> value, out bool wellFormed)
    {
        var entries = new List<byte[]>();
        wellFormed = false;
        if (value.Length < 1)
        {
            return entries;
        }

        var offset = 1; // version byte
        while (offset < value.Length)
        {
            if (value[offset] == EndMarker)
            {
                wellFormed = true;
                return entries;
            }

            if (offset + 4 > value.Length)
            {
                return entries;
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(value[offset..]);
            offset += 4;
            if (size < 2 || size > value.Length - offset)
            {
                return entries;
            }

            var idList = value.Slice(offset, (int)size);
            if (!IsValidIdList(idList))
            {
                return entries;
            }

            entries.Add(idList.ToArray());
            offset += (int)size;

            // The separator byte after each entry (0x00), or the end marker.
            if (offset < value.Length && value[offset] != EndMarker)
            {
                offset++;
            }
        }

        return entries;
    }

    /// <summary>A chain of <c>SHITEMID</c>s (<c>ushort cb</c> + data) ending with a zero <c>cb</c>, inside the buffer.</summary>
    public static bool IsValidIdList(ReadOnlySpan<byte> idList)
    {
        var offset = 0;
        var items = 0;
        while (offset + 2 <= idList.Length)
        {
            var cb = BinaryPrimitives.ReadUInt16LittleEndian(idList[offset..]);
            if (cb == 0)
            {
                return items > 0;
            }

            if (cb < 2 || offset + cb > idList.Length)
            {
                return false;
            }

            offset += cb;
            items++;
        }

        return false;
    }
}
