using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Migurdex.Core.Services;

public sealed class CanonicalEpisodeService : ICanonicalEpisodeService
{
    private static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EpisodeCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim ProviderGate = new(4, 4);
    private static readonly SemaphoreSlim ExtractGate = new(6, 6);

    private readonly IAnimeProviderRegistry _registry;
    private readonly ITrackerIdResolver _tracker;
    private readonly ICanonicalResolver _canonical;
    private readonly ISeasonChainService _seasons;
    private readonly IExtractorManager? _extractors;
    private readonly TimeSpan _providerTimeout;
    private readonly IMemoryCache _episodeCache;
    private readonly ILogger<CanonicalEpisodeService> _logger;

    public CanonicalEpisodeService(
        IAnimeProviderRegistry registry,
        ITrackerIdResolver tracker,
        ICanonicalResolver canonical,
        ISeasonChainService seasons,
        ILogger<CanonicalEpisodeService> logger)
        : this(registry, tracker, canonical, seasons, null, logger)
    {
    }

    public CanonicalEpisodeService(
        IAnimeProviderRegistry registry,
        ITrackerIdResolver tracker,
        ICanonicalResolver canonical,
        ISeasonChainService seasons,
        IExtractorManager? extractors,
        ILogger<CanonicalEpisodeService> logger,
        IMemoryCache? episodeCache = null,
        TimeSpan providerTimeout = default)
    {
        _registry = registry;
        _tracker = tracker;
        _canonical = canonical;
        _seasons = seasons;
        _extractors = extractors;
        _logger = logger;
        _episodeCache = episodeCache ?? new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        _providerTimeout = providerTimeout == default ? DefaultProviderTimeout : providerTimeout;
    }

    public async Task<CanonicalEpisodeResult?> GetEpisodesAsync(
        string canonicalId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParseId(canonicalId, out var anilistId, out var malId))
        {
            return null;
        }

