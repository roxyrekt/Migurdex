using Migurdex.Api.Common;
using Migurdex.Core.PluginSystem;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Api.Endpoints;

public static class TrackerSeasonEndpoints
{
    public static IEndpointRouteBuilder MapTrackerSeasonEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tracker/seasons", GetSeasonChain);
        app.MapGet("/api/v1/tracker/align", AlignEntry);

        return app;
    }

    private static async Task<IResult> GetSeasonChain(
        string?             anilistId,
        ISeasonChainService seasons,
        ILoggerFactory      loggerFactory,
        CancellationToken   cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("TrackerSeasonEndpoints");

        if (string.IsNullOrWhiteSpace(anilistId))
        {
            return ApiErrors.BadRequest("anilistId boş olamaz.");
        }

        try
        {
            var chain = await seasons.GetSeasonChainAsync(anilistId.Trim(), cancellationToken);
            return chain is not null
                       ? Results.Ok(chain)
                       : ApiErrors.NotFound("Sezon zinciri kurulamadı.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "season chain failed '{Id}'", anilistId);
            return Results.Problem("Sezon zinciri hatası.",
                                   statusCode: StatusCodes.Status502BadGateway, title: "Upstream hata");
        }
    }

    private static async Task<IResult> AlignEntry(
        string?             provider,
        string?             id,
        PluginLoader        loader,
        ITrackerIdResolver  resolver,
        ISeasonChainService seasons,
        ILoggerFactory      loggerFactory,
        CancellationToken   cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("TrackerSeasonEndpoints");

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id))
        {
            return ApiErrors.BadRequest("provider ve id boş olamaz.");
        }

        var animeProvider = loader.Providers.OfType<IAnimeProvider>().FirstOrDefault(p =>
                p.Name.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (animeProvider is null)
        {
            return ApiErrors.NotFound($"Sağlayıcı '{provider}' bulunamadı.");
        }

        try
        {
            var details = await animeProvider.GetDetailsAsync(id.Trim(), cancellationToken);
            if (details is null)
            {
                return ApiErrors.NotFound("Detay bulunamadı.");
            }

            var mappings = details.SeasonMappings ?? [];
            var direct = mappings.FirstOrDefault(m =>
                                                     !string.IsNullOrWhiteSpace(m.AniListId) ||
                                                     !string.IsNullOrWhiteSpace(m.MyAnimeListId));

            string? anilistId = direct?.AniListId;
            if (string.IsNullOrWhiteSpace(anilistId) && !string.IsNullOrWhiteSpace(direct?.MyAnimeListId))
            {
                var viaMal = await resolver.ResolveFromTrackerAsync(null, direct.MyAnimeListId, cancellationToken);
                anilistId = viaMal?.AniListId;
            }

            if (string.IsNullOrWhiteSpace(anilistId))
            {
                var resolved = await resolver.ResolveFromProviderAsync(
                                   animeProvider.Name, id.Trim(), details.GetAllTitles(),
                                   seasonMappings: mappings, cancellationToken: cancellationToken);
                anilistId = resolved.Entry?.AniListId;
                if (string.IsNullOrWhiteSpace(anilistId))
                {
                    return Results.Ok(new
                    {
                        details    = new { details.Title, details.Format },
                        resolved   = resolved.Entry,
                        ambiguous  = resolved.Ambiguous,
                        candidates = resolved.Candidates,
                        chain      = (object?)null,
                        alignment  = (object?)null
                    });
                }
            }

            var chain = await seasons.GetSeasonChainAsync(anilistId, cancellationToken);
            if (chain is null)
            {
                return Results.Ok(new
                {
                    details = new { details.Title, details.Format },
                    anilistId,
                    chain     = (object?)null,
                    alignment = (object?)null
                });
            }

            var alignment = seasons.AlignEntry(details, chain);
            return Results.Ok(new
            {
                details = new
                {
                    details.Title,
                    details.Format,
                    episodeCount = details.Episodes.Count,
                    mappings     = details.SeasonMappings
                },
                anilistId,
                chain,
                alignment
            });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "align failed for {Provider}:{Id}", provider, id);
            return Results.Problem("Hizalama hatası.",
                                   statusCode: StatusCodes.Status502BadGateway, title: "Upstream hata");
        }
    }
}
