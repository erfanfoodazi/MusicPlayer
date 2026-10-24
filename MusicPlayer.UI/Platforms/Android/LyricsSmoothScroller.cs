using Android.Content;
using AndroidX.RecyclerView.Widget;

namespace MusicPlayer.UI.Platforms.Android;

/// <summary>
/// A centring smooth scroller with a human-paced duration. Android's default
/// <see cref="LinearSmoothScroller"/> uses about 25 ms per inch, so a one-line lyric jump
/// can finish in a single frame and looks like a hard snap. The minimum duration below
/// gives every line change a short, visible glide.
/// </summary>
internal sealed class LyricsSmoothScroller : LinearSmoothScroller
{
    private const int MinimumDurationMilliseconds = 360;
    private const int MaximumDurationMilliseconds = 720;

    public LyricsSmoothScroller(Context context, int targetPosition) : base(context)
    {
        TargetPosition = targetPosition;
    }

    public override int CalculateDtToFit(
        int viewStart,
        int viewEnd,
        int boxStart,
        int boxEnd,
        int snapPreference)
    {
        // Focus the middle of the line in the middle of the lyrics viewport.
        return (boxStart + ((boxEnd - boxStart) / 2)) - (viewStart + ((viewEnd - viewStart) / 2));
    }

    protected override int CalculateTimeForDeceleration(int dx)
    {
        var naturalDuration = base.CalculateTimeForDeceleration(dx);
        return Math.Clamp(naturalDuration, MinimumDurationMilliseconds, MaximumDurationMilliseconds);
    }
}
