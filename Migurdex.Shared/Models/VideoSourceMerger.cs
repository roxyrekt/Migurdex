namespace Migurdex.Shared.Models;

public static class VideoSourceMerger
{
    public static VideoSource Merge(VideoSource extracted, VideoSource raw)
    {
        return new VideoSource
        {
            Url             = extracted.Url,
            Quality         = extracted.Quality,
            Bitrate         = extracted.Bitrate,
            Width           = extracted.Width,
            Height          = extracted.Height,
            VideoCodec      = extracted.VideoCodec,
            AudioCodec      = extracted.AudioCodec,
            SizeBytes       = extracted.SizeBytes,
            DurationSeconds = extracted.DurationSeconds,
            Type            = extracted.Type,
            Hoster          = extracted.Hoster ?? raw.Hoster,
            Group           = extracted.Group ?? raw.Group,
            Language        = extracted.Language ?? raw.Language,
            Headers         = MergeHeaders(extracted.Headers, raw.Headers),
            Subtitles       = extracted.Subtitles is { Count: > 0 } ? extracted.Subtitles : raw.Subtitles
        };
    }

    private static Dictionary<string, string>? MergeHeaders(Dictionary<string, string>? extracted,
        Dictionary<string, string>?                                                         raw)
    {
        if (raw is not { Count: > 0 })
        {
            return extracted;
        }

        if (extracted is not { Count: > 0 })
        {
            return raw;
        }

        var merged = new Dictionary<string, string>(raw, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in extracted)
        {
            merged[key] = value;
        }

        return merged;
    }
}
