using Migurdex.Api.Common;
using Migurdex.Core.PluginSystem;
using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Migurdex.Api.Endpoints;

public static class AnimeEndpoints
{
    private const int MaxQueryLength = 200;
    private const int MaxIdLength    = 512;

    public static IEndpointRouteBuilder MapAnimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/providers", GetProviders);
        app.MapGet("/api/v1/anime/search", SearchAnime);
        app.MapGet("/api/v1/anime/{provider}/groups", GetEpisodeGroups);
        app.MapGet("/api/v1/anime/{provider}/sources", GetVideoSources);
        app.MapGet("/api/v1/anime/{provider}/{*animeId}", GetAnimeDetails);

        return app;
    }

    private static IResult GetProviders(PluginLoader loader)
    {
        var providers = loader.Providers
                              .OfType<IAnimeProvider>()
                              .Select(p => new
                              {
                                  p.Name,
                                  p.Type,
                                  p.BaseUrl,
                                  p.Capabilities
                              })
                              .OrderBy(p => p.Name)
                              .ToList();

        return Results.Ok(providers);
    }

    private static async Task SearchAnime(
        string            q,
        string?           provider,
        bool?             stream,
        HttpContext       context,
        PluginLoader      loader,
        IBlameCollector   blame,
        ILoggerFactory    loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AnimeEndpoints");
        q = (q ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(q))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
                                                    {
                                                        error = "Arama sorgusu ('q') boş olamaz."
                                                    },
                                                    cancellationToken);
            return;
        }

        if (q.Length > MaxQueryLength)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
                                                    {
                                                        error =
                                                            $"Arama sorgusu en fazla {MaxQueryLength} karakter olabilir."
                                                    },
                                                    cancellationToken);
            return;
        }

        provider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();

        var animeProviders = loader.Providers.OfType<IAnimeProvider>();

        if (!string.IsNullOrEmpty(provider))
        {
            animeProviders = animeProviders.Where(p => p.Name.Equals(provider, StringComparison.OrdinalIgnoreCase));
        }

        var providersList = animeProviders.ToList();

        if (!string.IsNullOrEmpty(provider) && providersList.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new
                                                    {
                                                        error = $"Provider '{provider}' bulunamadı."
                                                    },
                                                    cancellationToken);
            return;
        }

        if (stream == true)
        {
            if (providersList.Count == 0)
            {
                SseHelper.InitializeSseResponse(context);
                await SseHelper.WriteDoneSummaryAsync(context,
                                                      new DoneSummary(0, 0, [], 0),
                                                      cancellationToken);
                return;
            }

            var channel            = Channel.CreateUnbounded<SseEnvelope>();
            var errors             = new ConcurrentBag<DoneErrorItem>();
            var succeededProviders = 0;
            var totalItems         = 0;

            var tasks = providersList.Select(async p =>
                                     {
                                         var sw = Stopwatch.StartNew();
                                         try
                                         {
                                             var searchResults = await p.SearchAsync(q, cancellationToken);
                                             logger.LogDebug("search completed for provider {Provider}: {Count} items in {ElapsedMs}ms",
                                                             p.Name,
                                                             searchResults.Count,
                                                             sw.ElapsedMilliseconds);
                                             blame.RecordProvider(p.Name,
                                                                  "search",
                                                                  sw.ElapsedMilliseconds,
                                                                  searchResults.Count == 0
                                                                      ? BlameOutcome.Empty
                                                                      : BlameOutcome.Ok);
                                             Interlocked.Increment(ref succeededProviders);
                                             foreach (var item in searchResults)
                                             {
                                                 Interlocked.Increment(ref totalItems);
                                                 await channel.Writer.WriteAsync(
                                                     new SseEnvelope(SseHelper.EventSearchResult,
                                                                     new
                                                                     {
                                                                         provider = p.Name,
                                                                         status   = "success",
                                                                         data     = item
                                                                     }),
                                                     cancellationToken);
                                             }
                                         }
                                         catch (Exception ex)
                                         {
                                             logger.LogWarning(ex,
                                                               "search failed for provider {Provider} query {Query}",
                                                               p.Name,
                                                               q);
                                             var (searchOutcome, searchError) =
                                                 ProviderErrorMapper.Map(ex, "Upstream arama hatası.");
                                             blame.RecordProvider(p.Name,
                                                                  "search",
                                                                  sw.ElapsedMilliseconds,
                                                                  searchOutcome);
                                             errors.Add(new DoneErrorItem(p.Name,
                                                                                  "search",
                                                                                  searchError));
                                             await channel.Writer.WriteAsync(
                                                 new SseEnvelope(SseHelper.EventProviderError,
                                                                 new ProviderErrorPayload(
                                                                     p.Name,
                                                                     "search",
                                                                     searchError)),
                                                 cancellationToken);
                                         }
                                     })
                                     .ToList();

            _ = Task.Run(async () =>
                         {
                             try
                             {
                                 await Task.WhenAll(tasks);
                                 var failed    = errors.Count;
                                 var succeeded = succeededProviders;
                                 await channel.Writer.WriteAsync(
                                     new SseEnvelope(SseHelper.EventDone,
                                                     new DoneSummary(succeeded, failed, errors.ToList(), totalItems)),
                                     cancellationToken);
                             }
                             finally
                             {
                                 channel.Writer.Complete();
                             }
                         },
                         cancellationToken);

            await SseHelper.StreamEnvelopesAsync(context, channel.Reader, cancellationToken);
            return;
        }

        var searchTasks = providersList.Select(async p =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var searchResults = await p.SearchAsync(q, cancellationToken);
                logger.LogDebug("search completed for provider {Provider}: {Count} items in {ElapsedMs}ms",
                                p.Name,
                                searchResults.Count,
                                sw.ElapsedMilliseconds);
                blame.RecordProvider(p.Name,
                                     "search",
                                     sw.ElapsedMilliseconds,
                                     searchResults.Count == 0 ? BlameOutcome.Empty : BlameOutcome.Ok);
                return (object) new
                {
                    provider = p.Name,
                    data     = searchResults
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "search failed for provider {Provider} query {Query}", p.Name, q);
                var (searchOutcome, searchError) = ProviderErrorMapper.Map(ex, "Upstream arama hatası.");
                blame.RecordProvider(p.Name, "search", sw.ElapsedMilliseconds, searchOutcome);
                return new
                {
                    provider = p.Name,
                    error    = searchError
                };
            }
        });

        var finalResults = await Task.WhenAll(searchTasks);
        await context.Response.WriteAsJsonAsync(finalResults, cancellationToken);
    }

    private static async Task<IResult> GetAnimeDetails(
        string            provider,
        string            animeId,
        PluginLoader      loader,
        IBlameCollector   blame,
        ILoggerFactory    loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AnimeEndpoints");

        if (loader.Providers.FirstOrDefault(x => x.Name.Equals(provider, StringComparison.OrdinalIgnoreCase)) is
            not IAnimeProvider p)
        {
            return ApiErrors.NotFound("Provider bulunamadı.");
        }

        animeId = (animeId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(animeId))
        {
            return ApiErrors.BadRequest("AnimeId boş olamaz.");
        }

        if (animeId.Length > MaxIdLength)
        {
            return ApiErrors.BadRequest($"AnimeId en fazla {MaxIdLength} karakter olabilir.");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var details = await p.GetDetailsAsync(animeId, cancellationToken);
            logger.LogDebug("details completed for provider {Provider} anime {AnimeId} in {ElapsedMs}ms",
                            provider,
                            animeId,
                            sw.ElapsedMilliseconds);
            blame.RecordProvider(provider, "details", sw.ElapsedMilliseconds, BlameOutcome.Ok);
            details.Normalize();
            return Results.Ok(details);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "getDetails failed for provider {Provider} anime {AnimeId}", provider, animeId);
            var (detailsOutcome, detailsError) = ProviderErrorMapper.Map(ex, $"Upstream detay hatası ({provider}).");
            blame.RecordProvider(provider, "details", sw.ElapsedMilliseconds, detailsOutcome);
            return Results.Problem(detailsError,
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
    }

    private static async Task<IResult> GetEpisodeGroups(
        string            provider,
        string            episodeId,
        PluginLoader      loader,
        IBlameCollector   blame,
        ILoggerFactory    loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AnimeEndpoints");

        if (loader.Providers.FirstOrDefault(x => x.Name.Equals(provider, StringComparison.OrdinalIgnoreCase)) is
            not IAnimeProvider p)
        {
            return ApiErrors.NotFound("Provider bulunamadı.");
        }

        episodeId = (episodeId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(episodeId))
        {
            return ApiErrors.BadRequest("episodeId boş olamaz.");
        }

        if (episodeId.Length > MaxIdLength)
        {
            return ApiErrors.BadRequest($"episodeId en fazla {MaxIdLength} karakter olabilir.");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var groups = await p.GetGroupsAsync(episodeId, cancellationToken);
            logger.LogDebug("groups completed for provider {Provider}: {Count} groups in {ElapsedMs}ms",
                            provider,
                            groups.Count,
                            sw.ElapsedMilliseconds);
            blame.RecordProvider(provider,
                                 "groups",
                                 sw.ElapsedMilliseconds,
                                 groups.Count == 0 ? BlameOutcome.Empty : BlameOutcome.Ok);
            return Results.Ok(groups);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "getGroups failed for provider {Provider} episode {EpisodeId}", provider, episodeId);
            var (groupsOutcome, groupsError) = ProviderErrorMapper.Map(ex, $"Upstream grup hatası ({provider}).");
            blame.RecordProvider(provider, "groups", sw.ElapsedMilliseconds, groupsOutcome);
            return Results.Problem(groupsError,
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
    }

    private static (List<VideoSource> Playable, List<VideoSource> RawEmbeds) PartitionEmbeds(
        List<VideoSource> sources)
    {
        var playable  = new List<VideoSource>(sources.Count);
        var rawEmbeds = new List<VideoSource>();
        foreach (var src in sources)
        {
            if (src.Type == VideoType.Embed)
            {
                rawEmbeds.Add(src);
            }
            else
            {
                playable.Add(src);
            }
        }

        return (playable, rawEmbeds);
    }

    private static Dictionary<string, string>? BuildRefererHeaders(IAnimeProvider p)
    {
        if (string.IsNullOrEmpty(p.BaseUrl))
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            { "Referer", p.BaseUrl }
        };
    }

    private static async Task<IResult> GetVideoSources(
        string            provider,
        string            episodeId,
        string?           group,
        bool?             stream,
        HttpContext       context,
        PluginLoader      loader,
        IBlameCollector   blame,
        IExtractorManager extractorManager,
        ILoggerFactory    loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("AnimeEndpoints");

        if (loader.Providers.FirstOrDefault(x => x.Name.Equals(provider, StringComparison.OrdinalIgnoreCase)) is
            not IAnimeProvider p)
        {
            return ApiErrors.NotFound("Provider bulunamadı.");
        }

        episodeId = (episodeId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(episodeId))
        {
            return ApiErrors.BadRequest("episodeId boş olamaz.");
        }

        if (episodeId.Length > MaxIdLength)
        {
            return ApiErrors.BadRequest($"episodeId en fazla {MaxIdLength} karakter olabilir.");
        }

        group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        if (group is { Length: > MaxIdLength })
        {
            return ApiErrors.BadRequest($"group en fazla {MaxIdLength} karakter olabilir.");
        }

        if (stream == true)
        {
            List<VideoSource> rawSources;
            var streamSw = Stopwatch.StartNew();
            try
            {
                rawSources = await p.GetVideoSourcesAsync(episodeId, group, cancellationToken);
                logger.LogDebug("sources listed for provider {Provider}: {Count} embeds in {ElapsedMs}ms",
                                provider,
                                rawSources.Count,
                                streamSw.ElapsedMilliseconds);
                blame.RecordProvider(provider,
                                     "sources",
                                     streamSw.ElapsedMilliseconds,
                                     rawSources.Count == 0 ? BlameOutcome.Empty : BlameOutcome.Ok);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                                  "getSources failed for provider {Provider} episode {EpisodeId} group {Group}",
                                  provider,
                                  episodeId,
                                  group);
                var (sourcesOutcome, sourcesError) =
                    ProviderErrorMapper.Map(ex, "Upstream kaynak hatası.");
                blame.RecordProvider(provider, "sources", streamSw.ElapsedMilliseconds, sourcesOutcome);
                SseHelper.InitializeSseResponse(context);
                await SseHelper.WriteProviderErrorAsync(context,
                                                        provider,
                                                        "sources",
                                                        sourcesError,
                                                        cancellationToken);
                await SseHelper.WriteDoneSummaryAsync(context,
                                                      new DoneSummary(0,
                                                                      1,
                                                                      [
                                                                          new DoneErrorItem(
                                                                              provider,
                                                                              "sources",
                                                                              sourcesError)
                                                                      ],
                                                                      0),
                                                      cancellationToken);
                return Results.Empty;
            }

            var sentUrls    = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var channel     = Channel.CreateUnbounded<SseEnvelope>();
            var sentCount   = 0;
            var failedCount = 0;
            var errors      = new ConcurrentBag<DoneErrorItem>();

            bool TryEnqueueSource(VideoSource src)
            {
                if (string.IsNullOrWhiteSpace(src.Url))
                {
                    return false;
                }

                if (sentUrls.TryAdd(src.Url, 0))
                {
                    Interlocked.Increment(ref sentCount);
                    return channel.Writer.TryWrite(new SseEnvelope(SseHelper.EventSource, src));
                }

                return false;
            }

            var extractionTasks = rawSources.Select(src => Task.Run(async () =>
                                                                    {
                                                                        var extractSw = Stopwatch.StartNew();
                                                                        try
                                                                        {
                                                                            if ((src.Type == VideoType.Embed
                                                                                    || src.Type == VideoType.M3U8)
                                                                                    && extractorManager.CanExtract(
                                                                                        src.Url))
                                                                            {
                                                                                var headers = BuildRefererHeaders(p);

                                                                                var outcome =
                                                                                    await extractorManager
                                                                                        .ExtractDetailedAsync(src.Url,
                                                                                            headers,
                                                                                            cancellationToken);
                                                                                var extracted = outcome.Sources;
                                                                                if (extracted.Count == 0)
                                                                                {
                                                                                    var detail = outcome.Failures.Count > 0
                                                                                        ? $"{outcome.Failures[0].Kind}: {outcome.Failures[0].Detail}"
                                                                                        : "Embed çözümlenemedi";
                                                                                    var reason = $"{detail} [{src.Url}]";
                                                                                    blame.RecordProvider(
                                                                                        p.Name,
                                                                                        "extract",
                                                                                        extractSw.ElapsedMilliseconds,
                                                                                        BlameOutcome.Empty);
                                                                                    Interlocked.Increment(ref failedCount);
                                                                                    errors.Add(new DoneErrorItem(
                                                                                        p.Name,
                                                                                        "extract",
                                                                                        reason));
                                                                                    channel.Writer.TryWrite(
                                                                                        new SseEnvelope(
                                                                                            SseHelper.EventProviderError,
                                                                                            new ProviderErrorPayload(
                                                                                                p.Name,
                                                                                                "extract",
                                                                                                reason)));
                                                                                }

                                                                                foreach (var ext in extracted)
                                                                                {
                                                                                    TryEnqueueSource(
                                                                                        VideoSourceMerger.Merge(ext, src));
                                                                                }

                                                                                if (extracted.Count > 0)
                                                                                {
                                                                                    blame.RecordProvider(
                                                                                        p.Name,
                                                                                        "extract",
                                                                                        extractSw.ElapsedMilliseconds,
                                                                                        BlameOutcome.Ok);
                                                                                }
                                                                            }
                                                                            else
                                                                            {
                                                                                var direct = new List<VideoSource>
                                                                                {
                                                                                    src
                                                                                };
                                                                                await extractorManager.EnrichSourcesAsync(
                                                                                    direct,
                                                                                    cancellationToken);
                                                                                TryEnqueueSource(src);
                                                                            }
                                                                        }
                                                                        catch (Exception ex)
                                                                        {
                                                                            blame.RecordProvider(
                                                                                p.Name,
                                                                                "extract",
                                                                                extractSw.ElapsedMilliseconds,
                                                                                BlameOutcome.Error);
                                                                            logger.LogWarning(ex,
                                                                                "source extraction failed for provider {Provider} url {Url}",
                                                                                provider,
                                                                                src.Url);
                                                                            Interlocked.Increment(ref failedCount);
                                                                            errors.Add(new DoneErrorItem(
                                                                                p.Name,
                                                                                "extract",
                                                                                $"Upstream extractor hatası. [{src.Url}]"));
                                                                            channel.Writer.TryWrite(
                                                                                new SseEnvelope(
                                                                                    SseHelper.EventProviderError,
                                                                                    new ProviderErrorPayload(
                                                                                        p.Name,
                                                                                        "extract",
                                                                                        $"Upstream extractor hatası. [{src.Url}]")));
                                                                        }
                                                                    },
                                                                    cancellationToken))
                                            .ToList();

            _ = Task.Run(async () =>
                         {
                             try
                             {
                                 await Task.WhenAll(extractionTasks);
                                 await channel.Writer.WriteAsync(
                                     new SseEnvelope(SseHelper.EventDone,
                                                     new DoneSummary(sentCount, Volatile.Read(ref failedCount), errors.ToList(), sentCount)),
                                     cancellationToken);
                             }
                             finally
                             {
                                 channel.Writer.Complete();
                             }
                         },
                         cancellationToken);

            await SseHelper.StreamEnvelopesAsync(context, channel.Reader, cancellationToken);

            return Results.Empty;
        }

        var sourcesSw = Stopwatch.StartNew();
        try
        {
            var rawSources = await p.GetVideoSourcesAsync(episodeId, group, cancellationToken);
            logger.LogDebug("sources listed for provider {Provider}: {Count} embeds in {ElapsedMs}ms",
                            provider,
                            rawSources.Count,
                            sourcesSw.ElapsedMilliseconds);
            blame.RecordProvider(provider,
                                 "sources",
                                 sourcesSw.ElapsedMilliseconds,
                                 rawSources.Count == 0 ? BlameOutcome.Empty : BlameOutcome.Ok);

            var tasks = rawSources.Select(src => Task.Run(async () =>
                                                          {
                                                              var extractSw = Stopwatch.StartNew();
                                                              try
                                                              {
                                                                  if ((src.Type == VideoType.Embed
                                                                      || src.Type == VideoType.M3U8)
                                                                      && extractorManager.CanExtract(src.Url))
                                                                  {
                                                                      var headers = BuildRefererHeaders(p);

                                                                      var outcome =
                                                                          await extractorManager.ExtractDetailedAsync(
                                                                              src.Url,
                                                                              headers,
                                                                              cancellationToken);
                                                                      var extracted = outcome.Sources;
                                                                      var resolved = new List<VideoSource>(extracted.Count);
                                                                      foreach (var ext in extracted)
                                                                      {
                                                                          resolved.Add(VideoSourceMerger.Merge(ext, src));
                                                                      }

                                                                      blame.RecordProvider(
                                                                          p.Name,
                                                                          "extract",
                                                                          extractSw.ElapsedMilliseconds,
                                                                          extracted.Count == 0
                                                                              ? BlameOutcome.Empty
                                                                              : BlameOutcome.Ok);
                                                                      if (extracted.Count == 0)
                                                                      {
                                                                          var reason = outcome.Failures.Count > 0
                                                                              ? $"{outcome.Failures[0].Kind}: {outcome.Failures[0].Detail}"
                                                                              : "sebep bilinmiyor";
                                                                          logger.LogWarning(
                                                                              "embed resolved empty for provider {Provider} url {Url} ({Reason})",
                                                                              provider,
                                                                              src.Url,
                                                                              reason);
                                                                      }
                                                                      return (resolved, outcome.Failures);
                                                                  }

                                                                  return ((List<VideoSource>) [src], new List<ExtractionFailure>());
                                                              }
                                                              catch (Exception ex)
                                                              {
                                                                  logger.LogWarning(ex,
                                                                      "source extraction failed for provider {Provider} url {Url}",
                                                                      provider,
                                                                      src.Url);
                                                                  blame.RecordProvider(
                                                                      p.Name,
                                                                      "extract",
                                                                      extractSw.ElapsedMilliseconds,
                                                                      BlameOutcome.Error);
                                                                  return ((List<VideoSource>) [], new List<ExtractionFailure>());
                                                              }
                                                          },
                                                          cancellationToken));

            var results         = await Task.WhenAll(tasks);
            var emptyEmbeds     = results.Count(x => x.Item1.Count == 0);
            if (emptyEmbeds > 0)
            {
                logger.LogWarning("{Empty}/{Total} embeds resolved empty for provider {Provider} episode {EpisodeId}",
                                  emptyEmbeds,
                                  results.Length,
                                  provider,
                                  episodeId);
            }

            var resolvedSources = results.SelectMany(x => x.Item1).ToList();
            await extractorManager.EnrichSourcesAsync(resolvedSources, cancellationToken);
            var finalSources = resolvedSources.GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
                                              .Select(x => x.OrderByDescending(s => s.Bitrate ?? 0).First())
                                              .ToList();
            var (playable, rawEmbeds) = PartitionEmbeds(finalSources);

            var emptyPairs = rawSources.Zip(results, (src, r) => (src, r))
                                           .Where(x => x.r.Item1.Count == 0)
                                           .ToList();
            var unresolved = emptyPairs.Select(x => ResolveWarningMapper.UnresolvedFrom(x.src.Url, x.r.Item2))
                                       .ToList();

            return Results.Ok(new
            {
                sources    = playable,
                embeds     = rawEmbeds,
                unresolved
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "getSources failed for provider {Provider}", provider);
            var (sourcesOutcome, sourcesError) =
                ProviderErrorMapper.Map(ex, $"Upstream kaynak hatası ({provider}).");
            blame.RecordProvider(provider, "sources", sourcesSw.ElapsedMilliseconds, sourcesOutcome);
            return Results.Problem(sourcesError,
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
    }
}
