using Microsoft.Extensions.Logging;
using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class VkExtractor : IExtractor
{
    private readonly HttpClient            _httpClient;
    private readonly ILogger<VkExtractor>  _logger;
    private readonly M3U8PlaylistExtractor _m3U8Extractor;

    private const string FirefoxUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:151.0) Gecko/20100101 Firefox/151.0";

    private const string VkClientId     = "52461373";
    private const string VkClientSecret = "o557NLIkAErNhakXrQ7A";
    private const string VkAppId        = "6287487";
    private const string VkApiVersion   = "5.289";

    public VkExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient(o =>
        {
            o.AllowAutoRedirect = true;
            o.UseCookies        = true;
        });

        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<VkExtractor>();

        _httpClient.DefaultRequestHeaders.Add("User-Agent", FirefoxUa);
    }

    public string Name => "VK";

    public bool CanExtract(string url)
    {
        return url.Contains("vk.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("vkvideo.ru", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            _logger.LogInformation("starting extraction for URL: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", FirefoxUa);

            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.Add("Referer", referer);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("failed to fetch page. Status: {StatusCode}", response.StatusCode);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var errorText = VideoExtMsgRegex().Match(html) is { Success: true } errorMatch
                ? System.Net.WebUtility.HtmlDecode(errorMatch.Groups[1].Value).Trim()
                : string.Empty;
            if (!string.IsNullOrEmpty(errorText))
            {
                throw new ExtractionException(UpstreamErrorClassifier.Detect(200, errorText), errorText);
            }

            var filesMatch = FilesJsonRegex().Match(html);
            if (filesMatch.Success)
            {
                var       filesJson = filesMatch.Groups[1].Value;
                using var doc       = JsonDocument.Parse(filesJson);

                await AddFilesSourcesAsync(doc.RootElement, url, sources, cancellationToken);
            }
            else
            {
                _logger.LogWarning("no files JSON in page, trying video API for {Url}", url);
                await AddApiSourcesAsync(url, sources, cancellationToken);
            }

            _logger.LogInformation("extracted {Count} sources", sources.Count);
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract from {Url}", url);
        }

        return sources;
    }

    private async Task AddFilesSourcesAsync(JsonElement filesRoot,
        string                                                  refererUrl,
        List<VideoSource>                                       sources,
        CancellationToken                                       cancellationToken)
    {
        foreach (var prop in filesRoot.EnumerateObject())
        {
            var key   = prop.Name;
            var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;

            if (string.IsNullOrEmpty(value) || !value.StartsWith("http"))
            {
                continue;
            }

            if (key.Contains("hls"))
            {
                var extracted = await _m3U8Extractor.ExtractAsync(value,
                                                                  new Dictionary<string, string>
                                                                  {
                                                                      { "Referer", refererUrl }
                                                                  },
                                                                  cancellationToken);
                foreach (var src in extracted)
                {
                    src.Headers               ??= new Dictionary<string, string>();
                    src.Headers["User-Agent"] =   FirefoxUa;
                    sources.Add(src);
                }
            }
            else if (key.StartsWith("mp4_"))
            {
                sources.Add(new VideoSource
                {
                    Url     = value,
                    Quality = key.Replace("mp4_", "") + "p",
                    Type    = VideoType.Mp4,
                    Headers = new Dictionary<string, string>
                    {
                        { "User-Agent", FirefoxUa }
                    }
                });
            }
        }
    }

    private async Task AddApiSourcesAsync(string url, List<VideoSource> sources, CancellationToken cancellationToken)
    {
        var oidMatch = OidRegex().Match(url);
        var idMatch  = VideoIdRegex().Match(url);
        if (!oidMatch.Success || !idMatch.Success)
        {
            return;
        }

        var token = await MintAnonymTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("VK anonym token mint failed for {Url}", url);

            return;
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["videos"]       = $"{oidMatch.Groups[1].Value}_{idMatch.Groups[1].Value}",
            ["extended"]     = "0",
            ["is_embed"]     = "true",
            ["access_token"] = token
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
                                                   $"https://api.vkvideo.ru/method/video.get?v={VkApiVersion}&client_id={VkClientId}")
        {
            Content = form
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("VK video.get failed: {StatusCode} for {Url}", response.StatusCode, url);

            return;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("error", out var err)
            && err.TryGetProperty("error_msg", out var errMsg))
        {
            throw new ExtractionException(UpstreamErrorClassifier.Detect(200, errMsg.GetString()),
                                          errMsg.GetString() ?? "VK API error");
        }

        if (!doc.RootElement.TryGetProperty("response", out var resp)
            || !resp.TryGetProperty("items", out var items))
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("content_restricted_message", out var restrictedMsg)
                && restrictedMsg.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(restrictedMsg.GetString()))
            {
                throw new ExtractionException(UpstreamErrorClassifier.Detect(200, restrictedMsg.GetString()),
                                              restrictedMsg.GetString()!);
            }

            if (item.TryGetProperty("files", out var files)
                && files.ValueKind == JsonValueKind.Object)
            {
                await AddFilesSourcesAsync(files, url, sources, cancellationToken);
            }
        }
    }

    private async Task<string?> MintAnonymTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_secret"]           = VkClientSecret,
                ["client_id"]               = VkClientId,
                ["scopes"]                  = "audio_anonymous,video_anonymous,photos_anonymous,profile_anonymous",
                ["isApiOauthAnonymEnabled"] = "false",
                ["version"]                 = "1",
                ["app_id"]                  = VkAppId
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://login.vk.ru/?act=get_anonym_token")
            {
                Content = form
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("access_token", out var token))
            {
                return token.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VK anonym token mint failed");
        }

        return null;
    }

    [GeneratedRegex(@"<div[^>]*id=""video_ext_msg""[^>]*>(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex VideoExtMsgRegex();

    [GeneratedRegex(@"[?&]oid=(-?\d+)")]
    private static partial Regex OidRegex();

    [GeneratedRegex(@"[?&]id=(\d+)")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"[""']files[""']\s*:\s*(\{.*?\})")]
    private static partial Regex FilesJsonRegex();
}
