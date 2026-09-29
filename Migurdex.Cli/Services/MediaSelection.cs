using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

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
