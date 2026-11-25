using SuperBar.Core.Settings;

namespace SuperBar.Widgets;

/// <summary>How the width of a widget's taskbar slot is decided.</summary>
public enum WidgetSizing
{
    /// <summary>The slot has the configured width (default width, or the user's choice clamped to min/max).</summary>
    Fixed,

    /// <summary>The slot follows the widget's content, clamped to min/max.</summary>
    Auto,
}

/// <summary>
/// Declares a widget type. Every non-abstract <see cref="IWidget"/> class carrying this attribute is discovered
/// at start-up (<c>AddWidgetsFrom</c>) and created with constructor injection: the first constructor argument
/// is the <see cref="WidgetContext"/>, the others are resolved from the service provider.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WidgetAttribute(string typeId, string displayName) : Attribute
{
    /// <summary>Stable identity, persisted in settings (for example <c>superbar.clock</c>). Never change it.</summary>
    public string Id { get; } = typeId;

    public string DisplayName { get; } = displayName;

    public string Description { get; init; } = "";

    /// <summary>Segoe Fluent Icons glyph shown next to the widget in the Control Center.</summary>
    public string Glyph { get; init; } = "\uE71D";

    public double DefaultWidth { get; init; } = 120;

    public double MinWidth { get; init; } = 48;

    public double MaxWidth { get; init; } = 320;

    public WidgetSizing Sizing { get; init; } = WidgetSizing.Fixed;

    /// <summary>Draw the shared widget card behind the view. Turn off for widgets that are a bar button themselves.</summary>
    public bool ShowBackground { get; init; } = true;

    /// <summary>The zone a new instance is placed in.</summary>
    public TaskbarZone DefaultZone { get; init; } = TaskbarZone.Right;

    /// <summary>Whether more than one instance makes sense on the same bar.</summary>
    public bool AllowMultiple { get; init; } = true;
}
