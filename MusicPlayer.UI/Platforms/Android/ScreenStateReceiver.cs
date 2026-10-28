#if ANDROID
using Android.Content;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Reports that the display was turned fully on. The receiver only observes: it never asks
/// the power manager for anything, so waking the phone stays a user action.
/// </summary>
internal sealed class ScreenStateReceiver : BroadcastReceiver
{
    private readonly Action _onScreenOn;

    public ScreenStateReceiver(Action onScreenOn) => _onScreenOn = onScreenOn;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action == Intent.ActionScreenOn)
        {
            _onScreenOn();
        }
    }
}
#endif
