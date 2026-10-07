using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Core.Services;

public sealed class CanonicalResolver : ICanonicalResolver
{
    private readonly IReadOnlyList<IMetadataProvider> _providers;
    private readonly ILogger<CanonicalResolver> _logger;

    public CanonicalResolver(
        IEnumerable<IMetadataProvider> providers,
        ILogger<CanonicalResolver> logger)
    {
        _providers = providers.ToArray();
        _logger = logger;
    }

    private IMetadataProvider? AniList =>
        _providers.FirstOrDefault(p => p.Name.Equals("AniList", StringComparison.OrdinalIgnoreCase));

    private IMetadataProvider? Jikan =>
        _providers.FirstOrDefault(p => p.Name.Equals("Jikan", StringComparison.OrdinalIgnoreCase));

    private IMetadataProvider? Mal =>
        _providers.FirstOrDefault(p => p.Name.Equals("MyAnimeList", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<CanonicalAnime>> ResolveCanonicalAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var q = query.Trim();
        if (q.Length > 200)
        {
            q = q[..200];
        }

        var (baseTitle, detectedSeason) = TitleNormalizer.SplitSeason(q);
        var normalizedQuery = TitleNormalizer.Normalize(q);
        string[] searchQueries = !string.IsNullOrWhiteSpace(baseTitle)
            && !baseTitle.Equals(normalizedQuery, StringComparison.Ordinal)
            ? [q, baseTitle]
            : [q];

        var anilistTask = AniList is not null
            ? SafeSearchManyAsync(AniList, searchQueries, cancellationToken)
            : Task.FromResult<IReadOnlyList<MediaMetadata>>([]);
        var malProvider = Mal ?? Jikan;
        var malTask = malProvider is not null
            ? SafeSearchManyAsync(malProvider, searchQueries, cancellationToken)
            : Task.FromResult<IReadOnlyList<MediaMetadata>>([]);

        await Task.WhenAll(anilistTask, malTask);

        var merged = new Dictionary<string, CanonicalAnime>(StringComparer.OrdinalIgnoreCase);
        var malIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in anilistTask.Result.Concat(malTask.Result))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var anilistId = m.AniListId
                ?? (m.Source == MetadataSource.AniList ? m.ExternalId : null);
            var malId = m.MyAnimeListId
                ?? (m.Source is MetadataSource.Jikan or MetadataSource.MyAnimeList ? m.ExternalId : null);
            anilistId = string.IsNullOrWhiteSpace(anilistId) ? null : anilistId.Trim();
            malId = string.IsNullOrWhiteSpace(malId) ? null : malId.Trim();

            string? existingKey = null;
            if (anilistId is not null && merged.ContainsKey($"anilist:{anilistId}"))
            {
                existingKey = $"anilist:{anilistId}";
            }
            else if (malId is not null && malIndex.TryGetValue(malId, out var mappedKey))
            {
                existingKey = mappedKey;
            }

            if (existingKey is not null)
            {
                merged[existingKey].Merge(m);
                var entry = merged[existingKey];
                if (!string.IsNullOrWhiteSpace(entry.MyAnimeListId))
                {
                    malIndex[entry.MyAnimeListId] = existingKey;
                }
            }
            else
            {
                var key = anilistId is not null
                    ? $"anilist:{anilistId}"
                    : malId is not null
                        ? $"mal:{malId}"
                        : $"title:{m.Title.Trim().ToLowerInvariant()}";
                merged[key] = CanonicalAnime.FromMetadata(m);
                if (malId is not null)
                {
                    malIndex[malId] = key;
                }
            }
        }

        if (merged.Count == 0)
        {
            return [];
        }

        var ranked = merged.Values
            .Select(c => (Anime: c, Score: BestTitleScore(c, q, detectedSeason)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Anime.Year ?? int.MaxValue)
            .ThenByDescending(x => x.Anime.Score ?? 0)
            .Take(10)
            .Select(x => x.Anime)
            .ToArray();

        return ranked;
    }

