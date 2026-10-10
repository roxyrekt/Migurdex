using Migurdex.Core.Services.Turnstile;

namespace Migurdex.Core.Infrastructure.Http;

public sealed class CfClearanceHandler : DelegatingHandler
{
    private readonly CfClearanceStore? _clearance;

    public CfClearanceHandler(CfClearanceStore? clearance, HttpMessageHandler innerHandler) : base(innerHandler)
    {
        _clearance = clearance;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken                                                     cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null
            || string.IsNullOrEmpty(uri.Host)
            || _clearance is null
            || !_clearance.TryGet(uri.Host, out var value, out _)
            || string.IsNullOrEmpty(value))
        {
            return base.SendAsync(request, cancellationToken);
        }

        var hasClearance = request.Headers.TryGetValues("Cookie", out var existing)
                           && existing.Any(h => h.Contains("cf_clearance=", StringComparison.Ordinal));
        if (!hasClearance)
        {
            var merged = existing is not null
                             ? string.Join("; ", existing.Append($"cf_clearance={value}"))
                             : $"cf_clearance={value}";
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", merged);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
