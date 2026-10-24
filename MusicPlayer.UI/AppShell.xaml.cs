using MusicPlayer.UI.Views;

namespace MusicPlayer.UI;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Library, Search and Settings are real Shell roots (see AppShell.xaml), so they are
        // reached with absolute routes. Detail screens are global routes pushed on top.
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
    /// <summary>Root routes for the three library sections.</summary>
    public const string Library = "//library";
    public const string Search = "//search";
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
