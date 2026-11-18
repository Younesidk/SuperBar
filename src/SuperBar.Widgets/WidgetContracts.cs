using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace SuperBar.Widgets;

/// <summary>
/// A widget that lives inside the taskbar surface (never in its own window).
/// Lifecycle: created lazily → <see cref="Activate"/> (visible, may update) ⇄ <see cref="Deactivate"/>
/// (hidden or suspended: must stop timers) → <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IWidget : IDisposable
{
    /// <summary>The visual hosted in the widget's taskbar slot. Created once.</summary>
    Control View { get; }

    void Activate();

    void Deactivate();

    /// <summary>Applies changed per-instance options without recreating the widget.</summary>
    void ApplyOptions(IReadOnlyDictionary<string, string> options);
}

/// <summary>Static description of a widget type: identity, sizing rules and factory.</summary>
public sealed record WidgetDescriptor
{
    public WidgetDescriptor(string typeId, string displayName, string description, double defaultWidth, double minWidth, double maxWidth, Func<WidgetContext, IWidget> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (minWidth <= 0 || maxWidth < minWidth || defaultWidth < minWidth || defaultWidth > maxWidth)
        {
            throw new ArgumentException($"Invalid width range for widget '{typeId}': min {minWidth}, default {defaultWidth}, max {maxWidth}");
        }

        TypeId = typeId;
        DisplayName = displayName;
        Description = description;
        DefaultWidth = defaultWidth;
        MinWidth = minWidth;
        MaxWidth = maxWidth;
        Factory = factory;
    }

    public string TypeId { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public double DefaultWidth { get; }
    public double MinWidth { get; }
    public double MaxWidth { get; }
    public Func<WidgetContext, IWidget> Factory { get; }

    public double ResolveWidth(double? requested) =>
        requested is { } w && !double.IsNaN(w) ? Math.Clamp(w, MinWidth, MaxWidth) : DefaultWidth;
}

/// <summary>What a widget instance receives when it is created.</summary>
public sealed class WidgetContext(string instanceId, IReadOnlyDictionary<string, string> options, IServiceProvider services, ILogger logger)
{
    public string InstanceId { get; } = instanceId;
    public IReadOnlyDictionary<string, string> Options { get; } = options;
    public IServiceProvider Services { get; } = services;
    public ILogger Logger { get; } = logger;
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
