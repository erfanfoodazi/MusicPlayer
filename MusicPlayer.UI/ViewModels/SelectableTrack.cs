using CommunityToolkit.Mvvm.ComponentModel;
using Domain.Models;

namespace MusicPlayer.UI.ViewModels;

/// <summary>One song in the add-to-playlist picker, with its own selection state.</summary>
public partial class SelectableTrack : ObservableObject
{
    public SelectableTrack(Music track) => Track = track;

    public Music Track { get; }

    public string FilePath => Track.FilePath;

    [ObservableProperty]
    private bool _isSelected;
}
