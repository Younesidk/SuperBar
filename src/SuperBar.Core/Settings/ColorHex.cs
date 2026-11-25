using System.Globalization;

namespace SuperBar.Core.Settings;

/// <summary>An ARGB color parsed from settings, independent of any UI framework.</summary>
public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    public ArgbColor WithAlpha(byte alpha) => this with { A = alpha };
}

/// <summary>Parses the color strings stored in settings: <c>#RGB</c>, <c>#RRGGBB</c> and <c>#AARRGGBB</c>.</summary>
public static class ColorHex
{
    public static bool TryParse(string? text, out ArgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return false;
        }

        color = new ArgbColor((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        return true;
    }

    public static ArgbColor ParseOrDefault(string? text, ArgbColor fallback) => TryParse(text, out var c) ? c : fallback;
}
