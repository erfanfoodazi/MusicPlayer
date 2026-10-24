using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Storage;
using MusicPlayer.UI.Services;

namespace MusicPlayer.UI.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    private const string CrossfadeEnabledKey = "settings.crossfade.enabled";
    private const string CrossfadeDurationKey = "settings.crossfade.duration";
    private const string NormalizeVolumeKey = "settings.normalizeVolume";
    private const string EqualizerKey = "settings.equalizer";
    private const string ShowLyricsKey = "settings.showLyrics";
    private const string AutoFetchArtworkKey = "settings.autoFetchArtwork";
    private const string AutoFetchLyricsKey = "settings.autoFetchLyrics";
    private const string DownloadWifiKey = "settings.downloadWifiOnly";
    private const string ThemeKey = "settings.theme";
    private const string QualityKey = "settings.audioQuality";

    [ObservableProperty]
    private bool _enableEqualizer;

    [ObservableProperty]
    private bool _crossfadeEnabled;

    [ObservableProperty]
    private int _crossfadeDuration = 5;

    [ObservableProperty]
    private bool _normalizeVolume;

    [ObservableProperty]
    private bool _showLyrics = true;

    [ObservableProperty]
    private bool _autoFetchArtwork = true;

    [ObservableProperty]
    private bool _autoFetchLyrics = true;

    [ObservableProperty]
    private string _theme = "System";

    [ObservableProperty]
    private string _accentId = "Violet";

    [ObservableProperty]
    private string _audioQuality = "High";

    [ObservableProperty]
    private bool _downloadOverWifiOnly = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public string AccentName => ThemeService.Current.Name;

    public ObservableCollection<string> ThemeOptions { get; } = ["System", "Light", "Dark"];

    public ObservableCollection<string> QualityOptions { get; } = ["Low", "Normal", "High", "Lossless"];

    private readonly IStorageAccessService _storageAccess;

    public SettingsViewModel(IStorageAccessService storageAccess)
    {
        _storageAccess = storageAccess;
        Title = "Settings";
        LoadSettings();
    }

    // ---------------------------------------------------------- file access

    public bool HasFullFileAccess => _storageAccess.HasFullFileAccess;

    public bool IsStorageAccessWarning => !HasFullFileAccess;

    public string StorageAccessStateText => HasFullFileAccess ? "Granted" : "Not granted";

    public string StorageAccessButtonText => HasFullFileAccess ? "Open system settings" : "Grant access";

    /// <summary>Re-checks the permission when the page reappears after the system screen.</summary>
    public void RefreshStorageAccess()
    {
        OnPropertyChanged(nameof(HasFullFileAccess));
        OnPropertyChanged(nameof(IsStorageAccessWarning));
        OnPropertyChanged(nameof(StorageAccessStateText));
        OnPropertyChanged(nameof(StorageAccessButtonText));
    }

    [RelayCommand]
    private async Task RequestStorageAccessAsync()
    {
        await _storageAccess.RequestFullFileAccessAsync();
        RefreshStorageAccess();

        StatusMessage = HasFullFileAccess
            ? "Full file access is granted."
            : "Turn on \"Allow management of all files\", then come back to this screen.";
    }

    private void LoadSettings()
    {
        EnableEqualizer = Preferences.Default.Get(EqualizerKey, false);
        CrossfadeEnabled = Preferences.Default.Get(CrossfadeEnabledKey, false);
        CrossfadeDuration = Preferences.Default.Get(CrossfadeDurationKey, 5);
        NormalizeVolume = Preferences.Default.Get(NormalizeVolumeKey, false);
        ShowLyrics = Preferences.Default.Get(ShowLyricsKey, true);
        AutoFetchArtwork = Preferences.Default.Get(AutoFetchArtworkKey, true);
        AutoFetchLyrics = Preferences.Default.Get(AutoFetchLyricsKey, true);
        DownloadOverWifiOnly = Preferences.Default.Get(DownloadWifiKey, true);
        Theme = Preferences.Default.Get(ThemeKey, "System");
        AccentId = ThemeService.CurrentId;
        AudioQuality = Preferences.Default.Get(QualityKey, "High");
        ApplyTheme();
        OnPropertyChanged(nameof(AccentName));
    }

    partial void OnEnableEqualizerChanged(bool value)
    {
        Preferences.Default.Set(EqualizerKey, value);
        PlayerService.Instance.SetEqualizerEnabled(value);
    }
    partial void OnCrossfadeEnabledChanged(bool value) => Preferences.Default.Set(CrossfadeEnabledKey, value);
    partial void OnCrossfadeDurationChanged(int value) => Preferences.Default.Set(CrossfadeDurationKey, value);
    partial void OnNormalizeVolumeChanged(bool value) => Preferences.Default.Set(NormalizeVolumeKey, value);
    partial void OnShowLyricsChanged(bool value) => Preferences.Default.Set(ShowLyricsKey, value);
    partial void OnAutoFetchArtworkChanged(bool value) => Preferences.Default.Set(AutoFetchArtworkKey, value);
    partial void OnAutoFetchLyricsChanged(bool value) => Preferences.Default.Set(AutoFetchLyricsKey, value);
    partial void OnDownloadOverWifiOnlyChanged(bool value) => Preferences.Default.Set(DownloadWifiKey, value);
    partial void OnAudioQualityChanged(string value) => Preferences.Default.Set(QualityKey, value);

    partial void OnThemeChanged(string value)
    {
        Preferences.Default.Set(ThemeKey, value);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var app = Microsoft.Maui.Controls.Application.Current;
        if (app is null) return;

        app.UserAppTheme = Theme switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }

    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            // Artwork now lives in app data (referenced by the saved library snapshot).
            var cache = Path.Combine(FileSystem.Current.AppDataDirectory, "artwork");
            if (Directory.Exists(cache))
            {
                foreach (var file in Directory.EnumerateFiles(cache))
                {
                    File.Delete(file);
                }
            }

            StatusMessage = "Artwork cache cleared. Rescan the library to extract covers again.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not clear cache: {ex.Message}";
        }
    }

    /// <summary>Selects a theme from the segmented control.</summary>
    [RelayCommand]
    private void SetTheme(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && ThemeOptions.Contains(value))
        {
            Theme = value;
        }
    }

    [RelayCommand]
    private async Task SetAccentAsync(string? id)
    {
        var palette = ThemeService.Palettes.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        if (palette is null || string.Equals(palette.Id, ThemeService.CurrentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ThemeService.Apply(palette.Id);
        AccentId = palette.Id;
        OnPropertyChanged(nameof(AccentName));
        Haptics.Tap();

        await AppDialogs.ShowAlertAsync(
            Shell.Current?.CurrentPage,
            $"{palette.Name} accent",
            "The new palette is ready. Auralis will refresh the interface so every screen uses it.",
            "Open");

        ThemeService.ReloadShell();
    }

    /// <summary>Selects an audio quality from the segmented control.</summary>
    [RelayCommand]
    private void SetQuality(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && QualityOptions.Contains(value))
        {
            AudioQuality = value;
        }
    }

    /// <summary>Steps the crossfade duration between 1 and 15 seconds.</summary>
    [RelayCommand]
    private void AdjustCrossfade(string? delta)
    {
        if (int.TryParse(delta, out var step))
        {
            CrossfadeDuration = Math.Clamp(CrossfadeDuration + step, 1, 15);
        }
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        EnableEqualizer = false;
        CrossfadeEnabled = false;
        CrossfadeDuration = 5;
        NormalizeVolume = false;
        ShowLyrics = true;
        AutoFetchArtwork = true;
        AutoFetchLyrics = true;
        DownloadOverWifiOnly = true;
        Theme = "System";
        AudioQuality = "High";
        ThemeService.Apply("Violet");
        AccentId = "Violet";
        OnPropertyChanged(nameof(AccentName));
        StatusMessage = "Settings reset to defaults. Reopen Settings to see the default accent everywhere.";
    }
}
