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
        app.MapGet("/api/v1/tracker/episode", MapEpisode);

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
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
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

        var animeProvider = loader.Providers.OfType<IAnimeProvider>()
                                  .FirstOrDefault(p =>
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

            details.Normalize();

            var mappings = details.SeasonMappings ?? [];
            var (anilistId, fuzzy) = await ResolveAniListIdAsync(resolver,
                                                                 animeProvider.Name,
                                                                 id.Trim(),
                                                                 mappings,
                                                                 details.GetAllTitles(),
                                                                 cancellationToken);

            if (string.IsNullOrWhiteSpace(anilistId))
            {
                return Results.Ok(new
                {
                    details = new
                    {
                        details.Title,
                        details.Format
                    },
                    resolved   = fuzzy?.Entry,
                    ambiguous  = fuzzy?.Ambiguous ?? false,
                    candidates = fuzzy?.Candidates ?? [],
                    chain      = (object?) null,
                    alignment  = (object?) null
                });
            }

            var chain = await seasons.GetSeasonChainAsync(anilistId, cancellationToken);
            if (chain is null)
            {
                return Results.Ok(new
                {
                    details = new
                    {
                        details.Title,
                        details.Format
                    },
                    anilistId,
                    chain     = (object?) null,
                    alignment = (object?) null
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
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
    }

    private static async Task<(string? AniListId, TrackerResolveResult? Fuzzy)> ResolveAniListIdAsync(
        ITrackerIdResolver           resolver,
        string                       providerName,
        string                       id,
        IReadOnlyList<SeasonMapping> mappings,
        IEnumerable<string>          titles,
        CancellationToken            cancellationToken)
    {
        var direct = mappings.FirstOrDefault(m =>
                                                 !string.IsNullOrWhiteSpace(m.AniListId)
                                                 || !string.IsNullOrWhiteSpace(m.MyAnimeListId));

        if (!string.IsNullOrWhiteSpace(direct?.AniListId))
        {
            return (direct.AniListId, null);
        }

        if (!string.IsNullOrWhiteSpace(direct?.MyAnimeListId))
        {
            var viaMal = await resolver.ResolveFromTrackerAsync(null, direct.MyAnimeListId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(viaMal?.AniListId))
            {
                return (viaMal.AniListId, null);
            }
        }

        var resolved = await resolver.ResolveFromProviderAsync(
                           providerName,
                           id,
                           titles,
                           seasonMappings: mappings,
                           cancellationToken: cancellationToken);
        return (resolved.Entry?.AniListId, resolved);
    }

    private static async Task<IResult> MapEpisode(
        string?             provider,
        string?             id,
        int?                season,
        double?             episode,
        PluginLoader        loader,
        ITrackerIdResolver  resolver,
        ISeasonChainService seasons,
        ILoggerFactory      loggerFactory,
        CancellationToken   cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("TrackerSeasonEndpoints");

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id) || episode is null)
        {
            return ApiErrors.BadRequest("provider, id ve episode boş olamaz.");
        }

        var animeProvider = loader.Providers.OfType<IAnimeProvider>()
                                  .FirstOrDefault(p =>
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

            details.Normalize();

            var (anilistId, _) = await ResolveAniListIdAsync(resolver,
                                                             animeProvider.Name,
                                                             id.Trim(),
                                                             details.SeasonMappings ?? [],
                                                             details.GetAllTitles(),
                                                             cancellationToken);
            if (string.IsNullOrWhiteSpace(anilistId))
            {
                return ApiErrors.NotFound("Tracker ID çözülemedi.");
            }

            var chain = await seasons.GetSeasonChainAsync(anilistId, cancellationToken);
            if (chain is null)
            {
                return ApiErrors.NotFound("Sezon zinciri kurulamadı.");
            }

            var alignment = seasons.AlignEntry(details, chain);
            var canonical = seasons.TranslateToCanonical(alignment, season, episode.Value);
            if (canonical is null)
            {
                return ApiErrors.NotFound("Bölüm eşlenemedi.");
            }

            var entry = chain.Entries.FirstOrDefault(e => e.SeasonNumber == canonical.Season);
            return Results.Ok(new TrackerEpisodeMapping
            {
                AniListId     = entry?.AniListId ?? anilistId,
                MyAnimeListId = entry?.MyAnimeListId,
                Season        = canonical.Season,
                Episode       = canonical.Number,
                TotalEpisodes = entry?.TotalEpisodes,
                IsOverflow    = canonical.IsOverflow
            });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "episode map failed for {Provider}:{Id} ep {Episode}", provider, id, episode);
            return Results.Problem("Bölüm eşleme hatası.",
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
    }
}
