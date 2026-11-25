using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace SuperBar.Runtime.Widgets;

/// <summary>
/// Base class of a widget view. It listens to its sources (the widget's presentation model and, when the view
/// needs live metrics, the <see cref="Theming.TaskbarTheme"/>) only while it is in the visual tree, and calls
/// <see cref="Refresh"/> whenever one of them changes. A view that leaves the bar (hidden zone, fault
/// placeholder, removed widget) therefore holds no subscriptions; the bar also disposes it when it is replaced.
/// </summary>
public abstract partial class WidgetViewBase : UserControl, IDisposable
{
    private readonly INotifyPropertyChanged[] _sources;
    private bool _subscribed;
    private bool _disposed;

    protected WidgetViewBase(params INotifyPropertyChanged[] sources)
    {
        _sources = sources;
        IsTabStop = false;
        Loaded += (_, _) => Subscribe();

        // Re-parenting can deliver a stale Unloaded after the new Loaded: only let go when really out of the tree.
        Unloaded += (_, _) =>
        {
            if (!IsLoaded)
            {
                Unsubscribe();
            }
        };
    }

    /// <summary>Copies the current state of the sources into the controls. Called on the UI thread.</summary>
    protected abstract void Refresh();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unsubscribe();
        GC.SuppressFinalize(this);
    }

    private void Subscribe()
    {
        if (_subscribed || _disposed)
        {
            return;
        }

        _subscribed = true;
        foreach (var source in _sources)
        {
            source.PropertyChanged += OnSourceChanged;
        }

        // Catch up with whatever changed while the view was detached.
        Refresh();
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        foreach (var source in _sources)
        {
            source.PropertyChanged -= OnSourceChanged;
        }
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_disposed)
        {
            Refresh();
        }
    }
}
