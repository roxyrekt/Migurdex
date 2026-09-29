using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Migurdex.Cli.Services.Downloads;

public sealed class DownloadHttpClientFactory : IDownloadHttpClientFactory
{
    public HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies         = false,
            UseProxy           = true
        };

        return new HttpClient(handler, true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}

public sealed class HttpMessageHandlerDownloadClientFactory : IDownloadHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public HttpMessageHandlerDownloadClientFactory(HttpMessageHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public HttpClient CreateClient()
    {
        return new HttpClient(_handler, false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}

internal static class DownloadHttp
{
    public const int MaxRedirects = 5;

    private static readonly HashSet<string> _crossOriginSafeHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept",
            "Accept-Language",
            "Origin",
            "Range",
            "Referer",
            "User-Agent"
        };

    private static readonly HashSet<string> _externalProcessSafeHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept",
            "Accept-Language",
            "Origin",
            "Referer",
            "User-Agent"
        };

    private static readonly HashSet<string> _subtitleFallbackSafeHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept",
            "Accept-Language",
            "Origin",
            "Referer",
            "User-Agent"
        };

    public static async Task<HttpResponseMessage> SendWithRedirectsAsync(
        HttpClient                    client,
        Uri                           initialUri,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken             cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateHttpUri(initialUri);

        var currentUri     = initialUri;
        var requestHeaders = CopyHeaders(headers);

        for (var redirectCount = 0;;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = CreateRequest(currentUri, requestHeaders);
            var response = await client.SendAsync(request,
                                                  HttpCompletionOption.ResponseHeadersRead,
                                                  cancellationToken)
                                   .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();

            if (redirectCount >= MaxRedirects)
            {
                throw new DownloadException("Yönlendirme sınırı aşıldı.");
            }

            if (location is null)
            {
                throw new DownloadException("Yönlendirme adresi eksik.");
            }

            var nextUri = location.IsAbsoluteUri
                              ? location
                              : new Uri(currentUri, location);
            ValidateHttpUri(nextUri);

            if (!IsSameOrigin(currentUri, nextUri))
            {
                // Redirect zinciri boyunca yalnız açıkça güvenli başlıklar korunur.
                // Böylece özel API-key/token başlıkları varsayılan olarak yeni origin'e taşınmaz.
                requestHeaders = FilterCrossOriginHeaders(requestHeaders);
            }

            currentUri    = nextUri;
            redirectCount++;
        }
    }

    public static Dictionary<string, string> CopyHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return copy;
        }

        foreach (var header in headers)
        {
            var key = header.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key)
                || header.Value is null
                || key.Contains('\r')
                || key.Contains('\n')
                || header.Value.Contains('\r')
                || header.Value.Contains('\n'))
            {
                continue;
            }

            copy[key] = header.Value;
        }

        return copy;
    }

    public static Dictionary<string, string> CopyExternalProcessHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        return FilterAllowedHeaders(headers, _externalProcessSafeHeaders);
    }

    public static Dictionary<string, string> CopySubtitleFallbackHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers,
        Uri?                              sourceUri,
        Uri                               subtitleUri)
    {
        var filtered = FilterAllowedHeaders(headers, _subtitleFallbackSafeHeaders);
        var result   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in filtered)
        {
            if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceUri is not null)
                {
                    if (!IsSameOrigin(sourceUri, subtitleUri))
                    {
                        continue;
                    }
                }
                else if (!Uri.TryCreate(value, UriKind.Absolute, out var headerUri)
                         || !IsSameOrigin(headerUri, subtitleUri))
                {
                    continue;
                }
            }

            result[name] = value;
        }

        return result;
    }

    public static string CreateSourceFingerprint(
        string?                                      sourceUrl,
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var normalizedHeaders = CopyHeaders(headers);
        normalizedHeaders.Remove("Range");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashField(hash, sourceUrl?.Trim() ?? string.Empty);
        foreach (var (name, value) in normalizedHeaders.OrderBy(item => item.Key,
                                                                    StringComparer.OrdinalIgnoreCase))
        {
            AppendHashField(hash, name.Trim());
            AppendHashField(hash, value);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24];
    }

    public static string CreateOpaqueDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                          .ToLowerInvariant();
    }

    public static bool IsHtml(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNonMediaContent(HttpResponseMessage response)
    {
        // Medya gövdesi asla metin/JSON/XML/görsel olmamalıdır. Sunucu hata sayfasını
        // 200 ile dönerse (CDN engelleri, hata proxy'leri) dosya bozuk video olarak kaydedilirdi.
        // Content-Type yoksa gerçek medya sunucularına güvenilir; bilinen medya türleri serbest bırakılır.
        var mediaType = response.Content.Headers.ContentType?.MediaType
                            ?.Split(';', StringSplitOptions.TrimEntries)[0]
                            .Trim()
                            .ToLowerInvariant();
        if (string.IsNullOrEmpty(mediaType)
            || mediaType is "application/octet-stream" or "application/binary")
        {
            return false;
        }

        if (mediaType is "application/xhtml+xml" or "application/x-www-form-urlencoded"
            || mediaType.EndsWith("+json", StringComparison.Ordinal)
            || mediaType.EndsWith("+xml", StringComparison.Ordinal))
        {
            return true;
        }

        return mediaType.StartsWith("text/", StringComparison.Ordinal)
               || mediaType.StartsWith("image/", StringComparison.Ordinal)
               || mediaType.StartsWith("font/", StringComparison.Ordinal);
    }

    public static bool IsSameOrigin(Uri left, Uri right)
    {
        return left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
               && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
               && left.Port == right.Port;
    }

    public static void ValidateHttpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new DownloadException("Yalnız HTTP ve HTTPS kaynakları desteklenir.");
        }
    }

    private static HttpRequestMessage CreateRequest(
        Uri                                    uri,
        IReadOnlyDictionary<string, string>    headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    private static Dictionary<string, string> FilterCrossOriginHeaders(
        IReadOnlyDictionary<string, string> headers)
    {
        return FilterAllowedHeaders(headers, _crossOriginSafeHeaders);
    }

    private static Dictionary<string, string> FilterAllowedHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers,
        HashSet<string>                            allowedNames)
    {
        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return filtered;
        }

        foreach (var (name, value) in CopyHeaders(headers))
        {
            if (allowedNames.Contains(name))
            {
                filtered[name] = value;
            }
        }

        return filtered;
    }

    private static void AppendHashField(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }
}
