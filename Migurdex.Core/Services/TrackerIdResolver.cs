using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Core.Services;

public sealed class TrackerIdResolver : ITrackerIdResolver
{
    public const  double MinSimilarity         = 0.80;
    private const double AmbiguityGap          = 0.05;
    private const double ExactTieEpsilon       = 1e-9;
    private const double YearBonus             = 0.05;
    private const double FormatBonus           = 0.03;
    private const double SeasonBonus           = 0.05;
    private const double SeasonMismatchPenalty = -0.10;
    private const double ContainmentFloor      = 0.90;
    private const int    MaxTitlesToQuery      = 2;

    private readonly IReadOnlyList<IMetadataProvider> _providers;
    private readonly TrackerMappingStore              _store;
    private readonly ILogger<TrackerIdResolver>       _logger;

    public TrackerIdResolver(
        IEnumerable<IMetadataProvider> providers,
        TrackerMappingStore            store,
        ILogger<TrackerIdResolver>     logger)
    {
        _providers = providers.ToArray();
        _store     = store;
        _logger    = logger;
    }

    private IMetadataProvider? AniList => _providers.FirstOrDefault(p =>
                                                                        p.Name.Equals(
                                                                            "AniList",
                                                                            StringComparison.OrdinalIgnoreCase));

    private IMetadataProvider? Jikan => _providers.FirstOrDefault(p =>
                                                                      p.Name.Equals(
                                                                          "Jikan", StringComparison.OrdinalIgnoreCase));

