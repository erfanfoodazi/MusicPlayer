using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;
using Application.Services;
using Domain.Interfaces;
using Infrastructure.Lyrics;
using Infrastructure.Metadata;
using Infrastructure.Persistence;
using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;
using MusicPlayer.UI.Views;

namespace MusicPlayer.UI;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Startup failures on Windows/WinUI are otherwise silent. Keep a small log so a
        // crash before the first window is diagnosable from the user's machine.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteStartupLog("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteStartupLog("TaskScheduler", e.Exception);
            e.SetObserved();
        };

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Inter-Regular.ttf", "Inter");
                fonts.AddFont("Inter-Medium.ttf", "InterMedium");
                fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
                fonts.AddFont("Inter-Bold.ttf", "InterBold");
                fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
            });

        // PlayerService exposes a singleton with a private constructor; register that exact
        // instance because controls bind to PlayerService.Instance statically.
        builder.Services.AddSingleton(_ => PlayerService.Instance);
        builder.Services.AddSingleton<LibraryService>();

        // Real library scanning (metadata + embedded artwork via TagLibSharp).
        // Artwork lives in app data (not cache) because the library snapshot references
        // these paths across launches; the OS may clear the cache directory at any time.
        var artworkCacheDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, "artwork");
        builder.Services.AddSingleton<IMetadataReader>(_ => new TagLibMetadataReader(artworkCacheDirectory));
        builder.Services.AddSingleton<IMetadataWriter, TagLibMetadataWriter>();
#if ANDROID
        builder.Services.AddSingleton<IArtworkThumbnailer, AndroidArtworkThumbnailer>();
        builder.Services.AddSingleton<IStorageAccessService, AndroidStorageAccessService>();
        builder.Services.AddSingleton<IMusicFileDiscovery, AndroidMusicFileDiscovery>();
#else
        builder.Services.AddSingleton<IArtworkThumbnailer, NoOpArtworkThumbnailer>();
        builder.Services.AddSingleton<IStorageAccessService, NoOpStorageAccessService>();
        builder.Services.AddSingleton<IMusicFileDiscovery, NoOpMusicFileDiscovery>();
#endif
        builder.Services.AddSingleton<MusicLibraryScanner>();

        // The scanned library is saved to app data so launch restores it without rescanning.
        builder.Services.AddSingleton(new LibraryJsonStore(
            Path.Combine(FileSystem.Current.AppDataDirectory, "library.json")));

        // Lyrics: local files first, then LRCLIB search. The chosen result is cached in app
        // data so it remains available offline (no scoped-storage permission required).
        var lyricsCacheDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, "lyrics");
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        builder.Services.AddSingleton<ILyricsSearchProvider, LrcLibLyricsSearchProvider>();
        builder.Services.AddSingleton(sp => new LyricsService(
            sp.GetRequiredService<ILyricsSearchProvider>(),
            lyricsCacheDirectory));

        // File-level edits (tags, delete, embedded lyrics) and the tag editor's carry-over.
        builder.Services.AddSingleton<MusicFileService>();
        builder.Services.AddSingleton<TagEditorSession>();
        builder.Services.AddSingleton<LyricsEditorSession>();
#if WINDOWS
        builder.Services.AddSingleton<LyricsFileService, WinUI.WindowsLyricsFileService>();
#else
        builder.Services.AddSingleton<LyricsFileService>();
#endif

#if WINDOWS
        builder.Services.AddSingleton<IFolderPicker, WinUI.WindowsFolderPicker>();
#elif ANDROID
        builder.Services.AddSingleton<IFolderPicker, AndroidFolderPicker>();
#else
        builder.Services.AddSingleton<IFolderPicker, NullFolderPicker>();
#endif

        builder.Services.AddTransient<LibraryViewModel>();
        builder.Services.AddSingleton<NowPlayingViewModel>();
        builder.Services.AddSingleton<LyricsViewModel>();
        builder.Services.AddTransient<TagEditorViewModel>();
        builder.Services.AddTransient<LyricsEditorViewModel>();
        builder.Services.AddTransient<SearchViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<AlbumDetailViewModel>();
        builder.Services.AddTransient<ArtistDetailViewModel>();
        builder.Services.AddTransient<PlaylistDetailViewModel>();
        builder.Services.AddTransient<AddSongsViewModel>();

        builder.Services.AddTransient<LibraryPage>();
        builder.Services.AddTransient<NowPlayingPage>();
        builder.Services.AddTransient<LockScreenPage>();
        builder.Services.AddTransient<LyricsPage>();
        builder.Services.AddTransient<TagEditorPage>();
        builder.Services.AddTransient<LyricsEditorPage>();
        builder.Services.AddTransient<SearchPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<AlbumDetailPage>();
        builder.Services.AddTransient<ArtistDetailPage>();
        builder.Services.AddTransient<PlaylistDetailPage>();
        builder.Services.AddTransient<AddSongsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

#if ANDROID
        // The native EditText underline is the single most dated-looking control on Android.
        // Remove it globally; the rounded search field supplies its own container.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("AuralisNoUnderline", (handler, _) =>
        {
            handler.PlatformView.BackgroundTintList =
                Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        });
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("AuralisNoUnderline", (handler, _) =>
        {
            handler.PlatformView.BackgroundTintList =
                Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        });
#endif

        return builder.Build();
    }

    private static void WriteStartupLog(string source, Exception? ex)
    {
        if (ex is null) return;

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "MusicPlayer-startup.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\r\n\r\n");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
