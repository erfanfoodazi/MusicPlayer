namespace MusicPlayer.UI.Services;

/// <summary>
/// Friendly name of the route the system is currently sending audio to. The app follows the
/// Android output route instead of owning one, so this is an indicator, not a selector.
/// </summary>
public static class AudioOutput
{
    public static string CurrentName()
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            if (context.GetSystemService(Android.Content.Context.AudioService)
                is not Android.Media.AudioManager manager)
            {
                return "Phone speaker";
            }

            var devices = manager.GetDevices(Android.Media.GetDevicesTargets.Outputs);
            if (devices is not null)
            {
                foreach (var device in devices)
                {
                    switch (device.Type)
                    {
                        case Android.Media.AudioDeviceType.BluetoothA2dp:
                        case Android.Media.AudioDeviceType.BluetoothSco:
                            return device.ProductName is { Length: > 0 } bluetooth
                                ? bluetooth
                                : "Bluetooth";
                        case Android.Media.AudioDeviceType.WiredHeadset:
                        case Android.Media.AudioDeviceType.WiredHeadphones:
                            return "Wired headphones";
                        case Android.Media.AudioDeviceType.UsbDevice:
                        case Android.Media.AudioDeviceType.UsbHeadset:
                            return "USB audio";
                    }
                }
            }
        }
        catch
        {
            // A route query must never break the player.
        }

        return "Phone speaker";
#elif WINDOWS
        return "Speakers";
#else
        return "Speaker";
#endif
    }
}
