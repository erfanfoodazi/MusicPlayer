using Domain.Models;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.Controls;

public partial class ArtistItem : Grid
{
    public static readonly BindableProperty ArtistProperty =
        BindableProperty.Create(nameof(Artist), typeof(Artist), typeof(ArtistItem), propertyChanged: OnArtistChanged);

    public Artist? Artist
    {
        get => (Artist?)GetValue(ArtistProperty);
        set => SetValue(ArtistProperty, value);
    }

    public ArtistItem()
    {
        InitializeComponent();
    }

    private static void OnArtistChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is ArtistItem item && newValue is Artist artist)
        {
            item.BindingContext = artist;
        }
    }

    private async void OnArtistTapped(object? sender, TappedEventArgs e)
    {
        if (Artist is null) return;

        Haptics.Tap();
        _ = Motion.PressAsync(this);

        await Shell.Current.GoToAsync($"{Routes.ArtistDetail}?artistId={Artist.Id}");
    }
}