        var cacheKey = $"canonical-episodes:{anilistId ?? "-"}:{malId ?? "-"}".ToLowerInvariant();
        if (_episodeCache.TryGetValue(cacheKey, out CanonicalEpisodeResult? cached) && cached is not null)
        {
            return cached;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var meta = await _tracker.ResolveFromTrackerAsync(anilistId, malId, cancellationToken);
        if (meta is null)
        {
            return null;
        }

        var anime = CanonicalAnime.FromMetadata(meta);
        _logger.LogDebug("canonical episodes {Id}: tracker {Ms}ms", anime.CanonicalId, sw.ElapsedMilliseconds);

        var anilistIdToChain = anime.AniListId;
        if (string.IsNullOrWhiteSpace(anilistIdToChain) && !string.IsNullOrWhiteSpace(anime.MyAnimeListId))
        {
            var bridge = await _tracker.ResolveFromTrackerAsync(null, anime.MyAnimeListId, cancellationToken);
            if (bridge is not null && !string.IsNullOrWhiteSpace(bridge.AniListId))
            {
                anilistIdToChain = bridge.AniListId;
            }
        }

        SeasonChain? chain = null;
        if (!string.IsNullOrWhiteSpace(anilistIdToChain))
        {
            try
            {
                chain = await _seasons.GetSeasonChainAsync(anilistIdToChain, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "canonical episodes: season chain failed for {Id}", anime.CanonicalId);
            }
        }

        var targetChainSeasons = new List<SeasonChainEntry>();
        if (chain is not null)
        {
            foreach (var e in chain.Entries)
            {
                if ((!string.IsNullOrWhiteSpace(anime.AniListId) && e.AniListId == anime.AniListId)
                    || (!string.IsNullOrWhiteSpace(anime.MyAnimeListId) && e.MyAnimeListId == anime.MyAnimeListId))
                {
                    targetChainSeasons.Add(e);
                }
            }
        }

        var targetSeasonSet = targetChainSeasons.Select(e => e.SeasonNumber).ToHashSet();
        var primarySeasonNumber = targetChainSeasons.FirstOrDefault()?.SeasonNumber ?? 1;

        var (_, detectedSeason) = TitleNormalizer.SplitSeason(anime.Title);
        var effectiveSeason = primarySeasonNumber > 1 ? primarySeasonNumber : detectedSeason;
        var queries = BuildProviderQueries(anime, chain, effectiveSeason);
        if (queries.Count == 0)
        {
            return new CanonicalEpisodeResult { Anime = anime };
        }

        var providers = _registry.AnimeProviders;
        var tasks = providers.Select(p => FetchProviderAsync(p, anime, queries, chain, cancellationToken));
        var fetched = await Task.WhenAll(tasks);
        _logger.LogDebug("canonical episodes {Id}: providers {Ms}ms ({Count}/{Total})",
                         anime.CanonicalId, sw.ElapsedMilliseconds, fetched.Count(f => f.Error is null), providers.Count);

        var result = new CanonicalEpisodeResult
        {
            Anime = anime,
            ProvidersQueried = providers.Count,
        };
        var entries = new Dictionary<double, CanonicalEpisodeEntry>();

        foreach (var f in fetched)
        {
            if (f.Error is not null)
            {
                result.ProviderErrors[f.ProviderName] = f.Error;
                continue;
            }

            if (f.Details is null)
            {
                continue;
            }

            result.ProvidersMatched++;
            result.MatchedProviders.Add(f.ProviderName);
            EntryAlignment? alignment = null;
            if (chain is not null)
            {
                try
                {
                    alignment = _seasons.AlignEntry(f.Details, chain);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "canonical episodes: align failed for {Provider}", f.ProviderName);
                }
            }

            if (alignment?.Warnings.Count > 0)
            {
                foreach (var warning in alignment.Warnings)
                {
                    result.Warnings.Add($"[{f.ProviderName}] {warning}");
                }
            }

            foreach (var ep in f.Details.Episodes)
            {
                var providerSeason = ep.Season is null or <= 0 ? 1 : ep.Season.Value;
                var season = providerSeason;
                var number = ep.Number;
                if (alignment is not null)
                {
                    try
                    {
                        var mapped = _seasons.TranslateToCanonical(alignment, providerSeason, ep.Number);
                        if (mapped is not null)
                        {
                            if (mapped.IsOverflow)
                            {
                                continue;
                            }

                            if (mapped.Season > 0)
                            {
                                season = mapped.Season;
                                number = mapped.Number;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "canonical episodes: translate failed for {Provider} ep {Ep}",
                                           f.ProviderName, ep.Number);
                    }
                }

                if (chain is not null && chain.Entries.Any(e => e.SeasonNumber > 1))
                {
                    if (targetSeasonSet.Count > 0 && !targetSeasonSet.Contains(season))
                    {
                        continue;
                    }
                    if (targetSeasonSet.Count == 0 && season != effectiveSeason)
                    {
                        continue;
                    }
                }
                else if (chain is null && anime.TotalEpisodes.HasValue && number > anime.TotalEpisodes.Value)
                {
                    continue;
                }

                var canonicalEpNumber = number;
                if (targetChainSeasons.Count > 1)
                {
                    var seasonIdx = targetChainSeasons.FindIndex(e => e.SeasonNumber == season);
                    if (seasonIdx > 0)
                    {
                        var offset = targetChainSeasons.Take(seasonIdx).Sum(e => e.TotalEpisodes ?? 0);
                        canonicalEpNumber = offset + number;
                    }
                }

                var maxAllowed = (targetChainSeasons.Count > 0
                    ? targetChainSeasons.FirstOrDefault(e => e.SeasonNumber == season)?.TotalEpisodes
                    : null) ?? anime.TotalEpisodes;
                if (maxAllowed.HasValue && canonicalEpNumber > maxAllowed.Value)
                {
                    continue;
                }

                var key = canonicalEpNumber;
                if (!entries.TryGetValue(key, out var entry))
                {
                    entry = new CanonicalEpisodeEntry { Season = 1, Number = canonicalEpNumber };
                    entries[key] = entry;
                }

                if (string.IsNullOrWhiteSpace(entry.Title) && !string.IsNullOrWhiteSpace(ep.Title))
                {
                    entry.Title = ep.Title.Trim();
                }

                entry.Sources.Add(new CanonicalEpisodeSource
                {
                    ProviderName = f.ProviderName,
                    ProviderAnimeId = f.AnimeId,
                    ProviderEpisodeId = ep.Id,
                    EpisodeTitle = string.IsNullOrWhiteSpace(ep.Title) ? null : ep.Title,
                    MatchScore = f.MatchScore,
                });
            }
        }

        result.Episodes = entries.Values
            .OrderBy(e => e.Number)
            .ToList();
        _episodeCache.Set(cacheKey,
                          result,
                          new MemoryCacheEntryOptions
                          {
                              AbsoluteExpirationRelativeToNow = EpisodeCacheTtl,
                              Size                            = 1
                          });
        return result;
    }

