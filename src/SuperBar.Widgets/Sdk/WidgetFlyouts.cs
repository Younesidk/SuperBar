namespace SuperBar.Widgets;

/// <summary>
/// A widget's flyout: a panel that opens from its slot on the bar. The content is a presentation model; the bar
/// renders it with the view marked <c>[WidgetViewFor(typeof(TFlyoutModel))]</c>, exactly like the slot itself.
/// Only one flyout is open on the bar at a time, and a widget can only open one while it is active. Flyouts close
/// on a click elsewhere (light dismiss), on Escape, and when the widget is deactivated, faults or is removed.
/// <para>
/// Give the flyout's view an explicit <c>Width</c>: the bar needs the width before the flyout opens to keep it on
/// screen, and some controls (a CalendarView, for one) report the whole available width when measured on their own.
/// </para>
/// </summary>
public interface IWidgetFlyoutHost
{
    /// <summary>This widget's flyout is open.</summary>
    bool IsOpen { get; }

    /// <summary>Opens <paramref name="model"/>, replacing any open flyout (this widget's or another one's).</summary>
    void Open(object model);

    /// <summary>
    /// Opens <paramref name="model"/>, or closes it when it is the one already open. This is what a click on the
    /// widget should call: a click that light-dismissed the flyout on its way in does not open it again.
    /// </summary>
    void Toggle(object model);

    /// <summary>Closes this widget's flyout, if it is open.</summary>
    void Close();
}

/// <summary>
/// The UI side of flyouts, implemented by the taskbar (SuperBar.Runtime). The <see cref="WidgetFlyoutCoordinator"/>
/// decides what is open; the surface only shows and hides it. UI thread only.
/// </summary>
public interface IWidgetFlyoutSurface
{
    /// <summary>Shows <paramref name="model"/> anchored to the slot of widget <paramref name="ownerInstanceId"/>, replacing what is shown. False when it cannot be shown now.</summary>
    bool Show(string ownerInstanceId, object model);

    /// <summary>Hides the flyout. Does not raise <see cref="Dismissed"/>.</summary>
    void Hide();

    /// <summary>The user closed the flyout: a click outside it, Escape, or the bar losing the foreground.</summary>
    event EventHandler? Dismissed;
}

/// <summary>A right-click menu entry a widget adds above SuperBar's own items (<c>WidgetBase.AddMenuItem</c>).</summary>
public sealed class WidgetMenuItem
{
    internal WidgetMenuItem(string text, string? glyph, Func<bool>? isChecked)
    {
        Text = text;
        Glyph = glyph;
        IsChecked = isChecked;
    }

    public string Text { get; }

    /// <summary>Segoe Fluent Icons glyph, or null.</summary>
    public string? Glyph { get; }

    /// <summary>Set for an on/off item: read each time the menu opens.</summary>
    public Func<bool>? IsChecked { get; }

    /// <summary>Runs the item's handler behind the widget's fault guard.</summary>
    public WidgetCommand Command { get; } = new();
}

/// <summary>Implemented by widgets that add right-click menu items (every <see cref="WidgetBase"/> does).</summary>
public interface IWidgetMenu
{
    IReadOnlyList<WidgetMenuItem> MenuItems { get; }
}

/// <summary>A menu item ready to show: its state read when the menu opened.</summary>
public sealed record WidgetMenuEntry(string Text, string? Glyph, bool? IsChecked, Action Invoke);
