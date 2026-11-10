using Migurdex.Shared.Models;
using System.Globalization;

namespace Migurdex.Cli.Services;

public sealed record EpisodeNumberRange(double Start, double End)
{
    public bool Contains(double number) => number >= Start && number <= End;
}

public sealed class EpisodeSelector
{
    public bool                              IsAll         { get; init; }
    public IReadOnlyList<EpisodeNumberRange> Ranges        { get; init; } = [];
    public double?                           SingleEpisode { get; init; }

    public bool IsBatch => IsAll || Ranges.Count > 1 || (Ranges.Count == 1 && !Ranges[0].Start.Equals(Ranges[0].End));

    public bool Matches(double episodeNumber)
    {
        if (IsAll)
        {
            return true;
        }

        foreach (var range in Ranges)
        {
            if (range.Contains(episodeNumber))
            {
                return true;
            }
        }

        return false;
    }
}

public static class MediaSelection
{
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

    public static List<Episode> PickEpisodes(AnimeDetails details, int? season, EpisodeSelector? selector)
    {
        ArgumentNullException.ThrowIfNull(details);

        if (selector is null || (!selector.IsAll && selector.Ranges.Count == 0))
        {
            var first = PickEpisode(details, season, selector?.SingleEpisode);
            return first is null ? [] : [first];
        }

        if (!selector.IsBatch && selector.SingleEpisode.HasValue)
        {
            var single = PickEpisode(details, season, selector.SingleEpisode.Value);
            return single is null ? [] : [single];
        }

        var ordered = details.Episodes
                             .OrderBy(episode => episode.Season ?? 1)
                             .ThenBy(episode => episode.Number)
                             .ThenBy(episode => episode.Id, StringComparer.Ordinal)
                             .ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var targetSeason = season ?? ordered[0].Season ?? 1;
        return ordered.Where(episode => (episode.Season ?? 1) == targetSeason
                                        && selector.Matches(episode.Number))
                      .ToList();
    }

    public static bool TryParseEpisodeSelector(
        string?              raw,
        out EpisodeSelector? selector,
        out string?          error)
    {
        selector = null;
        error    = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Bölüm numarası 0'dan büyük bir sayı olmalıdır.";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("*", StringComparison.Ordinal)
            || trimmed.Equals("tümü", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("hepsi", StringComparison.OrdinalIgnoreCase))
        {
            selector = new EpisodeSelector
            {
                IsAll = true
            };
            return true;
        }

        var parts  = trimmed.Split(',', StringSplitOptions.TrimEntries);
        var ranges = new List<EpisodeNumberRange>(parts.Length);

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                error = "Bölüm aralığı biçimi geçersiz.";
                return false;
            }

            var dashIndex = part.IndexOf('-', 1);
            if (dashIndex > 0)
            {
                var startRaw = part[..dashIndex].Trim();
                var endRaw   = part[(dashIndex + 1)..].Trim();

                if (!TryParsePositiveEpisodeNumber(startRaw, out var start)
                    || !TryParsePositiveEpisodeNumber(endRaw, out var end)
                    || end < start)
                {
                    error = "Bölüm aralığı geçersiz (örn. 1-12).";
                    return false;
                }

                ranges.Add(new EpisodeNumberRange(start, end));
            }
            else
            {
                if (!TryParsePositiveEpisodeNumber(part, out var number))
                {
                    error = "Bölüm numarası 0'dan büyük bir sayı olmalıdır.";
                    return false;
                }

                ranges.Add(new EpisodeNumberRange(number, number));
            }
        }

        if (ranges.Count == 1 && ranges[0].Start.Equals(ranges[0].End))
        {
            selector = new EpisodeSelector
            {
                Ranges        = ranges,
                SingleEpisode = ranges[0].Start
            };
            return true;
        }

        selector = new EpisodeSelector
        {
            Ranges = ranges
        };
        return true;
    }

    private static bool TryParsePositiveEpisodeNumber(string value, out double number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Contains(','))
        {
            return false;
        }

        return double.TryParse(value,
                               NumberStyles.AllowLeadingWhite
                               | NumberStyles.AllowTrailingWhite
                               | NumberStyles.AllowDecimalPoint,
                               CultureInfo.InvariantCulture,
                               out number)
               && double.IsFinite(number)
               && number > 0;
    }

    public static bool TryResolveProvider(IReadOnlyList<ProviderInfo> providers,
        string                                                        input,
        out string?                                                   provider,
        out string?                                                   error)
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
            error    = null;
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
