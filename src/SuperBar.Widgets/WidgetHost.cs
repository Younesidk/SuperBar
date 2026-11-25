using Microsoft.Extensions.Logging;
using SuperBar.Core.Settings;

namespace SuperBar.Widgets;

public enum WidgetState
{
    NotCreated,
    Active,
    Inactive,
    Faulted,
    Disposed,
}

/// <summary>
/// Owns one widget instance inside the taskbar: lazy creation, lifecycle transitions, width resolution
/// and fault isolation (a throwing widget is replaced by an error placeholder instead of taking down the bar).
/// </summary>
public sealed class WidgetHost : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private WidgetInstanceSettings _settings;
    private IWidget? _widget;
    private WidgetFaultPlaceholder? _faultView;

    public WidgetHost(WidgetDescriptor descriptor, WidgetInstanceSettings settings, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        Descriptor = descriptor;
        _settings = settings;
        _services = services;
        _logger = loggerFactory.CreateLogger($"SuperBar.Widget.{descriptor.TypeId}");
    }

    public WidgetDescriptor Descriptor { get; }

    public string InstanceId => _settings.InstanceId;

    public WidgetState State { get; private set; } = WidgetState.NotCreated;

    public double Width => Descriptor.ResolveWidth(_settings.Width);

    /// <summary>
    /// The content of the taskbar slot (see <see cref="IWidget.View"/>), or a <see cref="WidgetFaultPlaceholder"/>
    /// once the widget has faulted. Accessing it creates the widget on first use.
    /// </summary>
    public object View
    {
        get
        {
            EnsureCreated();
            return _widget?.View ?? _faultView!;
        }
    }

    public void Activate()
    {
        if (State is WidgetState.Disposed or WidgetState.Faulted or WidgetState.Active)
        {
            return;
        }

        EnsureCreated();
        Guard(() => _widget?.Activate(), "activate");
        if (State != WidgetState.Faulted)
        {
            State = WidgetState.Active;
        }
    }

    public void Deactivate()
    {
        if (State != WidgetState.Active)
        {
            return;
        }

        Guard(() => _widget?.Deactivate(), "deactivate");
        if (State != WidgetState.Faulted)
        {
            State = WidgetState.Inactive;
        }
    }

    public void Apply(WidgetInstanceSettings settings)
    {
        var optionsChanged = !OptionsEqual(_settings.Options, settings.Options);
        _settings = settings;
        if (optionsChanged && _widget is not null)
        {
            Guard(() => _widget.ApplyOptions(settings.Options), "apply options");
        }
    }

    private void EnsureCreated()
    {
        if (_widget is not null || _faultView is not null || State == WidgetState.Disposed)
        {
            return;
        }

        try
        {
            _widget = Descriptor.Factory(new WidgetContext(InstanceId, _settings.Options, _services, _logger));
            State = WidgetState.Inactive;
            _logger.LogDebug("Widget {InstanceId} created", InstanceId);
        }
        catch (Exception ex)
        {
            Fault(ex, "create");
        }
    }

    private void Guard(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Fault(ex, operation);
        }
    }

    private void Fault(Exception ex, string operation)
    {
        _logger.LogError(ex, "Widget {InstanceId} failed to {Operation}; showing placeholder", InstanceId, operation);
        try
        {
            _widget?.Dispose();
        }
        catch
        {
            // already faulted
        }

        _widget = null;
        _faultView ??= new WidgetFaultPlaceholder(Descriptor.DisplayName);
        State = WidgetState.Faulted;
    }

    private static bool OptionsEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public void Dispose()
    {
        if (State == WidgetState.Disposed)
        {
            return;
        }

        Deactivate();
        Guard(() => _widget?.Dispose(), "dispose");
        _widget = null;
        State = WidgetState.Disposed;
    }
}

/// <summary>
/// Shown in place of a widget that threw. A plain model (no UI types), so fault isolation works and is testable
/// without a UI thread; the taskbar renders it as a subtle "Widget error" label.
/// </summary>
public sealed class WidgetFaultPlaceholder(string widgetName)
{
    public string WidgetName { get; } = widgetName;

    public string Message => "Widget error";
}
