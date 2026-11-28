using Microsoft.Extensions.Logging;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;

namespace SuperBar.App;

/// <summary>
/// Keeps Windows' start-up registration in line with the "Start with Windows" setting: once at start-up (which also
/// repairs a registration that points at an older install folder) and whenever the setting changes, including a
/// reset to defaults. Does nothing in a development build.
/// </summary>
internal sealed class StartWithWindowsSync(ISettingsService settings, IStartupRegistration registration, ILogger<StartWithWindowsSync> logger) : IDisposable
{
    private bool _started;

    public void Start()
    {
        if (_started || !registration.IsAvailable)
        {
            return;
        }

        _started = true;
        Apply(settings.Current.Behavior.StartWithWindows);
        settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Current.Behavior.StartWithWindows != e.Previous.Behavior.StartWithWindows)
        {
            Apply(e.Current.Behavior.StartWithWindows);
        }
    }

    private void Apply(bool enabled)
    {
        try
        {
            if (registration.IsEnabled != enabled)
            {
                registration.SetEnabled(enabled);
                logger.LogInformation("Start with Windows: {State}", enabled ? "registered" : "unregistered");
            }
        }
        catch (Exception ex)
        {
            // The setting stays as the user chose it; the next start-up tries again.
            logger.LogError(ex, "Could not update the 'Start with Windows' registration");
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            settings.Changed -= OnSettingsChanged;
        }
    }
}
