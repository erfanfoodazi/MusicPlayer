namespace Domain.Models;

/// <summary>
/// Technical, file-level information about the audio stream itself (spec section 7,
/// "technical information"). This data is derived from the file's encoding and is
/// separate from the user-editable tags stored in <see cref="Music"/>.
/// </summary>
public class AudioFileInfo
{
    /// <summary>Codec/container description reported by the tag library, e.g. "MPEG ADTS, layer III".</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Bitrate in kilobits per second (0 when unknown, e.g. variable-bitrate edge cases).</summary>
    public int BitrateKbps { get; set; }

    /// <summary>Sample rate in hertz (0 when unknown).</summary>
    public int SampleRateHz { get; set; }

    /// <summary>Number of audio channels (0 when unknown).</summary>
    public int Channels { get; set; }

    /// <summary>True when the file contains at least one embedded picture.</summary>
    public bool HasEmbeddedArtwork { get; set; }
}
