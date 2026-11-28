using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SuperBar.Core.Shell;

namespace SuperBar.Infrastructure.Persistence;

/// <summary>
/// Pin icons as small raw files (<c>SBIC</c>, width, height, BGRA pixels), one per pin key, written atomically. Raw
/// pixels need no image codec and load in microseconds, which is what lets the bar show its pins' icons before
/// Explorer is ready at sign-in. A missing or damaged file just means no cached icon.
/// </summary>
public sealed class FilePinIconStore(string directory) : IPinIconStore
{
    private static ReadOnlySpan<byte> Magic => "SBIC"u8;
    private const int HeaderSize = 12;

    public IconPixels? Load(string pinKey)
    {
        try
        {
            var path = PathFor(pinKey);
            return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Save(string pinKey, IconPixels icon)
    {
        var bytes = Encode(icon);
        var path = PathFor(pinKey);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return false;
        }

        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    private string PathFor(string pinKey) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pinKey)))[..32] + ".sbic");

    internal static byte[] Encode(IconPixels icon)
    {
        var bytes = new byte[HeaderSize + icon.Bgra.Length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), icon.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), icon.Height);
        icon.Bgra.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    internal static IconPixels? Decode(byte[] bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        if (width is <= 0 or > 1024 || height is <= 0 or > 1024 || bytes.Length != HeaderSize + width * height * 4)
        {
            return null;
        }

        return new IconPixels(width, height, bytes[HeaderSize..]);
    }
}
