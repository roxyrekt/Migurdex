using Migurdex.Api.Common;
using Migurdex.Core.PluginSystem;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
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
                                         try
                                         {
                                             var searchResults = await p.SearchAsync(q, cancellationToken);
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
                                             errors.Add(new DoneErrorItem(p.Name, "search", "Upstream arama hatası."));
                                             await channel.Writer.WriteAsync(
                                                 new SseEnvelope(SseHelper.EventProviderError,
                                                                 new ProviderErrorPayload(
                                                                     p.Name,
                                                                     "search",
                                                                     "Upstream arama hatası.")),
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
            try
            {
                var searchResults = await p.SearchAsync(q, cancellationToken);
                return (object) new
                {
                    provider = p.Name,
                    data     = searchResults
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "search failed for provider {Provider} query {Query}", p.Name, q);
                return new
                {
                    provider = p.Name,
                    error    = "Upstream arama hatası."
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
        MetadataManager   metadataManager,
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

        try
        {
            var details = await p.GetDetailsAsync(animeId, cancellationToken);

            foreach (var mapping in details.SeasonMappings.OrderBy(m => m.SeasonNumber))
            {
                var resolvedMeta = await metadataManager.FindBestMatchOrOverrideAsync(
                                       details,
                                       details.Format,
                                       season: mapping.SeasonNumber,
                                       cancellationToken: cancellationToken);

                if (resolvedMeta != null)
                {
                    switch (resolvedMeta.Source)
                    {
                        case MetadataSource.AniList:
                            mapping.AniListId     ??= resolvedMeta.ExternalId;
                            mapping.MyAnimeListId ??= resolvedMeta.MyAnimeListId;
                            break;
                        case MetadataSource.Jikan:
                            mapping.MyAnimeListId ??= resolvedMeta.ExternalId;
                            mapping.AniListId     ??= resolvedMeta.AniListId;
                            break;
                        case MetadataSource.Tmdb:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }

            return Results.Ok(details);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "getDetails failed for provider {Provider} anime {AnimeId}", provider, animeId);
            return Results.Problem($"Upstream detay hatası ({provider}).",
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
    }

    private static async Task<IResult> GetEpisodeGroups(
        string            provider,
        string            episodeId,
        PluginLoader      loader,
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

        try
        {
            var groups = await p.GetGroupsAsync(episodeId, cancellationToken);
            return Results.Ok(groups);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "getGroups failed for provider {Provider} episode {EpisodeId}", provider, episodeId);
            return Results.Problem($"Upstream grup hatası ({provider}).",
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
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
            try
            {
                rawSources = await p.GetVideoSourcesAsync(episodeId, group, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                                  "getSources failed for provider {Provider} episode {EpisodeId} group {Group}",
                                  provider,
                                  episodeId,
                                  group);
                SseHelper.InitializeSseResponse(context);
                await SseHelper.WriteProviderErrorAsync(context,
                                                        provider,
                                                        "sources",
                                                        "Upstream kaynak hatası.",
                                                        cancellationToken);
                await SseHelper.WriteDoneSummaryAsync(context,
                                                      new DoneSummary(0,
                                                                      1,
                                                                      [
                                                                          new DoneErrorItem(
                                                                              provider,
                                                                              "sources",
                                                                              "Upstream kaynak hatası.")
                                                                      ],
                                                                      0),
                                                      cancellationToken);
                return Results.Empty;
            }

            var sentUrls  = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var channel   = Channel.CreateUnbounded<SseEnvelope>();
            var sentCount = 0;

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
                                                                        try
                                                                        {
                                                                            if (src.Type == VideoType.Embed
                                                                                    && extractorManager.CanExtract(
                                                                                        src.Url))
                                                                            {
                                                                                var headers = BuildRefererHeaders(p);

                                                                                var extracted =
                                                                                    await extractorManager
                                                                                        .ExtractAsync(src.Url,
                                                                                            headers,
                                                                                            cancellationToken);
                                                                                foreach (var ext in extracted)
                                                                                {
                                                                                    ext.Group ??= src.Group;
                                                                                    TryEnqueueSource(ext);
                                                                                }
                                                                            }
                                                                            else
                                                                            {
                                                                                TryEnqueueSource(src);
                                                                            }
                                                                        }
                                                                        catch (Exception ex)
                                                                        {
                                                                            logger.LogWarning(ex,
                                                                                "source extraction failed for provider {Provider} url {Url}",
                                                                                provider,
                                                                                src.Url);
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
                                                     new DoneSummary(sentCount, 0, [], sentCount)),
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

        try
        {
            var rawSources = await p.GetVideoSourcesAsync(episodeId, group, cancellationToken);

            var tasks = rawSources.Select(src => Task.Run(async () =>
                                                          {
                                                              try
                                                              {
                                                                  if (src.Type == VideoType.Embed
                                                                      && extractorManager.CanExtract(src.Url))
                                                                  {
                                                                      var headers = BuildRefererHeaders(p);

                                                                      var extracted =
                                                                          await extractorManager.ExtractAsync(
                                                                              src.Url,
                                                                              headers,
                                                                              cancellationToken);
                                                                      foreach (var ext in extracted)
                                                                      {
                                                                          ext.Group ??= src.Group;
                                                                      }

                                                                      return extracted;
                                                                  }

                                                                  return (List<VideoSource>) [src];
                                                              }
                                                              catch (Exception ex)
                                                              {
                                                                  logger.LogWarning(ex,
                                                                      "source extraction failed for provider {Provider} url {Url}",
                                                                      provider,
                                                                      src.Url);
                                                                  return (List<VideoSource>) [];
                                                              }
                                                          },
                                                          cancellationToken));

            var results         = await Task.WhenAll(tasks);
            var resolvedSources = results.SelectMany(x => x).ToList();
            var finalSources = resolvedSources.GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
                                              .Select(x => x.First())
                                              .ToList();

            return Results.Ok(finalSources);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "getSources failed for provider {Provider}", provider);
            return Results.Problem($"Upstream kaynak hatası ({provider}).",
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
    }
}
