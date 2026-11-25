using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBar.Widgets;

/// <summary>
/// Shows a property of a widget's settings class in the Control Center's generated settings form.
/// Supported types: <c>bool</c> (switch), numbers (slider when both <see cref="Minimum"/> and <see cref="Maximum"/>
/// are set, number box otherwise), enums (drop-down; label each member with <see cref="DescriptionAttribute"/>)
/// and <c>string</c> (text box).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SettingAttribute(string label) : Attribute
{
    public string Label { get; } = label;

    public string Description { get; init; } = "";

    public double Minimum { get; init; } = double.NaN;

    public double Maximum { get; init; } = double.NaN;

    public double Step { get; init; } = double.NaN;

    /// <summary>Shown after numbers, for example "min" or "%".</summary>
    public string Unit { get; init; } = "";

    /// <summary>Position in the form; ties keep declaration order.</summary>
    public int Order { get; init; }
}

public enum SettingKind
{
    Toggle,
    Number,
    Choice,
    Text,
}

public sealed record SettingChoice(object Value, string Label);

/// <summary>One field of a generated settings form. UI-framework free.</summary>
public sealed class SettingField
{
    internal SettingField(PropertyInfo property, SettingAttribute attribute, SettingKind kind, object? defaultValue, IReadOnlyList<SettingChoice> choices)
    {
        Property = property;
        Key = WidgetSettingsBinder.KeyOf(property.Name);
        Label = attribute.Label;
        Description = attribute.Description;
        Kind = kind;
        ValueType = property.PropertyType;
        Minimum = double.IsNaN(attribute.Minimum) ? null : attribute.Minimum;
        Maximum = double.IsNaN(attribute.Maximum) ? null : attribute.Maximum;
        Step = double.IsNaN(attribute.Step) ? null : attribute.Step;
        Unit = attribute.Unit;
        Order = attribute.Order;
        DefaultValue = defaultValue;
        Choices = choices;
        IsInteger = Type.GetTypeCode(Nullable.GetUnderlyingType(ValueType) ?? ValueType) is TypeCode.Byte or TypeCode.SByte
            or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64;
    }

    /// <summary>The JSON key the value is stored under.</summary>
    public string Key { get; }

    public string Label { get; }

    public string Description { get; }

    public SettingKind Kind { get; }

    public Type ValueType { get; }

    public double? Minimum { get; }

    public double? Maximum { get; }

    public double? Step { get; }

    public string Unit { get; }

    public int Order { get; }

    /// <summary>The value a new settings object has.</summary>
    public object? DefaultValue { get; }

    /// <summary>The options of a <see cref="SettingKind.Choice"/> field, in declaration order.</summary>
    public IReadOnlyList<SettingChoice> Choices { get; }

    /// <summary>A whole-number field: values are rounded.</summary>
    public bool IsInteger { get; }

    /// <summary>Both ends of the range are known, so a slider fits.</summary>
    public bool HasRange => Minimum is not null && Maximum is not null;

    internal PropertyInfo Property { get; }
}

/// <summary>
/// The generated settings form of a settings class: its <see cref="SettingAttribute"/> properties as
/// <see cref="SettingField"/>s, plus typed reads and writes of a widget instance's options. UI-framework free.
/// </summary>
public sealed class WidgetSettingsSchema
{
    private static readonly ConcurrentDictionary<Type, WidgetSettingsSchema> Cache = new();

    private WidgetSettingsSchema(Type settingsType, IReadOnlyList<SettingField> fields)
    {
        SettingsType = settingsType;
        Fields = fields;
    }

    public Type SettingsType { get; }

    public IReadOnlyList<SettingField> Fields { get; }

    /// <summary>The schema of <paramref name="settingsType"/>. Throws when a <see cref="SettingAttribute"/> cannot be honored.</summary>
    public static WidgetSettingsSchema For(Type settingsType)
    {
        ArgumentNullException.ThrowIfNull(settingsType);
        return Cache.GetOrAdd(settingsType, Build);
    }

