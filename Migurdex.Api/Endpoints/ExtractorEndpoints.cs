using Migurdex.Api.Common;
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

    private static async Task<bool> ResolvesToBlockedAddressAsync(string host, CancellationToken cancellationToken)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsBlockedAddress(literal);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch
        {
            return false;
        }

        return addresses.Any(IsBlockedAddress);
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 0.0.0.0/8, 10/8, 172.16/12, 192.168/16,
            // 169.254/16, 100.64/10
            return bytes[0] == 0
                   || bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }
}
