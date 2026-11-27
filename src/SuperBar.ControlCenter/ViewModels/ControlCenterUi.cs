namespace SuperBar.ControlCenter.ViewModels;

/// <summary>What the Control Center's view models ask of its window: dialogs, the clipboard, file pickers.</summary>
public interface IControlCenterUi
{
    /// <summary>Asks the user to confirm. Returns true only when they pressed the confirming button.</summary>
    /// <param name="destructive">Styles the confirming button as destructive and makes Cancel the default button.</param>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);

    /// <summary>Puts <paramref name="text"/> on the clipboard. Call on the UI thread.</summary>
    void CopyText(string text);

    // Phase 3 adds: Task<string?> PickExecutableAsync();
}
