namespace MusicPlayer.UI.Services;

/// <summary>
/// Haptic feedback is a nice touch but is not available on every target (and Android
/// requires the VIBRATE permission). Never let it break a tap.
/// </summary>
public static class Haptics
{
    public static void Tap()
    {
        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch
        {
            // Unsupported platform or missing permission.
        }
    }
}
