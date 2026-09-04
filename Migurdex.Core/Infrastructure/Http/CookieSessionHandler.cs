using System.Net;

namespace Migurdex.Core.Infrastructure.Http;

internal class CookieSessionHandler : DelegatingHandler
{
    private readonly CookieContainer _cookieContainer = new();

    public CookieSessionHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken                                                           cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri != null)
        {
            var cookieHeader = _cookieContainer.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (uri != null && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var cookieHeader in setCookies)
            {
                _cookieContainer.SetCookies(uri, cookieHeader);
            }
        }

        return response;
    }
}
