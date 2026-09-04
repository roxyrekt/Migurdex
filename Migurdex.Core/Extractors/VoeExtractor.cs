using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class VoeExtractor : IExtractor
{
    private readonly HttpClient            _httpClient;
    private readonly ILogger<VoeExtractor> _logger;
    private readonly M3U8PlaylistExtractor _m3U8Extractor;
    private readonly IMp4MetadataReader    _metadataReader;

    public VoeExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _m3U8Extractor  = m3U8Extractor;
        _metadataReader = bridge.MetadataReader;
        _logger         = bridge.CreateLogger<VoeExtractor>();
    }

    public string Name => "Voe";

    public bool CanExtract(string url)
    {
        return url.Contains("voe.sx", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("fetching Voe embed page: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed for: {Url}", url);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", url);

                return sources;
            }

            var jsRedirectMatch = JsRedirectRegex().Match(html);
            if (jsRedirectMatch.Success)
            {
                var redirectUrl = jsRedirectMatch.Groups[1].Value;
                _logger.LogInformation("following JavaScript client-side redirect: {RedirectUrl}", redirectUrl);

                var redirectRequest = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
                redirectRequest.Headers.Add("Referer", url);
                redirectRequest.Headers.Add("User-Agent",
                                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                var redirectResponse = await _httpClient.SendAsync(redirectRequest, cancellationToken);
                if (!redirectResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("redirected page failed for: {RedirectUrl}",
                                       redirectUrl);

                    return sources;
                }

                html = await redirectResponse.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrEmpty(html))
                {
                    _logger.LogWarning("redirected page empty response for: {RedirectUrl}", redirectUrl);

                    return sources;
                }
            }

            var scriptMatch = ScriptJsonRegex().Match(html);
            if (!scriptMatch.Success)
            {
                _logger.LogWarning("could not find application/json script block in Voe HTML");

                return sources;
            }

            var       jsonArrayStr = scriptMatch.Groups[1].Value.Trim();
            using var doc          = JsonDocument.Parse(jsonArrayStr);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                _logger.LogWarning("script block did not contain a valid JSON array");

                return sources;
            }

            var encodedData = doc.RootElement[0].GetString();
            if (string.IsNullOrEmpty(encodedData))
            {
                _logger.LogWarning("first element in array is null or empty");

                return sources;
            }

            _logger.LogDebug("executing decryption pipeline...");

            var step1 = Rot13(encodedData);
            var step2 = ReplacePatterns(step1);
            var step3 = step2.Replace("_", "");
            var step4 = Base64Decode(step3);
            var step5 = CharShift(step4, 3);
            var step6 = Reverse(step5);

            var decryptedJson = Base64Decode(step6);

            _logger.LogDebug("decryption finished");

            using var decryptedDoc = JsonDocument.Parse(decryptedJson);
            var       root         = decryptedDoc.RootElement;

            if (root.TryGetProperty("source", out var m3U8Prop))
            {
                var m3U8Url = m3U8Prop.GetString();
                if (!string.IsNullOrEmpty(m3U8Url))
                {
                    _logger.LogDebug("resolving HLS qualities from: {M3u8Url}", m3U8Url);
                    var extractedSources = await _m3U8Extractor.ExtractAsync(m3U8Url,
                                                                             new Dictionary<string, string>
                                                                             {
                                                                                 { "Referer", "https://voe.sx" }
                                                                             });
                    foreach (var src in extractedSources)
                    {
                        src.Headers = new Dictionary<string, string>
                        {
                            { "Referer", "https://voe.sx" }
                        };

                        sources.Add(src);
                    }
                }
            }

            if (root.TryGetProperty("direct_access_url", out var directProp))
            {
                var mp4Url = directProp.GetString();
                if (!string.IsNullOrEmpty(mp4Url))
                {
                    var quality =
                        await _metadataReader.GetVideoQualityAsync(mp4Url,
                                                                   "https://voe.sx",
                                                                   cancellationToken: cancellationToken);

                    sources.Add(new VideoSource
                    {
                        Url     = mp4Url,
                        Quality = quality,
                        Type    = VideoType.Mp4
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Voe video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"window\.location\.href\s*=\s*['""](https?://[^'""]+)['""]")]
    private static partial Regex JsRedirectRegex();

    [GeneratedRegex(@"<script[^>]+type=['""]application/json['""][^>]*>\s*(\[[^<]+\])\s*</script>",
                    RegexOptions.IgnoreCase)]
    private static partial Regex ScriptJsonRegex();

    [GeneratedRegex("[a-zA-Z]")]
    private static partial Regex Rot13Regex();

    [GeneratedRegex(@"@\$|\^\^|~@|%\?|\*~|!!|#&")]
    private static partial Regex ReplacePatternsRegex();

    private static string Rot13(string input)
    {
        return Rot13Regex()
            .Replace(input,
                     m =>
                     {
                         var c        = m.Value[0];
                         var baseChar = c <= 'Z' ? 'A' : 'a';

                         return ((char) (((c - baseChar + 13) % 26) + baseChar)).ToString();
                     });
    }

    private static string ReplacePatterns(string input)
    {
        return ReplacePatternsRegex().Replace(input, "_");
    }

    private static string Base64Decode(string base64)
    {
        var cleanBase64 = base64.Replace('-', '+').Replace('_', '/');
        switch (cleanBase64.Length % 4)
        {
            case 2:
                cleanBase64 += "==";

                break;
            case 3:
                cleanBase64 += "=";

                break;
        }

        var bytes = Convert.FromBase64String(cleanBase64);

        return Encoding.UTF8.GetString(bytes);
    }

    private static string CharShift(string input, int shift)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            sb.Append((char) (c - shift));
        }

        return sb.ToString();
    }

    private static string Reverse(string input)
    {
        var charArray = input.ToCharArray();
        Array.Reverse(charArray);

        return new string(charArray);
    }
}
