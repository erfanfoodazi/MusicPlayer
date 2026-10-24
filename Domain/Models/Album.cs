using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Domain.Models;

public class Album : INotifyPropertyChanged
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string AlbumArtist { get; set; } = string.Empty;

    public int? Year { get; set; }

    private string? _artworkPath;
    public string? ArtworkPath
    {
        get => _artworkPath;
        set
        {
            if (_artworkPath != value)
            {
                _artworkPath = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Cover path computed from the album's tracks, same pattern as Music.ArtworkPath.
    /// Always returns the first track's artwork path (thumbnail preferred, file fallback),
    /// so the cover shows as soon as any track's artwork is available.</summary>
    public string? CoverArtPath
    {
        get
        {
            foreach (var track in Tracks)
            {
                var path = track.ArtworkPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            return ArtworkPath;
        }
    }

    public List<Music> Tracks { get; set; } = [];

    public int TrackCount => Tracks.Count;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
