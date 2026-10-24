using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Domain.Models;

namespace MusicPlayer.UI.ViewModels;

/// <summary>
/// One line in the synced lyrics list. <see cref="IsActive"/> (and every word's state) is set
/// by the view model from the real player position, never by a separate timer.
///
/// Lines from enhanced LRC files carry <see cref="Words"/>; the page then renders the line as
/// individually highlightable words instead of one label.
/// </summary>
public partial class LyricsLineViewModel : ObservableObject
{
    public LyricsLineViewModel(LyricsLine line)
    {
        Text = line.Text;
        StartTime = line.StartTime;
        Words = line.Words.Count > 0
            ? new ObservableCollection<LyricsWordViewModel>(
                line.Words.Select(word => new LyricsWordViewModel(word)))
            : [];
    }

    public string Text { get; }

    public TimeSpan StartTime { get; }

    public ObservableCollection<LyricsWordViewModel> Words { get; }

    public bool HasWords => Words.Count > 0;

    /// <summary>The plain single-label template is used when no word timings exist.</summary>
    public bool ShowPlainText => Words.Count == 0;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Turns every word highlight off (called when the active line moves away).</summary>
    public void ClearWordHighlights()
    {
        foreach (var word in Words)
        {
            word.IsActive = false;
        }
    }
}
