using Migurdex.Api.Common;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Api.Endpoints;

public static class TrackerResolveEndpoints
{
    public static IEndpointRouteBuilder MapTrackerResolveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tracker/resolve", ResolveFromProvider);
        app.MapGet("/api/v1/tracker/lookup", LookupFromTracker);

        return app;
    }

    private static async Task<IResult> ResolveFromProvider(
        string?            provider,
        string?            id,
        string?            title,
        string?            title2,
        int?               year,
        string?            format,
        string?            malId,
        string?            anilistId,
        ITrackerIdResolver resolver,
        ILoggerFactory     loggerFactory,
        CancellationToken  cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("TrackerResolveEndpoints");

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id))
        {
            return ApiErrors.BadRequest("provider ve id boş olamaz.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return ApiErrors.BadRequest("En az bir başlık (title) gerekli.");
        }

        ContentFormat? parsedFormat = null;
        if (!string.IsNullOrWhiteSpace(format) && Enum.TryParse<ContentFormat>(format, true, out var f))
        {
            parsedFormat = f;
        }

        var titles = string.IsNullOrWhiteSpace(title2)
                         ? [title]
                         : new[] { title, title2 };

        IReadOnlyList<SeasonMapping>? seasonMappings = null;
        if (!string.IsNullOrWhiteSpace(malId) || !string.IsNullOrWhiteSpace(anilistId))
        {
            seasonMappings =
            [
                new SeasonMapping
                {
                    SeasonNumber  = 1,
                    MyAnimeListId = string.IsNullOrWhiteSpace(malId) ? null : malId.Trim(),
                    AniListId     = string.IsNullOrWhiteSpace(anilistId) ? null : anilistId.Trim()
                }
            ];
        }

        try
        {
            var result = await resolver.ResolveFromProviderAsync(
                             provider.Trim(), id.Trim(), titles, year, parsedFormat,
                             seasonMappings: seasonMappings, cancellationToken: cancellationToken);
            return Results.Ok(result);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "tracker resolve failed for {Provider}:{Id}", provider, id);
            return Results.Problem("Tracker çözümleme hatası.",
                                   statusCode: StatusCodes.Status502BadGateway, title: "Upstream hata");
        }
    }

    private static async Task<IResult> LookupFromTracker(
        string?            anilistId,
        string?            malId,
        ITrackerIdResolver resolver,
        ILoggerFactory     loggerFactory,
        CancellationToken  cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("TrackerResolveEndpoints");

        if (string.IsNullOrWhiteSpace(anilistId) && string.IsNullOrWhiteSpace(malId))
        {
            return ApiErrors.BadRequest("anilistId veya malId gerekli.");
        }

        try
        {
            var meta = await resolver.ResolveFromTrackerAsync(
                           anilistId?.Trim(), malId?.Trim(), cancellationToken);
            return meta is not null
                       ? Results.Ok(meta)
                       : ApiErrors.NotFound("Eşleşen tracker kaydı bulunamadı.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "tracker lookup failed anilist:{A} mal:{M}", anilistId, malId);
            return Results.Problem("Tracker arama hatası.",
                                   statusCode: StatusCodes.Status502BadGateway, title: "Upstream hata");
        }
    }
}
