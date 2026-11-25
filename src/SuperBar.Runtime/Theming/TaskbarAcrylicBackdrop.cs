using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SuperBar.Runtime.Theming;

/// <summary>
/// Desktop acrylic for the taskbar, driven by a <see cref="DesktopAcrylicController"/> with our own
/// <see cref="SystemBackdropConfiguration"/>.
/// <para>
/// Why not <c>DesktopAcrylicBackdrop</c>: its default configuration follows window activation and falls back to
/// a flat color whenever the window is inactive, and a taskbar is inactive nearly all the time. Here
/// <see cref="SystemBackdropConfiguration.IsInputActive"/> is pinned to true so the blur stays on.
/// </para>
/// The controller adds blur only. The tint and <c>BackgroundOpacity</c> come from the semi-transparent surface
/// brush drawn on top (<see cref="TaskbarTheme.Surface"/>), matching the previous look.
/// </summary>
internal sealed class TaskbarAcrylicBackdrop(ILogger logger) : SystemBackdrop
{
    private readonly SystemBackdropConfiguration _configuration = new() { IsInputActive = true };
    private DesktopAcrylicController? _controller;
    private Color _fallback = Color.FromArgb(0xFF, 0x1A, 0x1D, 0x24);

    /// <summary>Updates the backdrop theme and the color used when transparency is unavailable (battery saver, effects off).</summary>
    public void Update(ElementTheme theme, Color fallbackColor)
    {
        _configuration.Theme = theme == ElementTheme.Light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        _configuration.IsInputActive = true;
        _fallback = fallbackColor;
        ApplyTint();
    }

    /// <summary>Called on window activation changes: the taskbar keeps its acrylic even when inactive.</summary>
    public void KeepInputActive() => _configuration.IsInputActive = true;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_controller is not null)
        {
            return;
        }

        if (!DesktopAcrylicController.IsSupported())
        {
            logger.LogInformation("Desktop acrylic is not supported here; the bar uses its solid surface color");
            return;
        }

        _controller = new DesktopAcrylicController();
        ApplyTint();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
        logger.LogDebug("Desktop acrylic connected");
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_controller is null)
        {
            return;
        }

        _controller.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller.Dispose();
        _controller = null;
        logger.LogDebug("Desktop acrylic disconnected");
    }

    /// <summary>
    /// Deliberately ignores the window's default configuration (which tracks activation, and whose base handler throws
    /// for a custom backdrop): this backdrop owns <see cref="_configuration"/>; theme changes arrive through <see cref="Update"/>.
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
    }

    private void ApplyTint()
    {
        if (_controller is null)
        {
            return;
        }

        // Neutral blur; the surface brush on top supplies the themed tint and the user's opacity.
        _controller.TintColor = Color.FromArgb(0xFF, _fallback.R, _fallback.G, _fallback.B);
        _controller.TintOpacity = 0f;
        _controller.LuminosityOpacity = 0.12f;
        _controller.FallbackColor = _fallback;
    }
}
