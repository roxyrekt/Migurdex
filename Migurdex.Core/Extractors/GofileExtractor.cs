using Jint;
using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class GofileExtractor : IExtractor
{
    private const string TargetUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:152.0) Gecko/20100101 Firefox/152.0";

    private readonly SemaphoreSlim            _cacheLock = new(1, 1);
    private readonly HttpClient               _httpClient;
    private readonly ILogger<GofileExtractor> _logger;
    private readonly IMp4MetadataReader       _metadataReader;
    private          DateTime?                _cacheExpiry;
    private          double                   _cachedDivisor = 14400.0;

    private string? _cachedSalt;

    public GofileExtractor(ISharedBridge bridge)
    {
        _httpClient     = bridge.CreateHttpClient();
        _logger         = bridge.CreateLogger<GofileExtractor>();
        _metadataReader = bridge.MetadataReader;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", TargetUserAgent);
    }

    public string Name => "Gofile";

    public bool CanExtract(string url)
    {
        return url.Contains("gofile.io/d/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var contentId = ExtractContentId(url);
            if (string.IsNullOrEmpty(contentId))
            {
                _logger.LogWarning("could not extract content ID from Gofile URL: {Url}", url);

                return sources;
            }

            _logger.LogDebug("extracting Gofile content for ID: {ContentId}", contentId);

            var token = await GetGuestTokenAsync(cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("could not get guest token for Gofile");

                return sources;
            }

            _logger.LogDebug("retrieved guest token for Gofile: token->{Token}", token);

            var websiteToken = await GenerateWebsiteTokenAsync(token, cancellationToken);

            _logger.LogDebug("generated website token for Gofile: websiteToken->{WebsiteToken}", websiteToken);

            var apiUrl =
                $"https://api.gofile.io/contents/{contentId}?contentFilter=&page=1&pageSize=1000&sortField=name&sortDirection=1";

            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            request.Headers.Add("Host", "api.gofile.io");

            request.Headers.Add("X-Bl", "en-US");
            request.Headers.Add("X-Website-Token", websiteToken);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("gofile API returned status {StatusCode} for ID {ContentId}",
                                   response.StatusCode,
                                   contentId);

                return sources;
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            if (root.GetProperty("status").GetString() != "ok")
            {
                _logger.LogWarning("gofile API returned non-ok status: {Status}",
                                   root.GetProperty("status").GetString());

                return sources;
            }

            var data = root.GetProperty("data");
            if (data.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateObject())
                {
                    var item = child.Value;
                    var type = item.GetProperty("type").GetString();

                    if (type == "file")
                    {
                        var mimeType = item.GetProperty("mimetype").GetString() ?? "";
                        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                        {
                            var name = item.GetProperty("name").GetString();
                            var link = item.GetProperty("link").GetString();

                            var fileHeaders = new Dictionary<string, string>
                            {
                                { "Cookie", $"accountToken={token}" }
                            };

                            var quality =
                                await _metadataReader.GetVideoQualityAsync(
                                    link ?? string.Empty,
                                    fileHeaders,
                                    cancellationToken);

                            sources.Add(new VideoSource
                            {
                                Url     = link ?? string.Empty,
                                Quality = quality,
                                Type    = VideoType.Mp4,
                                Headers = fileHeaders
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Gofile video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"gofile\.io/d/([a-zA-Z0-9]+)")]
    private static partial Regex GofileContentIdRegex();

    private async Task<string?> GetGuestTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsync("https://api.gofile.io/accounts", null, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var       json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc  = JsonDocument.Parse(json);
                if (doc.RootElement.GetProperty("status").GetString() == "ok")
                {
                    return doc.RootElement.GetProperty("data").GetProperty("token").GetString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to get Gofile guest token");
        }

        return null;
    }

    private async Task<string> GenerateWebsiteTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        const string language = "en-US";

        var (salt, divisor) = await GetCachedOrLiveConfigAsync(cancellationToken);

        var timestampPart = Math.Floor(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / divisor)
                                .ToString(CultureInfo.InvariantCulture);

        var input = $"{TargetUserAgent}::{language}::{token}::{timestampPart}::{salt}";

        using var sha256 = SHA256.Create();
        var       bytes  = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));

        return BitConverter.ToString(bytes).Replace("-", "").ToLower();
    }

    private async Task<(string salt, double divisor)> GetCachedOrLiveConfigAsync(CancellationToken cancellationToken =
        default)
    {
        if (_cachedSalt != null && _cacheExpiry > DateTime.UtcNow)
        {
            return (_cachedSalt, _cachedDivisor);
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedSalt != null && _cacheExpiry > DateTime.UtcNow)
            {
                return (_cachedSalt, _cachedDivisor);
            }

            _logger.LogDebug("gofile salt cache expired or empty. Fetching live config with Jint...");
            var (salt, timeBucketStr) = await GetLiveConfigWithJintAsync(cancellationToken);

            if (!string.IsNullOrEmpty(salt))
            {
                _cachedSalt = salt;

                if (double.TryParse(timeBucketStr,
                                    NumberStyles.Any,
                                    CultureInfo.InvariantCulture,
                                    out var parsedBucket)
                    && parsedBucket > 0)
                {
                    double nowInSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _cachedDivisor = Math.Round(nowInSeconds / parsedBucket);

                    if (_cachedDivisor <= 0)
                    {
                        _cachedDivisor = 14400.0;
                    }
                }
                else
                {
                    _cachedDivisor = 14400.0;
                }

                _cacheExpiry = DateTime.UtcNow.AddHours(2);
                _logger.LogInformation("successfully updated dynamic Gofile config. Salt: {Salt}, Divisor: {Divisor}",
                                       _cachedSalt,
                                       _cachedDivisor);
            }
            else
            {
                _logger.LogWarning("failed to extract dynamic Gofile config. Falling back to default values");
                SetFallbackConfig(DateTime.UtcNow.AddMinutes(10));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "an error occurred during Gofile dynamic config extraction. Using fallback values");
            SetFallbackConfig(DateTime.UtcNow.AddMinutes(5));
        }
        finally
        {
            _cacheLock.Release();
        }

        return (_cachedSalt!, _cachedDivisor);
    }

    private void SetFallbackConfig(DateTime expiry)
    {
        _cachedSalt    = "9844d94d963d30";
        _cachedDivisor = 14400.0;
        _cacheExpiry   = expiry;
    }

    private static string ExtractContentId(string url)
    {
        var match = GofileContentIdRegex().Match(url);

        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private async Task<(string? salt, string? timeBucket)> GetLiveConfigWithJintAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var obfCode = await _httpClient.GetStringAsync("https://gofile.io/dist/js/wt.obf.js", cancellationToken);

            var engine = new Engine();

            engine.SetValue("navigator",
                            new
                            {
                                userAgent = TargetUserAgent,
                                language  = "en-US"
                            });

            engine.SetValue("console",
                            new
                            {
                                log = new Action<object>(msg => { })
                            });

            await engine.ExecuteAsync(obfCode);

            string? extractedSalt       = null;
            string? extractedTimeBucket = null;

            engine.SetValue("hook",
                            new Action<string>(rawString =>
                            {
                                if (string.IsNullOrEmpty(rawString))
                                {
                                    return;
                                }

                                var parts = rawString.Split("::");
                                if (parts.Length < 5)
                                {
                                    return;
                                }

                                extractedSalt       = parts[4];
                                extractedTimeBucket = parts[3];
                            }));

            await engine.ExecuteAsync("_sha256 = function(rawString) { hook(rawString); return 'dummy'; };");
            await engine.ExecuteAsync("generateWT('PROBE_TOKEN');");

            return (extractedSalt, extractedTimeBucket);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to run Jint engine on Gofile obfuscated script");
        }

        return (null, null);
    }
}
