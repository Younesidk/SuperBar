using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SuperBar.Widgets;

/// <summary>
/// Converts between a widget instance's persisted JSON object and its typed settings class.
/// <para>
/// Reading is per property and forgiving: a missing key keeps the property's default, and a value that cannot
/// be converted keeps the default for that property only (and is logged) — a bad value never throws and never
/// resets the other settings. Values stored as strings by the first settings format
/// (<c>"true"</c>, <c>"25"</c>, <c>"Compact"</c>) are converted to the property's type.
/// </para>
/// <para>Writing produces typed JSON with camelCase keys and enum names.</para>
/// </summary>
public static class WidgetSettingsBinder
{
    private static readonly JsonSerializerOptions Lenient = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions Writing = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TSettings Bind<TSettings>(JsonObject? source, ILogger? logger = null)
        where TSettings : class, new() =>
        (TSettings)Bind(typeof(TSettings), source, logger);

    public static object Bind(Type settingsType, JsonObject? source, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settingsType);
        var settings = Activator.CreateInstance(settingsType)
            ?? throw new InvalidOperationException($"{settingsType.FullName} needs a public parameterless constructor.");
        if (source is null || source.Count == 0)
        {
            return settings;
        }

        foreach (var property in WritableProperties(settingsType))
        {
            if (!TryFind(source, property.Name, out var node) || node is null)
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
                    property.Name, settingsType.Name, node.ToJsonString(), property.PropertyType.Name);
            }
        }

        return settings;
    }

    /// <summary>The JSON form of <paramref name="settings"/>: one typed value per public read-write property.</summary>
    public static JsonObject ToJson(object settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = new JsonObject();
        foreach (var property in WritableProperties(settings.GetType()))
        {
            json[KeyOf(property.Name)] = ToNode(property.GetValue(settings), property.PropertyType);
        }

        return json;
    }

    /// <summary>A deep copy, made through the JSON form.</summary>
    public static TSettings Clone<TSettings>(TSettings settings)
        where TSettings : class, new() =>
        Bind<TSettings>(ToJson(settings));

    /// <summary>The key a property is stored under (camelCase).</summary>
    public static string KeyOf(string propertyName) => JsonNamingPolicy.CamelCase.ConvertName(propertyName);

    /// <summary>The typed JSON form of one value.</summary>
    public static JsonNode? ToNode(object? value, Type type) => JsonSerializer.SerializeToNode(value, type, Writing);

    /// <summary>Reads one value as <paramref name="type"/> with the same leniency as <see cref="Bind"/>.</summary>
    public static bool TryRead(JsonNode? node, Type type, out object? value)
    {
        if (node is null)
        {
            value = null;
            return false;
        }

        return TryConvert(node, type, out value);
    }

    /// <summary>Finds <paramref name="name"/> exactly, else ignoring case (older rows used PascalCase keys).</summary>
    internal static bool TryFind(JsonObject source, string name, out JsonNode? node)
    {
        if (source.TryGetPropertyValue(name, out node) || source.TryGetPropertyValue(KeyOf(name), out node))
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

    private static IEnumerable<PropertyInfo> WritableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetSetMethod() is not null && p.GetGetMethod() is not null && p.GetIndexParameters().Length == 0);

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
            if (value is not null && (Nullable.GetUnderlyingType(type) ?? type) is { IsEnum: true } enumType && !Enum.IsDefined(enumType, value))
            {
                value = null; // a number outside the enum
            }

            return value is not null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException)
        {
            value = null;
            return false;
        }
    }
}

/// <summary>Edits a widget instance's options object.</summary>
public static class WidgetOptions
{
    /// <summary>
    /// A copy of <paramref name="current"/> with every value of <paramref name="changes"/> written over it. Keys
    /// that only differ in case are replaced too, and keys the changes do not mention are kept (for example
    /// settings written by a newer version).
    /// </summary>
    public static JsonObject Merge(JsonObject? current, JsonObject changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var merged = current is null ? new JsonObject() : (JsonObject)current.DeepClone();
        foreach (var (key, value) in changes)
        {
            Set(merged, key, value?.DeepClone());
        }

        return merged;
    }

    /// <summary>Sets <paramref name="key"/>, removing any differently-cased spelling of it first.</summary>
    public static void Set(JsonObject options, string key, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var existing in options.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            options.Remove(existing);
        }

        options[key] = value;
    }
}
