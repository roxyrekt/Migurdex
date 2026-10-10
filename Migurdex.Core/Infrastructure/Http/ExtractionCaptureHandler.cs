using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;

namespace Migurdex.Core.Infrastructure.Http;

public class ExtractionCaptureHandler : DelegatingHandler
{
    private const int MaxBufferBytes = 512 * 1024;
    private const int MaxSuccessSniffBytes = 64 * 1024;

    public ExtractionCaptureHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken                                                           cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (!ExtractionCapture.IsActive || response.Content is null)
        {
            return response;
        }

        if (!response.IsSuccessStatusCode)
        {
            ExtractionCapture.Record((int) response.StatusCode, await SniffAsync(response, MaxBufferBytes, cancellationToken));

            return response;
        }

        var sniffed = await SniffAsync(response, MaxSuccessSniffBytes, cancellationToken);
        if (!string.IsNullOrEmpty(sniffed)
            && UpstreamErrorClassifier.Detect(200, sniffed) is { } kind
            && kind != UpstreamErrorKind.Unknown)
        {
            ExtractionCapture.RecordLowConfidence((int) response.StatusCode, sniffed);
        }

        return response;
    }

    private static async Task<string> SniffAsync(HttpResponseMessage response,
        int                                                           maxBytes,
        CancellationToken                                             cancellationToken)
    {
        try
        {
            var length = response.Content.Headers.ContentLength;
            if (IsTextLike(response) && (length is null || length <= maxBytes))
            {
                await response.Content.LoadIntoBufferAsync(maxBytes);

                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static bool IsTextLike(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrEmpty(mediaType))
        {
            return true;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase)
               || mediaType.EndsWith("xml", StringComparison.OrdinalIgnoreCase);
    }
}
