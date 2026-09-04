using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class DoodStreamExtractor : IExtractor
{
    private readonly HttpClient                   _httpClient;
    private readonly ILogger<DoodStreamExtractor> _logger;
    private readonly IMp4MetadataReader           _metadataReader;

    public DoodStreamExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient(o => o.AllowAutoRedirect = true);
        _metadataReader = bridge.MetadataReader;
        _logger         = bridge.CreateLogger<DoodStreamExtractor>();
    }

    public string Name => "DoodStream";

    public bool CanExtract(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        return DoodHostRegex().IsMatch(url);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            if (url.Contains("/d/"))
            {
                url = url.Replace("/d/", "/e/");
            }

            if (url.Contains("/f/"))
            {
                url = url.Replace("/f/", "/e/");
            }

            var requestHeaders = new Dictionary<string, string>
            {
                {
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                }
            };

            var targetUrl = url;

            _logger.LogDebug("fetching DoodStream embed page: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            request.Headers.Add("User-Agent", requestHeaders["User-Agent"]);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed: {Url}", targetUrl);

                return sources;
            }

            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", targetUrl);

                return sources;
            }

            var passMd5Match = PassMd5Regex().Match(html);
            if (!passMd5Match.Success)
            {
                _logger.LogWarning("could not find pass_md5 path in HTML");

                return sources;
            }

            var passMd5Path = passMd5Match.Groups[1].Value;
            var token       = passMd5Path.Split('/').Last();

            var uri        = new Uri(targetUrl);
            var baseDomain = $"{uri.Scheme}://{uri.Host}";
            var passMd5Url = $"{baseDomain}/pass_md5/{passMd5Path}";

            _logger.LogDebug("fetching stream base from: {PassMd5Url}", passMd5Url);

            var passRequest = new HttpRequestMessage(HttpMethod.Get, passMd5Url);
            passRequest.Headers.Add("User-Agent", requestHeaders["User-Agent"]);
            passRequest.Headers.Add("Referer", url);

            var passResponse = await _httpClient.SendAsync(passRequest, cancellationToken);
            if (!passResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("pass_md5 request failed");

                return sources;
            }

            var streamBase = await passResponse.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(streamBase))
            {
                _logger.LogWarning("pass_md5 request empty response");

                return sources;
            }

            var randomStr     = GenerateRandomString(10);
            var expiry        = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var finalVideoUrl = $"{streamBase}{randomStr}?token={token}&expiry={expiry}";

            _logger.LogInformation("successfully extracted direct video URL");

            var quality =
                await _metadataReader.GetVideoQualityAsync(finalVideoUrl,
                                                           baseDomain + "/",
                                                           cancellationToken: cancellationToken);

            sources.Add(new VideoSource
            {
                Url     = finalVideoUrl,
                Quality = quality,
                Type    = VideoType.Mp4,
                Headers = new Dictionary<string, string>
                {
                    { "Referer", baseDomain }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"dood(stream\.com|\.to|\.so|\.la|\.ws|\.pm|\.wf|\.cx|\.sh|\.re|\.watch)|vide0|playmogo\.com",
                    RegexOptions.IgnoreCase)]
    private static partial Regex DoodHostRegex();

    [GeneratedRegex(@"/pass_md5/([^'""]+)")]
    private static partial Regex PassMd5Regex();

    private static string GenerateRandomString(int length)
    {
        const string chars  = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var          random = new Random();

        return new string(Enumerable.Repeat(chars, length)
                                    .Select(s => s[random.Next(s.Length)])
                                    .ToArray());
    }
}
