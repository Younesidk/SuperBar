using System.Windows.Input;

namespace SuperBar.Widgets;

/// <summary>
/// The one way a view talks back to its widget: the presentation model exposes commands, the widget attaches
/// the handlers with <see cref="WidgetBase.On(WidgetCommand, Action)"/> and the view binds them (or calls
/// <see cref="Execute"/>). The model stays free of widget and service references, and handlers run behind the
/// host's fault guard.
/// <code>
/// public sealed class PomodoroModel : ObservableObject { public WidgetCommand Toggle { get; } = new(); }
/// // in the widget's constructor:
/// On(Model.Toggle, () => _running = !_running);
/// </code>
/// </summary>
public sealed class WidgetCommand : ICommand
{
    private Action<object?>? _handler;
    private bool _isEnabled = true;

    public event EventHandler? CanExecuteChanged;

    /// <summary>Lets the widget grey the command out.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool CanExecute(object? parameter = null) => _isEnabled && _handler is not null;

    public void Execute(object? parameter = null)
    {
        if (CanExecute(parameter))
        {
            _handler!(parameter);
        }
    }

    /// <summary>Replaces the handler (never adds a second one); null unbinds the command and disables it.</summary>
    internal void Bind(Action<object?>? handler)
    {
        _handler = handler;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
