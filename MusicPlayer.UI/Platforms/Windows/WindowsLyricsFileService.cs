using MusicPlayer.UI.Services;
using Windows.Storage.Pickers;

namespace MusicPlayer.UI.WinUI;

/// <summary>
/// Windows export path for .lrc files: the share sheet does not exist here, so the native
/// "Save as" dialog is used instead.
/// </summary>
public sealed class WindowsLyricsFileService : LyricsFileService
{
    public override async Task<bool> ExportLrcAsync(string suggestedFileName, string content)
    {
        try
        {
            var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
            {
                return false;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(SanitizeFileName(suggestedFileName)),
            };
            picker.FileTypeChoices.Add("Lyrics file", [".lrc"]);

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return false;
            }

            await Windows.Storage.FileIO.WriteTextAsync(file, content);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