    public async Task<TrackerResolveResult> ResolveFromProviderAsync(
        string                        providerName,
        string                        providerId,
        IEnumerable<string>           titles,
        int?                          year              = null,
        ContentFormat?                format            = null,
        IReadOnlyList<SeasonMapping>? seasonMappings    = null,
        CancellationToken             cancellationToken = default)
    {
        if (_store.TryGet(providerName, providerId, out var cached) && cached is not null && !string.IsNullOrWhiteSpace(cached.AniListId))
        {
            return new TrackerResolveResult
            {
                Entry      = cached,
                FromCache  = true,
                Candidates = []
            };
        }

        var shortcuts = seasonMappings?.Where(m => !string.IsNullOrWhiteSpace(m.AniListId) ||
                                                                  !string.IsNullOrWhiteSpace(m.MyAnimeListId))
                                           .ToArray() ?? [];
        foreach (var shortcut in shortcuts)
        {
            var meta = await ResolveFromTrackerAsync(shortcut.AniListId, shortcut.MyAnimeListId, cancellationToken);
            if (meta is null)
            {
                continue;
            }

            var anilistId = meta.AniListId ?? shortcut.AniListId;
            if (string.IsNullOrWhiteSpace(anilistId) && !string.IsNullOrWhiteSpace(meta.MyAnimeListId) && AniList is AniListProvider aniList)
            {
                var aniMeta = await aniList.GetMetadataByMalIdAsync(meta.MyAnimeListId, cancellationToken);
                anilistId = aniMeta?.AniListId;
            }

            if (string.IsNullOrWhiteSpace(anilistId))
            {
                continue;
            }

            var entry = new TrackerMappingEntry
            {
                ProviderName  = providerName,
                ProviderId    = providerId,
                AniListId     = anilistId,
                MyAnimeListId = meta.MyAnimeListId ?? shortcut.MyAnimeListId,
                MatchedTitle  = meta.Title,
                Score         = 1.0
            };
            _store.Set(entry);
            return new TrackerResolveResult { Entry = entry };
        }

        var distinctTitles = titles
                             .Where(t => !string.IsNullOrWhiteSpace(t))
                             .Select(t => t.Trim())
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Take(MaxTitlesToQuery + 2)
                             .ToArray();

        if (distinctTitles.Length == 0)
        {
            return new TrackerResolveResult { Ambiguous = true };
        }

        var queries    = BuildQueries(distinctTitles);
        var candidates = new Dictionary<string, MediaMetadata>(StringComparer.OrdinalIgnoreCase);

        if (AniList is not null)
        {
            foreach (var t in queries)
            {
                try
                {
                    var list = await AniList.SearchMetadataAsync(t, cancellationToken: cancellationToken);
                    foreach (var m in list)
                    {
                        candidates.TryAdd($"anilist:{m.ExternalId}", m);
                    }

                    if (candidates.Count >= 10)
                    {
                        break;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "tracker resolve: anilist search failed for '{Title}'", t);
                }
            }
        }

        if (candidates.Count == 0 && AniList is not null)
        {
            foreach (var token in FallbackTokens(distinctTitles))
            {
                try
                {
                    var list = await AniList.SearchMetadataAsync(token, cancellationToken: cancellationToken);
                    foreach (var m in list)
                    {
                        candidates.TryAdd($"anilist:{m.ExternalId}", m);
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "tracker resolve: token fallback failed for '{Token}'", token);
                }
            }
        }

        if (candidates.Count == 0 && Jikan is not null)
        {
            try
            {
                foreach (var m in await Jikan.SearchMetadataAsync(distinctTitles[0],
                                                                  cancellationToken: cancellationToken))
                {
                    candidates.TryAdd($"jikan:{m.ExternalId}", m);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "tracker resolve: jikan search failed for '{Title}'", distinctTitles[0]);
            }
        }

        if (candidates.Count == 0)
        {
            return new TrackerResolveResult { Ambiguous = true };
        }

        var (_, providerSeason) = TitleNormalizer.SplitSeason(distinctTitles[0]);
        int? providerYear = year ?? TryParseYear(distinctTitles.Skip(1).FirstOrDefault());

        var ranked = candidates.Values
                               .Select(m => (Metadata: m,
                                             Raw: ScoreCandidate(m, distinctTitles, providerSeason, providerYear,
                                                                 format)))
                               .OrderByDescending(x => x.Raw)
                               .Take(3)
                               .ToArray();

        var scored = ranked
                     .Select(x => new TrackerCandidate
                     {
                         Metadata = x.Metadata,
                         Score    = Math.Min(x.Raw, 1.0)
                     })
                     .ToArray();

        var top = ranked[0];

        if (top.Raw < MinSimilarity)
        {
            _logger.LogInformation("tracker resolve: below threshold ({Score:F2}) for '{Title}'",
                                   top.Raw, distinctTitles[0]);
            return new TrackerResolveResult { Ambiguous = true, Candidates = scored };
        }

        if (ranked.Length > 1 && top.Raw - ranked[1].Raw < AmbiguityGap)
        {
            var tied = ranked.TakeWhile(x => top.Raw - x.Raw < ExactTieEpsilon).ToArray();
            if (tied.Length > 1)
            {
                var winner = tied
                             .OrderByDescending(x => x.Metadata.TotalEpisodes ?? 0)
                             .ThenByDescending(x => x.Metadata.Year ?? 0)
                             .First();
                var winnerEps = winner.Metadata.TotalEpisodes ?? 0;
                var uniqueMax = winnerEps > 0 && tied.All(x =>
                                                              ReferenceEquals(x.Metadata, winner.Metadata)
                                                              || (x.Metadata.TotalEpisodes ?? 0) < winnerEps);

                if (uniqueMax)
                {
                    _logger.LogInformation(
                        "tracker resolve: exact tie broken by episodes for '{Title}' -> {Id}",
                        distinctTitles[0], winner.Metadata.ExternalId);
                    return await BuildResolvedAsync(providerName, providerId, winner, scored, cancellationToken);
                }
            }

            _logger.LogInformation("tracker resolve: ambiguous ({A:F2} vs {B:F2}) for '{Title}'",
                                   top.Raw, ranked[1].Raw, distinctTitles[0]);
            return new TrackerResolveResult { Ambiguous = true, Candidates = scored };
        }

        return await BuildResolvedAsync(providerName, providerId, top, scored, cancellationToken);
    }

