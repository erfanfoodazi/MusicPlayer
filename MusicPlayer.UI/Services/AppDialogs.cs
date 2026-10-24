using MusicPlayer.UI.Views;

namespace MusicPlayer.UI.Services;

/// <summary>One row inside the app's bottom action sheet.</summary>
public sealed class AppSheetAction
{
    public required string Label { get; init; }
    public string Glyph { get; init; } = "\ue5cc";
    public string? Description { get; init; }
    public bool IsDestructive { get; init; }
    public bool IsSelected { get; init; }
    public int Index { get; set; }
}

/// <summary>
/// Styled replacements for the stock action sheets and alert dialogs. Both are modal pages
/// so they look identical on Android and Windows and don't inherit either platform's dated
/// dialog chrome.
/// </summary>
public static class AppDialogs
{
    public static async Task<int> ShowActionsAsync(
        Page? host,
        string title,
        IReadOnlyList<AppSheetAction> actions,
        string? subtitle = null)
    {
        host ??= Shell.Current?.CurrentPage;
        if (host is null)
        {
            return -1;
        }

        for (var i = 0; i < actions.Count; i++)
        {
            actions[i].Index = i;
        }

        var page = new ActionSheetPage(title, subtitle, actions);
        await host.Navigation.PushModalAsync(page, false);
        return await page.Result;
    }

    public static async Task<bool> ConfirmAsync(
        Page? host,
        string title,
        string message,
        string confirmText = "Delete",
        string cancelText = "Cancel",
        bool destructive = true)
    {
        host ??= Shell.Current?.CurrentPage;
        if (host is null)
        {
            return false;
        }

        var page = new ConfirmDialogPage(title, message, confirmText, cancelText, destructive);
        await host.Navigation.PushModalAsync(page, false);
        return await page.Result;
    }

    /// <summary>Styled text prompt. Resolves with the entered text, or null when cancelled.</summary>
    public static async Task<string?> PromptAsync(
        Page? host,
        string title,
        string confirmText = "Create",
        string? hint = null,
        string? initialValue = null)
    {
        host ??= Shell.Current?.CurrentPage;
        if (host is null)
        {
            return null;
        }

        var page = new InputDialogPage(title, confirmText, hint, initialValue);
        await host.Navigation.PushModalAsync(page, false);
        return await page.Result;
    }

    public static async Task ShowAlertAsync(
        Page? host,
        string title,
        string message,
        string buttonText = "OK")
    {
        host ??= Shell.Current?.CurrentPage;
        if (host is null)
        {
            return;
        }

        var page = new ConfirmDialogPage(title, message, buttonText, "Cancel", destructive: false, showCancel: false);
        await host.Navigation.PushModalAsync(page, false);
        await page.Result;
    }
}