    public IReadOnlyList<ProviderMatch> MatchProviders(
        IReadOnlyList<SearchResult> providerResults,
        CanonicalAnime canonical)
    {
        if (providerResults.Count == 0)
        {
            return [];
        }

        var canonicalTitles = canonical.GetAllTitles().ToArray();
        var (_, canonicalSeason) = TitleNormalizer.SplitSeason(canonical.Title);
        var canonicalTokens = new HashSet<string>(
            canonicalTitles.SelectMany(t => TitleNormalizer.Normalize(t)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.Ordinal);

        var matches = new List<ProviderMatch>(providerResults.Count);
        foreach (var r in providerResults)
        {
            var providerTitles = r.GetAllTitles().ToArray();
            if (providerTitles.Length == 0)
            {
                providerTitles = [r.Title];
            }

            var best = 0.0;
            foreach (var ct in canonicalTitles)
            {
                var (ctBase, ctSeason) = TitleNormalizer.SplitSeason(ct);
                var contained = TitleMatcher.VariantContained(ct, ctBase, providerTitles);
                foreach (var pt in providerTitles)
                {
                    var s = TitleMatcher.ScorePair(ct, pt);
                    if (s < TitleMatcher.ContainmentFloor && contained)
                    {
                        s = TitleMatcher.ContainmentFloor;
                        s = TitleMatcher.ApplyCoveragePenalty(s, pt, canonicalTokens);
                    }

                    var (_, ptSeason) = TitleNormalizer.SplitSeason(pt);
                    var effectiveCandSeason = ctSeason > 1 ? ctSeason : canonicalSeason;

                    if (effectiveCandSeason > 1 || ptSeason > 1)
                    {
                        if (effectiveCandSeason == ptSeason)
                        {
                            s += TitleMatcher.SeasonBonus;
                        }
                        else
                        {
                            s += TitleMatcher.SeasonMismatchPenalty;
                        }
                    }

                    best = Math.Max(best, s);
                }
            }

            if (!string.IsNullOrWhiteSpace(r.Year)
                && canonical.Year.HasValue
                && int.TryParse(r.Year.AsSpan(0, Math.Min(4, r.Year.Length)), out var py))
            {
                best += py == canonical.Year.Value ? TitleMatcher.YearBonus : TitleMatcher.YearMismatchPenalty;
            }

            if (r.Format != ContentFormat.Unknown && r.Format == canonical.Format)
            {
                best += TitleMatcher.FormatBonus;
            }

            best = Math.Min(best, 1.0);
            matches.Add(new ProviderMatch
            {
                Result = r,
                Score = best,
                Matched = best >= TitleMatcher.MinSimilarity,
            });
        }

        return matches
            .OrderByDescending(m => m.Matched)
            .ThenByDescending(m => m.Score)
            .ThenBy(m => m.Result.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<MediaMetadata>> SafeSearchAsync(
        IMetadataProvider provider,
        string query,
        CancellationToken ct)
    {
        try
        {
            return await provider.SearchMetadataAsync(query, cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "canonical resolve: {Provider} search failed for '{Query}'", provider.Name, query);
            return [];
        }
    }

    private async Task<IReadOnlyList<MediaMetadata>> SafeSearchManyAsync(
        IMetadataProvider provider,
        string[] queries,
        CancellationToken ct)
    {
        if (queries.Length == 1)
        {
            return await SafeSearchAsync(provider, queries[0], ct);
        }

        var all = new List<MediaMetadata>();
        foreach (var query in queries)
        {
            all.AddRange(await SafeSearchAsync(provider, query, ct));
        }

        return all;
    }

    private static double BestTitleScore(CanonicalAnime c, string query, int querySeason = 1)
    {
        const double synonymCap = TitleMatcher.ContainmentFloor - 0.05;

        var primary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new[] { c.Title, c.EnglishTitle, c.RomajiTitle, c.JapaneseTitle })
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                primary.Add(p.Trim());
            }
        }

        var queryTokens = TitleNormalizer.Normalize(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var singleTokenQuery = queryTokens.Length == 1 && queryTokens[0].Length >= 3
            ? queryTokens[0]
            : null;

        var best = 0.0;
        foreach (var t in c.GetAllTitles())
        {
            var isPrimary = primary.Contains(t.Trim());
            var (tb, titleSeason) = TitleNormalizer.SplitSeason(t);
            var s = TitleMatcher.ScorePair(t, query);
            if (s < TitleMatcher.ContainmentFloor
                && TitleMatcher.VariantContained(t, tb, [query]))
            {
                s = TitleMatcher.ContainmentFloor;
            }

            if (querySeason > 1)
            {
                s += titleSeason == querySeason ? TitleMatcher.SeasonBonus : TitleMatcher.SeasonMismatchPenalty;
            }

            if (singleTokenQuery is not null && isPrimary)
            {
                var titleTokens = TitleNormalizer.Normalize(t)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (titleTokens.Contains(singleTokenQuery, StringComparer.Ordinal))
                {
                    s = Math.Max(s, TitleMatcher.ContainmentFloor);
                }
            }

            if (!isPrimary && s > synonymCap)
            {
                s = synonymCap;
            }

            best = Math.Max(best, s);
        }

        return best;
    }
}
