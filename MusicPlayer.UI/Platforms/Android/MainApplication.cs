using Android.App;
using Android.Runtime;

namespace MusicPlayer.UI;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
		// Install before anything else so even startup crashes are captured and shown.
		AndroidCrashLogger.Install();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
