using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SuperBar.Widgets;

/// <summary>
/// Base class of a widget without settings. Override <see cref="OnActivated"/> and start timers, polling and
/// subscriptions through the scope; nothing has to be stopped by hand.
/// </summary>
public abstract class WidgetBase : IWidget
{
    private bool _disposed;

    protected WidgetBase(WidgetContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
    }

    protected WidgetContext Context { get; }

    protected ILogger Logger => Context.Logger;

    public abstract object ViewModel { get; }

    /// <summary>One-time start-up work, after the settings were loaded and before the first activation.</summary>
    protected virtual Task OnInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The widget became visible. Must return immediately; background work goes through <paramref name="scope"/>.</summary>
    protected abstract void OnActivated(IWidgetScope scope);

    /// <summary>The widget was hidden or suspended; the scope has already stopped everything it started.</summary>
    protected virtual void OnDeactivated()
    {
    }

    /// <summary>Attaches the handler of a model command (see <see cref="WidgetCommand"/>).</summary>
    protected void On(WidgetCommand command, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        On(command, _ => handler());
    }

    /// <inheritdoc cref="On(WidgetCommand, Action)"/>
    protected void On(WidgetCommand command, Action<object?> handler)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);
        command.Bind(parameter => Context.Invoke(() => handler(parameter), "command"));
    }

    private protected virtual void LoadSettingsCore(JsonObject settings)
    {
    }

    private protected virtual void ApplySettingsCore(JsonObject settings)
    {
    }

    void IWidget.LoadSettings(JsonObject settings) => LoadSettingsCore(settings);

    Task IWidget.InitializeAsync(CancellationToken cancellationToken) => OnInitializeAsync(cancellationToken);

    void IWidget.Activate(IWidgetScope scope) => OnActivated(scope);

    void IWidget.Deactivate() => OnDeactivated();

    void IWidget.ApplySettings(JsonObject settings) => ApplySettingsCore(settings);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases what the widget created outside of a scope (rare).</summary>
    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>
/// Base class of a widget with typed settings and a typed presentation model.
/// <see cref="Settings"/> is populated before <see cref="WidgetBase.OnInitializeAsync"/> runs, without calling
/// <see cref="OnSettingsChanged"/>; later edits arrive through <see cref="OnSettingsChanged"/>.
/// </summary>
public abstract class WidgetBase<TSettings, TModel>(WidgetContext context, TModel model) : WidgetBase(context)
    where TSettings : class, new()
    where TModel : class
{
    public TModel Model { get; } = model ?? throw new ArgumentNullException(nameof(model));

    public sealed override object ViewModel => Model;

    /// <summary>The current settings of this instance. Treat as read-only.</summary>
    protected TSettings Settings { get; private set; } = new();

    /// <summary>The user changed the settings; <see cref="Settings"/> already holds the new values.</summary>
    protected virtual void OnSettingsChanged(TSettings previous)
    {
    }

    private protected sealed override void LoadSettingsCore(JsonObject settings) =>
        Settings = WidgetSettingsBinder.Bind<TSettings>(settings, Logger);

    private protected sealed override void ApplySettingsCore(JsonObject settings)
    {
        var previous = Settings;
        Settings = WidgetSettingsBinder.Bind<TSettings>(settings, Logger);
        OnSettingsChanged(previous);
    }
}
