using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;

namespace Migurdex.Plugins.TrAnimeIzle;

public class TrAnimeCaptchaHandler : DelegatingHandler
{
    private readonly string  _baseUrl;
    private readonly ILogger _logger;

    public TrAnimeCaptchaHandler(string baseUrl, ILogger logger, HttpMessageHandler? innerHandler = null)
        : base(innerHandler
               ?? new HttpClientHandler
               {
                   CookieContainer = new CookieContainer(),
                   UseCookies      = true
               })
    {
        _baseUrl = baseUrl;
        _logger  = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken                                                           cancellationToken)
    {
        var originalResponse = await base.SendAsync(request, cancellationToken);
        var currentUrl       = originalResponse.RequestMessage?.RequestUri?.ToString() ?? "";

        if (!currentUrl.Contains("/api/CaptchaChallenge"))
        {
            return originalResponse;
        }

        _logger.LogInformation("captcha challenge detected, solving");

        originalResponse.Dispose();

        var captchaFormData = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("cID", "0"),
            new KeyValuePair<string, string>("rT", "1"),
            new KeyValuePair<string, string>("tM", "light")
        ]);

        var captchaRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/Captcha/")
        {
            Content = captchaFormData
        };

        captchaRequest.Headers.Referrer = new Uri(currentUrl);
        captchaRequest.Headers.Add("User-Agent",
                                   "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        captchaRequest.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        var imagesResponse = await base.SendAsync(captchaRequest, cancellationToken);
        if (!imagesResponse.IsSuccessStatusCode)
        {
            _logger.LogError("failed to initiate captcha challenge");
            imagesResponse.Dispose();

            return await base.SendAsync(request, cancellationToken);
        }

        var imagesJson = await imagesResponse.Content.ReadAsStringAsync(cancellationToken);
        imagesResponse.Dispose();

        imagesJson = imagesJson.Trim();
        if (imagesJson.StartsWith("[") && imagesJson.EndsWith("]"))
        {
            imagesJson = imagesJson.Substring(1, imagesJson.Length - 2);
        }

        var imageIds = imagesJson.Split(',')
                                 .Select(id => id.Trim().Trim('"'))
                                 .Where(id => !string.IsNullOrEmpty(id))
                                 .ToList();

        if (imageIds.Count == 0)
        {
            _logger.LogError("no captcha image hashes returned");

            return await base.SendAsync(request, cancellationToken);
        }

        _logger.LogDebug("computing MD5 hashes for {Count} captcha images", imageIds.Count);

        var hashes = new List<(string Id, string Hash)>();
        foreach (var id in imageIds)
        {
            try
            {
                var imageRequest = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/Captcha/?cid=0&hash={id}");
                imageRequest.Headers.Referrer = new Uri(currentUrl);
                imageRequest.Headers.Add("User-Agent",
                                         "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                var imageResponse = await base.SendAsync(imageRequest, cancellationToken);
                if (imageResponse.IsSuccessStatusCode)
                {
                    var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                    var hash       = ComputeMd5(imageBytes);
                    hashes.Add((id, hash));
                }

                imageResponse.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to fetch or hash captcha image: {Id}", id);
            }
        }

        var grouped = hashes.GroupBy(x => x.Hash)
                            .Select(g => new
                            {
                                Hash    = g.Key,
                                Count   = g.Count(),
                                FirstId = g.First().Id
                            })
                            .OrderBy(g => g.Count)
                            .ToList();

        if (grouped.Count == 0)
        {
            _logger.LogError("failed to compute image hashes to solve captcha");

            return await base.SendAsync(request, cancellationToken);
        }

        var correctId = grouped.First().FirstId;
        _logger.LogInformation("identified captcha solution: {CorrectId}", correctId);

        var finalFormData = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("cID", "0"),
            new KeyValuePair<string, string>("rT", "2"),
            new KeyValuePair<string, string>("pC", correctId)
        ]);

        var finalRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/Captcha/")
        {
            Content = finalFormData
        };
        finalRequest.Headers.Referrer = new Uri(currentUrl);
        finalRequest.Headers.Add("User-Agent",
                                 "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        finalRequest.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        var finalResponse = await base.SendAsync(finalRequest, cancellationToken);
        if (!finalResponse.IsSuccessStatusCode)
        {
            _logger.LogError("failed to submit captcha solution");
            finalResponse.Dispose();

            return await base.SendAsync(request, cancellationToken);
        }

        finalResponse.Dispose();

        _logger.LogInformation("captcha solved successfully");

        var          decodedUrl    = Uri.UnescapeDataString(currentUrl);
        var          uri           = new Uri(decodedUrl);
        var          path          = uri.AbsolutePath;
        const string captchaPrefix = "/api/CaptchaChallenge/";

        string finalUrl;
        if (path.StartsWith(captchaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var remainingPath = path[captchaPrefix.Length..];
            finalUrl = new Uri(new Uri(_baseUrl), remainingPath).ToString();
        }
        else
        {
            finalUrl = decodedUrl;
        }

        var finalGetRequest = new HttpRequestMessage(request.Method, finalUrl);
        if (request.Content != null)
        {
            finalGetRequest.Content = request.Content;
        }

        foreach (var header in request.Headers)
        {
            finalGetRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return await base.SendAsync(finalGetRequest, cancellationToken);
    }

    private static string ComputeMd5(byte[] input)
    {
        using var md5       = MD5.Create();
        var       hashBytes = md5.ComputeHash(input);

        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}
