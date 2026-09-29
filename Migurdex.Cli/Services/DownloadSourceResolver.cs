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

    /// <summary>
    ///     Doğrudan indirilebilir kaynak bulunamadığında gösterilecek mesajı üretir. API'den gelen
    ///     hata sayısı sıfırdan büyükse gerçek sebep extractor çözümlemesidir ve mesaj bunu belirtir.
    /// </summary>
    public static string BuildNoDirectSourceMessage(int reportedErrors)
    {
        return reportedErrors > 0
                   ? $"API'den indirilebilir doğrudan MP4/HLS kaynağı bulunamadı; {reportedErrors} kaynak çözümlemesi başarısız oldu."
                   : "API'den indirilebilir doğrudan MP4/HLS kaynağı bulunamadı.";
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

    public static SearchResult? PickSearchResult(IReadOnlyList<SearchResult> results, string query)
    {
        if (results.Count == 0)
        {
            return null;
        }

        return results.FirstOrDefault(result => string.Equals(result.Title,
                                                               query,
                                                               StringComparison.OrdinalIgnoreCase))
               ?? results[0];
    }

    public static Episode? PickEpisode(AnimeDetails details, int? season, double? episodeNumber)
    {
        ArgumentNullException.ThrowIfNull(details);

        var candidates = details.Episodes.AsEnumerable();
        if (season.HasValue)
        {
            candidates = candidates.Where(episode => (episode.Season ?? 1) == season.Value);
        }

        var ordered = candidates.OrderBy(episode => episode.Season ?? 1)
                               .ThenBy(episode => episode.Number)
                               .ThenBy(episode => episode.Id, StringComparer.Ordinal)
                               .ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        if (!episodeNumber.HasValue)
        {
            return ordered[0];
        }

        return ordered.FirstOrDefault(episode => episode.Number.Equals(episodeNumber.Value));
    }

    public static bool TryResolveProvider(IReadOnlyList<ProviderInfo> providers,
        string                                         input,
        out string?                                    provider,
        out string?                                    error)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var names = providers.Select(item => item.Name)
                             .Where(item => !string.IsNullOrWhiteSpace(item))
                             .ToList();
        provider = names.FirstOrDefault(name => name.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(provider))
        {
            error = null;
            return true;
        }

        var matches = names.Where(name => name.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            provider = matches[0];
            error = null;
            return true;
        }

        provider = null;
        error = matches.Count == 0
                   ? $"'{input}' sağlayıcısı bulunamadı ({string.Join(", ", names)})."
                   : $"'{input}' belirsiz ({string.Join(", ", matches)}).";
        return false;
    }

    public static bool TryResolveGroup(IReadOnlyList<string> groups, string input, out string? group)
    {
        ArgumentNullException.ThrowIfNull(groups);
        group = groups.FirstOrDefault(item => string.Equals(item, input, StringComparison.OrdinalIgnoreCase));
        return !string.IsNullOrWhiteSpace(group);
    }
}
