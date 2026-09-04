using Migurdex.Api.Common;
using Migurdex.Core.Services;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Api.Endpoints;

public static class MetadataEndpoints
{
    private const int MaxQueryLength = 200;

    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/metadata/search", SearchMetadata);
        app.MapGet("/api/v1/metadata/{source}/{id}", GetMetadata);

        return app;
    }

    private static async Task<IResult> SearchMetadata(
        string                         q,
        string?                        source,
        IEnumerable<IMetadataProvider> providers,
        ILoggerFactory                 loggerFactory,
        CancellationToken              cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("MetadataEndpoints");
        q = (q ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(q))
        {
            return ApiErrors.BadRequest("Arama sorgusu ('q') boş olamaz.");
        }

        if (q.Length > MaxQueryLength)
        {
            return ApiErrors.BadRequest($"Arama sorgusu en fazla {MaxQueryLength} karakter olabilir.");
        }

        var metadataProviders = providers as IMetadataProvider[] ?? providers.ToArray();

        if (!string.IsNullOrEmpty(source))
        {
            var providerName = source.Equals("mal", StringComparison.OrdinalIgnoreCase) ? "Jikan" : source;
            var targetProvider =
                metadataProviders.FirstOrDefault(x => x.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));

            if (targetProvider == null)
            {
                return ApiErrors.NotFound($"Metadata sağlayıcısı '{source}' bulunamadı.");
            }

            try
            {
                var results = await targetProvider.SearchMetadataAsync(q, cancellationToken: cancellationToken);
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "metadata search failed for source {Source}", source);
                return Results.Problem($"Upstream metadata hatası ({source}).",
                                       statusCode: StatusCodes.Status502BadGateway,
                                       title: "Upstream hata");
            }
        }

        var searchTasks = metadataProviders.Select(async p =>
        {
            try
            {
                return await p.SearchMetadataAsync(q, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "metadata search failed for provider {Provider}", p.Name);
                return new List<MediaMetadata>();
            }
        });

        var allResults = await Task.WhenAll(searchTasks);
        var combined   = allResults.SelectMany(x => x).ToList();

        return Results.Ok(combined);
    }

    private static async Task<IResult> GetMetadata(
        string                         source,
        string                         id,
        IEnumerable<IMetadataProvider> providers,
        ILoggerFactory                 loggerFactory,
        CancellationToken              cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("MetadataEndpoints");

        if (string.IsNullOrWhiteSpace(source))
        {
            return ApiErrors.BadRequest("source boş olamaz.");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return ApiErrors.BadRequest("id boş olamaz.");
        }

        source = source.Trim();
        var providerName = source.Equals("mal", StringComparison.OrdinalIgnoreCase) ? "Jikan" : source;
        var metadataProviders = providers as IMetadataProvider[] ?? providers.ToArray();
        var p = metadataProviders.FirstOrDefault(x => x.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));

        if (p == null)
        {
            return ApiErrors.NotFound($"Metadata sağlayıcısı '{source}' bulunamadı.");
        }

        var cleanId          = id.Trim();
        var hasMalPrefix     = cleanId.StartsWith("mal:", StringComparison.OrdinalIgnoreCase);
        var hasAniListPrefix = !hasMalPrefix && cleanId.StartsWith("anilist:", StringComparison.OrdinalIgnoreCase);
        if (hasMalPrefix)
        {
            cleanId = cleanId[4..].Trim();
        }
        else if (hasAniListPrefix)
        {
            cleanId = cleanId[8..].Trim();
        }

        if (string.IsNullOrWhiteSpace(cleanId))
        {
            return ApiErrors.BadRequest("id boş olamaz.");
        }

        try
        {
            if (source.Equals("anilist", StringComparison.OrdinalIgnoreCase) && p is AniListProvider aniListProvider)
            {
                if (hasMalPrefix)
                {
                    var meta = await aniListProvider.GetMetadataByMalIdAsync(cleanId, cancellationToken);
                    return meta != null
                               ? Results.Ok(meta)
                               : ApiErrors.NotFound("Eşleşen AniList kaydı bulunamadı.");
                }
            }

            if (providerName.Equals("Jikan", StringComparison.OrdinalIgnoreCase) && hasAniListPrefix)
            {
                if (metadataProviders.FirstOrDefault(x => x.Name.Equals("AniList", StringComparison.OrdinalIgnoreCase))
                    is AniListProvider aniList)
                {
                    var aniMeta = await aniList.GetMetadataByIdAsync(cleanId, cancellationToken);
                    if (aniMeta != null && !string.IsNullOrEmpty(aniMeta.MyAnimeListId))
                    {
                        var malMeta = await p.GetMetadataByIdAsync(aniMeta.MyAnimeListId, cancellationToken);
                        if (malMeta != null)
                        {
                            return Results.Ok(malMeta);
                        }
                    }
                }
            }

            var metadata = await p.GetMetadataByIdAsync(cleanId, cancellationToken);
            return metadata != null ? Results.Ok(metadata) : ApiErrors.NotFound("Metadata bulunamadı.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "metadata fetch failed for source {Source} id {Id}", source, cleanId);
            return Results.Problem($"Upstream metadata hatası ({source}).",
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
    }
}
