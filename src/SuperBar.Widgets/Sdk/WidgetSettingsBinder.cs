using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SuperBar.Widgets;

/// <summary>
/// Turns a widget instance's persisted JSON object into its typed settings class.
/// Binding is per property and forgiving: a missing key keeps the property's default, and a value that cannot
/// be converted keeps the default for that property only (and is logged) — a bad value never throws and never
/// resets the other settings. Values stored as strings by the first settings format
/// (<c>"true"</c>, <c>"25"</c>, <c>"Compact"</c>) are converted to the property's type.
/// </summary>
public static class WidgetSettingsBinder
{
    private static readonly JsonSerializerOptions Lenient = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TSettings Bind<TSettings>(JsonObject? source, ILogger? logger = null)
        where TSettings : class, new()
    {
        var settings = new TSettings();
        if (source is null || source.Count == 0)
        {
            return settings;
        }

        foreach (var property in typeof(TSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetSetMethod() is null || property.GetIndexParameters().Length > 0 || !TryFind(source, property.Name, out var node) || node is null)
            {
                continue;
            }

            if (TryConvert(node, property.PropertyType, out var value))
            {
                property.SetValue(settings, value);
            }
            else
            {
                logger?.LogWarning(
                    "Ignoring setting {Setting} of {SettingsType}: {Value} is not a valid {PropertyType}",
                    property.Name, typeof(TSettings).Name, node.ToJsonString(), property.PropertyType.Name);
            }
        }

        return settings;
    }

    private static bool TryFind(JsonObject source, string name, out JsonNode? node)
    {
        if (source.TryGetPropertyValue(name, out node))
        {
            return true;
        }

        foreach (var pair in source)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                node = pair.Value;
                return true;
            }
        }

        node = null;
        return false;
    }

    private static bool TryConvert(JsonNode node, Type type, out object? value)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        var text = node is JsonValue scalar && scalar.TryGetValue<string>(out var s) ? s : null;

        if (text is not null && TryConvertText(text.Trim(), target, out value))
        {
            return true;
        }

        if (target == typeof(string))
        {
            // A number or boolean stored where a string is expected.
            value = node is JsonValue ? node.ToString() : null;
            return value is not null;
        }

        if (TryDeserialize(node, type, out value))
        {
            return true;
        }

        // A list or object that the first settings format stored as JSON text.
        if (text is not null && text.Length > 0 && (text[0] == '[' || text[0] == '{'))
        {
            try
            {
                return JsonNode.Parse(text) is { } parsed && TryDeserialize(parsed, type, out value);
            }
            catch (JsonException)
            {
            }
        }

        value = null;
        return false;
    }

    private static bool TryConvertText(string text, Type target, out object? value)
    {
        value = null;
        if (target == typeof(string))
        {
            value = text;
            return true;
        }

        if (target == typeof(bool))
        {
            switch (text.ToLowerInvariant())
            {
                case "true" or "1" or "yes" or "on":
                    value = true;
                    return true;
                case "false" or "0" or "no" or "off":
                    value = false;
                    return true;
                default:
                    return false;
            }
        }

        if (target.IsEnum)
        {
            return Enum.TryParse(target, text, ignoreCase: true, out value) && Enum.IsDefined(target, value!);
        }

        if (target == typeof(TimeSpan) && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
        {
            value = span;
            return true;
        }

        return false;
    }

    private static bool TryDeserialize(JsonNode node, Type type, out object? value)
    {
        try
        {
            value = node.Deserialize(type, Lenient);
            return value is not null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException)
        {
            value = null;
            return false;
        }
    }
}
