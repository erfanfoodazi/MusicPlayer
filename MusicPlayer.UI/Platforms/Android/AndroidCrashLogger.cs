using Android.OS;
using Android.Runtime;
using Android.Util;
using Microsoft.Maui.ApplicationModel;

namespace MusicPlayer.UI;

/// <summary>
/// Captures unhandled exceptions on Android and makes them visible: written to a log file
/// in the app cache AND shown in a native dialog, so a crash is never silent even without
/// adb access. The dialog offers to send the log text (via the share sheet) for diagnosis.
///
/// NOTE: the UnhandledExceptionRaiser hook marks the exception Handled so the dialog can be
/// seen. That leaves the process in an unknown state, so confirming the dialog closes the
/// app. This is a deliberate diagnostic trade-off until startup crashes are root-caused.
/// </summary>
public static class AndroidCrashLogger
{
    public static string LogFilePath { get; } = System.IO.Path.Combine(
        Android.App.Application.Context.CacheDir?.AbsolutePath
            ?? System.IO.Path.GetTempPath(),
        "crash.log");

    public static void Install()
    {
        // Java-side and most managed crashes on Android surface here first.
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            Write("AndroidEnvironment", e.Exception);
            e.Handled = true;
        };

        // Managed crashes that escape the above.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write("AppDomain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
        };

        // Faulted tasks nobody awaited.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string source, Exception ex)
    {
        try
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n";
            System.IO.File.AppendAllText(LogFilePath, entry);
            Log.Error("Auralis", ex.ToString());

            if (Platform.CurrentActivity is not { } activity) return;

            activity.RunOnUiThread(() =>
            {
                var summary = $"{ex.GetType().Name}: {ex.Message}";
                new Android.App.AlertDialog.Builder(activity)
                    .SetTitle("Auralis crashed")
                    .SetMessage($"{summary}\n\nLog saved to:\n{LogFilePath}")
                    .SetPositiveButton("Send log", (_, _) => ShareLog(activity))
                    .SetNegativeButton("Close", (_, _) =>
                    {
                        activity.FinishAffinity();
                        Java.Lang.JavaSystem.Exit(0);
                    })
                    .SetCancelable(false)
                    .Show();
            });
        }
        catch
        {
            // Crash logging must never throw.
        }
    }

    private static void ShareLog(Android.App.Activity activity)
    {
        try
        {
            var text = System.IO.File.ReadAllText(LogFilePath);
            var intent = new Android.Content.Intent(Android.Content.Intent.ActionSend);
            intent.SetType("text/plain");
            intent.PutExtra(Android.Content.Intent.ExtraText, text);
            activity.StartActivity(intent);
        }
        catch
        {
            // Ignore; the dialog log path is still shown to the user.
        }
    }
}
