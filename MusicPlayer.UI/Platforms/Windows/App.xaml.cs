using Microsoft.UI.Xaml;
using System.Diagnostics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MusicPlayer.UI.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		try
		{
			this.InitializeComponent();
			Debug.WriteLine("WinUI App.InitializeComponent() succeeded");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"WinUI App constructor error: {ex}");
			throw;
		}
		
		this.UnhandledException += App_UnhandledException;
	}

	private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
	{
		Debug.WriteLine($"WinUI UnhandledException: {e.Exception}");
		LogCrash(e.Exception);
		e.Handled = true;
	}

	/// <summary>
	/// Writes unhandled exceptions to a log file. Without this, a crash during startup is
	/// completely silent: the process exits and no window ever appears.
	/// </summary>
	private static void LogCrash(Exception ex)
	{
		try
		{
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"MusicPlayer");
			Directory.CreateDirectory(dir);

			var logPath = Path.Combine(dir, "crash.log");
			File.AppendAllText(logPath,
				$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
		}
		catch
		{
			// Never let crash logging itself take the app down.
		}
	}

	protected override MauiApp CreateMauiApp()
	{
		try
		{
			Debug.WriteLine("Calling MauiProgram.CreateMauiApp()");
			var app = MauiProgram.CreateMauiApp();
			Debug.WriteLine("MauiProgram.CreateMauiApp() succeeded");
			return app;
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"WinUI CreateMauiApp error: {ex}");
			throw;
		}
	}
}