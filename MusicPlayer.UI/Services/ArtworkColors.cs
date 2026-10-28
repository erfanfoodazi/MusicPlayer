using Microsoft.Maui.Graphics;
#if ANDROID
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidBitmapFactory = Android.Graphics.BitmapFactory;
#endif

namespace MusicPlayer.UI.Services;

/// <summary>
/// Finds the colour a cover is "made of", so full-screen pages can tint their backdrop with
/// the artwork instead of showing a darkened copy of the image.
///
/// The cover is decoded to a 48 px sample, weighted towards saturated pixels (artwork with a
/// lot of black background would otherwise read as black), then normalised so the result is
/// always usable behind white text. Results are cached per file path because the player
/// re-reads the colour on every track change and on every page visit.
/// </summary>
public static class ArtworkColors
{
    private const int SampleSize = 48;

    private static readonly Dictionary<string, Color?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>Dominant colour of the artwork, or null when it cannot be read.</summary>
    public static async Task<Color?> GetDominantColorAsync(
        string? artworkPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artworkPath) || !File.Exists(artworkPath))
        {
            return null;
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(artworkPath, out var cached))
            {
                return cached;
            }
        }

        Color? color = null;

        try
        {
            var pixels = await Task.Run(() => ReadPixels(artworkPath), cancellationToken);
            if (pixels is { Length: > 3 })
            {
                color = ComputeDominantColor(pixels);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A broken cover must never break a page; the theme accent takes over.
            color = null;
        }

        lock (CacheLock)
        {
            Cache[artworkPath] = color;
        }

        return color;
    }

    /// <summary>The page colour the gradient fades into.</summary>
    public static Color BackdropBase(bool darkTheme) =>
        Color.FromArgb(darkTheme ? "#07070C" : "#F4F4F9");

    /// <summary>
    /// One cover-derived palette shared by every player surface. The backdrop keeps the cover
    /// colour all the way to the bottom of the page instead of washing out to black, so the
    /// transport row reads as part of the same surface.
    /// </summary>
    public static ArtworkTheme CreateTheme(Color? accent, bool darkTheme)
    {
        var baseColor = BackdropBase(darkTheme);
        var vivid = NormalizeAccent(accent, darkTheme);

        var soft = darkTheme
            ? Mix(vivid, baseColor, 0.82)
            : Mix(vivid, Color.FromArgb("#FFFFFF"), 0.86);
        var panel = darkTheme
            ? Mix(vivid, baseColor, 0.88)
            : Mix(vivid, Color.FromArgb("#FFFFFF"), 0.93);

        var top = Mix(vivid, baseColor, darkTheme ? 0.34 : 0.76);
        var middle = Mix(vivid, baseColor, darkTheme ? 0.48 : 0.83);
        var bottom = Mix(vivid, baseColor, darkTheme ? 0.4 : 0.8);

        var backdrop = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(top, 0f),
                new(middle, 0.55f),
                new(bottom, 1f),
            },
            new Point(0, 0),
            new Point(0, 1));

        var buttonGradient = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(ShiftLightness(vivid, darkTheme ? 0.06 : -0.02), 0f),
                new(ShiftLightness(vivid, darkTheme ? -0.08 : -0.14), 1f),
            },
            new Point(0, 0),
            new Point(1, 1));

        // The main play tile is a pale version of the cover colour with a near-black glyph,
        // matching the soft button modern players use instead of a saturated accent.
        var playButton = Mix(vivid, Color.FromArgb("#FFFFFF"), 0.58);

        return new ArtworkTheme(
            vivid,
            soft,
            panel,
            vivid.WithAlpha(darkTheme ? 0.78f : 0.72f),
            vivid.WithAlpha(darkTheme ? 0.3f : 0.26f),
            buttonGradient,
            backdrop,
            top,
            bottom,
            baseColor,
            playButton);
    }

    public sealed record ArtworkTheme(
        Color Accent,
        Color AccentSoft,
        Color Panel,
        Color AccentMuted,
        Color AccentBorder,
        Brush AccentGradient,
        Brush Backdrop,
        Color BackdropTop,
        Color BackdropBottom,
        Color Base,
        Color PlayButton);

    private static Color NormalizeAccent(Color? accent, bool darkTheme)
    {
        if (accent is null)
        {
            return ThemeAccent(darkTheme);
        }

        ToHsl(accent.Red, accent.Green, accent.Blue, out var hue, out var saturation, out var lightness);
        if (saturation < 0.1)
        {
            // Nearly grey artwork has no usable hue; the app accent is a better button colour.
            return ThemeAccent(darkTheme);
        }

        saturation = Math.Clamp(saturation, 0.52, 0.9);
        lightness = darkTheme
            ? Math.Clamp(lightness, 0.58, 0.72)
            : Math.Clamp(lightness, 0.38, 0.52);

        return FromHsl(hue, saturation, lightness);
    }

    private static Color ShiftLightness(Color color, double amount)
    {
        ToHsl(color.Red, color.Green, color.Blue, out var hue, out var saturation, out var lightness);
        return FromHsl(hue, saturation, Math.Clamp(lightness + amount, 0.05, 0.95));
    }

    private static Color ThemeAccent(bool darkTheme)
    {
        var key = darkTheme ? "PrimaryDark" : "Primary";
        if (Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color)
        {
            return color;
        }

        return Color.FromArgb(darkTheme ? "#A78BFF" : "#7C5CFF");
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        var t = (float)Math.Clamp(amount, 0, 1);
        return new Color(
            from.Red + (to.Red - from.Red) * t,
            from.Green + (to.Green - from.Green) * t,
            from.Blue + (to.Blue - from.Blue) * t,
            1f);
    }

    /// <summary>Weighted colour histogram over a BGRA sample, then a usable HSL result.</summary>
    private static Color? ComputeDominantColor(byte[] bgra)
    {
        var buckets = new Dictionary<int, Bucket>();

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var blue = bgra[i];
            var green = bgra[i + 1];
            var red = bgra[i + 2];
            var alpha = bgra[i + 3];

            if (alpha < 200)
            {
                continue;
            }

            var max = Math.Max(red, Math.Max(green, blue));
            var min = Math.Min(red, Math.Min(green, blue));
            var lightness = (max + min) / 2.0;

            // Skip letterboxing black and blown-out highlights: neither is the cover's colour.
            if (lightness < 16 || lightness > 244)
            {
                continue;
            }

            var saturation = max == 0 ? 0 : (max - min) / (double)max;
            var weight = 1 + (saturation * 3);

            var key = ((red >> 4) << 8) | ((green >> 4) << 4) | (blue >> 4);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
                buckets[key] = bucket;
            }

            bucket.Weight += weight;
            bucket.Red += red * weight;
            bucket.Green += green * weight;
            bucket.Blue += blue * weight;
        }

        if (buckets.Count == 0)
        {
            return null;
        }

        var best = buckets.Values.OrderByDescending(bucket => bucket.Weight).First();
        if (best.Weight <= 0)
        {
            return null;
        }

        var r = best.Red / best.Weight / 255.0;
        var g = best.Green / best.Weight / 255.0;
        var b = best.Blue / best.Weight / 255.0;

        ToHsl(r, g, b, out var hue, out var sat, out var lum);

        // Keep the tint colourful but dark enough for the white typography on top of it.
        sat = Math.Clamp(sat, 0.34, 0.82);
        lum = Math.Clamp(lum, 0.3, 0.5);

        return FromHsl(hue, sat, lum);
    }

    private static void ToHsl(double r, double g, double b, out double hue, out double saturation, out double lightness)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        lightness = (max + min) / 2;

        if (delta <= double.Epsilon)
        {
            hue = 0;
            saturation = 0;
            return;
        }

        saturation = lightness > 0.5
            ? delta / (2 - max - min)
            : delta / (max + min);

        hue = max switch
        {
            _ when Math.Abs(max - r) < 1e-6 => (g - b) / delta + (g < b ? 6 : 0),
            _ when Math.Abs(max - g) < 1e-6 => (b - r) / delta + 2,
            _ => (r - g) / delta + 4,
        };

        hue /= 6;
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        if (saturation <= double.Epsilon)
        {
            var gray = (float)lightness;
            return new Color(gray, gray, gray, 1f);
        }

        var q = lightness < 0.5
            ? lightness * (1 + saturation)
            : lightness + saturation - (lightness * saturation);
        var p = (2 * lightness) - q;

        var red = HueToRgb(p, q, hue + (1.0 / 3));
        var green = HueToRgb(p, q, hue);
        var blue = HueToRgb(p, q, hue - (1.0 / 3));

        return new Color((float)red, (float)green, (float)blue, 1f);
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6)
        {
            return p + ((q - p) * 6 * t);
        }

        if (t < 1.0 / 2)
        {
            return q;
        }

        if (t < 2.0 / 3)
        {
            return p + ((q - p) * ((2.0 / 3) - t) * 6);
        }

        return p;
    }

    /// <summary>Reads the cover as a small BGRA buffer. Runs off the UI thread.</summary>
    private static byte[]? ReadPixels(string path)
    {
#if ANDROID
        var bounds = new AndroidBitmapFactory.Options { InJustDecodeBounds = true };
        AndroidBitmapFactory.DecodeFile(path, bounds);

        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
        {
            return null;
        }

        var sampleSize = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sampleSize > SampleSize * 2)
        {
            sampleSize *= 2;
        }

        using var decoded = AndroidBitmapFactory.DecodeFile(
            path,
            new AndroidBitmapFactory.Options { InSampleSize = sampleSize });

        if (decoded is null)
        {
            return null;
        }

        var width = Math.Min(SampleSize, decoded.Width);
        var height = Math.Min(SampleSize, decoded.Height);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        using var scaled = AndroidBitmap.CreateScaledBitmap(decoded, width, height, true);
        if (scaled is null)
        {
            return null;
        }

        var pixels = new int[width * height];
        scaled.GetPixels(pixels, 0, width, 0, 0, width, height);

        var buffer = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];
            buffer[i * 4] = (byte)(pixel & 0xFF);
            buffer[(i * 4) + 1] = (byte)((pixel >> 8) & 0xFF);
            buffer[(i * 4) + 2] = (byte)((pixel >> 16) & 0xFF);
            buffer[(i * 4) + 3] = (byte)((pixel >> 24) & 0xFF);
        }

        return buffer;
#elif WINDOWS
        return Task.Run(async () =>
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);

            try
            {
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var transform = new Windows.Graphics.Imaging.BitmapTransform
                {
                    ScaledWidth = (uint)SampleSize,
                    ScaledHeight = (uint)SampleSize,
                };

                var data = await decoder.GetPixelDataAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    transform,
                    Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                    Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

                return data?.DetachPixelData();
            }
            finally
            {
                stream.Dispose();
            }
        }).GetAwaiter().GetResult();
#else
        return null;
#endif
    }

    private sealed class Bucket
    {
        public double Weight;
        public double Red;
        public double Green;
        public double Blue;
    }
}
