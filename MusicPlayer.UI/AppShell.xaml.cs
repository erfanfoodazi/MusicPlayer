using MusicPlayer.UI.Views;

namespace MusicPlayer.UI;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Settings and Search are tab roots (see AppShell.xaml); everything else is pushed
        // on top of the active tab.
        Routing.RegisterRoute(Routes.NowPlaying, typeof(NowPlayingPage));
        Routing.RegisterRoute(Routes.LockScreen, typeof(LockScreenPage));
        Routing.RegisterRoute(Routes.Lyrics, typeof(LyricsPage));
        Routing.RegisterRoute(Routes.LyricsEditor, typeof(LyricsEditorPage));
        Routing.RegisterRoute(Routes.TagEditor, typeof(TagEditorPage));
        Routing.RegisterRoute(Routes.AlbumDetail, typeof(AlbumDetailPage));
        Routing.RegisterRoute(Routes.ArtistDetail, typeof(ArtistDetailPage));
        Routing.RegisterRoute(Routes.PlaylistDetail, typeof(PlaylistDetailPage));
        Routing.RegisterRoute(Routes.AddSongs, typeof(AddSongsPage));
    }
}

public static class Routes
{
    /// <summary>Tab route - navigate with "//settings" so the tab bar stays in sync.</summary>
    public const string Settings = "//settings";
    public const string NowPlaying = "nowplaying";
    public const string LockScreen = "lockscreen";
    public const string Lyrics = "lyrics";
    public const string LyricsEditor = "lyricseditor";
    public const string TagEditor = "tageditor";
    public const string AlbumDetail = "albumdetail";
    public const string ArtistDetail = "artistdetail";
    public const string PlaylistDetail = "playlistdetail";
    public const string AddSongs = "addsongs";
}
