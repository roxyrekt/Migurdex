using Migurdex.Api.Common;
using Migurdex.Core.Services;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Api.Endpoints;

public static class CanonicalEndpoints
{
    public static IEndpointRouteBuilder MapCanonicalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/canonical/search", SearchCanonical);
        app.MapGet("/api/v1/canonical/episodes", GetCanonicalEpisodes);
        app.MapGet("/api/v1/canonical/sources", GetCanonicalSources);
        return app;
    }

    private static async Task<IResult> SearchCanonical(
        string q,
        ICanonicalResolver resolver,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("CanonicalEndpoints");
        q = (q ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(q))
        {
            return ApiErrors.BadRequest("Arama sorgusu ('q') boş olamaz.");
        }

        if (q.Length > 200)
        {
            return ApiErrors.BadRequest("Arama sorgusu en fazla 200 karakter olabilir.");
        }

        try
        {
            var result = await resolver.ResolveCanonicalAsync(q, cancellationToken);
            return Results.Ok(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "canonical search failed for '{Query}'", q);
            return Results.Problem("Canonical arama hatası.",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Upstream hata");
        }
    }

    private static async Task<IResult> GetCanonicalEpisodes(
        string? canonicalId,
        ICanonicalEpisodeService episodes,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("CanonicalEndpoints");

        if (string.IsNullOrWhiteSpace(canonicalId))
        {
            return ApiErrors.BadRequest("canonicalId boş olamaz (örn. anilist:154587).");
        }

        try
        {
            var result = await episodes.GetEpisodesAsync(canonicalId.Trim(), cancellationToken);
            return result is not null
                ? Results.Ok(result)
                : ApiErrors.NotFound("Canonical kayıt bulunamadı.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "canonical episodes failed for '{Id}'", canonicalId);
            return Results.Problem("Canonical bölüm listesi hatası.",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Upstream hata");
        }
    }

    private static async Task<IResult> GetCanonicalSources(
        string? canonicalId,
        int? season,
        double? number,
        string? group,
        bool? stream,
        ICanonicalEpisodeService episodes,
        HttpContext context,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("CanonicalEndpoints");

        if (string.IsNullOrWhiteSpace(canonicalId)
            || !CanonicalEpisodeService.TryParseId(canonicalId.Trim(), out _, out _))
        {
            return ApiErrors.BadRequest("canonicalId boş olamaz (örn. anilist:154587).");
        }

        if (number is null)
        {
            return ApiErrors.BadRequest("number gerekli (örn. ?number=1).");
        }

        group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        ICanonicalEpisodeService svc = episodes;
        string cid = canonicalId.Trim();
        double n = number.Value;
        string? g = group;

        if (stream == true)
        {
            var channel = System.Threading.Channels.Channel.CreateUnbounded<SseEnvelope>();
            var sentCount = 0;

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var src in svc.StreamEpisodeSourcesAsync(cid, n, g, cancellationToken))
                    {
                        Interlocked.Increment(ref sentCount);
                        channel.Writer.TryWrite(new SseEnvelope(SseHelper.EventSource, src));
                    }

                    await channel.Writer.WriteAsync(
                        new SseEnvelope(SseHelper.EventDone,
                                        new DoneSummary(sentCount, 0, [], sentCount)),
                        cancellationToken);
                }
                catch (KeyNotFoundException)
                {
                    await channel.Writer.WriteAsync(
                        new SseEnvelope(SseHelper.EventError, new { error = "Bölüm bulunamadı." }),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "canonical sources failed for '{Id}'", cid);
                    await channel.Writer.WriteAsync(
                        new SseEnvelope(SseHelper.EventError, new { error = "Upstream kaynak hatası." }),
                        cancellationToken);
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, cancellationToken);

            await SseHelper.StreamEnvelopesAsync(context, channel.Reader, cancellationToken);
            return Results.Empty;
        }

        try
        {
            var list = new List<VideoSource>();
            await foreach (var src in svc.StreamEpisodeSourcesAsync(cid, n, g, cancellationToken))
            {
                list.Add(src);
            }

            return Results.Ok(list);
        }
        catch (KeyNotFoundException)
        {
            return ApiErrors.NotFound("Bölüm bulunamadı.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "canonical sources failed for '{Id}'", cid);
            return Results.Problem("Canonical kaynak hatası.",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Upstream hata");
        }
    }
}
