using Microsoft.Extensions.Logging;
using Migurdex.Core.Services;
using Migurdex.Core.Utils;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.PluginSystem;

public partial class MetadataManager
{
    private readonly ILogger<MetadataManager>       _logger;
    private readonly IEnumerable<IMetadataProvider> _providers;

    public MetadataManager(IEnumerable<IMetadataProvider> providers, ILogger<MetadataManager> logger)
    {
        _providers = providers;
        _logger    = logger;
    }

    public async Task<MediaMetadata?> FindBestMatchOrOverrideAsync(AnimeDetails details,
        ContentFormat                                                           expectedFormat = ContentFormat.Unknown,
        int?                                                                    year = null,
        int?                                                                    season = null,
        CancellationToken                                                       cancellationToken = default)
    {
        var aniListProvider =
            _providers.FirstOrDefault(p => p.Name.Equals("AniList", StringComparison.OrdinalIgnoreCase)) as
                AniListProvider;

        var jikanProvider = _providers.FirstOrDefault(p => p.Name.Equals("Jikan", StringComparison.OrdinalIgnoreCase));

        var targetSeason = season ?? 1;

        string? activeAniListId = null;
        string? activeMalId     = null;

        var seasonMapping = details.SeasonMappings.FirstOrDefault(m => m.SeasonNumber == targetSeason);
        if (seasonMapping != null)
        {
            activeAniListId = seasonMapping.AniListId;
            activeMalId     = seasonMapping.MyAnimeListId;
        }

        if (string.IsNullOrEmpty(activeAniListId) && string.IsNullOrEmpty(activeMalId))
        {
            foreach (var rawTitle in details.GetAllTitles())
            {
                var englishTitle = NormalizeTitleToEnglish(rawTitle);
                var titlesToTry  = new[] { rawTitle, englishTitle }.Where(t => !string.IsNullOrEmpty(t)).Distinct();

                foreach (var titleToTry in titlesToTry)
                {
                    try
                    {
                        var directMatch = await FindBestMatchAsync(titleToTry, expectedFormat, year, cancellationToken);
                        if (directMatch != null)
                        {
                            var otherMappedAniListIds = details.SeasonMappings
                                                               .Where(m => m.SeasonNumber != targetSeason
                                                                           && !string.IsNullOrEmpty(m.AniListId))
                                                               .Select(m => m.AniListId)
                                                               .ToHashSet();

                            var otherMappedMalIds = details.SeasonMappings
                                                           .Where(m => m.SeasonNumber != targetSeason
                                                                       && !string.IsNullOrEmpty(m.MyAnimeListId))
                                                           .Select(m => m.MyAnimeListId)
                                                           .ToHashSet();

                            var isDuplicate =
                                (directMatch.Source == MetadataSource.AniList
                                 && otherMappedAniListIds.Contains(directMatch.ExternalId))
                                || (directMatch.Source == MetadataSource.Jikan
                                    && otherMappedMalIds.Contains(directMatch.ExternalId));

                            if (!isDuplicate)
                            {
                                if (directMatch.Source == MetadataSource.AniList)
                                {
                                    activeAniListId = directMatch.ExternalId;
                                    activeMalId     = directMatch.MyAnimeListId;
                                }
                                else if (directMatch.Source == MetadataSource.Jikan)
                                {
                                    activeMalId     = directMatch.ExternalId;
                                    activeAniListId = directMatch.AniListId;
                                }

                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "failed to fetch override match for title: {Title}", titleToTry);
                    }
                }

                if (!string.IsNullOrEmpty(activeAniListId) || !string.IsNullOrEmpty(activeMalId))
                {
                    break;
                }
            }
        }

        // 1. cross-ref id
        if (string.IsNullOrEmpty(activeAniListId) && !string.IsNullOrEmpty(activeMalId) && aniListProvider != null)
        {
            try
            {
                var crossMeta = await aniListProvider.GetMetadataByMalIdAsync(activeMalId, cancellationToken);
                if (crossMeta != null && !string.IsNullOrEmpty(crossMeta.AniListId))
                {
                    activeAniListId = crossMeta.AniListId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to cross-reference MAL ID: {MalId} on AniList", activeMalId);
            }
        }

        // 2. sequel resolution layer
        if (string.IsNullOrEmpty(activeAniListId) && targetSeason > 1 && aniListProvider != null)
        {
            var seedAniListId = details.SeasonMappings.FirstOrDefault(m => m.SeasonNumber == 1)?.AniListId;

            if (string.IsNullOrEmpty(seedAniListId))
            {
                var baseTitle = GetBaseTitle(details.Title);
                if (!string.IsNullOrEmpty(baseTitle) && baseTitle != details.Title)
                {
                    try
                    {
                        var baseMatch = await FindBestMatchAsync(baseTitle, expectedFormat, year, cancellationToken);
                        if (baseMatch != null)
                        {
                            seedAniListId = baseMatch.Source == MetadataSource.AniList
                                                ? baseMatch.ExternalId
                                                : baseMatch.AniListId;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "failed to resolve seed AniList ID for title: {Title}", baseTitle);
                    }
                }
            }

            if (!string.IsNullOrEmpty(seedAniListId))
            {
                try
                {
                    var targetId =
                        await ResolveAniListSequelIdAsync(aniListProvider,
                                                          seedAniListId,
                                                          targetSeason,
                                                          cancellationToken);
                    if (!string.IsNullOrEmpty(targetId))
                    {
                        activeAniListId = targetId;

                        activeMalId = null;

                        var crossMeta = await aniListProvider.GetMetadataByIdAsync(activeAniListId, cancellationToken);
                        if (crossMeta != null && !string.IsNullOrEmpty(crossMeta.MyAnimeListId))
                        {
                            activeMalId = crossMeta.MyAnimeListId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                                       "failed to resolve AniList sequel ID for seed: {SeedId}, Season: {Season}",
                                       seedAniListId,
                                       targetSeason);
                }
            }
        }

        // 2.5 myanimelist sequel resolution layer
        if (string.IsNullOrEmpty(activeMalId) && targetSeason > 1 && jikanProvider is JikanProvider jp)
        {
            var seedMalId = details.SeasonMappings.FirstOrDefault(m => m.SeasonNumber == 1)?.MyAnimeListId;

            if (string.IsNullOrEmpty(seedMalId))
            {
                var baseTitle = GetBaseTitle(details.Title);
                if (!string.IsNullOrEmpty(baseTitle) && baseTitle != details.Title)
                {
                    try
                    {
                        var baseMatch = await FindBestMatchAsync(baseTitle, expectedFormat, year, cancellationToken);
                        if (baseMatch != null)
                        {
                            seedMalId = baseMatch.Source == MetadataSource.Jikan
                                            ? baseMatch.ExternalId
                                            : baseMatch.MyAnimeListId;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "failed to resolve seed MAL ID for title: {Title}", baseTitle);
                    }
                }
            }

            if (!string.IsNullOrEmpty(seedMalId))
            {
                try
                {
                    var targetId =
                        await ResolveMyAnimeListSequelIdAsync(jp, seedMalId, targetSeason, cancellationToken);
                    if (!string.IsNullOrEmpty(targetId))
                    {
                        activeMalId = targetId;

                        if (aniListProvider != null)
                        {
                            var crossMeta =
                                await aniListProvider.GetMetadataByMalIdAsync(activeMalId, cancellationToken);
                            if (crossMeta != null && !string.IsNullOrEmpty(crossMeta.AniListId))
                            {
                                activeAniListId = crossMeta.AniListId;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                                       "failed to resolve MyAnimeList sequel ID for seed: {SeedId}, Season: {Season}",
                                       seedMalId,
                                       targetSeason);
                }
            }
        }

        // 3. aniList id
        if (!string.IsNullOrEmpty(activeAniListId) && aniListProvider != null)
        {
            try
            {
                var meta = await aniListProvider.GetMetadataByIdAsync(activeAniListId, cancellationToken);
                if (meta != null)
                {
                    return meta;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to fetch metadata by AniList ID: {AniListId}", activeAniListId);
            }
        }

        // 4. myanimelist id
        if (!string.IsNullOrEmpty(activeMalId) && jikanProvider != null)
        {
            try
            {
                var meta = await jikanProvider.GetMetadataByIdAsync(activeMalId, cancellationToken);
                if (meta != null)
                {
                    return meta;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to fetch metadata by MAL ID: {MalId}", activeMalId);
            }
        }

        var candidate = await FindBestMatchAsync(details.GetAllTitles(), expectedFormat, year, cancellationToken);
        if (candidate != null && targetSeason > 1)
        {
            var usedAniListIds = details.SeasonMappings
                                        .Where(m => m.SeasonNumber != targetSeason
                                                    && !string.IsNullOrEmpty(m.AniListId))
                                        .Select(m => m.AniListId)
                                        .ToHashSet();

            var usedMalIds = details.SeasonMappings
                                    .Where(m => m.SeasonNumber != targetSeason
                                                && !string.IsNullOrEmpty(m.MyAnimeListId))
                                    .Select(m => m.MyAnimeListId)
                                    .ToHashSet();

            var isDuplicate =
                (candidate.Source == MetadataSource.AniList && usedAniListIds.Contains(candidate.ExternalId))
                || (candidate.Source == MetadataSource.Jikan && usedMalIds.Contains(candidate.ExternalId));

            if (isDuplicate)
            {
                return null;
            }
        }

        return candidate;
    }

    private async Task<string?> ResolveAniListSequelIdAsync(AniListProvider aniListProvider,
        string                                                              baseAniListId,
        int                                                                 targetSeason,
        CancellationToken                                                   cancellationToken = default)
    {
        var currentId = baseAniListId;

        for (var currentSeason = 1; currentSeason < targetSeason; currentSeason++)
        {
            var sequelId = await QueryAniListSequelIdDirectly(aniListProvider, currentId, cancellationToken);
            if (string.IsNullOrEmpty(sequelId))
            {
                break;
            }

            currentId = sequelId;
        }

        return currentId == baseAniListId ? null : currentId;
    }

    private async Task<string?> ResolveMyAnimeListSequelIdAsync(JikanProvider jikanProvider,
        string                                                                baseMalId,
        int                                                                   targetSeason,
        CancellationToken                                                     cancellationToken = default)
    {
        var currentId = baseMalId;

        for (var currentSeason = 1; currentSeason < targetSeason; currentSeason++)
        {
            var sequelId = await jikanProvider.QuerySequelIdAsync(currentId, cancellationToken);
            if (string.IsNullOrEmpty(sequelId))
            {
                break;
            }

            currentId = sequelId;
        }

        return currentId == baseMalId ? null : currentId;
    }

    private async Task<string?> QueryAniListSequelIdDirectly(AniListProvider provider,
        string                                                               id,
        CancellationToken                                                    cancellationToken = default)
    {
        try
        {
            return await provider.QuerySequelIdAsync(id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to query AniList sequel ID directly for ID: {Id}", id);

            return null;
        }
    }

    public async Task<MediaMetadata?> FindBestMatchAsync(IEnumerable<string> titles,
        ContentFormat                                                        expectedFormat    = ContentFormat.Unknown,
        int?                                                                 year              = null,
        CancellationToken                                                    cancellationToken = default)
    {
        MediaMetadata? bestCandidate = null;
        double         bestScore     = -1;

        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var candidate = await FindBestMatchAsync(title, expectedFormat, year, cancellationToken);
            if (candidate != null)
            {
                var score = CalculateScore(candidate, title, expectedFormat, year);
                if (score > bestScore)
                {
                    bestScore     = score;
                    bestCandidate = candidate;
                }
            }
        }

        return bestCandidate;
    }

    public async Task<MediaMetadata?> FindBestMatchAsync(string title,
        ContentFormat                                           expectedFormat    = ContentFormat.Unknown,
        int?                                                    year              = null,
        CancellationToken                                       cancellationToken = default)
    {
        var allCandidates = new List<MediaMetadata>();

        foreach (var provider in _providers)
        {
            try
            {
                var results = await provider.SearchMetadataAsync(title, expectedFormat, cancellationToken);
                allCandidates.AddRange(results);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to search metadata using provider: {Provider}", provider.Name);
            }
        }

        if (!allCandidates.Any())
        {
            return null;
        }

        var scoredResults = allCandidates.Select(m => new
                                         {
                                             Metadata = m,
                                             Score    = CalculateScore(m, title, expectedFormat, year)
                                         })
                                         .OrderByDescending(x => x.Score)
                                         .ToList();

        return scoredResults.FirstOrDefault()?.Metadata;
    }

    private static double CalculateScore(MediaMetadata m, string searchTitle, ContentFormat expectedFormat, int? year)
    {
        double score = 0;

        var titleScores = new List<double>
        {
            FuzzyMatcher.CalculateSimilarity(searchTitle, m.Title),
            FuzzyMatcher.CalculateSimilarity(searchTitle, m.EnglishTitle ?? ""),
            FuzzyMatcher.CalculateSimilarity(searchTitle, m.RomajiTitle ?? ""),
            FuzzyMatcher.CalculateSimilarity(searchTitle, m.JapaneseTitle ?? ""),
            FuzzyMatcher.CalculateSimilarity(searchTitle, m.OriginalTitle ?? "")
        };

        if (m.Synonyms.Any())
        {
            titleScores.AddRange(m.Synonyms.Select(s => FuzzyMatcher.CalculateSimilarity(searchTitle, s)));
        }

        score += titleScores.Max();

        if (expectedFormat != ContentFormat.Unknown && m.Format != ContentFormat.Unknown)
        {
            if (m.Format == expectedFormat)
            {
                score += 0.5;
            }
        }

        if (year.HasValue && m.Year.HasValue)
        {
            if (m.Year == year)
            {
                score += 0.3;
            }
            else if (Math.Abs(m.Year.Value - year.Value) <= 1)
            {
                score += 0.1;
            }
        }

        return score;
    }

    [GeneratedRegex(@"\b(?:Sezonu|Sezon)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SezonuSezonRegex();

    [GeneratedRegex(@"\b(?:Kısım|Partı)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KisimPartiRegex();

    [GeneratedRegex(@"\b(\d+)\.?\s*Season\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumSeasonRegex();

    [GeneratedRegex(@"\b(\d+)\.?\s*Part\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumPartRegex();

    [GeneratedRegex(@"\s*\b(?:Season|Sezon|Part|Kısım)\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex CleanSeasonPartRegex();

    [GeneratedRegex(@"\s*\b\d+\.?(?:st|nd|rd|th)?\.?\s*(?:Season|Sezon|Part|Kısım)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CleanNumSeasonPartRegex();

    [GeneratedRegex(@"\s*\b(?:The\s+)?Final\s+(?:Season|Sezonu)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CleanFinalSeasonRegex();

    [GeneratedRegex(@"\s*\b(I|II|III|IV|V|VI|VII|VIII|IX|X)\b$", RegexOptions.IgnoreCase)]
    private static partial Regex CleanRomanNumeralRegex();

    private static string NormalizeTitleToEnglish(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var normalized = title;

        // "Sezonu/Sezon" -> "Season", "Kısım/Partı" -> "Part"
        normalized = SezonuSezonRegex().Replace(normalized, "Season");

        normalized = KisimPartiRegex().Replace(normalized, "Part");

        // "X. Season" to "Season X"
        normalized = NumSeasonRegex().Replace(normalized, "Season $1");

        // "X. Part" to "Part X"
        normalized = NumPartRegex().Replace(normalized, "Part $1");

        return normalized.Trim();
    }

    private static string GetBaseTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var clean = title;

        // "Season X", "Sezon X", "Part X", "Kısım X"
        clean = CleanSeasonPartRegex().Replace(clean, "");

        // "X. Sezon", "X. Kısım", "Xnd Season", "X. Part"
        clean = CleanNumSeasonPartRegex().Replace(clean, "");

        // "The Final Season" or "Final Sezonu"
        clean = CleanFinalSeasonRegex().Replace(clean, "");

        // Roman numerals at the end of the title
        clean = CleanRomanNumeralRegex().Replace(clean, "");

        return clean.Trim(' ', '-', ':', ',');
    }
}
