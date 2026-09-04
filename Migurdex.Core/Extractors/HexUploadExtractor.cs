using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class HexUploadExtractor : IExtractor
{
    private readonly HttpClient                  _httpClient;
    private readonly ILogger<HexUploadExtractor> _logger;
    private readonly IMp4MetadataReader          _metadataReader;

    public HexUploadExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _metadataReader = bridge.MetadataReader;
        _logger         = bridge.CreateLogger<HexUploadExtractor>();
    }

    public string Name => "HexUpload";

    public bool CanExtract(string url)
    {
        return url.Contains("hexload.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("hexupload.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("extracting URL: {Url}", url);

            var idMatch = ComIdRegex().Match(url);
            if (!idMatch.Success)
            {
                _logger.LogWarning("could not extract ID from URL: {Url}", url);

                return sources;
            }

            var id = idMatch.Groups[1].Value.Split('?')[0];

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "op", "download3" },
                { "id", id },
                { "ajax", "1" },
                { "method_free", "1" },
                { "dataType", "json" }
            });

            var response = await _httpClient.PostAsync("https://hexload.com/download", content, cancellationToken);
            var result   = await response.Content.ReadFromJsonAsync<HexloadResponse>(cancellationToken);

            var videoUrl = result?.Result?.Url;
            if (string.IsNullOrEmpty(videoUrl))
            {
                _logger.LogWarning("failed to get video URL for: {Url}", url);

                return sources;
            }

            var quality =
                await _metadataReader.GetVideoQualityAsync(videoUrl,
                                                           "https://hexload.com/",
                                                           cancellationToken: cancellationToken);

            sources.Add(new VideoSource
            {
                Url     = videoUrl,
                Quality = quality,
                Type    = VideoType.Mp4
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"\.com/(.+)")]
    private static partial Regex ComIdRegex();

    private class HexloadResponse
    {
        [JsonPropertyName("result")]
        public HexloadResult? Result { get; set; }
    }

    private class HexloadResult
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
