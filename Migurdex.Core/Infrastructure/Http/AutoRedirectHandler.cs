using Migurdex.Shared.Infrastructure;
using System.Net;

namespace Migurdex.Core.Infrastructure.Http;

public class AutoRedirectHandler : DelegatingHandler
{
    private const int MaxRedirects = 5;

    public AutoRedirectHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken                                                           cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        response.RequestMessage ??= request;

        if (request.Options.TryGetValue(RustHttpOptions.NoFollowKey, out var noFollow) && noFollow)
        {
            return response;
        }

        if (request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("NoFollow"), out var legacyNf) && legacyNf)
        {
            return response;
        }

        var redirectCount = 0;
        var currentUri    = request.RequestUri;

        while (IsRedirect(response.StatusCode) && redirectCount < MaxRedirects)
        {
            var location = response.Headers.Location;

            if (location == null)
            {
                break;
            }

            var redirectUri = location.IsAbsoluteUri ? location : new Uri(currentUri!, location);
            currentUri = redirectUri;

            response.Dispose();

            var redirectRequest = new HttpRequestMessage(HttpMethod.Get, redirectUri);

            foreach (var header in request.Headers)
            {
                redirectRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var option in request.Options)
            {
                redirectRequest.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            }

            response = await base.SendAsync(redirectRequest, cancellationToken);

            response.RequestMessage = redirectRequest;

            redirectCount++;
        }

        return response;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.Redirect
               || statusCode == HttpStatusCode.MovedPermanently
               || statusCode == HttpStatusCode.TemporaryRedirect
               || statusCode == HttpStatusCode.PermanentRedirect;
    }
}