    /// <summary>The current value of <paramref name="field"/> in <paramref name="options"/> (the default when missing or invalid).</summary>
    public object? Read(JsonObject? options, SettingField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (options is not null
            && WidgetSettingsBinder.TryFind(options, field.Property.Name, out var node)
            && WidgetSettingsBinder.TryRead(node, field.ValueType, out var value)
            && value is not null)
        {
            return Coerce(field, value);
        }

        return field.DefaultValue;
    }

    /// <summary>Stores <paramref name="value"/> as the typed JSON value of <paramref name="field"/>: numbers are clamped and rounded.</summary>
    public void Write(JsonObject options, SettingField field, object? value)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(field);
        WidgetOptions.Set(options, field.Key, WidgetSettingsBinder.ToNode(Coerce(field, value), field.ValueType));
    }

    /// <summary>Converts a value from an editor (a double from a slider, an enum, a string) to the field's type.</summary>
    public static object? Coerce(SettingField field, object? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        var target = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;
        switch (field.Kind)
        {
            case SettingKind.Toggle:
                return value is bool b ? b : field.DefaultValue;
            case SettingKind.Text:
                return value?.ToString() ?? field.DefaultValue;
            case SettingKind.Choice:
                return value is not null && value.GetType() == target && Enum.IsDefined(target, value) ? value : field.DefaultValue;
            case SettingKind.Number:
                double number;
                try
                {
                    number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return field.DefaultValue;
                }

                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    return field.DefaultValue;
                }

                if (field.Minimum is { } min)
                {
                    number = Math.Max(number, min);
                }

                if (field.Maximum is { } max)
                {
                    number = Math.Min(number, max);
                }

                if (field.IsInteger)
                {
                    number = Math.Round(number, MidpointRounding.AwayFromZero);
                }

                return Convert.ChangeType(number, target, CultureInfo.InvariantCulture);
            default:
                return value;
        }
    }

    private static WidgetSettingsSchema Build(Type settingsType)
    {
        if (settingsType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException($"Settings type {settingsType.FullName} needs a public parameterless constructor.");
        }

        var defaults = Activator.CreateInstance(settingsType)!;
        var fields = new List<SettingField>();
        foreach (var property in settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
        {
            if (property.GetCustomAttribute<SettingAttribute>() is not { } attribute)
            {
                continue;
            }

            if (property.GetSetMethod() is null || property.GetGetMethod() is null)
            {
                throw new InvalidOperationException($"[Setting] property {settingsType.Name}.{property.Name} must have a public getter and setter.");
            }

            var kind = KindOf(property.PropertyType)
                ?? throw new InvalidOperationException($"[Setting] property {settingsType.Name}.{property.Name} has unsupported type {property.PropertyType.Name}; use bool, a number, an enum or string.");
            if (attribute.Minimum > attribute.Maximum)
            {
                throw new InvalidOperationException($"[Setting] property {settingsType.Name}.{property.Name} has Minimum above Maximum.");
            }

            var choices = kind == SettingKind.Choice ? ChoicesOf(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) : [];
            fields.Add(new SettingField(property, attribute, kind, property.GetValue(defaults), choices));
        }

        // Stable sort: equal Order keeps declaration order.
        return new WidgetSettingsSchema(settingsType, fields.OrderBy(f => f.Order).ToList());
    }

    private static SettingKind? KindOf(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(bool))
        {
            return SettingKind.Toggle;
        }

        if (target == typeof(string))
        {
            return SettingKind.Text;
        }

        if (target.IsEnum)
        {
            return SettingKind.Choice;
        }

        return Type.GetTypeCode(target) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal => SettingKind.Number,
            _ => null,
        };
    }

    private static IReadOnlyList<SettingChoice> ChoicesOf(Type enumType) =>
        enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .OrderBy(f => f.MetadataToken)
            .Select(f => new SettingChoice(f.GetValue(null)!, f.GetCustomAttribute<DescriptionAttribute>()?.Description ?? Humanize(f.Name)))
            .ToList();

    /// <summary>"TwentyFourHour" → "Twenty four hour".</summary>
    internal static string Humanize(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                text.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
