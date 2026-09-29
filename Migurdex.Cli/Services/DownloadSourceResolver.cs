using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

public enum DownloadSourceFormat
{
    Auto,
    Mp4,
    Hls
}

public static class DownloadSourceResolver
{
    public const int MaxCandidates = 3;

    public static bool IsDirectDownloadable(VideoSource? source)
    {
        if (source is null
            || source.Type is not (VideoType.Mp4 or VideoType.M3U8)
            || string.IsNullOrWhiteSpace(source.Url)
            || !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    public static List<VideoSource> SelectCandidates(IEnumerable<VideoSource> sources,
        DownloadSourceFormat                                             format,
        CliConfig                                                       config)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(config);

        var direct = sources.Where(IsDirectDownloadable).ToList();
        if (format == DownloadSourceFormat.Mp4)
        {
            direct = direct.Where(source => source.Type == VideoType.Mp4).ToList();
        }
        else if (format == DownloadSourceFormat.Hls)
        {
            direct = direct.Where(source => source.Type == VideoType.M3U8).ToList();
        }
        else
        {
            var eligible = direct.Where(source => SourceSelector.IsAutoEligible(source, config)).ToList();
            if (eligible.Count > 0)
            {
                direct = eligible;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<VideoSource>(MaxCandidates);
        foreach (var source in SourceSelector.SortVideoSources(direct, config))
        {
            var sourceKey = source.Type + ":"
                            + DownloadHttp.CreateSourceFingerprint(source.Url, source.Headers);
            if (seen.Add(sourceKey))
            {
                candidates.Add(source);
                if (candidates.Count == MaxCandidates)
                {
                    break;
                }
            }
        }

        return candidates;
    }
}