    internal static IReadOnlyList<string> BuildProviderQueries(
        CanonicalAnime anime,
        SeasonChain? chain,
        int targetSeason)
    {
        var queries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? q)
        {
            if (!string.IsNullOrWhiteSpace(q))
            {
                var trimmed = q.Trim();
                if (seen.Add(trimmed))
                {
                    queries.Add(trimmed);
                }
            }
        }

        foreach (var t in anime.GetAllTitles().Take(3))
        {
            Add(t);
        }

        if (targetSeason > 1)
        {
            var chainRootTitle = chain?.Entries.FirstOrDefault(e => e.SeasonNumber == 1)?.Title;
            var (baseTitle, _) = TitleNormalizer.SplitSeason(anime.Title);
            var (engBaseTitle, _) = TitleNormalizer.SplitSeason(anime.EnglishTitle);

            var rootTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(chainRootTitle)) rootTitles.Add(chainRootTitle);
            if (!string.IsNullOrWhiteSpace(baseTitle)) rootTitles.Add(baseTitle);
            if (!string.IsNullOrWhiteSpace(engBaseTitle)) rootTitles.Add(engBaseTitle);

            foreach (var rt in rootTitles)
            {
                Add($"{rt} {targetSeason}. Sezon");
                Add($"{rt} {targetSeason}.Sezon");
                Add($"{rt} {targetSeason}");
                Add($"{rt} S{targetSeason}");
            }

