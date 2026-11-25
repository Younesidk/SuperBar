namespace SuperBar.Core.Imaging;

/// <summary>Pixel-format helpers for 32-bit BGRA buffers (pure, UI-framework independent).</summary>
public static class BgraPixels
{
    /// <summary>
    /// Converts straight (non-premultiplied) BGRA32 to premultiplied BGRA32: <c>c' = (c * a + 127) / 255</c>.
    /// UI surfaces such as WinUI's <c>WriteableBitmap</c> expect premultiplied pixels; feeding them straight alpha
    /// draws dark fringes around anti-aliased edges.
    /// </summary>
    public static byte[] Premultiply(ReadOnlySpan<byte> straight)
    {
        var length = straight.Length - straight.Length % 4;
        var result = new byte[length];
        for (var i = 0; i < length; i += 4)
        {
            int a = straight[i + 3];
            if (a == 255)
            {
                result[i] = straight[i];
                result[i + 1] = straight[i + 1];
                result[i + 2] = straight[i + 2];
            }
            else if (a != 0)
            {
                result[i] = (byte)((straight[i] * a + 127) / 255);
                result[i + 1] = (byte)((straight[i + 1] * a + 127) / 255);
                result[i + 2] = (byte)((straight[i + 2] * a + 127) / 255);
            }

            // a == 0: color channels stay 0, as premultiplication requires
            result[i + 3] = (byte)a;
        }

        return result;
    }
}
