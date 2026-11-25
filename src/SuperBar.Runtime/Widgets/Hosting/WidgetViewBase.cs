using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using SuperBar.Core.Mvvm;

namespace SuperBar.Runtime.Widgets;

/// <summary>
/// Base class of a widget view. It listens to its sources (the widget's presentation model and, when the view
/// needs live metrics, the <see cref="Theming.TaskbarTheme"/>) only while it is in the visual tree, and calls
/// <see cref="Refresh"/> whenever one of them changes. A view that leaves the bar (hidden zone, fault
/// placeholder, removed widget) therefore holds no subscriptions; the bar also disposes it when it is replaced.
/// The Loaded / Unloaded ordering rules live in <see cref="LoadedSubscription"/>, which is tested headlessly.
/// </summary>
public abstract partial class WidgetViewBase : UserControl, IDisposable
{
    private readonly LoadedSubscription _subscription;

    protected WidgetViewBase(params INotifyPropertyChanged[] sources)
    {
        _subscription = new LoadedSubscription(sources, Refresh);
        IsTabStop = false;
        Loaded += (_, _) => _subscription.OnLoaded();
        Unloaded += (_, _) => _subscription.OnUnloaded(stillLoaded: IsLoaded);
    }

    /// <summary>Copies the current state of the sources into the controls. Called on the UI thread.</summary>
    protected abstract void Refresh();

    public void Dispose()
    {
        _subscription.Dispose();
        GC.SuppressFinalize(this);
    }
}