    private async Task<TrackerResolveResult> BuildResolvedAsync(
        string                               providerName,
        string                               providerId,
        (MediaMetadata Metadata, double Raw) top,
        TrackerCandidate[]                   scored,
        CancellationToken                    cancellationToken = default)
    {
        var anilistId = top.Metadata.AniListId
                        ?? (top.Metadata.Source == MetadataSource.AniList ? top.Metadata.ExternalId : null);
        var malId = top.Metadata.MyAnimeListId
                    ?? (top.Metadata.Source == MetadataSource.Jikan ? top.Metadata.ExternalId : null);

        if (string.IsNullOrWhiteSpace(anilistId) && !string.IsNullOrWhiteSpace(malId) &&
            AniList is AniListProvider aniList)
        {
            try
            {
                var aniMeta = await aniList.GetMetadataByMalIdAsync(malId, cancellationToken);
                anilistId = aniMeta?.AniListId ?? aniMeta?.ExternalId;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "tracker resolve: mal->anilist bridge failed for '{MalId}'", malId);
            }
        }

        if (string.IsNullOrWhiteSpace(anilistId))
        {
            _logger.LogInformation("tracker resolve: mal-only entry for '{Title}' has no anilist id, asking user",
                                   top.Metadata.Title);
            return new TrackerResolveResult { Ambiguous = true, Candidates = scored };
        }

        var resolved = new TrackerMappingEntry
        {
            ProviderName  = providerName,
            ProviderId    = providerId,
            AniListId     = anilistId,
            MyAnimeListId = malId,
            MatchedTitle  = top.Metadata.Title,
            Score         = Math.Min(top.Raw, 1.0)
        };

        _store.Set(resolved);

