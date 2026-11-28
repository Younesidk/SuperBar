using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SuperBar.Widgets;

/// <summary>
/// Decides which widget flyout is open on the bar: at most one at a time. Widgets reach it through their
/// <see cref="IWidgetFlyoutHost"/>; the host closes a widget's flyout when the widget deactivates, faults or is
/// removed. UI-framework free: the <see cref="IWidgetFlyoutSurface"/> does the showing. UI thread only.
/// <para>
/// The toggle race: a click on a widget whose flyout is open first light-dismisses the flyout (pointer down), then
/// reaches the widget, which toggles. Without care that would re-open what the user just closed. A toggle for the
/// same widget and model that arrives within <see cref="ToggleGuard"/> of a dismissal therefore counts as the close.
/// </para>
/// </summary>
public sealed class WidgetFlyoutCoordinator
{
    /// <summary>How soon after a light dismiss a toggle of the same flyout is taken as the click that dismissed it.</summary>
    public static readonly TimeSpan ToggleGuard = TimeSpan.FromMilliseconds(400);

    private readonly IWidgetFlyoutSurface? _surface;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private (string Owner, object Model, long At)? _lastDismissal;

    public WidgetFlyoutCoordinator(IWidgetFlyoutSurface? surface, TimeProvider? time = null, ILogger<WidgetFlyoutCoordinator>? logger = null)
    {
        _surface = surface;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<WidgetFlyoutCoordinator>.Instance;
        if (surface is not null)
        {
            surface.Dismissed += (_, _) => OnDismissed();
        }
    }

    /// <summary>Raised when a flyout opens or closes.</summary>
    public event EventHandler? Changed;

    /// <summary>The instance whose flyout is open, or null.</summary>
    public string? Owner { get; private set; }

    /// <summary>The model shown in the open flyout, or null.</summary>
    public object? Model { get; private set; }

    public bool IsOpenFor(string owner) => Owner == owner;

    public void Open(string owner, object model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);
        if (Owner == owner && ReferenceEquals(Model, model))
        {
            return;
        }

        _lastDismissal = null;
        bool shown;
        try
        {
            shown = _surface?.Show(owner, model) ?? true;
        }
        catch (Exception ex)
        {
            // The bar failed to show it; that is not the widget's fault.
            _logger.LogError(ex, "Could not show the flyout of widget {InstanceId}", owner);
            shown = false;
        }

        if (!shown)
        {
            Set(null, null);
            return;
        }

        Set(owner, model);
    }

    public void Toggle(string owner, object model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);
        if (Owner == owner && ReferenceEquals(Model, model))
        {
            Close(owner);
            return;
        }

        if (Owner is null
            && _lastDismissal is { } last
            && last.Owner == owner
            && ReferenceEquals(last.Model, model)
            && _time.GetElapsedTime(last.At) < ToggleGuard)
        {
            // The click that light-dismissed this very flyout: it meant "close", which already happened.
            _lastDismissal = null;
            return;
        }

        Open(owner, model);
    }

    /// <summary>Closes <paramref name="owner"/>'s flyout if it is the open one.</summary>
    public void Close(string owner)
    {
        if (Owner != owner)
        {
            return;
        }

        Set(null, null);
        try
        {
            _surface?.Hide();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not hide the flyout of widget {InstanceId}", owner);
        }
    }

    public void CloseAll()
    {
        if (Owner is { } owner)
        {
            Close(owner);
        }
    }

    private void OnDismissed()
    {
        if (Owner is not { } owner || Model is not { } model)
        {
            return;
        }

        _lastDismissal = (owner, model, _time.GetTimestamp());
        Set(null, null);
    }

    private void Set(string? owner, object? model)
    {
        var changed = Owner != owner || !ReferenceEquals(Model, model);
        Owner = owner;
        Model = model;
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>One widget's view of the coordinator: opens only while the widget is alive and active.</summary>
internal sealed class WidgetFlyout(WidgetFlyoutCoordinator coordinator, string owner, Func<bool> canOpen) : IWidgetFlyoutHost
{
    public bool IsOpen => coordinator.IsOpenFor(owner);

    public void Open(object model)
    {
        if (canOpen())
        {
            coordinator.Open(owner, model);
        }
    }

    public void Toggle(object model)
    {
        if (canOpen())
        {
            coordinator.Toggle(owner, model);
        }
        else
        {
            coordinator.Close(owner);
        }
    }

    public void Close() => coordinator.Close(owner);
}
