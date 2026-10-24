using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Domain.Models;

namespace Application.Services;

/// <summary>
/// Parses LRC text into the domain <see cref="Lyrics"/> model.
///
/// Supported input:
///  - Plain lyrics (no timestamps)  -> LyricsType.Plain
///  - Standard LRC [mm:ss.xx]       -> LyricsType.Synced
///  - Enhanced LRC [mm:ss.xx]&lt;mm:ss.xx&gt;word -> LyricsType.Enhanced (word timings kept)
///
/// This is pure business logic (no files, network or UI), so it lives in the application
/// layer and can be unit-tested with plain strings.
/// </summary>
public static class LrcParser
{
    // [mm:ss.xx], [mm:ss.xxx], [mm:ss] - one line may contain several timestamps.
    private static readonly Regex TimeTagRegex =
        new(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    // Enhanced LRC word timings: <mm:ss.xx>word
    private static readonly Regex WordTagRegex =
        new(@"<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    // Same shape but with capture groups, used when the actual word timings are needed.
    private static readonly Regex WordTimeTagRegex =
        new(@"<(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled);

    // Metadata tags such as [ar:...], [ti:...], [offset:...]
    private static readonly Regex IdTagRegex =
        new(@"^\[[A-Za-z]+:.*\]$", RegexOptions.Compiled);

    private static readonly Regex OffsetTagRegex =
        new(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses <paramref name="text"/> and returns a <see cref="Lyrics"/> instance whose
    /// <see cref="Lyrics.Text"/> is always the readable (unsynced) lyrics, and whose
    /// <see cref="Lyrics.Lines"/> is populated only when timestamps were found.
    /// </summary>
    public static Lyrics Parse(string? text, LyricsSource source)
    {
        var lyrics = new Lyrics { Source = source, Text = string.Empty };

        if (string.IsNullOrWhiteSpace(text))
        {
            lyrics.Type = LyricsType.Plain;
            return lyrics;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var offsetMilliseconds = ParseOffset(normalized);

        var syncedLines = new List<LyricsLine>();
        var plainLines = new List<string>();
        var hasWordTimings = false;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var timeMatches = TimeTagRegex.Matches(line);
            if (timeMatches.Count == 0)
            {
                // [ar:...], [ti:...], comments etc. are metadata, not lyrics.
                if (IdTagRegex.IsMatch(line))
                {
                    continue;
                }

                var plain = StripWordTags(line);
                if (plain.Length > 0)
                {
                    plainLines.Add(plain);
                }

                continue;
            }

            // Everything after the last time tag is the lyric for this line. In enhanced
            // LRC that text also carries per-word <mm:ss.xx> tags.
            var lastTag = timeMatches[^1];
            var lyricContent = line[(lastTag.Index + lastTag.Length)..];
            var wordMatches = WordTimeTagRegex.Matches(lyricContent);
            var lineHasWords = wordMatches.Count > 0;
            if (lineHasWords)
            {
                hasWordTimings = true;
            }

            var lyricText = StripWordTags(lyricContent);
            if (lyricText.Length == 0)
            {
                // Timestamp-only lines are intentional gaps in many LRC files.
                continue;
            }

            foreach (Match match in timeMatches)
            {
                if (!TryParseTimestamp(match, offsetMilliseconds, out var startTime))
                {
                    continue;
                }

                var parsedLine = new LyricsLine
                {
                    Text = lyricText,
                    StartTime = startTime,
                };

                if (lineHasWords)
                {
                    parsedLine.Words = BuildWords(lyricContent, wordMatches, offsetMilliseconds, startTime);
                }

                syncedLines.Add(parsedLine);
            }
        }

        if (syncedLines.Count > 0)
        {
            syncedLines.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            for (var i = 0; i < syncedLines.Count; i++)
            {
                syncedLines[i].Index = i;
                syncedLines[i].EndTime = i + 1 < syncedLines.Count
                    ? syncedLines[i + 1].StartTime
                    : null;
            }

            lyrics.Type = hasWordTimings ? LyricsType.Enhanced : LyricsType.Synced;
            lyrics.Lines = syncedLines;
            lyrics.Text = string.Join(Environment.NewLine, syncedLines.Select(l => l.Text));
            return lyrics;
        }

        lyrics.Type = LyricsType.Plain;
        lyrics.Text = plainLines.Count > 0
            ? string.Join(Environment.NewLine, plainLines)
            : normalized.Trim();
        return lyrics;
    }

    /// <summary>
    /// Rebuilds LRC text from parsed lines so fetched, imported or edited lyrics can be
    /// cached in the same format the app reads from local .lrc files. Lines with word
    /// timings are written in enhanced LRC format.
    /// </summary>
    public static string ToLrc(Lyrics lyrics)
    {
        if (lyrics.Lines.Count == 0)
        {
            return lyrics.Text;
        }

        var builder = new StringBuilder();
        foreach (var line in lyrics.Lines.OrderBy(l => l.StartTime))
        {
            builder.Append('[');
            AppendTimestamp(builder, line.StartTime);
            builder.Append(']');

            if (line.Words.Count > 0)
            {
                foreach (var word in line.Words.OrderBy(w => w.StartTime))
                {
                    builder.Append('<');
                    AppendTimestamp(builder, word.StartTime);
                    builder.Append('>').Append(word.Text).Append(' ');
                }

                // No trailing space before the newline.
                if (builder.Length > 0 && builder[^1] == ' ')
                {
                    builder.Length--;
                }
            }
            else
            {
                builder.Append(line.Text);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Moves every line (and word) by <paramref name="delta"/>, then re-sorts and re-links
    /// the lines. Used by the lyrics page's "sync offset" buttons.
    /// </summary>
    public static void Shift(Lyrics lyrics, TimeSpan delta)
    {
        if (lyrics.Lines.Count == 0)
        {
            return;
        }

        foreach (var line in lyrics.Lines)
        {
            line.StartTime = Clamp(line.StartTime + delta);
            if (line.EndTime is { } end)
            {
                line.EndTime = Clamp(end + delta);
            }

            foreach (var word in line.Words)
            {
                word.StartTime = Clamp(word.StartTime + delta);
            }
        }

        lyrics.Lines.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        for (var i = 0; i < lyrics.Lines.Count; i++)
        {
            lyrics.Lines[i].Index = i;
            lyrics.Lines[i].EndTime = i + 1 < lyrics.Lines.Count
                ? lyrics.Lines[i + 1].StartTime
                : null;
        }
    }

    private static TimeSpan Clamp(TimeSpan value)
        => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static List<LyricWord> BuildWords(
        string content,
        MatchCollection wordMatches,
        int offsetMilliseconds,
        TimeSpan lineStart)
    {
        var words = new List<LyricWord>(wordMatches.Count);

        for (var i = 0; i < wordMatches.Count; i++)
        {
            var match = wordMatches[i];
            var textStart = match.Index + match.Length;
            var textEnd = i + 1 < wordMatches.Count ? wordMatches[i + 1].Index : content.Length;

            if (textEnd <= textStart)
            {
                continue;
            }

            var text = content[textStart..textEnd].Trim();
            if (text.Length == 0 ||
                !TryParseTimestamp(match, offsetMilliseconds, out var start))
            {
                continue;
            }

            words.Add(new LyricWord { Text = text, StartTime = start });
        }

        // Any text before the first word tag belongs to the line start.
        if (wordMatches.Count > 0 && wordMatches[0].Index > 0)
        {
            var leading = content[..wordMatches[0].Index].Trim();
            if (leading.Length > 0)
            {
                words.Insert(0, new LyricWord { Text = leading, StartTime = lineStart });
            }
        }

        return words;
    }

    private static void AppendTimestamp(StringBuilder builder, TimeSpan time)
    {
        builder
            .Append((int)time.TotalMinutes)
            .Append(':')
            .Append(time.Seconds.ToString("D2", CultureInfo.InvariantCulture))
            .Append('.')
            .Append((time.Milliseconds / 10).ToString("D2", CultureInfo.InvariantCulture));
    }

    private static int ParseOffset(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var match = OffsetTagRegex.Match(rawLine.Trim());
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
            {
                return offset;
            }
        }

        return 0;
    }

    private static bool TryParseTimestamp(Match match, int offsetMilliseconds, out TimeSpan startTime)
    {
        startTime = TimeSpan.Zero;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var fraction = match.Groups[3].Success ? match.Groups[3].Value : "0";
        var milliseconds = fraction.Length switch
        {
            1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
            2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
            _ => int.Parse(fraction[..3], CultureInfo.InvariantCulture),
        };

        var timestamp = TimeSpan.FromMinutes(minutes)
                        + TimeSpan.FromSeconds(seconds)
                        + TimeSpan.FromMilliseconds(milliseconds);

        // LRC convention: a positive [offset] makes lyrics appear sooner, so it is
        // subtracted from the timestamp.
        var shifted = timestamp - TimeSpan.FromMilliseconds(offsetMilliseconds);
        startTime = shifted < TimeSpan.Zero ? TimeSpan.Zero : shifted;
        return true;
    }

    private static string StripWordTags(string value)
        => WordTagRegex.Replace(value, string.Empty).Trim();
}
