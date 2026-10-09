using Migurdex.Shared.Enums;
using System.Globalization;

namespace Migurdex.Shared.Models;

public class VideoSource
{
    public string                      Url             { get; set; } = string.Empty;
    public string                      Quality         { get; set; } = string.Empty;
    public long?                       Bitrate         { get; set; }
    public int?                        Width           { get; set; }
    public int?                        Height          { get; set; }
    public string?                     VideoCodec      { get; set; }
    public string?                     AudioCodec      { get; set; }
    public long?                       SizeBytes       { get; set; }
    public double?                     DurationSeconds { get; set; }
    public VideoType                   Type      { get; set; } = VideoType.Unknown;
    public string?                     Hoster    { get; set; }
    public string?                     Group     { get; set; }
    public string?                     Language  { get; set; }
    public Dictionary<string, string>? Headers   { get; set; }
    public List<Subtitle>?             Subtitles { get; set; }

    public string DisplayLabel
    {
        get
        {
            var quality = string.IsNullOrEmpty(Quality) ? "Çözülemedi" : Quality;
            var bitrate = FormatBitrate(Bitrate);
            var label = string.IsNullOrEmpty(bitrate) ? quality : $"{quality} • {bitrate}";
            return string.IsNullOrEmpty(VideoCodec) ? label : $"{label} • {VideoCodec}";
        }
    }

    public static string FormatBitrate(long? bitrate)
    {
        if (bitrate is null or <= 0)
        {
            return string.Empty;
        }

        if (bitrate >= 1_000_000)
        {
            return string.Format(CultureInfo.InvariantCulture,
                                "{0:0.#} Mbps",
                                bitrate.Value / 1_000_000.0);
        }

        if (bitrate >= 1_000)
        {
            return string.Format(CultureInfo.InvariantCulture,
                                "{0:0.#} kbps",
                                bitrate.Value / 1_000.0);
        }

        return $"{bitrate.Value} bps";
    }
}
