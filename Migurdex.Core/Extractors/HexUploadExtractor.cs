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

    public HexUploadExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _logger         = bridge.CreateLogger<HexUploadExtractor>();
    }

    public string Name => "HexUpload";

    public bool CanExtract(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsHostOrSubdomain(uri.Host, "hexload.com")
               || IsHostOrSubdomain(uri.Host, "hexupload.com")
               || IsHostOrSubdomain(uri.Host, "hexupload.net");
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            _logger.LogDebug("extracting URL: {Url}", url);

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !CanExtract(url))
            {
                _logger.LogWarning("could not parse HexUpload URL");

                return sources;
            }

            var idMatch = FileIdRegex().Match(uri.AbsolutePath);
            if (!idMatch.Success)
            {
                _logger.LogWarning("could not extract ID from URL: {Url}", url);

                return sources;
            }

            var id = idMatch.Groups[1].Value;

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
            sources.Add(new VideoSource
            {
                Url  = videoUrl,
                Type = VideoType.Mp4
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for: {Url}", url);
        }

        return sources;
    }

    private static bool IsHostOrSubdomain(string host, string domain)
    {
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^/(.+)$")]
    private static partial Regex FileIdRegex();

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
