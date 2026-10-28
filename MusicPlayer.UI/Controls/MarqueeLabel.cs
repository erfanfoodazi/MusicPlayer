namespace MusicPlayer.UI.Controls;

/// <summary>
/// One-line label that scrolls long text like a tape. Two copies of the string travel left
/// with a gap between them; when the strip resets, the second copy sits exactly where the
/// first started, so the loop never shows a jump. Short text stays centred or left aligned
/// like a plain label.
/// </summary>
public sealed class MarqueeLabel : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text),
        typeof(string),
        typeof(MarqueeLabel),
        string.Empty,
        propertyChanged: (bindable, _, _) => ((MarqueeLabel)bindable).OnTextChanged());

    public static readonly BindableProperty FontFamilyProperty = BindableProperty.Create(
        nameof(FontFamily),
        typeof(string),
        typeof(MarqueeLabel),
        null,
        propertyChanged: (bindable, _, _) => ((MarqueeLabel)bindable).ApplyFont());

    public static readonly BindableProperty FontSizeProperty = BindableProperty.Create(
        nameof(FontSize),
        typeof(double),
        typeof(MarqueeLabel),
        16d,
        propertyChanged: (bindable, _, _) => ((MarqueeLabel)bindable).ApplyFont());

    public static readonly BindableProperty TextColorProperty = BindableProperty.Create(
        nameof(TextColor),
        typeof(Color),
        typeof(MarqueeLabel),
        null,
        propertyChanged: (bindable, _, _) => ((MarqueeLabel)bindable).ApplyFont());

    public static readonly BindableProperty CharacterSpacingProperty = BindableProperty.Create(
        nameof(CharacterSpacing),
        typeof(double),
        typeof(MarqueeLabel),
        0d,
        propertyChanged: (bindable, _, _) => ((MarqueeLabel)bindable).ApplyFont());

    /// <summary>Where the text sits when it is short enough to fit.</summary>
    public static readonly BindableProperty TextAlignmentProperty = BindableProperty.Create(
        nameof(TextAlignment),
        typeof(TextAlignment),
        typeof(MarqueeLabel),
        TextAlignment.Start,
        propertyChanged: (bindable, _, _) => ((MarqueeLabel)bindable).ApplyAlignment());

    /// <summary>Scroll speed in device-independent units per second.</summary>
    public static readonly BindableProperty SpeedProperty = BindableProperty.Create(
        nameof(Speed),
        typeof(double),
        typeof(MarqueeLabel),
        36d);

    /// <summary>Distance between the end of the text and the start of its repeat.</summary>
    public static readonly BindableProperty GapProperty = BindableProperty.Create(
        nameof(Gap),
        typeof(double),
        typeof(MarqueeLabel),
        56d);

    private readonly Grid _viewport = new()
    {
        IsClippedToBounds = true,
        HorizontalOptions = LayoutOptions.Fill,
    };

    private readonly HorizontalStackLayout _track = new()
    {
        HorizontalOptions = LayoutOptions.Start,
        VerticalOptions = LayoutOptions.Fill,
    };

    private readonly Label _primary = CreateTextLabel();
    private readonly Label _secondary = CreateTextLabel();

    private CancellationTokenSource? _loop;
    private bool _running;
    private bool _scrolling;
    private bool _evaluating;
    private bool _evaluationQueued;
    private double _textWidth;

    public MarqueeLabel()
    {
        _secondary.IsVisible = false;
        _track.Add(_primary);
        _track.Add(_secondary);
        _viewport.Add(_track);
        _viewport.SizeChanged += (_, _) => QueueEvaluate();
        Content = _viewport;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? FontFamily
    {
        get => (string?)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public Color? TextColor
    {
        get => (Color?)GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    public double CharacterSpacing
    {
        get => (double)GetValue(CharacterSpacingProperty);
        set => SetValue(CharacterSpacingProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>Begins scrolling while the owner page is visible.</summary>
    public void Start()
    {
        _running = true;
        QueueEvaluate();
    }

    /// <summary>Stops the tape and parks the text back at its start.</summary>
    public void Stop()
    {
        _running = false;
        StopLoop();
    }

    private static Label CreateTextLabel() => new()
    {
        LineBreakMode = LineBreakMode.NoWrap,
        MaxLines = 1,
        HorizontalTextAlignment = TextAlignment.Start,
        VerticalTextAlignment = TextAlignment.Center,
        HorizontalOptions = LayoutOptions.Start,
        VerticalOptions = LayoutOptions.Fill,
    };

    private void OnTextChanged()
    {
        _primary.Text = Text ?? string.Empty;
        _secondary.Text = Text ?? string.Empty;
        StopLoop();
        QueueEvaluate();
    }

    private void ApplyFont()
    {
        foreach (var label in new[] { _primary, _secondary })
        {
            label.FontFamily = FontFamily;
            label.FontSize = FontSize;
            label.CharacterSpacing = CharacterSpacing;

            if (TextColor is { } color)
            {
                label.TextColor = color;
            }
        }

        StopLoop();
        QueueEvaluate();
    }

    private void ApplyAlignment()
    {
        _track.HorizontalOptions = !_scrolling && TextAlignment == TextAlignment.Center
            ? LayoutOptions.Center
            : LayoutOptions.Start;
    }

    private void QueueEvaluate()
    {
        if (_evaluationQueued)
        {
            return;
        }

        _evaluationQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _evaluationQueued = false;
            Evaluate();
        });
    }

    private void Evaluate()
    {
        if (_evaluating)
        {
            return;
        }

        var available = _viewport.Width;
        var text = Text ?? string.Empty;

        if (available <= 0 || text.Length == 0 || _primary.Handler is null)
        {
            return;
        }

        _evaluating = true;

        try
        {
            // Measuring with an unbounded width is what reveals the real length of the
            // string; the label's own width request would otherwise clamp the result.
            _primary.WidthRequest = -1;
            var natural = _primary.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;

            if (double.IsNaN(natural) || natural <= 0)
            {
                return;
            }

            _textWidth = natural;

            var scroll = natural > available + 0.5;
            SetScrolling(scroll);

            if (scroll && _running)
            {
                StartLoop();
            }
        }
        finally
        {
            _evaluating = false;
        }
    }

    private void SetScrolling(bool scroll)
    {
        if (_scrolling == scroll)
        {
            ApplyAlignment();
            return;
        }

        _scrolling = scroll;
        _secondary.IsVisible = scroll;
        _track.Spacing = scroll ? Gap : 0;
        _primary.WidthRequest = scroll ? _textWidth : -1;
        _secondary.WidthRequest = scroll ? _textWidth : -1;
        ApplyAlignment();

        if (!scroll)
        {
            StopLoop();
        }
    }

    private void StartLoop()
    {
        if (_loop is not null || !_scrolling)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _loop = cancellation;
        _ = RunAsync(cancellation);
    }

    private async Task RunAsync(CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;

        try
        {
            // A beat before moving so the start of the title can be read at rest.
            await Task.Delay(900, token);

            while (!token.IsCancellationRequested && _scrolling)
            {
                var distance = _textWidth + Math.Max(Gap, 8);
                var speed = Math.Max(Speed, 8);
                var duration = (uint)Math.Clamp(distance / speed * 1000, 1500, 40000);

                await _track.TranslateTo(-distance, 0, duration, Easing.Linear);

                if (token.IsCancellationRequested)
                {
                    break;
                }

                // The repeat copy now occupies the frame the text started from, so the reset
                // is invisible and the tape keeps travelling one way.
                _track.TranslationX = 0;
            }
        }
        catch
        {
            // Cancelling aborts the animation task.
        }
        finally
        {
            if (ReferenceEquals(_loop, cancellation))
            {
                _loop = null;
            }

            cancellation.Dispose();
        }
    }

    private void StopLoop()
    {
        var cancellation = _loop;
        _loop = null;
        cancellation?.Cancel();

        _track.CancelAnimations();
        _track.TranslationX = 0;
    }
}
