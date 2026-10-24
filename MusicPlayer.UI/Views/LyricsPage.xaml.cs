using MusicPlayer.UI.Services;
using MusicPlayer.UI.ViewModels;
#if ANDROID
using AndroidX.RecyclerView.Widget;
#endif

namespace MusicPlayer.UI.Views;

public partial class LyricsPage : ContentPage
{
    private bool _scrollSubscribed;
    private bool _scrollScheduled;
    private bool _introPlayed;
    private int _pendingScrollIndex = -1;

    private LyricsViewModel ViewModel => (LyricsViewModel)BindingContext;

    public LyricsPage()
    {
        InitializeComponent();
        BindingContext = ServiceHelper.GetRequiredService<LyricsViewModel>();
        Shell.SetNavBarIsVisible(this, false);
        Shell.SetTabBarIsVisible(this, false);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        SystemChrome.ApplyAppTheme();

        // The lyrics editor (when it was used) hands its saved text back through the session.
        var editorSession = ServiceHelper.GetRequiredService<LyricsEditorSession>();
        if (editorSession.Result is { } edited &&
            ReferenceEquals(editorSession.Track, ViewModel.CurrentTrack))
        {
            editorSession.Result = null;
            editorSession.Track = null;
            ViewModel.ApplyExternalLyrics(edited);
        }

        ViewModel.IsPageVisible = true;

        if (!_scrollSubscribed)
        {
            ViewModel.ScrollToLineRequested += OnScrollToLineRequested;
            _scrollSubscribed = true;
        }

        ViewModel.Refresh();

        if (!_introPlayed)
        {
            _introPlayed = true;
            _ = RunIntroAsync();
        }

        // The CollectionView needs one layout pass before ScrollTo can center the line.
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(350), ScrollToActiveLine);
    }

    private async Task RunIntroAsync()
    {
        Motion.Prepare(PageHeader, offsetY: -8);
        Motion.Prepare(TrackBlock);
        Motion.Prepare(LyricsContent, offsetY: 20);

        await Task.Delay(16);
        await Motion.IntroAsync(0, 70, PageHeader, TrackBlock, LyricsContent);
    }

    protected override void OnDisappearing()
    {
        ViewModel.IsPageVisible = false;

        if (_scrollSubscribed)
        {
            ViewModel.ScrollToLineRequested -= OnScrollToLineRequested;
            _scrollSubscribed = false;
        }

        _pendingScrollIndex = -1;
        _scrollScheduled = false;

        SystemChrome.ApplyAppTheme();
        base.OnDisappearing();
    }

    private void OnLyricLineTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject { BindingContext: LyricsLineViewModel line })
        {
            ViewModel.SeekToLineCommand.Execute(line);
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private async void OnMoreClicked(object? sender, EventArgs e)
    {
        var choice = await AppDialogs.ShowActionsAsync(
            this,
            "Lyrics options",
            [
                new AppSheetAction { Label = "Choose version", Glyph = "\ue030", Description = "Automatic, embedded, local or cached" },
                new AppSheetAction { Label = "Edit lyrics text", Glyph = "\ue745", Description = "Fine-tune the words or timing" },
                new AppSheetAction { Label = "Import .lrc file", Glyph = "\ue2c6", Description = "Bring in lyrics from storage" },
                new AppSheetAction { Label = "Export as .lrc", Glyph = "\ue2c4", Description = "Save a copy you can share" },
                new AppSheetAction { Label = "Save into the music file", Glyph = "\ue161", Description = "Write lyrics into the audio tags" },
                new AppSheetAction { Label = "Search online", Glyph = "\uf02f", Description = "Look up synced lyrics on LRCLIB" },
                new AppSheetAction { Label = "Reset sync offset", Glyph = "\ue166", Description = "Put the current lyrics back at 0.0s" },
            ]);

        switch (choice)
        {
            case 0:
                await ShowVersionPickerAsync();
                break;
            case 1:
                ViewModel.EditLyricsCommand.Execute(null);
                break;
            case 2:
                ViewModel.ImportLrcCommand.Execute(null);
                break;
            case 3:
                ViewModel.ExportLrcCommand.Execute(null);
                break;
            case 4:
                ViewModel.SaveLyricsToFileCommand.Execute(null);
                break;
            case 5:
                ViewModel.SearchOnlineCommand.Execute(null);
                break;
            case 6:
                ViewModel.ResetOffsetCommand.Execute(null);
                break;
        }
    }

    private async Task ShowVersionPickerAsync()
    {
        if (ViewModel.CurrentTrack is null)
        {
            return;
        }

        var versions = await ViewModel.GetVersionsAsync();

        var options = new List<AppSheetAction>
        {
            new()
            {
                Label = "Automatic (best available)",
                Glyph = "\ue65f",
                Description = "Pick the richest version available",
                IsSelected = true
            }
        };

        options.AddRange(versions.Select(version => new AppSheetAction
        {
            Label = version.Label,
            Glyph = version.IsAvailable ? "\ue5ca" : "\ue001",
            Description = version.IsAvailable ? null : "Not available for this track"
        }));

        var index = await AppDialogs.ShowActionsAsync(
            this,
            "Lyrics version",
            options,
            "Where Auralis should load lyrics from");

        if (index < 0)
        {
            return;
        }

        if (index == 0)
        {
            await ViewModel.ApplyAutomaticVersionAsync();
            return;
        }

        var selected = versions.ElementAtOrDefault(index - 1);
        if (selected is null)
        {
            return;
        }

        if (!selected.IsAvailable)
        {
            await AppDialogs.ShowAlertAsync(
                this,
                "Version not available",
                $"\"{selected.Label}\" was not found for this track.");
            return;
        }

        await ViewModel.ApplyVersionAsync(selected);
    }

    private void OnUseSearchResultClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: LyricsSearchResultViewModel result })
        {
            ViewModel.ApplySearchResultCommand.Execute(result);
        }
    }

    private void OnScrollToLineRequested(object? sender, int index) => ScrollToLine(index);

    private void ScrollToActiveLine() => ScrollToLine(ViewModel.ActiveLineIndex);

    private void ScrollToLine(int index)
    {
        if (index < 0 || index >= ViewModel.Lines.Count)
        {
            return;
        }

        // Coalesce rapid line changes into one scroll request. Starting a native smooth
        // scroll for every intermediate line made short lines look jumpy.
        _pendingScrollIndex = index;
        if (_scrollScheduled)
        {
            return;
        }

        _scrollScheduled = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(70), FlushScrollToLine);
    }

    private void FlushScrollToLine()
    {
        _scrollScheduled = false;

        var index = _pendingScrollIndex;
        _pendingScrollIndex = -1;

        if (!ViewModel.IsPageVisible || index < 0 || index >= ViewModel.Lines.Count)
        {
            return;
        }

        try
        {
#if ANDROID
            if (LinesView.Handler?.PlatformView is RecyclerView recycler &&
                recycler.Context is { } context &&
                recycler.GetLayoutManager() is { } layoutManager)
            {
                // The adapter includes the CollectionView header at position zero.
                var adapterIndex = index + (LinesView.Header is not null ? 1 : 0);
                layoutManager.StartSmoothScroll(
                    new Platforms.Android.LyricsSmoothScroller(context, adapterIndex));
                return;
            }
#endif

            // Animate after the new line has settled into the list, so the centring glide
            // runs once and is not interrupted by the next highlight update.
            LinesView.ScrollTo(index, position: ScrollToPosition.Center, animate: true);
        }
        catch
        {
            // The list can be re-templated while lyrics are being applied; the next
            // position tick or page appearance retries the scroll.
        }
    }
}
