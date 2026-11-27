using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SuperBar.ControlCenter.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace SuperBar.ControlCenter.Views;

/// <summary>
/// <see cref="IControlCenterUi"/> on top of the Control Center window. Created before the window (the view models
/// need it in their constructors) and attached by the window once its content exists.
/// </summary>
public sealed class WindowControlCenterUi : IControlCenterUi
{
    private FrameworkElement? _root;
    private bool _dialogOpen;

    /// <summary>
    /// Connects this service to the window; <paramref name="root"/> supplies the XamlRoot, theme and styles. Once the
    /// window closes, every request is refused (dialogs return false).
    /// </summary>
    public void Attach(Window window, FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(window);
        _root = root;
        window.Closed += (_, _) => _root = null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        // Only one ContentDialog may be open at a time: a second ShowAsync throws.
        if (_root?.XamlRoot is not { } xamlRoot || _dialogOpen)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = _root.ActualTheme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };

        // A ContentDialog made in code gets the legacy look unless it is given the current default style.
        if (Application.Current?.Resources is { } appResources && FindStyle(appResources, "DefaultContentDialogStyle") is { } dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        if (destructive && FindStyle(_root.Resources, "DangerAccentButton") is { } dangerStyle)
        {
            dialog.PrimaryButtonStyle = dangerStyle;
        }

        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    public void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    /// <summary>The style under <paramref name="key"/> in the dictionary or any of its merged dictionaries; null when absent.</summary>
    private static Style? FindStyle(ResourceDictionary dictionary, string key)
    {
        if (dictionary.TryGetValue(key, out var value))
        {
            return value as Style;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindStyle(merged, key) is { } style)
            {
                return style;
            }
        }

        return null;
    }
}
