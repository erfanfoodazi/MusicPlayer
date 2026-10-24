using CommunityToolkit.Mvvm.ComponentModel;
using Domain.Models;

namespace MusicPlayer.UI.ViewModels;

/// <summary>
/// One word of an enhanced-LRC line. <see cref="IsActive"/> is driven by the real playback
/// position so the word highlight follows seeks and pauses correctly.
/// </summary>
public partial class LyricsWordViewModel : ObservableObject
{
    public LyricsWordViewModel(LyricWord word)
    {
        Text = word.Text;
        StartTime = word.StartTime;
    }

    public string Text { get; }

    public TimeSpan StartTime { get; }

    [ObservableProperty]
    private bool _isActive;
}
