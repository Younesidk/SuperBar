using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SuperBar.Core.Settings;

namespace SuperBar.Widgets;

/// <summary>
/// Static description of a widget type: identity, sizing rules and factory. Normally built from a
/// <see cref="WidgetAttribute"/> by <see cref="FromType"/>; tests construct one directly.
/// </summary>
public sealed record WidgetDescriptor
{
    public WidgetDescriptor(
        string typeId,
        string displayName,
        Func<IServiceProvider, WidgetContext, IWidget> factory,
        string description = "",
        double defaultWidth = 120,
        double minWidth = 48,
        double maxWidth = 320)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(factory);
        if (!(minWidth > 0) || !(maxWidth >= minWidth) || !(defaultWidth >= minWidth) || !(defaultWidth <= maxWidth))
        {
            throw new ArgumentException($"Invalid width range for widget '{typeId}': min {minWidth}, default {defaultWidth}, max {maxWidth}");
        }

        TypeId = typeId;
        DisplayName = displayName;
        Factory = factory;
        Description = description;
        DefaultWidth = defaultWidth;
        MinWidth = minWidth;
        MaxWidth = maxWidth;
    }

    public string TypeId { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public double DefaultWidth { get; }
    public double MinWidth { get; }
    public double MaxWidth { get; }
    public Func<IServiceProvider, WidgetContext, IWidget> Factory { get; }

    public string Glyph { get; init; } = "\uE71D";
    public WidgetSizing Sizing { get; init; } = WidgetSizing.Fixed;
    public bool ShowBackground { get; init; } = true;
    public TaskbarZone DefaultZone { get; init; } = TaskbarZone.Right;
    public bool AllowMultiple { get; init; } = true;

    /// <summary>The widget class this descriptor was discovered from; null for hand-built descriptors.</summary>
    public Type? WidgetType { get; init; }

    /// <summary>The widget's settings class (<c>TSettings</c> of <see cref="WidgetBase{TSettings, TModel}"/>), if it has one.</summary>
    public Type? SettingsType
    {
        get => _settingsType;
        init
        {
            _settingsType = value;

            // Built eagerly: a [Setting] the form cannot show fails at start-up, not when the Control Center opens.
            SettingsSchema = value is null ? null : WidgetSettingsSchema.For(value);
        }
    }

    /// <summary>The generated settings form of <see cref="SettingsType"/>; null for widgets without settings.</summary>
    public WidgetSettingsSchema? SettingsSchema { get; private init; }

    private readonly Type? _settingsType;

    public double ResolveWidth(double? requested) =>
        requested is { } w && !double.IsNaN(w) ? Math.Clamp(w, MinWidth, MaxWidth) : DefaultWidth;

    /// <summary>
    /// Builds the descriptor of a class marked with <see cref="WidgetAttribute"/>. Its instances are created with
    /// constructor injection: the <see cref="WidgetContext"/> is supplied, everything else comes from the container.
    /// </summary>
    public static WidgetDescriptor FromType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var attribute = type.GetCustomAttribute<WidgetAttribute>()
            ?? throw new ArgumentException($"{type.FullName} is not marked with [Widget].", nameof(type));
        if (!type.IsClass || type.IsAbstract || !typeof(IWidget).IsAssignableFrom(type))
        {
            throw new ArgumentException($"[Widget] type {type.FullName} must be a non-abstract class implementing IWidget.", nameof(type));
        }

        if (type.GetConstructors().Length == 0)
        {
            throw new ArgumentException($"[Widget] type {type.FullName} needs a public constructor.", nameof(type));
        }

        return new WidgetDescriptor(
            attribute.Id,
            attribute.DisplayName,
            (services, context) => (IWidget)ActivatorUtilities.CreateInstance(services, type, context),
            attribute.Description,
            attribute.DefaultWidth,
            attribute.MinWidth,
            attribute.MaxWidth)
        {
            Glyph = attribute.Glyph,
            Sizing = attribute.Sizing,
            ShowBackground = attribute.ShowBackground,
            DefaultZone = attribute.DefaultZone,
            AllowMultiple = attribute.AllowMultiple,
            WidgetType = type,
            SettingsType = SettingsTypeOf(type),
        };
    }

    private static Type? SettingsTypeOf(Type widgetType)
    {
        for (var t = widgetType.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(WidgetBase<,>))
            {
                return t.GetGenericArguments()[0];
            }
        }

        return null;
    }
}

public interface IWidgetRegistry
{
    IReadOnlyCollection<WidgetDescriptor> All { get; }

    bool TryGet(string typeId, out WidgetDescriptor descriptor);
}

public sealed class WidgetRegistry : IWidgetRegistry
{
    private readonly Dictionary<string, WidgetDescriptor> _descriptors;

    public WidgetRegistry(IEnumerable<WidgetDescriptor> descriptors)
    {
        _descriptors = new Dictionary<string, WidgetDescriptor>(StringComparer.Ordinal);
        foreach (var d in descriptors)
        {
            if (!_descriptors.TryAdd(d.TypeId, d))
            {
                throw new InvalidOperationException($"Duplicate widget type id '{d.TypeId}'");
            }
        }
    }

    public IReadOnlyCollection<WidgetDescriptor> All => _descriptors.Values;

    public bool TryGet(string typeId, out WidgetDescriptor descriptor) =>
        _descriptors.TryGetValue(typeId, out descriptor!);
}
