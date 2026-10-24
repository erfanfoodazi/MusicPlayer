using Android.Graphics;
using Application.Services;
using Domain.Models;
using IOPath = System.IO.Path;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Android artwork downscaler. It runs on the scanner's background thread and writes:
///   512 px  -> player / album grid
///   192 px  -> 48–60 px list rows and the mini player
/// Full-resolution covers are never decoded by the UI. Decoding a 512 px JPEG for every
/// 48 px row was the main reason scrolling and page transitions stuttered.
/// </summary>
public sealed class AndroidArtworkThumbnailer : IArtworkThumbnailer
{
    private const int MaxDimension = 512;
    private const int ListDimension = 192;
    private const int JpegQuality = 82;
    private const int ListJpegQuality = 80;

    public void EnsureThumbnail(Artwork artwork, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(artwork.FilePath))
            {
                return;
            }

            var thumbnailPath = BuildThumbnailPath(artwork.FilePath, "_thumb.jpg");
            var listThumbnailPath = BuildThumbnailPath(artwork.FilePath, "_thumb_sm.jpg");

            if (thumbnailPath is null || listThumbnailPath is null)
            {
                return;
            }

            // The cached 512 px cover is enough to create the small one, even when the
            // original extracted artwork has since been cleaned up.
            if (File.Exists(thumbnailPath) && new FileInfo(thumbnailPath).Length > 0)
            {
                artwork.ThumbnailPath = thumbnailPath;
                EnsureListThumbnail(thumbnailPath, listThumbnailPath, artwork, cancellationToken);
                return;
            }

            if (!File.Exists(artwork.FilePath))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(artwork.FilePath, bounds);

            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
            {
                return;
            }

            // Decode at roughly twice the target size, then scale smoothly. This keeps
            // large covers from allocating tens of megabytes just to make a small image.
            var sampleSize = 1;
            while ((bounds.OutWidth / sampleSize) > MaxDimension * 2 ||
                   (bounds.OutHeight / sampleSize) > MaxDimension * 2)
            {
                sampleSize *= 2;
            }

            var options = new BitmapFactory.Options { InSampleSize = sampleSize };
            using var decoded = BitmapFactory.DecodeFile(artwork.FilePath, options);
            if (decoded is null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var resized = Scale(decoded, MaxDimension);
            if (resized is null)
            {
                return;
            }

            using (var stream = File.Create(thumbnailPath))
            {
                resized.Compress(Bitmap.CompressFormat.Jpeg!, JpegQuality, stream);
            }

            artwork.ThumbnailPath = thumbnailPath;

            using var listBitmap = Scale(resized, ListDimension);
            if (listBitmap is not null)
            {
                using var listStream = File.Create(listThumbnailPath);
                listBitmap.Compress(Bitmap.CompressFormat.Jpeg!, ListJpegQuality, listStream);
                artwork.SmallThumbnailPath = listThumbnailPath;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A broken cover must never fail the scan; the UI falls back to the original.
        }
    }

    private static void EnsureListThumbnail(
        string thumbnailPath,
        string listPath,
        Artwork artwork,
        CancellationToken cancellationToken)
    {
        if (File.Exists(listPath) && new FileInfo(listPath).Length > 0)
        {
            artwork.SmallThumbnailPath = listPath;
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var decoded = BitmapFactory.DecodeFile(thumbnailPath);
        if (decoded is null)
        {
            return;
        }

        using var resized = Scale(decoded, ListDimension);
        if (resized is null)
        {
            return;
        }

        using var stream = File.Create(listPath);
        resized.Compress(Bitmap.CompressFormat.Jpeg!, ListJpegQuality, stream);
        artwork.SmallThumbnailPath = listPath;
    }

    private static Bitmap? Scale(Bitmap source, int maxDimension)
    {
        var scale = Math.Min(
            1.0,
            maxDimension / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        return width == source.Width && height == source.Height
            ? source.Copy(source.GetConfig() ?? Bitmap.Config.Argb8888!, false)
            : Bitmap.CreateScaledBitmap(source, width, height, true);
    }

    private static string? BuildThumbnailPath(string sourcePath, string suffix)
    {
        var directory = IOPath.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var name = IOPath.GetFileNameWithoutExtension(sourcePath);
        return IOPath.Combine(directory, $"{name}{suffix}");
    }
}
