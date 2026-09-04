using Microsoft.Extensions.Logging;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class SistennExtractor : IExtractor
{
    private const string StaticKey = "kiemtienmua911ca";

    private readonly HttpClient                _httpClient;
    private readonly ILogger<SistennExtractor> _logger;
    private readonly M3U8PlaylistExtractor     _m3U8Extractor;

    public SistennExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<SistennExtractor>();
    }

    public string Name => "Sistenn";

    public bool CanExtract(string url)
    {
        return url.Contains("uns.bio", StringComparison.OrdinalIgnoreCase)
               || url.Contains("upns.one", StringComparison.OrdinalIgnoreCase)
               || url.Contains("sistenn", StringComparison.OrdinalIgnoreCase)
               || url.Contains("rpmvid", StringComparison.OrdinalIgnoreCase)
               || url.Contains("strp2p", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            var idMatch = IdPathRegex().Match(url);
            if (!idMatch.Success)
            {
                idMatch = IdParamRegex().Match(url);
            }

            if (!idMatch.Success)
            {
                _logger.LogWarning("could not extract ID from URL: {Url}", url);

                return sources;
            }

            var id  = idMatch.Groups[1].Value;
            var uri = new Uri(url);

            var apiUrl = $"{uri.Scheme}://{uri.Host}/api/v1/video?id={id}";

            _logger.LogDebug("fetching video info: {ApiUrl}", apiUrl);

            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("Referer", url);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("API request failed: {ApiUrl}", apiUrl);

                return sources;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(responseBody))
            {
                _logger.LogWarning("empty response from API: {ApiUrl}", apiUrl);

                return sources;
            }

            string payload;
            var    trimmedResponse = responseBody.Trim();

            if (trimmedResponse.StartsWith("{") && trimmedResponse.EndsWith("}"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmedResponse);
                    if (doc.RootElement.TryGetProperty("data", out var dataProp))
                    {
                        payload = dataProp.GetString() ?? string.Empty;
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.String)
                    {
                        payload = doc.RootElement.GetString() ?? string.Empty;
                    }
                    else
                    {
                        payload = trimmedResponse;
                    }
                }
                catch
                {
                    payload = trimmedResponse;
                }
            }
            else
            {
                payload = trimmedResponse.Trim('"');
            }

            if (string.IsNullOrEmpty(payload))
            {
                _logger.LogWarning("could not find payload in response");

                return sources;
            }

            var candidateHosts = new List<string>
            {
                uri.Host
            };
            if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
            {
                candidateHosts.Add(refUri.Host);
            }

            var hostParts = uri.Host.Split('.');
            if (hostParts.Length > 2)
            {
                candidateHosts.Add(string.Join('.', hostParts.Skip(1)));
            }

            var candidateProtocols = new[] { uri.Scheme + ":", uri.Scheme };

            string? decryptedJson = null;

            foreach (var candHost in candidateHosts.Distinct())
            {
                foreach (var candProto in candidateProtocols)
                {
                    var rawDecrypted = DecryptPayload(payload, candHost, candProto);
                    if (string.IsNullOrWhiteSpace(rawDecrypted))
                    {
                        continue;
                    }

                    var cleaned = CleanDecryptedJson(rawDecrypted);
                    try
                    {
                        using var testDoc = JsonDocument.Parse(cleaned,
                                                               new JsonDocumentOptions
                                                               {
                                                                   AllowTrailingCommas = true
                                                               });
                        if (testDoc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            decryptedJson = cleaned;
                            break;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }

                if (decryptedJson != null)
                {
                    break;
                }
            }

            if (string.IsNullOrEmpty(decryptedJson))
            {
                _logger.LogWarning("decryption and JSON cleanup failed for payload");

                return sources;
            }

            _logger.LogDebug("decrypted JSON: {DecryptedJson}", decryptedJson);

            using var decryptedDoc = JsonDocument.Parse(decryptedJson,
                                                        new JsonDocumentOptions
                                                        {
                                                            AllowTrailingCommas = true
                                                        });
            var root = decryptedDoc.RootElement;

            var urlsToExtract = new List<string>();

            if (root.TryGetProperty("source", out var sourceProp))
            {
                var sourceUrl = sourceProp.GetString();
                if (!string.IsNullOrEmpty(sourceUrl))
                {
                    urlsToExtract.Add(sourceUrl);
                }
            }

            if (root.TryGetProperty("cf", out var cfProp))
            {
                var cfUrl = cfProp.GetString();

                if (!string.IsNullOrEmpty(cfUrl))
                {
                    var enrichedCfUrl = BuildCloudflareUrl(cfUrl, root);

                    urlsToExtract.Add(enrichedCfUrl);
                }
            }

            foreach (var m3U8Url in urlsToExtract)
            {
                try
                {
                    _logger.LogDebug("found stream URL: {M3u8Url}", m3U8Url);
                    var extracted = await _m3U8Extractor.ExtractAsync(m3U8Url,
                                                                      new Dictionary<string, string>
                                                                      {
                                                                          { "Referer", $"{uri.Scheme}://{uri.Host}/" }
                                                                      });
                    if (extracted.Any())
                    {
                        extracted.ForEach(x => x.Headers = new Dictionary<string, string>
                        {
                            { "Referer", $"{uri.Scheme}://{uri.Host}/" }
                        });
                        sources.AddRange(extracted);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "error extracting from source: {M3u8Url}", m3U8Url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error extracting from {Url}", url);
        }

        return sources;
    }

    private static string BuildCloudflareUrl(string cfUrl, JsonElement root)
    {
        var queryParams = new Dictionary<string, string>();

        if (root.TryGetProperty("streamingConfig", out var configProp))
        {
            try
            {
                JsonDocument? configDoc = null;
                JsonElement   configElement;

                if (configProp.ValueKind == JsonValueKind.String)
                {
                    var configJson = configProp.GetString();
                    if (!string.IsNullOrEmpty(configJson))
                    {
                        configDoc     = JsonDocument.Parse(configJson);
                        configElement = configDoc.RootElement;
                    }
                    else
                    {
                        configElement = default;
                    }
                }
                else
                {
                    configElement = configProp;
                }

                if (configElement.ValueKind == JsonValueKind.Object
                    && configElement.TryGetProperty("adjust", out var adjustProp)
                    && adjustProp.TryGetProperty("Cloudflare", out var cfAdjust)
                    && cfAdjust.TryGetProperty("params", out var paramsProp)
                    && paramsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in paramsProp.EnumerateObject())
                    {
                        var val = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            queryParams[prop.Name] = val;
                        }
                    }
                }

                configDoc?.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        if (root.TryGetProperty("pk", out var pkProp) && pkProp.ValueKind == JsonValueKind.Object)
        {
            if (pkProp.TryGetProperty("k", out var kProp) && kProp.ValueKind == JsonValueKind.String)
            {
                var k = kProp.GetString();
                if (!string.IsNullOrEmpty(k))
                {
                    queryParams["k"] = k;
                }
            }

            if (pkProp.TryGetProperty("kx", out var kxProp))
            {
                var kx = kxProp.ValueKind == JsonValueKind.Number
                             ? kxProp.GetInt64().ToString()
                             : kxProp.GetString();
                if (!string.IsNullOrEmpty(kx))
                {
                    queryParams["kx"] = kx;
                }
            }
        }

        if (queryParams.Count == 0)
        {
            return cfUrl;
        }

        var queryString = string.Join("&",
                                      queryParams.Select(kv =>
                                                             $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return cfUrl.Contains('?') ? $"{cfUrl}&{queryString}" : $"{cfUrl}?{queryString}";
    }

    [GeneratedRegex(@"[/#]([a-zA-Z0-9]+)$")]
    private static partial Regex IdPathRegex();

    [GeneratedRegex(@"id=([a-zA-Z0-9]+)")]
    private static partial Regex IdParamRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]+$")]
    private static partial Regex HexCharRegex();

    [GeneratedRegex(@"([{,])\s*%+([a-zA-Z])")]
    private static partial Regex CleanJsonKeysPercentRegex();

    [GeneratedRegex(@"([{,])\s*[^""a-zA-Z0-9_]*([a-zA-Z_][a-zA-Z0-9_]*)\s*:")]
    private static partial Regex CleanJsonKeysRegex();

    private string DecryptPayload(string payload, string hostname, string protocol)
    {
        try
        {
            string hexData;
            payload = payload.Trim().Trim('"');

            if (IsHexString(payload))
            {
                hexData = payload;
            }
            else
            {
                try
                {
                    var decodedBytes = Convert.FromBase64String(payload);
                    hexData = Encoding.UTF8.GetString(decodedBytes);
                }
                catch
                {
                    return string.Empty;
                }
            }

            hexData = WhitespaceRegex().Replace(hexData, "");

            if (!IsHexString(hexData))
            {
                try
                {
                    var decodedBytes = Convert.FromBase64String(payload);

                    return DecryptInternal(decodedBytes, hostname, protocol);
                }
                catch
                {
                    return string.Empty;
                }
            }

            var ciphertext = Enumerable.Range(0, hexData.Length / 2)
                                       .Select(x => Convert.ToByte(hexData.Substring(x * 2, 2), 16))
                                       .ToArray();

            return DecryptInternal(ciphertext, hostname, protocol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "decryption error");

            return string.Empty;
        }
    }

    private string DecryptInternal(byte[] ciphertext, string hostname, string protocol)
    {
        try
        {
            if (ciphertext.Length % 16 != 0)
            {
                _logger.LogWarning("ciphertext length is not a multiple of 16: {Length}", ciphertext.Length);

                return string.Empty;
            }

            var keyBytes = Encoding.UTF8.GetBytes(StaticKey);
            var ivBytes  = GenerateIv(hostname, protocol);

            using var aes = Aes.Create();
            aes.Key     = keyBytes;
            aes.IV      = ivBytes;
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            using var decryptor      = aes.CreateDecryptor();
            var       decryptedBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

            return Encoding.UTF8.GetString(decryptedBytes).TrimEnd('\0', '\r', '\n', ' ');
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "decryptInternal error");

            return string.Empty;
        }
    }

    private static bool IsHexString(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        var cleaned = WhitespaceRegex().Replace(s, "");

        return cleaned.Length > 0 && cleaned.Length % 2 == 0 && HexCharRegex().IsMatch(cleaned);
    }

    private static byte[] GenerateIv(string hostname, string protocol)
    {
        var       p = protocol + "//";
        var       g = protocol.Length * p.Length;
        const int n = 1;

        var iv  = new byte[16];
        var idx = 0;

        for (var i = n; i < 10; i++)
        {
            iv[idx++] = (byte) (i + g);
        }

        const string oe = "111";

        var ye = hostname.Length > n ? hostname[n] : 0;

        var he = (int.Parse(oe) * n) + protocol.Length;
        var k  = he + 4;
        int se = protocol[1];
        var pe = (se * n) - 2;

        iv[idx++] = (byte) g;
        iv[idx++] = (byte) int.Parse(oe);
        iv[idx++] = (byte) ye;
        iv[idx++] = (byte) he;
        iv[idx++] = (byte) k;
        iv[idx++] = (byte) se;
        // ReSharper disable once RedundantAssignment
        iv[idx++] = (byte) pe;

        return iv;
    }

    private static string CleanDecryptedJson(string decryptedJson)
    {
        if (string.IsNullOrWhiteSpace(decryptedJson))
        {
            return string.Empty;
        }

        var firstBrace = decryptedJson.IndexOf('{');
        if (firstBrace != -1)
        {
            decryptedJson = decryptedJson[firstBrace..];
        }

        var lastBraceIndex = decryptedJson.LastIndexOf('}');
        if (lastBraceIndex != -1)
        {
            decryptedJson = decryptedJson[..(lastBraceIndex + 1)];
        }

        decryptedJson = CleanJsonKeysPercentRegex().Replace(decryptedJson, "$1\"$2");
        decryptedJson = CleanJsonKeysRegex().Replace(decryptedJson, "$1\"$2\":");

        return decryptedJson;
    }
}
