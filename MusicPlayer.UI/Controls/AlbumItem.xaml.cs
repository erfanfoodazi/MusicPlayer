using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.Controls;

public partial class AlbumItem : Grid
{
    public static readonly BindableProperty AlbumProperty =
        BindableProperty.Create(nameof(Album), typeof(Album), typeof(AlbumItem), propertyChanged: OnAlbumChanged);

    public Album? Album
    {
        get => (Album?)GetValue(AlbumProperty);
        set => SetValue(AlbumProperty, value);
    }

    public AlbumItem()
    {
        InitializeComponent();
    }

    private static void OnAlbumChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is AlbumItem item && newValue is Album album)
        {
            item.BindingContext = album;
        }
    }

    private async void OnAlbumTapped(object? sender, TappedEventArgs e)
    {
        if (BindingContext is not Album album) return;

        await Shell.Current.GoToAsync($"{Routes.AlbumDetail}?albumId={album.Id}");
    }

    private void OnPlayClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not Album album || album.Tracks.Count == 0)
        {
            return;
        }

        PlayerService.Instance.SetQueue(album.Tracks);
    }
}
