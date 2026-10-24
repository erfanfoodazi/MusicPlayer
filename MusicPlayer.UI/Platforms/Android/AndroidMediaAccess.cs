#if ANDROID
using Android.Content.PM;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Tracks the runtime audio-read permission. The first library load can start before the
/// startup permission dialog closes, so discovery waits for that dialog's result instead of
/// racing it and storing an empty library.
/// </summary>
internal static class AndroidMediaAccess
{
    private const string ReadMediaAudio = "android.permission.READ_MEDIA_AUDIO";
    private static TaskCompletionSource<bool>? _startupRequest;

    public static bool HasReadAccess
    {
        get
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                return true;
            }

            var permission = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? ReadMediaAudio
                : Android.Manifest.Permission.ReadExternalStorage;

            try
            {
                return Android.App.Application.Context.CheckSelfPermission(permission)
                       == Permission.Granted;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void BeginStartupRequest()
    {
        Interlocked.CompareExchange(
            ref _startupRequest,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            null);
    }

    public static void CompleteStartupRequest()
        => Interlocked.Exchange(ref _startupRequest, null)?.TrySetResult(HasReadAccess);

    public static async Task WaitForStartupRequestAsync(CancellationToken cancellationToken)
    {
        var request = Volatile.Read(ref _startupRequest);
        if (request is null)
        {
            return;
        }

        try
        {
            await request.Task
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A dismissed or stalled system dialog must not hang the library forever.
        }
    }
}
#endif
