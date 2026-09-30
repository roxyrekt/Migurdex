using Migurdex.Api.Common;
using Migurdex.Shared;
using Migurdex.Shared.Interfaces;
using System.Net;
using System.Net.Sockets;

namespace Migurdex.Api.Endpoints;

public record ResolveExtractorRequest(string Url, Dictionary<string, string>? Headers = null);

public static class ExtractorEndpoints
{
    private const int MaxUrlLength    = 2048;
    private const int MaxHeaders      = 20;
    private const int MaxHeaderLength = 4096;

    private static readonly HashSet<string> _blockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host",
        "authorization",
        "proxy-authorization",
        "proxy-authenticate"
    };

    public static IEndpointRouteBuilder MapExtractorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/extractors", GetExtractors);
        app.MapPost("/api/v1/extractors/resolve", ResolveExtractor);

        return app;
    }

    private static IResult GetExtractors(IExtractorManager extractorManager)
    {
        var extractors = extractorManager.Extractors
                                         .Select(e => new
                                         {
                                             e.Name
                                         })
                                         .OrderBy(e => e.Name)
                                         .ToList();

        return Results.Ok(extractors);
    }

    private static async Task<IResult> ResolveExtractor(
        ResolveExtractorRequest request,
        IExtractorManager       extractorManager,
        ILoggerFactory          loggerFactory,
        CancellationToken       cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ExtractorEndpoints");

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return ApiErrors.BadRequest("URL boş olamaz.");
        }

        var url = request.Url.Trim();

        if (url.Length > MaxUrlLength)
        {
            return ApiErrors.BadRequest($"URL en fazla {MaxUrlLength} karakter olabilir.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ApiErrors.BadRequest("URL yalnızca http/https olabilir.");
        }

        if (request.Headers is { Count: > MaxHeaders })
        {
            return ApiErrors.BadRequest($"En fazla {MaxHeaders} header gönderilebilir.");
        }

        if (request.Headers != null)
        {
            foreach (var kv in request.Headers)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)
                    || kv.Key.Length > MaxHeaderLength
                    || (kv.Value?.Length ?? 0) > MaxHeaderLength)
                {
                    return ApiErrors.BadRequest("Header anahtar/değer çok uzun.");
                }

                if (_blockedHeaders.Contains(kv.Key.Trim()))
                {
                    return ApiErrors.BadRequest($"Header '{kv.Key.Trim()}' gönderilemez.");
                }
            }
        }

        if (await ResolvesToBlockedAddressAsync(uri.Host, cancellationToken))
        {
            logger.LogWarning("extractor resolve blocked for private host {Host}", uri.Host);
            return ApiErrors.BadRequest("Bu host'a istek gönderilemez.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var sources = await extractorManager.ExtractAsync(
                              url,
                              request.Headers,
                              timeoutCts.Token);

            return Results.Ok(new
            {
                url,
                canExtract = extractorManager.CanExtract(url),
                results    = sources
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("extractor resolve timed out for url {Url}", url);
            return Results.Problem("Extractor zaman aşımı.",
                                   statusCode: StatusCodes.Status504GatewayTimeout,
                                   title: "Zaman aşımı");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "extractor resolve failed for url {Url}", url);
            return Results.Problem("Upstream extractor hatası.",
                                   statusCode: StatusCodes.Status502BadGateway,
                                   title: "Upstream hata");
        }
    }

    private static Task<bool> ResolvesToBlockedAddressAsync(string host, CancellationToken cancellationToken)
    {
        return NetworkGuard.ResolvesToBlockedAddressAsync(host, cancellationToken);
    }
}
