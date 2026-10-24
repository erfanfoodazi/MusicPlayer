using Windows.Storage.Pickers;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.WinUI;

/// <summary>
/// Windows folder picker. WinUI dialogs must be parented to a window handle, so we fetch the
/// native HWND from the current MAUI window and hand it to the picker before showing it -
/// otherwise the dialog opens detached (or fails) on Windows 11.
/// </summary>
public class WindowsFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync()
    {
        var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
        {
            return null;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            CommitButtonText = "Select",
        };

        // A FolderPicker must have at least one file type filter; "*" means "any folder".
        picker.FileTypeFilter.Add("*");

        // Associate the dialog with our window handle (required since Windows 11).
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