            foreach (var rt in rootTitles)
            {
                Add(rt);
            }
        }

        return queries;
    }

    private async Task<ProviderFetch> FetchProviderAsync(
        IAnimeProvider provider,
        CanonicalAnime anime,
        IReadOnlyList<string> queries,
        SeasonChain? chain,
        CancellationToken ct)
    {
        var name = provider.Name;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_providerTimeout);

        try
        {
            ProviderMatch? best = null;
            foreach (var q in queries)
            {
                var candidateResults = await provider.SearchAsync(q, cts.Token);
                if (candidateResults.Count == 0)
                {
                    continue;
                }

                var candidateBest = _canonical.MatchProviders(candidateResults, anime)
                    .FirstOrDefault(m => m.Matched);
                if (candidateBest is not null)
                {
                    best = candidateBest;
                    break;
                }
            }

            if (best is null)
            {
                _logger.LogDebug("canonical fetch {Provider}: no match {Ms}ms", name, sw.ElapsedMilliseconds);
                return new ProviderFetch(name) { Error = "eşleşme yok" };
            }

            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(_providerTimeout);
            var details = await provider.GetDetailsAsync(best.Result.Id, cts2.Token);
            _logger.LogDebug("canonical fetch {Provider}: ok {Ms}ms ({Episodes} bl)",
                             name, sw.ElapsedMilliseconds, details.Episodes.Count);
            return new ProviderFetch(name)
            {
                AnimeId = best.Result.Id,
                MatchScore = best.Score,
                Details = details,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProviderFetch(name) { Error = "zaman aşımı" };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "canonical episodes: provider failed {Provider}", name);
            return new ProviderFetch(name) { Error = "sağlayıcı hatası" };
        }
    }

    public async IAsyncEnumerable<VideoSource> StreamEpisodeSourcesAsync(
        string canonicalId,
        double number,
        string? group = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_extractors is null)
        {
            throw new InvalidOperationException("ExtractorManager bağlı değil.");
        }

        var result = await GetEpisodesAsync(canonicalId, cancellationToken);
        var entry = result?.Episodes.FirstOrDefault(e => e.Number.Equals(number));
        if (entry is null)
        {
            throw new KeyNotFoundException($"Bölüm bulunamadı: E{number}");
        }

        var byProvider = _registry.AnimeProviders.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var channel = Channel.CreateUnbounded<VideoSource>();
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        bool TryEmit(VideoSource src)
        {
            if (string.IsNullOrWhiteSpace(src.Url) || !seen.TryAdd(src.Url, 0))
            {
                return false;
            }

            return channel.Writer.TryWrite(src);
        }

        var groupTasks = entry.Sources.Select(src => Task.Run(async () =>
        {
            if (!byProvider.TryGetValue(src.ProviderName, out var provider))
            {
                return;
            }

            List<VideoSource> raw;
            await ProviderGate.WaitAsync(cancellationToken);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_providerTimeout);
                raw = await provider.GetVideoSourcesAsync(src.ProviderEpisodeId, group, cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("canonical sources: timeout {Provider} {Episode}", src.ProviderName, src.ProviderEpisodeId);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "canonical sources: groups failed {Provider}", src.ProviderName);
                return;
            }
            finally
            {
                ProviderGate.Release();
            }

            var extractTasks = raw.Select(r => Task.Run(async () =>
            {
                await ExtractGate.WaitAsync(cancellationToken);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(_providerTimeout);
                    if (r.Type == VideoType.Embed && _extractors.CanExtract(r.Url))
                    {
                        Dictionary<string, string>? headers = null;
                        if (!string.IsNullOrEmpty(provider.BaseUrl))
                        {
                            headers = new Dictionary<string, string> { { "Referer", provider.BaseUrl } };
                        }

                        var extracted = await _extractors.ExtractAsync(r.Url, headers, cts.Token);
                        foreach (var ext in extracted)
                        {
                            TryEmit(MergeSource(ext, r, src.ProviderName));
                        }
                    }
                    else
                    {
                        TryEmit(MergeSource(r, r, src.ProviderName));
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("canonical sources: extract timeout {Url}", r.Url);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "canonical sources: extract failed {Url}", r.Url);
                }
                finally
                {
                    ExtractGate.Release();
                }
            }, cancellationToken));

            await Task.WhenAll(extractTasks);
        }, cancellationToken)).ToArray();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(groupTasks);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public static VideoSource MergeSource(VideoSource extracted, VideoSource raw, string providerName)
    {
        return new VideoSource
        {
            Url = extracted.Url,
            Quality = extracted.Quality,
            Type = extracted.Type,
            Hoster = extracted.Hoster ?? raw.Hoster,
            Group = extracted.Group ?? raw.Group,
            Language = extracted.Language ?? raw.Language,
            ProviderName = providerName,
            Headers = MergeHeaders(extracted.Headers, raw.Headers),
            Subtitles = extracted.Subtitles is { Count: > 0 } ? extracted.Subtitles : raw.Subtitles,
        };
    }

    private static Dictionary<string, string>? MergeHeaders(
        Dictionary<string, string>? extracted,
        Dictionary<string, string>? raw)
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

    public static bool TryParseId(string canonicalId, out string? anilistId, out string? malId)
    {
        anilistId = null;
        malId = null;
        if (string.IsNullOrWhiteSpace(canonicalId))
        {
            return false;
        }

        var id = canonicalId.Trim();
        var sep = id.IndexOf(':');
        if (sep > 0)
        {
            var scheme = id[..sep].ToLowerInvariant();
            var value = id[(sep + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (scheme is "anilist")
            {
                anilistId = value;
                return true;
            }

            if (scheme is "mal")
            {
                malId = value;
                return true;
            }

            return false;
        }

        if (id.All(char.IsDigit))
        {
            anilistId = id;
            return true;
        }

        return false;
    }

    private sealed class ProviderFetch(string providerName)
    {
        public string ProviderName { get; } = providerName;
        public string AnimeId { get; set; } = string.Empty;
        public double MatchScore { get; set; }
        public AnimeDetails? Details { get; set; }
        public string? Error { get; set; }
    }
}
