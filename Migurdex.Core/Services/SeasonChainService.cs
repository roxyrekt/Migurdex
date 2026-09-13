using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Core.Services;

public sealed class SeasonChainService : ISeasonChainService
{
    private const           int      MaxChainLength = 8;
    private static readonly TimeSpan ChainCacheTtl  = TimeSpan.FromHours(6);
    private static readonly TimeSpan ErrorCacheTtl  = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<IMetadataProvider>                                    _providers;
    private readonly IMemoryCache                                                        _cache;
    private readonly ILogger<SeasonChainService>                                         _logger;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<RelationEdge>>>? _relationsResolver;

    public SeasonChainService(
        IEnumerable<IMetadataProvider>                                      providers,
        IMemoryCache                                                        cache,
        ILogger<SeasonChainService>                                         logger,
        Func<string, CancellationToken, Task<IReadOnlyList<RelationEdge>>>? relationsResolver = null)
    {
        _providers         = providers.ToArray();
        _cache             = cache;
        _logger            = logger;
        _relationsResolver = relationsResolver;
    }

    private IMetadataProvider? AniList => _providers.FirstOrDefault(p =>
                                                                        p.Name.Equals(
                                                                            "AniList",
                                                                            StringComparison.OrdinalIgnoreCase));

    public async Task<SeasonChain?> GetSeasonChainAsync(string anilistId, CancellationToken cancellationToken = default)
    {
        anilistId = anilistId.Trim();
        if (string.IsNullOrEmpty(anilistId))
        {
            return null;
        }

        var cacheKey = $"seasonchain:{anilistId}";
        if (_cache.TryGetValue<SeasonChain>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        if (AniList is null)
        {
            return null;
        }

        MediaMetadata? root;
        try
        {
            root = await AniList.GetMetadataByIdAsync(anilistId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "season chain: root fetch failed '{Id}'", anilistId);
            return null;
        }

        if (root is null)
        {
            return null;
        }

        var path   = new List<MediaMetadata> { root };
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.ExternalId };
        var cursor = root.ExternalId;
        var memo   = new Dictionary<string, IReadOnlyList<RelationEdge>>(StringComparer.OrdinalIgnoreCase);

        // Traversal sırasında hata olursa zincir eksik kalabilir; o zaman
        // 6 saatlik cache'e yazılmaz (kısa TTL ile tekrar denenir).
        var fetchError = false;

        var headCapped = true;
        for (var i = 0; i < MaxChainLength - 1; i++)
        {
            var (edges, failed) = await GetRelationsAsync(cursor, memo, cancellationToken);
            if (failed)
            {
                fetchError = true;
                headCapped = false;
                break;
            }

            var prequel = edges.FirstOrDefault(e =>
                                                   e.RelationType.Equals(
                                                       "PREQUEL", StringComparison.OrdinalIgnoreCase));
            if (prequel is null || !seen.Add(prequel.Id))
            {
                headCapped = false;
                break;
            }

            MediaMetadata? prequelMeta;
            try
            {
                prequelMeta = await AniList.GetMetadataByIdAsync(prequel.Id, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "season chain: prequel fetch failed '{Id}'", prequel.Id);
                fetchError = true;
                headCapped = false;
                break;
            }

            if (prequelMeta is null)
            {
                fetchError = true;
                headCapped = false;
                break;
            }

            path.Add(prequelMeta);
            cursor = prequelMeta.ExternalId;
        }

        var headTruncated = false;
        if (headCapped)
        {
            var (head, headFailed) = await GetRelationsAsync(cursor, memo, cancellationToken);
            if (headFailed)
            {
                fetchError = true;
            }
            else
            {
                headTruncated = head.Any(e => e.RelationType.Equals("PREQUEL", StringComparison.OrdinalIgnoreCase));
            }
        }

        var ordered    = path.AsEnumerable().Reverse().ToList();
        var orderedIds = new HashSet<string>(ordered.Select(m => m.ExternalId), StringComparer.OrdinalIgnoreCase);
        cursor = ordered[^1].ExternalId;
        var truncated = false;

        while (ordered.Count < MaxChainLength)
        {
            var (edges, failed) = await GetRelationsAsync(cursor, memo, cancellationToken);
            if (failed)
            {
                fetchError = true;
                break;
            }

            var sequel = edges.FirstOrDefault(e =>
                                                  e.RelationType.Equals("SEQUEL", StringComparison.OrdinalIgnoreCase));
            if (sequel is null || !orderedIds.Add(sequel.Id))
            {
                break;
            }

            MediaMetadata? sequelMeta;
            try
            {
                sequelMeta = await AniList.GetMetadataByIdAsync(sequel.Id, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "season chain: sequel fetch failed '{Id}'", sequel.Id);
                fetchError = true;
                break;
            }

            if (sequelMeta is null)
            {
                fetchError = true;
                break;
            }

            ordered.Add(sequelMeta);
            cursor = sequelMeta.ExternalId;
        }

        if (ordered.Count >= MaxChainLength)
        {
            var (tail, tailFailed) = await GetRelationsAsync(cursor, memo, cancellationToken);
            if (tailFailed)
            {
                fetchError = true;
            }
            else
            {
                truncated = tail.Any(e => e.RelationType.Equals("SEQUEL", StringComparison.OrdinalIgnoreCase));
            }
        }

        truncated = truncated || headTruncated || fetchError;

        var chain                = new SeasonChain { RootAniListId = ordered[0].ExternalId };
        var firstTv              = ordered.FindIndex(m => m.Format == ContentFormat.Tv);
        if (firstTv < 0) firstTv = 0;
        var seasonNo             = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var season = i >= firstTv && IsSeasonFormat(ordered[i].Format) ? ++seasonNo : 0;
            chain.Entries.Add(ToEntry(season, ordered[i]));
        }