        return new TrackerResolveResult
        {
            Entry      = resolved,
            Candidates = scored
        };
    }

    public async Task<MediaMetadata?> ResolveFromTrackerAsync(
        string?           anilistId,
        string?           malId,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(anilistId) && AniList is not null)
        {
            try
            {
                var meta = await AniList.GetMetadataByIdAsync(anilistId.Trim(), cancellationToken);
                if (meta is not null)
                {
                    return meta;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "tracker lookup: anilist id failed '{Id}'", anilistId);
            }
        }

        if (!string.IsNullOrWhiteSpace(malId))
        {
            if (AniList is AniListProvider aniList)
            {
                try
                {
                    var meta = await aniList.GetMetadataByMalIdAsync(malId.Trim(), cancellationToken);
                    if (meta is not null)
                    {
                        return meta;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "tracker lookup: anilist mal bridge failed '{Id}'", malId);
                }
            }

            if (Jikan is not null)
            {
                try
                {
                    return await Jikan.GetMetadataByIdAsync(malId.Trim(), cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "tracker lookup: jikan id failed '{Id}'", malId);
                }
            }
        }

        return null;
    }

    private static double ScoreCandidate(
        MediaMetadata  candidate,
        string[]       providerTitles,
        int            providerSeason,
        int?           providerYear,
        ContentFormat? providerFormat)
    {
        var variants = new List<string> { candidate.Title };
        if (!string.IsNullOrWhiteSpace(candidate.EnglishTitle))
        {
            variants.Add(candidate.EnglishTitle);
        }

        if (!string.IsNullOrWhiteSpace(candidate.RomajiTitle))
        {
            variants.Add(candidate.RomajiTitle);
        }

        if (!string.IsNullOrWhiteSpace(candidate.JapaneseTitle))
        {
            variants.Add(candidate.JapaneseTitle);
        }

        variants.AddRange(candidate.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));

        var best = 0.0;
        foreach (var variant in variants.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (candBase, candSeason) = TitleNormalizer.SplitSeason(variant);

            var variantBest = 0.0;
            foreach (var pt in providerTitles)
            {
                var (ptBase, _) = TitleNormalizer.SplitSeason(pt);
                var exactRatio  = TitleNormalizer.Ratio(variant, pt);
                var baseRatio   = TitleNormalizer.Ratio(candBase, string.IsNullOrEmpty(ptBase) ? pt : ptBase);

                if (candSeason > 1 && providerSeason == 1)
                {
                    baseRatio += SeasonMismatchPenalty;
                }

                variantBest = Math.Max(variantBest, Math.Max(exactRatio, baseRatio));
            }

            if (providerSeason > 1)
            {
                variantBest += candSeason == providerSeason ? SeasonBonus : SeasonMismatchPenalty;
            }

            if (variantBest < ContainmentFloor
                && (candSeason == providerSeason || providerSeason <= 1)
                && VariantContained(variant, candBase, providerTitles))
            {
                variantBest = ContainmentFloor;
            }

            best = Math.Max(best, variantBest);
        }

        if (providerYear.HasValue && candidate.Year.HasValue && providerYear == candidate.Year)
        {
            best += YearBonus;
        }

        if (providerFormat.HasValue && providerFormat != ContentFormat.Unknown
                                    && candidate.Format == providerFormat)
        {
            best += FormatBonus;
        }

        return best;
    }

    internal static IReadOnlyList<string> BuildQueries(string[] distinctTitles)
    {
        var queries = new List<string>();
        foreach (var t in distinctTitles.Take(MaxTitlesToQuery))
        {
            if (!queries.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(t);
            }

            var (baseTitle, _) = TitleNormalizer.SplitSeason(t);
            if (baseTitle.Length >= 3
                && !queries.Contains(baseTitle, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(baseTitle);
            }
        }

        var longestToken = distinctTitles
                           .SelectMany(t => TitleNormalizer.Normalize(t)
                                                           .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                           .Where(w => w.Length >= 5)
                           .OrderByDescending(w => w.Length)
                           .FirstOrDefault();
        if (longestToken is not null
            && !queries.Contains(longestToken, StringComparer.OrdinalIgnoreCase))
        {
            queries.Add(longestToken);
        }

        return queries;
    }

    internal static bool VariantContained(string variant, string candBase, string[] providerTitles)
    {
        var variantTokens = TitleNormalizer.Normalize(variant)
                                           .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var baseTokens = TitleNormalizer.Normalize(candBase)
                                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pt in providerTitles)
        {
            var ptTokens = TitleNormalizer.Normalize(pt)
                                          .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var (ptBase, _) = TitleNormalizer.SplitSeason(pt);
            var ptBaseTokens = ptBase.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (variantTokens.Length >= 2 && IsSubset(variantTokens, ptTokens))
            {
                return true;
            }

            if (baseTokens.Length >= 2 && IsSubset(baseTokens, ptBaseTokens))
            {
                return true;
            }

            if (IsValidQuerySubset(ptTokens, variantTokens))
            {
                return true;
            }

            if (IsValidQuerySubset(ptBaseTokens, baseTokens))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidQuerySubset(string[] queryTokens, string[] candidateTokens)
    {
        if (queryTokens.Length == 0 || candidateTokens.Length == 0 || queryTokens.Length > candidateTokens.Length)
        {
            return false;
        }

        if (queryTokens.Length == 1 && queryTokens[0].Length < 5)
        {
            return false;
        }

        return IsSubset(queryTokens, candidateTokens);
    }

    private static bool IsSubset(string[] small, string[] big)
    {
        if (small.Length == 0 || big.Length == 0 || small.Length > big.Length)
        {
            return false;
        }

        var set = new HashSet<string>(big, StringComparer.Ordinal);
        return small.All(set.Contains);
    }

    internal static IReadOnlyList<string> FallbackTokens(string[] distinctTitles)
    {
        return distinctTitles
               .SelectMany(t => TitleNormalizer.Normalize(t).Split(' ', StringSplitOptions.RemoveEmptyEntries))
               .Where(w => w.Length >= 4)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderByDescending(w => w.Length)
               .Take(2)
               .ToArray();
    }

    private static int? TryParseYear(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var m = System.Text.RegularExpressions.Regex.Match(s, @"\b(19\d{2}|20\d{2})\b");
        return m.Success && int.TryParse(m.Value, out var y) ? y : null;
    }
}
