using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class StreamainExtractor : IExtractor
{
    private readonly HttpClient                  _httpClient;
    private readonly ILogger<StreamainExtractor> _logger;
    private readonly IMp4MetadataReader          _metadataReader;

    public StreamainExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _logger         = bridge.CreateLogger<StreamainExtractor>();
        _metadataReader = bridge.MetadataReader;
    }

    public string Name => "Streamain";

    public bool CanExtract(string url)
    {
        return url.Contains("streamain.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("fetching embed page: {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.AddHeaders(headers);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed: {StatusCode} for {Url}",
                                   response.StatusCode,
                                   url);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var dataLinkMatch = PlaybobVideoRegex().Match(html);
            if (dataLinkMatch.Success)
            {
                var videoUrl = dataLinkMatch.Groups[1].Value;
                var quality =
                    await _metadataReader.GetVideoQualityAsync(videoUrl, cancellationToken: cancellationToken);
                sources.Add(new VideoSource
                {
                    Url     = videoUrl,
                    Quality = quality,
                    Type    = VideoType.Mp4
                });
            }
            else
            {
                dataLinkMatch = DataLinkRegex().Match(html);
                if (dataLinkMatch.Success)
                {
                    var videoUrl = dataLinkMatch.Groups[1].Value;
                    var quality =
                        await _metadataReader.GetVideoQualityAsync(videoUrl, cancellationToken: cancellationToken);
                    sources.Add(new VideoSource
                    {
                        Url     = videoUrl,
                        Quality = quality,
                        Type    = VideoType.Mp4
                    });
                }
                else
                {
                    _logger.LogWarning("could not find video link in HTML for: {Url}", url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"id=""playbob-video""[^>]+data-link=""([^""]+)""")]
    private static partial Regex PlaybobVideoRegex();

    [GeneratedRegex(@"data-link=""([^""]+\.mp4[^""]*)""")]
    private static partial Regex DataLinkRegex();
}