        var firstNumbered = chain.Entries.FirstOrDefault(e => e.SeasonNumber > 0);
        if (firstNumbered?.AniListId is not null)
        {
            chain.RootAniListId = firstNumbered.AniListId;
        }

        chain.Truncated = truncated;
        _cache.Set(cacheKey, chain, fetchError ? ErrorCacheTtl : ChainCacheTtl);
        return chain;
    }

    private async Task<(IReadOnlyList<RelationEdge> Edges, bool Failed)> GetRelationsAsync(
        string id, Dictionary<string, IReadOnlyList<RelationEdge>> memo, CancellationToken cancellationToken)
    {
        if (memo.TryGetValue(id, out var memoized))
        {
            return (memoized, false);
        }

        try
        {
            IReadOnlyList<RelationEdge> edges = [];
            if (_relationsResolver is not null)
            {
                edges = await _relationsResolver(id, cancellationToken);
            }
            else if (AniList is AniListProvider aniList)
            {
                edges = await aniList.QueryAnimeRelationsAsync(id, cancellationToken);
            }

            memo[id] = edges;
            return (edges, false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "season chain: relations query failed '{Id}'", id);
            return ([], true);
        }
    }

    private static bool IsSeasonFormat(ContentFormat format)
    {
        return format is ContentFormat.Tv or ContentFormat.Ova or ContentFormat.Unknown;
    }

    public EntryAlignment AlignEntry(AnimeDetails details, SeasonChain chain)
    {
        var alignment = new EntryAlignment();
        var episodes  = details.Episodes ?? [];

        var tv = chain.Entries.Where(e => e.SeasonNumber > 0).ToList();

        if (tv.Count == 0 || episodes.Count == 0)
        {
            alignment.Warnings.Add("Zincir veya bölüm listesi boş; hizalama yapılamadı.");
            return alignment;
        }

        var providerSeasons = episodes
                              .Select(e => e.Season ?? 1)
                              .Distinct()
                              .OrderBy(s => s)
                              .ToArray();

        var mappings  = details.SeasonMappings ?? [];
        var usedChain = new HashSet<int>();
        foreach (var mapping in mappings)
        {
            var chainEntry = chain.Entries.FirstOrDefault(c =>
                                                              (!string.IsNullOrWhiteSpace(mapping.AniListId)
                                                               && mapping.AniListId.Equals(
                                                                   c.AniListId, StringComparison.OrdinalIgnoreCase))
                                                              || (!string.IsNullOrWhiteSpace(mapping.MyAnimeListId)
                                                                  && mapping.MyAnimeListId.Equals(
                                                                      c.MyAnimeListId,
                                                                      StringComparison.OrdinalIgnoreCase)));

            if (chainEntry is null || !usedChain.Add(chainEntry.SeasonNumber))
            {
                continue;
            }

            var count = episodes.Count(e => (e.Season ?? 1) == mapping.SeasonNumber);

            if (mappings.Count == 1)
            {
                count = episodes.Count;

                if (providerSeasons.Length == 1 && tv.Count > 1
                                                && chainEntry.TotalEpisodes.HasValue &&
                                                count > chainEntry.TotalEpisodes.Value)
                {
                    var expandIdx = tv.FindIndex(c => c.SeasonNumber == chainEntry.SeasonNumber);
                    if (expandIdx >= 0
                        && TryExpandAbsolute(tv, expandIdx, count, alignment, mapping.SeasonNumber))
                    {
                        continue;
                    }
                }
            }

            alignment.Seasons.Add(new AlignedSeason
            {
                ProviderSeasonNumber  = mapping.SeasonNumber,
                CanonicalSeasonNumber = chainEntry.SeasonNumber,
                AniListId             = chainEntry.AniListId,
                MyAnimeListId         = chainEntry.MyAnimeListId,
                ProviderEpisodeCount  = count,
                CanonicalEpisodeCount = chainEntry.TotalEpisodes,
                StartOffset           = 1
            });
        }

        if (alignment.Seasons.Count > 0 && alignment.Seasons.Count < providerSeasons.Length)
        {
            var alignedProvider = new HashSet<int>(alignment.Seasons.Select(s => s.ProviderSeasonNumber));
            foreach (var ps in providerSeasons)
            {
                if (!alignedProvider.Add(ps))
                {
                    continue;
                }

                var startIdx = tv.FindIndex(c => c.SeasonNumber == ps && !usedChain.Contains(c.SeasonNumber));
                if (startIdx < 0)
                {
                    continue;
                }

                var count = episodes.Count(e => (e.Season ?? 1) == ps);

                var span = SpanSeasons(tv, startIdx, count, usedChain);
                var used = 0;
                for (var i = 0; i < span.Count; i++)
                {
                    var (entry, offset) = span[i];
                    var size = span.Count == 1
                                   ? count
                                   : i < span.Count - 1
                                       ? entry.TotalEpisodes ?? 0
                                       : count - used;
                    used += size;
                    usedChain.Add(entry.SeasonNumber);
                    alignment.Seasons.Add(new AlignedSeason
                    {
                        ProviderSeasonNumber  = ps,
                        CanonicalSeasonNumber = entry.SeasonNumber,
                        AniListId             = entry.AniListId,
                        MyAnimeListId         = entry.MyAnimeListId,
                        ProviderEpisodeCount  = size,
                        CanonicalEpisodeCount = entry.TotalEpisodes,
                        StartOffset           = offset
                    });
                }

                alignment.Warnings.Add(
                    $"S{span[0].Entry.SeasonNumber}: ID yok, sezon numarasıyla eşlendi (doğrulayın).");
            }
        }

        if (alignment.Seasons.Count == 0)
        {
            var titleSeason = AnimeDetails.ParseSeasonNumber(details.Title);
            if (providerSeasons.Length == 1)
            {
                var startIdx = titleSeason > 1 && titleSeason <= tv.Count
                                   ? titleSeason - 1
                                   : 0;

                if (titleSeason > tv.Count)
                {
                    alignment.Warnings.Add(
                        $"Başlık S{titleSeason} diyor ama zincirde {tv.Count} sezon var; S1'den başlandı (doğrulayın).");
                }

                if (TryExpandAbsolute(tv, startIdx, episodes.Count, alignment, providerSeasons[0]))
                {
                }
                else
                {
                    var target = tv[startIdx];
                    alignment.Seasons.Add(new AlignedSeason
                    {
                        ProviderSeasonNumber  = providerSeasons[0],
                        CanonicalSeasonNumber = target.SeasonNumber,
                        AniListId             = target.AniListId,
                        MyAnimeListId         = target.MyAnimeListId,
                        ProviderEpisodeCount  = episodes.Count,
                        CanonicalEpisodeCount = target.TotalEpisodes,
                        StartOffset           = 1
                    });
                }
            }
            else
            {
                foreach (var ps in providerSeasons)
                {
                    if (ps == 0)
                    {
                        // Özel bölümler kanonik TV sezonlarını tüketmemeli;
                        // zincirdeki numarasız (S0) girdiye bağlanır.
                        var special = chain.Entries.FirstOrDefault(e => e.SeasonNumber == 0);
                        if (special is not null)
                        {
                            alignment.Seasons.Add(new AlignedSeason
                            {
                                ProviderSeasonNumber  = 0,
                                CanonicalSeasonNumber = 0,
                                AniListId             = special.AniListId,
                                MyAnimeListId         = special.MyAnimeListId,
                                ProviderEpisodeCount  = episodes.Count(e => (e.Season ?? 1) == 0),
                                CanonicalEpisodeCount = special.TotalEpisodes,
                                StartOffset           = 1
                            });
                        }

                        continue;
                    }

                    var startIdx = tv.FindIndex(c => c.SeasonNumber >= ps && !usedChain.Contains(c.SeasonNumber));
                    if (startIdx < 0)
                    {
                        startIdx = tv.FindIndex(c => !usedChain.Contains(c.SeasonNumber));
                    }

                    if (startIdx < 0)
                    {
                        continue;
                    }

                    var count = episodes.Count(e => (e.Season ?? 1) == ps);
                    var span  = SpanSeasons(tv, startIdx, count, usedChain);
                    var used  = 0;
                    for (var i = 0; i < span.Count; i++)
                    {
                        var (entry, offset) = span[i];
                        var size = span.Count == 1
                                       ? count
                                       : i < span.Count - 1
                                           ? entry.TotalEpisodes ?? 0
                                           : count - used;
                        used += size;
                        usedChain.Add(entry.SeasonNumber);
                        alignment.Seasons.Add(new AlignedSeason
                        {
                            ProviderSeasonNumber  = ps,
                            CanonicalSeasonNumber = entry.SeasonNumber,
                            AniListId             = entry.AniListId,
                            MyAnimeListId         = entry.MyAnimeListId,
                            ProviderEpisodeCount  = size,
                            CanonicalEpisodeCount = entry.TotalEpisodes,
                            StartOffset           = offset
                        });
                    }
                }
            }
        }
        else if (providerSeasons.Length > 1)
        {
            var multiGroups = new HashSet<int>(alignment.Seasons
                                                        .GroupBy(s => s.ProviderSeasonNumber)
                                                        .Where(g => g.Count() > 1)
                                                        .Select(g => g.Key));
            var offset = 1;
            foreach (var s in alignment.Seasons.OrderBy(s => s.ProviderSeasonNumber))
            {
                if (multiGroups.Contains(s.ProviderSeasonNumber))
                {
                    offset += s.ProviderEpisodeCount;
                    continue;
                }

                s.ProviderEpisodeCount =  episodes.Count(e => (e.Season ?? 1) == s.ProviderSeasonNumber);
                s.StartOffset          =  offset;
                offset                 += s.ProviderEpisodeCount;
            }
        }

        alignment.NumberingMode = DetectNumberingMode(episodes, alignment, tv);

        foreach (var s in alignment.Seasons)
        {
            if (s.CanonicalEpisodeCount.HasValue
                && s.ProviderEpisodeCount > s.CanonicalEpisodeCount.Value)
            {
                alignment.Warnings.Add(
                    $"S{s.CanonicalSeasonNumber}: provider {s.ProviderEpisodeCount} bölüm veriyor, " +
                    $"kanonik {s.CanonicalEpisodeCount} (recap/özel karışmış olabilir).");
            }
        }

        if (alignment.Seasons.Count == 0)
        {
            alignment.Warnings.Add("Hiçbir sezon eşleşmedi.");
        }

        return alignment;
    }

    public CanonicalEpisode? TranslateToCanonical(
        EntryAlignment alignment, int? providerSeason, double number)
    {
        if (alignment.Seasons.Count == 0)
        {
            return null;
        }

        if (alignment.NumberingMode == EntryNumberingMode.Absolute)
        {
            AlignedSeason? lastAbsolute = null;
            foreach (var s in alignment.Seasons.OrderBy(s => s.StartOffset))
            {
                lastAbsolute = s;
                var end = s.StartOffset + s.ProviderEpisodeCount - 1;
                if (number >= s.StartOffset && number <= end)
                {
                    return new CanonicalEpisode
                    {
                        Season = s.CanonicalSeasonNumber,
                        Number = number - s.StartOffset + 1,
                        IsOverflow = s.CanonicalEpisodeCount.HasValue
                                     && number - s.StartOffset + 1 > s.CanonicalEpisodeCount.Value
                    };
                }
            }

            if (lastAbsolute is not null && number > lastAbsolute.StartOffset)
            {
                return new CanonicalEpisode
                {
                    Season     = lastAbsolute.CanonicalSeasonNumber,
                    Number     = number - lastAbsolute.StartOffset + 1,
                    IsOverflow = true
                };
            }

            return null;
        }

        var ps = providerSeason ?? 1;
        var group = alignment.Seasons
                             .Where(s => s.ProviderSeasonNumber == ps)
                             .OrderBy(s => s.StartOffset)
                             .ToArray();
        var match = group.FirstOrDefault()
                    ?? (alignment.Seasons.Count == 1 ? alignment.Seasons[0] : null);

        if (match is null)
        {
            return null;
        }

        if (group.Length > 1)
        {
            foreach (var slice in group)
            {
                var end = slice.StartOffset + slice.ProviderEpisodeCount - 1;
                if (number >= slice.StartOffset && number <= end)
                {
                    return new CanonicalEpisode
                    {
                        Season = slice.CanonicalSeasonNumber,
                        Number = number - slice.StartOffset + 1,
                        IsOverflow = slice.CanonicalEpisodeCount.HasValue
                                     && number - slice.StartOffset + 1 > slice.CanonicalEpisodeCount.Value
                    };
                }
            }

            // Dilim aralığının altında kalan numaralar (örn. 0) son dilime
            // taşmamalı; ilk dilime aynen verilir.
            if (number < group[0].StartOffset)
            {
                return new CanonicalEpisode
                {
                    Season     = group[0].CanonicalSeasonNumber,
                    Number     = number,
                    IsOverflow = false
                };
            }

            var last = group[^1];
            return new CanonicalEpisode
            {
                Season     = last.CanonicalSeasonNumber,
                Number     = number - last.StartOffset + 1,
                IsOverflow = true
            };
        }

        return new CanonicalEpisode
        {
            Season     = match.CanonicalSeasonNumber,
            Number     = number,
            IsOverflow = match.CanonicalEpisodeCount.HasValue && number > match.CanonicalEpisodeCount.Value
        };
    }

    private static SeasonChainEntry ToEntry(int seasonNumber, MediaMetadata meta)
    {
        return new SeasonChainEntry
        {
            SeasonNumber  = seasonNumber,
            AniListId     = meta.AniListId ?? (meta.Source == MetadataSource.AniList ? meta.ExternalId : null),
            MyAnimeListId = meta.MyAnimeListId,
            Title         = meta.Title,
            TotalEpisodes = meta.TotalEpisodes,
            Year          = meta.Year,
            Format        = meta.Format
        };
    }

    private static List<(SeasonChainEntry Entry, int Offset)> SpanSeasons(
        List<SeasonChainEntry> tv, int startIdx, int episodeCount, HashSet<int> usedChain)
    {
        var single = new List<(SeasonChainEntry, int)> { (tv[startIdx], 1) };

        for (var k = tv.Count - startIdx; k >= 2; k--)
        {
            var slice = tv.Skip(startIdx).Take(k).ToArray();
            if (slice.Any(e => !e.TotalEpisodes.HasValue)
                || slice.Any(e => e.SeasonNumber != tv[startIdx].SeasonNumber + Array.IndexOf(slice, e))
                || slice.Skip(1).Any(e => usedChain.Contains(e.SeasonNumber)))
            {
                continue;
            }

            var sum = slice.Sum(e => e.TotalEpisodes!.Value);
            if (episodeCount < sum - 2 || episodeCount > sum + 5)
            {
                continue;
            }

            var span   = new List<(SeasonChainEntry, int)>();
            var offset = 1;
            foreach (var entry in slice)
            {
                span.Add((entry, offset));
                offset += entry.TotalEpisodes!.Value;
            }

            return span;
        }

        return single;
    }

    private static bool TryExpandAbsolute(
        List<SeasonChainEntry> tv, int startIdx, int episodeCount, EntryAlignment alignment,
        int providerSeason = 1)
    {
        for (var k = tv.Count - startIdx; k >= 2; k--)
        {
            var slice = tv.Skip(startIdx).Take(k).ToArray();
            if (slice.Any(e => !e.TotalEpisodes.HasValue))
            {
                continue;
            }

            var sum = slice.Sum(e => e.TotalEpisodes!.Value);
            if (episodeCount < sum - 2 || episodeCount > sum + 5)
            {
                continue;
            }

            var offset = 1;
            for (var i = 0; i < slice.Length; i++)
            {
                var entry = slice[i];
                var count = i < slice.Length - 1
                                ? entry.TotalEpisodes!.Value
                                : episodeCount - offset + 1;

                alignment.Seasons.Add(new AlignedSeason
                {
                    ProviderSeasonNumber  = providerSeason,
                    CanonicalSeasonNumber = entry.SeasonNumber,
                    AniListId             = entry.AniListId,
                    MyAnimeListId         = entry.MyAnimeListId,
                    ProviderEpisodeCount  = count,
                    CanonicalEpisodeCount = entry.TotalEpisodes,
                    StartOffset           = offset
                });
                offset += count;
            }

            return true;
        }

        return false;
    }

    private static EntryNumberingMode DetectNumberingMode(
        List<Episode> episodes, EntryAlignment alignment, List<SeasonChainEntry> tv)
    {
        var distinctSeasons = episodes.Select(e => e.Season ?? 1).Distinct().Count();
        if (distinctSeasons > 1)
        {
            return EntryNumberingMode.PerSeason;
        }

        if (alignment.Seasons.Count > 1)
        {
            return EntryNumberingMode.Absolute;
        }

        var canonicalTotal = tv.Sum(c => c.TotalEpisodes ?? 0);

        if (canonicalTotal > 0 && episodes.Count >= canonicalTotal - 2 && episodes.Count <= canonicalTotal + 5)
        {
            var maxCanonicalSingle = tv.Max(c => c.TotalEpisodes ?? 0);
            if (episodes.Count > maxCanonicalSingle)
            {
                return EntryNumberingMode.Absolute;
            }
        }

        return alignment.Seasons.Count > 0 ? EntryNumberingMode.PerSeason : EntryNumberingMode.Unknown;
    }
}
