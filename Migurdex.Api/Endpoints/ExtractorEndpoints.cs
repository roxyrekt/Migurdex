using Migurdex.Api.Common;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net;
using System.Net.Sockets;

namespace Migurdex.Api.Endpoints;

public record ResolveExtractorRequest(string Url, Dictionary<string, string>? Headers = null);

public sealed record UnresolvedEmbed(string Url, string Code, string Message);

public sealed record ResolveWarning(string Code, string Message, List<string> Urls)
{
    public static ResolveWarning From(ExtractionWarning reason, string message)
    {
        return new ResolveWarning(reason.ToString(), message, []);
    }
}

public static class ResolveWarningMapper
{
    public static List<ResolveWarning> MapFailuresToWarnings(bool canExtract,
        int                                                            sourceCount,
        List<ExtractionFailure>                                        failures)
    {
        var warnings = new List<ResolveWarning>();
        if (!canExtract)
        {
            warnings.Add(ResolveWarning.From(ExtractionWarning.NoExtractorMatched,
                                             "Bu URL ile eşleşen extractor bulunamadı."));

            return warnings;
        }

        var relevant = sourceCount > 0
            ? failures.Where(f => f.Confident && f.Kind != UpstreamErrorKind.Unknown)
            : failures;

        foreach (var kind in relevant.Select(f => f.Kind).Distinct())
        {
            var detail = relevant.First(f => f.Kind == kind).Detail;
            warnings.Add(ResolveWarning.From(MapKind(kind),
                                             string.IsNullOrWhiteSpace(detail) ? DefaultMessage(kind) : detail));
        }

        if (sourceCount == 0 && warnings.Count == 0)
        {
            warnings.Add(ResolveWarning.From(ExtractionWarning.EmptyResult,
                                             "Extractor eşleşti ancak oynatılabilir kaynak dönmedi (upstream engeli, özel içerik veya probe filtresi olabilir)."));
        }

        return warnings;
    }

    public static UnresolvedEmbed UnresolvedFrom(string url, List<ExtractionFailure> failures)
    {
        var first = failures.FirstOrDefault();
        if (first is null)
        {
            return new UnresolvedEmbed(url,
                                       ExtractionWarning.EmptyResult.ToString(),
                                       "sebep bilinmiyor");
        }

        var detail = string.IsNullOrWhiteSpace(first.Detail) ? DefaultMessage(first.Kind) : first.Detail;

        return new UnresolvedEmbed(url, MapKind(first.Kind).ToString(), detail);
    }

    private static ExtractionWarning MapKind(UpstreamErrorKind kind)
    {
        return kind switch
        {
            UpstreamErrorKind.QuotaExceeded    => ExtractionWarning.QuotaExceeded,
            UpstreamErrorKind.RateLimited      => ExtractionWarning.RateLimited,
            UpstreamErrorKind.Private          => ExtractionWarning.Private,
            UpstreamErrorKind.Deleted          => ExtractionWarning.Deleted,
            UpstreamErrorKind.NotFound         => ExtractionWarning.NotFound,
            UpstreamErrorKind.EmbedBlocked     => ExtractionWarning.EmbedBlocked,
            UpstreamErrorKind.CopyrightBlocked => ExtractionWarning.CopyrightBlocked,
            UpstreamErrorKind.UpstreamChanged  => ExtractionWarning.UpstreamChanged,
            UpstreamErrorKind.CloudflareBlocked => ExtractionWarning.CloudflareBlocked,
            UpstreamErrorKind.Processing       => ExtractionWarning.Processing,
            _                                  => ExtractionWarning.EmptyResult
        };
    }

    private static string DefaultMessage(UpstreamErrorKind kind)
    {
        return kind switch
        {
            UpstreamErrorKind.QuotaExceeded    => "Upstream kotası tükendi.",
            UpstreamErrorKind.RateLimited      => "Upstream hız sınırı (çok fazla istek).",
            UpstreamErrorKind.Private          => "İçerik özel veya giriş gerektiriyor.",
            UpstreamErrorKind.Deleted          => "İçerik silinmiş veya kaldırılmış.",
            UpstreamErrorKind.NotFound         => "İçerik bulunamadı.",
            UpstreamErrorKind.EmbedBlocked     => "İçerik gömülü oynatıcıya kapalı.",
            UpstreamErrorKind.CopyrightBlocked => "İçerik telif nedeniyle engelli.",
            UpstreamErrorKind.UpstreamChanged  => "Kaynak site yapısı değişmiş olabilir.",
            UpstreamErrorKind.CloudflareBlocked => "Cloudflare bot koruması devrede.",
            UpstreamErrorKind.Processing       => "Video işleniyor, daha sonra tekrar dene.",
            _                                  => "Extractor eşleşti ancak oynatılabilir kaynak dönmedi."
        };
    }
}

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

            var outcome = await extractorManager.ExtractDetailedAsync(
                              url,
                              request.Headers,
                              timeoutCts.Token);
            var sources = outcome.Sources;

            var canExtract = extractorManager.CanExtract(url);
            var warnings   = ResolveWarningMapper.MapFailuresToWarnings(canExtract, sources.Count, outcome.Failures);

            return Results.Ok(new
            {
                url,
                canExtract,
                results    = sources,
                warnings
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
