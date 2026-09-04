using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

public partial class AbyssExtractor : IExtractor
{
    private readonly HttpClient              _httpClient;
    private readonly ILogger<AbyssExtractor> _logger;

    public AbyssExtractor(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = bridge.CreateLogger<AbyssExtractor>();
    }

    public string Name => "Abyss";

    public bool CanExtract(string url)
    {
        return url.Contains("abysscdn.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("abyss.to", StringComparison.OrdinalIgnoreCase)
               || url.Contains("abyssplayer.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("short.icu", StringComparison.OrdinalIgnoreCase)
               || url.Contains("iamcdn.net", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer       = headers.GetReferer();
            var normalizedUrl = url;

            var vMatch = VParamRegex().Match(url);
            if (vMatch.Success)
            {
                normalizedUrl = $"https://abysscdn.com/?v={vMatch.Groups[1].Value}";
            }
            else
            {
                var pathMatch = PathEmbedRegex().Match(url);
                if (pathMatch.Success)
                {
                    normalizedUrl = $"https://abysscdn.com/?v={pathMatch.Groups[1].Value}";
                }
                else
                {
                    try
                    {
                        var uri         = new Uri(url);
                        var lastSegment = uri.AbsolutePath.Trim('/');
                        if (!string.IsNullOrEmpty(lastSegment)
                            && !lastSegment.Contains('/')
                            && SegmentRegex().IsMatch(lastSegment))
                        {
                            normalizedUrl = $"https://abysscdn.com/?v={lastSegment}";
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            _logger.LogDebug("fetching Abyss embed page: {Url} (Normalized: {NormalizedUrl}) {Referer}",
                             url,
                             normalizedUrl,
                             referer);

            var request = new HttpRequestMessage(HttpMethod.Get, normalizedUrl);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.Add("Referer", referer);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("embed page failed for: {Url}", normalizedUrl);

                return sources;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("embed page empty response for: {Url}", normalizedUrl);

                return sources;
            }

            var datasMatch = DatasVarRegex().Match(html);
            if (!datasMatch.Success)
            {
                _logger.LogWarning("could not find 'datas' variable in Abyss HTML");

                return sources;
            }

            var datasB64    = datasMatch.Groups[1].Value;
            var jsonBytes   = Convert.FromBase64String(datasB64);
            var decodedJson = Encoding.Latin1.GetString(jsonBytes);

            using var doc  = JsonDocument.Parse(decodedJson);
            var       root = doc.RootElement;

            if (!root.TryGetProperty("slug", out var slugProp)
                || !root.TryGetProperty("user_id", out var userIdProp)
                || !root.TryGetProperty("md5_id", out var md5IdProp)
                || !root.TryGetProperty("media", out var mediaProp))
            {
                _logger.LogWarning("decoded 'datas' JSON is missing expected properties");

                return sources;
            }

            var slug     = slugProp.GetString() ?? "";
            var mediaStr = mediaProp.GetString() ?? "";

            var userId = userIdProp.ValueKind == JsonValueKind.Number
                             ? userIdProp.GetInt64().ToString()
                             : userIdProp.GetString() ?? "";

            var md5Id = md5IdProp.ValueKind == JsonValueKind.Number
                            ? md5IdProp.GetInt64().ToString()
                            : md5IdProp.GetString() ?? "";

            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(mediaStr))
            {
                _logger.LogWarning("invalid slug or media values in datas JSON");

                return sources;
            }

            var encryptedBytes = Encoding.Latin1.GetBytes(mediaStr);

            var     decryptedStr      = string.Empty;
            var     decryptionSuccess = false;
            byte[]? decryptedBytes    = null;

            var highPriorityKeys = new List<string>(10);
            AddKeyIfValid(highPriorityKeys, userId, slug, md5Id);
            AddKeyIfValid(highPriorityKeys, userId, slug, "undefined");
            AddKeyIfValid(highPriorityKeys, "undefined", slug, md5Id);
            AddKeyIfValid(highPriorityKeys, "undefined", slug, "undefined");
            AddKeyIfValid(highPriorityKeys, userId, slug, "");
            AddKeyIfValid(highPriorityKeys, userId, slug, "null");
            AddKeyIfValid(highPriorityKeys, userId, slug, "0");

            using var aes = Aes.Create();
            aes.Mode    = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            var aesKeyBytes     = new byte[32];
            var counterBuffer   = new byte[16];
            var keyStreamBuffer = new byte[16];

            foreach (var keyString in highPriorityKeys)
            {
                GetMd5HexBytes(keyString, aesKeyBytes);

                if (TryDecryptKey(aes, encryptedBytes, aesKeyBytes, counterBuffer, keyStreamBuffer, out decryptedBytes))
                {
                    decryptedStr      = Encoding.UTF8.GetString(decryptedBytes!);
                    decryptionSuccess = true;
                    _logger.LogInformation("decryption succeeded using high-priority key string: {KeyString}",
                                           keyString);

                    break;
                }
            }

            if (!decryptionSuccess)
            {
                var fallbackUserIds = new[] { userId, "undefined" };

                for (var i = 1; i <= 500; i++)
                {
                    var idStr = i.ToString();
                    foreach (var possibleUserId in fallbackUserIds)
                    {
                        var keyString = $"{possibleUserId}:{slug}:{idStr}";
                        GetMd5HexBytes(keyString, aesKeyBytes);

                        if (TryDecryptKey(aes,
                                          encryptedBytes,
                                          aesKeyBytes,
                                          counterBuffer,
                                          keyStreamBuffer,
                                          out decryptedBytes))
                        {
                            decryptedStr      = Encoding.UTF8.GetString(decryptedBytes!);
                            decryptionSuccess = true;
                            _logger.LogInformation("decryption succeeded using fallback key string: {KeyString}",
                                                   keyString);

                            break;
                        }
                    }

                    if (decryptionSuccess)
                    {
                        break;
                    }
                }
            }

            if (!decryptionSuccess || string.IsNullOrEmpty(decryptedStr))
            {
                _logger.LogWarning("aES-CTR decryption failed for all possible local database IDs");

                return sources;
            }

            _logger.LogDebug("decrypted media JSON: {DecryptedStr}", decryptedStr);

            using var mediaDoc  = JsonDocument.Parse(decryptedStr);
            var       mediaRoot = mediaDoc.RootElement;

            if (mediaRoot.TryGetProperty("mp4", out var mp4Prop) && mp4Prop.ValueKind == JsonValueKind.Object)
            {
                if (mediaRoot.TryGetProperty("mp4", out var mp4Obj)
                    && mp4Obj.TryGetProperty("sources", out var sourcesProp)
                    && sourcesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var source in sourcesProp.EnumerateArray())
                    {
                        string? label = null;
                        if (source.TryGetProperty("label", out var lProp))
                        {
                            label = lProp.GetString();
                        }

                        string? directBaseUrl = null;
                        if (source.TryGetProperty("url", out var uProp))
                        {
                            directBaseUrl = uProp.GetString();
                        }

                        string? directPath = null;
                        if (source.TryGetProperty("path", out var pathProp))
                        {
                            directPath = pathProp.GetString();
                        }

                        if (!source.TryGetProperty("size", out var sizeProp)
                            || sizeProp.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                        {
                            continue;
                        }

                        var sizeStr = sizeProp.ValueKind == JsonValueKind.Number
                                          ? sizeProp.GetInt64().ToString()
                                          : sizeProp.GetString() ?? "";

                        if (string.IsNullOrEmpty(sizeStr) || sizeStr == "0")
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(directBaseUrl) || string.IsNullOrEmpty(directPath))
                        {
                            continue;
                        }

                        var directCombinedUrl = $"{directBaseUrl.TrimEnd('/')}/{directPath.TrimStart('/')}";
                        sources.Add(new VideoSource
                        {
                            Url     = directCombinedUrl,
                            Quality = $"{label ?? "Auto"}",
                            Type    = VideoType.Mp4,
                            Headers = new Dictionary<string, string>
                            {
                                { "Referer", "https://abysscdn.com/" }
                            }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract Abyss video sources for: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"[?&]v=([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VParamRegex();

    [GeneratedRegex(@"/(?:video/embed|video|embed)/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PathEmbedRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"const\s+datas\s*=\s*""([^""]+)""")]
    private static partial Regex DatasVarRegex();

    private static void AddKeyIfValid(List<string> list, string userId, string slug, string id)
    {
        var key = $"{userId}:{slug}:{id}";
        if (!list.Contains(key))
        {
            list.Add(key);
        }
    }

    private static void GetMd5HexBytes(string input, Span<byte> destination)
    {
        var maxByteCount = Encoding.UTF8.GetByteCount(input);
        var inputBytes   = maxByteCount <= 256 ? stackalloc byte[256] : new byte[maxByteCount];
        var bytesWritten = Encoding.UTF8.GetBytes(input, inputBytes);

        Span<byte> hashBytes = stackalloc byte[16];
        MD5.HashData(inputBytes[..bytesWritten], hashBytes);

        for (var i = 0; i < 16; i++)
        {
            var b = hashBytes[i];
            destination[i * 2]       = GetHexCharByte(b >> 4);
            destination[(i * 2) + 1] = GetHexCharByte(b & 0x0F);
        }
    }

    private static byte GetHexCharByte(int val)
    {
        return (byte) (val < 10 ? '0' + val : 'a' + (val - 10));
    }

    private static bool TryDecryptKey(
        Aes         aes,
        byte[]      ciphertext,
        byte[]      key,
        byte[]      counter,
        byte[]      keyStream,
        out byte[]? decryptedBytes)
    {
        decryptedBytes = null;
        aes.Key        = key;

        using var encryptor = aes.CreateEncryptor();

        Buffer.BlockCopy(key, 0, counter, 0, 16);

        encryptor.TransformBlock(counter, 0, 16, keyStream, 0);

        var  firstBlockSize = Math.Min(16, ciphertext.Length);
        byte firstJsonByte  = 0;
        for (var i = 0; i < firstBlockSize; i++)
        {
            var decryptedByte = (byte) (ciphertext[i] ^ keyStream[i]);

            if (decryptedByte is 0x20 or 0x09 or 0x0D or 0x0A)
            {
                continue;
            }

            firstJsonByte = decryptedByte;

            break;
        }

        if (firstJsonByte is not 0x7B and not 0x5B)
        {
            return false;
        }

        decryptedBytes = new byte[ciphertext.Length];

        for (var j = 0; j < firstBlockSize; j++)
        {
            decryptedBytes[j] = (byte) (ciphertext[j] ^ keyStream[j]);
        }

        for (var k = 15; k >= 0; k--)
        {
            if (++counter[k] != 0)
            {
                break;
            }
        }

        var blockCount = (ciphertext.Length + 15) / 16;
        for (var i = 1; i < blockCount; i++)
        {
            encryptor.TransformBlock(counter, 0, 16, keyStream, 0);

            var offset         = i * 16;
            var bytesToProcess = Math.Min(16, ciphertext.Length - offset);

            for (var j = 0; j < bytesToProcess; j++)
            {
                decryptedBytes[offset + j] = (byte) (ciphertext[offset + j] ^ keyStream[j]);
            }

            for (var k = 15; k >= 0; k--)
            {
                if (++counter[k] != 0)
                {
                    break;
                }
            }
        }

        return true;
    }
}
