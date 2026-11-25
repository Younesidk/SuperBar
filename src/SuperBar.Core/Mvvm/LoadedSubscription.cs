using System.ComponentModel;

namespace SuperBar.Core.Mvvm;

/// <summary>
/// Listens to a view's sources only while the view is in the visual tree, independent of the UI framework.
/// The view forwards its Loaded / Unloaded events; this class turns any order of them into exactly one or zero
/// subscriptions per source.
/// <para>
/// WinUI can deliver a stale <c>Unloaded</c> after the <c>Loaded</c> of a re-parented element (a widget moving
/// between zones), so <see cref="OnUnloaded"/> takes whether the view is still loaded and keeps listening then.
/// </para>
/// </summary>
public sealed class LoadedSubscription : IDisposable
{
    private readonly INotifyPropertyChanged[] _sources;
    private readonly Action _refresh;
    private bool _disposed;

    public LoadedSubscription(IEnumerable<INotifyPropertyChanged> sources, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(refresh);
        _sources = sources.ToArray();
        _refresh = refresh;
    }

    public bool IsAttached { get; private set; }

    /// <summary>The view entered the visual tree: listen (once) and catch up with what changed while detached.</summary>
    public void OnLoaded()
    {
        if (IsAttached || _disposed)
        {
            return;
        }

        IsAttached = true;
        foreach (var source in _sources)
        {
            source.PropertyChanged += OnSourceChanged;
        }

        _refresh();
    }

    /// <summary>The view left the visual tree, unless <paramref name="stillLoaded"/> says it was re-parented meanwhile.</summary>
    public void OnUnloaded(bool stillLoaded)
    {
        if (!stillLoaded)
        {
            Detach();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Detach();
    }

    private void Detach()
    {
        if (!IsAttached)
        {
            return;
        }

        IsAttached = false;
        foreach (var source in _sources)
        {
            source.PropertyChanged -= OnSourceChanged;
        }
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsAttached)
        {
            _refresh();
        }
    }
}
