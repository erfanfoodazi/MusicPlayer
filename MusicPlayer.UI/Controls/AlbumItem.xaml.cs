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
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// The cover is a square that fills its grid cell, so the tile looks right on
    /// both narrow phones and wide tablets without hard-coded pixel sizes.
    /// </summary>
    private void OnSizeChanged(object? sender, EventArgs e)
    {
        var size = Width - Padding.HorizontalThickness;
        if (size <= 0 || double.IsNaN(size))
        {
            return;
        }

        if (Math.Abs(Artwork.WidthRequest - size) >= 0.5)
        {
            Artwork.WidthRequest = size;
            Artwork.HeightRequest = size;
        }

        // The CollectionView measures the first album before it has a column width. Giving
        // the tile an explicit total height after the square is known keeps the title and
        // artist rows from being clipped away when the item is recycled.
        var targetHeight = size + 58;
        if (Math.Abs(HeightRequest - targetHeight) >= 0.5)
        {
            HeightRequest = targetHeight;
        }
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
        if (Album is null) return;

        // A short press feedback before navigation makes the grid feel tactile without
        // delaying the page transition.
        try
        {
            Artwork.CancelAnimations();
            await Artwork.ScaleTo(0.97, 80, Easing.CubicOut);
            await Artwork.ScaleTo(1, 130, Easing.CubicOut);
        }
        catch
        {
            Artwork.Scale = 1;
        }

        await Shell.Current.GoToAsync($"{Routes.AlbumDetail}?albumId={Album.Id}");
    }

    private void OnPlayClicked(object? sender, EventArgs e)
    {
        if (Album is not { Tracks.Count: > 0 } album)
        {
            return;
        }

        Haptics.Tap();
        PlayerService.Instance.SetQueue(album.Tracks);
    }
}
