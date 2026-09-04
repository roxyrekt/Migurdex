using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class StreamtapeExtractor : IExtractor
{
    private readonly HttpClient                   _httpClient;
    private readonly ILogger<StreamtapeExtractor> _logger;

    public StreamtapeExtractor(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = bridge.CreateLogger<StreamtapeExtractor>();
    }

    public string Name => "Streamtape";

    public bool CanExtract(string url)
    {
        return url.Contains("streamtape.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("streamtape.to", StringComparison.OrdinalIgnoreCase)
               || url.Contains("streamta.pe", StringComparison.OrdinalIgnoreCase)
               || url.Contains("stape.com", StringComparison.OrdinalIgnoreCase);
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
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:151.0) Gecko/20100101 Firefox/151.0");

            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.Referrer = new Uri(referer);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("failed to fetch page. Status: {StatusCode}", response.StatusCode);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var match = ReconstructionRegex().Match(html);

            if (!match.Success)
            {
                _logger.LogWarning("could not find link reconstruction script");

                return sources;
            }

            var prefix    = match.Groups["prefix"].Value;
            var encrypted = match.Groups["encrypted"].Value;
            var sub1      = int.Parse(match.Groups["sub1"].Value);
            var sub2      = match.Groups["sub2"].Success ? int.Parse(match.Groups["sub2"].Value) : 0;

            var finalUrl = BuildUrl(prefix, encrypted, sub1, sub2, url);

            if (!string.IsNullOrEmpty(finalUrl))
            {
                _logger.LogInformation("successfully reconstructed URL: {StreamUrl}", finalUrl);

                sources.Add(new VideoSource
                {
                    Url  = finalUrl,
                    Type = VideoType.Mp4
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract from {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(
        @"document\.getElementById\(['""](?:botlink|robotlink)['""]\)\.innerHTML\s*=\s*['""](?<prefix>[^'""\s]+?)['""]\s*\+\s*(?:['""][^'""]*['""]\s*\+\s*)*\(['""](?<encrypted>[^'""\s]+?)['""]\)\.substring\((?<sub1>\d+)\)(?:\.substring\((?<sub2>\d+)\))?;",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReconstructionRegex();

    private static string BuildUrl(string prefix, string encrypted, int sub1, int sub2, string originalUrl)
    {
        if (sub1 < 0 || sub1 > encrypted.Length)
        {
            return string.Empty;
        }

        var finalPart = encrypted[sub1..];
        if (sub2 > 0 && sub2 <= finalPart.Length)
        {
            finalPart = finalPart[sub2..];
        }

        var streamUrl = prefix + finalPart;

        if (streamUrl.StartsWith("//"))
        {
            streamUrl = "https:" + streamUrl;
        }
        else if (!streamUrl.StartsWith("http"))
        {
            var uri           = new Uri(originalUrl);
            var pathSeparator = streamUrl.StartsWith("/") ? "" : "/";
            streamUrl = $"{uri.Scheme}://{uri.Host}{pathSeparator}{streamUrl}";
        }

        if (!streamUrl.Contains("stream=1"))
        {
            streamUrl += "&stream=1";
        }

        return streamUrl;
    }
}
