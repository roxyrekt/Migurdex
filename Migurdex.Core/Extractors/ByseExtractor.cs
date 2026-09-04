using Microsoft.Extensions.Logging;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Extractors;

[JsonSerializable(typeof(ByseExtractor.AttestPayload))]
[JsonSerializable(typeof(ByseExtractor.PlaybackRequestPayload))]
[JsonSerializable(typeof(ByseExtractor.CaptchaChallengeRequest))]
[JsonSerializable(typeof(ByseExtractor.CaptchaVerifyRequest))]
internal partial class ByseJsonContext : JsonSerializerContext
{
}

public partial class ByseExtractor : IExtractor
{
    private static readonly ThreadLocal<(ECDsa Key, string X, string Y)> _sEcdsaContainer = new(() =>
    {
        var ecdsa        = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecParameters = ecdsa.ExportParameters(true);
        var x            = Base64Url.EncodeToString(ecParameters.Q.X!);
        var y            = Base64Url.EncodeToString(ecParameters.Q.Y!);

        return (ecdsa, x, y);
    });

    private readonly HttpClient             _httpClient;
    private readonly ILogger<ByseExtractor> _logger;
    private readonly M3U8PlaylistExtractor  _m3U8Extractor;

    public ByseExtractor(M3U8PlaylistExtractor m3U8Extractor, ISharedBridge bridge)
    {
        _httpClient    = bridge.CreateHttpClient();
        _m3U8Extractor = m3U8Extractor;
        _logger        = bridge.CreateLogger<ByseExtractor>();
    }

    public string Name => "Byse";

    public bool CanExtract(string url)
    {
        return url.Contains("filemoon.sx", StringComparison.OrdinalIgnoreCase)
               || url.Contains("filemoon.la", StringComparison.OrdinalIgnoreCase)
               || url.Contains("byse.net", StringComparison.OrdinalIgnoreCase)
               || url.Contains("bysesukior.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("bysesayeveum.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var videoId = ExtractVideoId(url);
            if (string.IsNullOrEmpty(videoId))
            {
                _logger.LogWarning("could not extract video ID from URL: {Url}", url);

                return sources;
            }

            var initialDomain = GetDomain(url);
            _logger.LogInformation("start extraction for ID: {VideoId} on domain: {Domain}",
                                   videoId,
                                   initialDomain);

            var detailsUrl = $"https://{initialDomain}/api/videos/{videoId}/embed/details";

            var detailsRequest = new HttpRequestMessage(HttpMethod.Get, detailsUrl);
            detailsRequest.Headers.Add("Referer", url);
            detailsRequest.Headers.Add("X-Embed-Parent", url);
            detailsRequest.Headers.Add("X-Embed-Origin", initialDomain);
            detailsRequest.Headers.Add("User-Agent",
                                       "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            _logger.LogDebug("fetching video details from: {Url}", detailsUrl);
            var detailsResponse = await _httpClient.SendAsync(detailsRequest, cancellationToken);
            if (!detailsResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("video details response failed for: {VideoId}", videoId);

                return sources;
            }

            var embedFrameUrl = "";

            await using (var detailsStream = await detailsResponse.Content.ReadAsStreamAsync())
            using (var detailsDoc = await JsonDocument.ParseAsync(detailsStream))
            {
                if (detailsDoc.RootElement.TryGetProperty("embed_frame_url", out var embedProp))
                {
                    embedFrameUrl = embedProp.GetString() ?? "";
                }
            }

            if (string.IsNullOrEmpty(embedFrameUrl))
            {
                _logger.LogWarning("could not resolve embed_frame_url from details response");

                return sources;
            }

            var attestDomain = GetDomain(embedFrameUrl);
            _logger.LogDebug("resolved protected domain: {Domain} for ID: {VideoId}",
                             attestDomain,
                             videoId);

            var challengeUrl = $"https://{attestDomain}/api/videos/access/challenge";
            _logger.LogDebug("requesting challenge from: {Url}", challengeUrl);

            var challengeRequest = new HttpRequestMessage(HttpMethod.Post, challengeUrl);
            challengeRequest.Headers.Add("Referer", embedFrameUrl);
            challengeRequest.Headers.Add("Origin", $"https://{attestDomain}");
            challengeRequest.Headers.Add("User-Agent",
                                         "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            challengeRequest.Headers.Add("X-Embed-Parent", url);
            challengeRequest.Headers.Add("X-Embed-Origin", initialDomain);

            var challengeResponse = await _httpClient.SendAsync(challengeRequest, cancellationToken);
            if (!challengeResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("challenge response failed for: {VideoId}", videoId);

                return sources;
            }

            string? challengeId;
            string? nonce;

            await using (var challengeStream = await challengeResponse.Content.ReadAsStreamAsync())
            using (var challengeDoc = await JsonDocument.ParseAsync(challengeStream))
            {
                challengeId = challengeDoc.RootElement.GetProperty("challenge_id").GetString();
                nonce       = challengeDoc.RootElement.GetProperty("nonce").GetString();
            }

            if (string.IsNullOrEmpty(challengeId) || string.IsNullOrEmpty(nonce))
            {
                _logger.LogWarning("invalid challenge data received (ID or Nonce missing)");

                return sources;
            }

            _logger.LogDebug("challenge verified. Nonce: {Nonce}", nonce);

            _logger.LogDebug("generating cryptographically signed attest payload...");
            var context = GenerateAttestPayload(challengeId, nonce);

            var attestUrl = $"https://{attestDomain}/api/videos/access/attest";
            _logger.LogDebug("sending attest request to: {Url}", attestUrl);

            var attestRequest = new HttpRequestMessage(HttpMethod.Post, attestUrl)
            {
                Content = new StringContent(context.Payload, Encoding.UTF8, "application/json")
            };
            attestRequest.Headers.Add("Referer", embedFrameUrl);
            attestRequest.Headers.Add("Origin", $"https://{attestDomain}");
            attestRequest.Headers.Add("User-Agent",
                                      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            attestRequest.Headers.Add("X-Embed-Parent", url);
            attestRequest.Headers.Add("X-Embed-Origin", initialDomain);

            var attestResponse = await _httpClient.SendAsync(attestRequest, cancellationToken);
            if (!attestResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("attest request failed");

                return sources;
            }

            string? playbackToken = null;
            await using (var attestStream = await attestResponse.Content.ReadAsStreamAsync())
            using (var attestDoc = await JsonDocument.ParseAsync(attestStream))
            {
                if (attestDoc.RootElement.TryGetProperty("token", out var playTokenProp))
                {
                    playbackToken = playTokenProp.GetString();
                }
                else if (attestDoc.RootElement.TryGetProperty("error", out var errProp))
                {
                    _logger.LogWarning("attest rejected with error: {Error}", errProp.GetString());

                    return sources;
                }
            }

            if (string.IsNullOrEmpty(playbackToken))
            {
                _logger.LogWarning("no playback token returned from attest");

                return sources;
            }

            _logger.LogDebug("attestation successful. Token: {Token}", playbackToken[..15] + "...");

            var settingsUrl = $"https://{attestDomain}/api/videos/{videoId}/embed/settings";
            _logger.LogDebug("fetching video settings from: {Url}", settingsUrl);

            var settingsRequest = new HttpRequestMessage(HttpMethod.Get, settingsUrl);
            settingsRequest.Headers.Add("Referer", embedFrameUrl);
            settingsRequest.Headers.Add("X-Embed-Parent", url);
            settingsRequest.Headers.Add("X-Embed-Origin", initialDomain);
            settingsRequest.Headers.Add("User-Agent",
                                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var settingsResponse = await _httpClient.SendAsync(settingsRequest, cancellationToken);
            var captchaRequired  = false;
            if (settingsResponse.IsSuccessStatusCode)
            {
                await using var settingsStream = await settingsResponse.Content.ReadAsStreamAsync();
                using var       settingsDoc    = await JsonDocument.ParseAsync(settingsStream);
                if (settingsDoc.RootElement.TryGetProperty("captcha_required", out var capProp))
                {
                    captchaRequired = capProp.GetBoolean();
                }
            }

            string? captchaToken = null;
            if (captchaRequired)
            {
                _logger.LogInformation("proof of Work (PoW) captcha is required. Generating challenge...");

                var captchaUrl = $"https://{attestDomain}/api/videos/{videoId}/embed/captcha";
                var fingerprintPayload = new FingerprintPayload(
                    playbackToken,
                    context.ViewerId,
                    context.DeviceId,
                    0.93
                );
                var captchaBodyObj = new CaptchaChallengeRequest(fingerprintPayload);
                var captchaBody =
                    JsonSerializer.Serialize(captchaBodyObj, ByseJsonContext.Default.CaptchaChallengeRequest);

                var captchaRequest = new HttpRequestMessage(HttpMethod.Post, captchaUrl)
                {
                    Content = new StringContent(captchaBody, Encoding.UTF8, "application/json")
                };
                captchaRequest.Headers.Add("Referer", embedFrameUrl);
                captchaRequest.Headers.Add("Origin", $"https://{attestDomain}");
                captchaRequest.Headers.Add("User-Agent",
                                           "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                captchaRequest.Headers.Add("X-Embed-Parent", url);
                captchaRequest.Headers.Add("X-Embed-Origin", initialDomain);

                var captchaResponse = await _httpClient.SendAsync(captchaRequest, cancellationToken);
                if (captchaResponse.IsSuccessStatusCode)
                {
                    var powNonce      = "";
                    var powDifficulty = 0;
                    var powToken      = "";

                    await using (var captchaStream = await captchaResponse.Content.ReadAsStreamAsync())
                    using (var captchaDoc = await JsonDocument.ParseAsync(captchaStream))
                    {
                        powNonce      = captchaDoc.RootElement.GetProperty("pow_nonce").GetString() ?? "";
                        powDifficulty = captchaDoc.RootElement.GetProperty("pow_difficulty").GetInt32();
                        powToken      = captchaDoc.RootElement.GetProperty("pow_token").GetString() ?? "";
                    }

                    _logger.LogInformation("solving Proof of Work (Difficulty: {Diff})...", powDifficulty);
                    var solution = SolvePow(powNonce, powDifficulty);
                    _logger.LogDebug("PoW Solved. Solution: {Sol}", solution);

                    var verifyUrl = $"https://{attestDomain}/api/videos/{videoId}/embed/captcha/verify";
                    var verifyBodyObj = new CaptchaVerifyRequest(
                        powToken,
                        solution,
                        fingerprintPayload
                    );
                    var verifyBody =
                        JsonSerializer.Serialize(verifyBodyObj, ByseJsonContext.Default.CaptchaVerifyRequest);

                    var verifyRequest = new HttpRequestMessage(HttpMethod.Post, verifyUrl)
                    {
                        Content = new StringContent(verifyBody, Encoding.UTF8, "application/json")
                    };
                    verifyRequest.Headers.Add("Referer", embedFrameUrl);
                    verifyRequest.Headers.Add("Origin", $"https://{attestDomain}");
                    verifyRequest.Headers.Add("User-Agent",
                                              "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    verifyRequest.Headers.Add("X-Embed-Parent", url);
                    verifyRequest.Headers.Add("X-Embed-Origin", initialDomain);

                    var verifyResponse = await _httpClient.SendAsync(verifyRequest, cancellationToken);
                    if (verifyResponse.IsSuccessStatusCode)
                    {
                        await using var verifyStream = await verifyResponse.Content.ReadAsStreamAsync();
                        using var       verifyDoc    = await JsonDocument.ParseAsync(verifyStream);
                        captchaToken = verifyDoc.RootElement.GetProperty("token").GetString();
                    }
                }
            }

            var playbackUrl = $"https://{attestDomain}/api/videos/{videoId}/embed/playback";
            var playbackBodyObj = new PlaybackRequestPayload(
                new FingerprintPayload(
                    playbackToken,
                    context.ViewerId,
                    context.DeviceId,
                    0.93
                )
            );
            var playbackBody =
                JsonSerializer.Serialize(playbackBodyObj, ByseJsonContext.Default.PlaybackRequestPayload);

            _logger.LogDebug("requesting encrypted playback configs from: {Url}", playbackUrl);

            var playbackRequest = new HttpRequestMessage(HttpMethod.Post, playbackUrl)
            {
                Content = new StringContent(playbackBody, Encoding.UTF8, "application/json")
            };
            playbackRequest.Headers.Add("Referer", embedFrameUrl);
            playbackRequest.Headers.Add("Origin", $"https://{attestDomain}");
            playbackRequest.Headers.Add("User-Agent",
                                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            playbackRequest.Headers.Add("X-Embed-Parent", url);
            playbackRequest.Headers.Add("X-Embed-Origin", initialDomain);

            if (!string.IsNullOrEmpty(captchaToken))
            {
                playbackRequest.Headers.Add("X-Captcha-Token", captchaToken);
            }

            var playbackResponse = await _httpClient.SendAsync(playbackRequest, cancellationToken);
            if (!playbackResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("playback config response failed (Status: {Status})",
                                   playbackResponse.StatusCode);

                return sources;
            }

            var ivBase64Url      = "";
            var payloadBase64Url = "";
            var version          = "";
            var keyPartsList     = new List<string>();

            await using (var playbackStream = await playbackResponse.Content.ReadAsStreamAsync())
            using (var playbackDoc = await JsonDocument.ParseAsync(playbackStream))
            {
                if (!playbackDoc.RootElement.TryGetProperty("playback", out var playbackObj))
                {
                    _logger.LogWarning("playback payload key not found");

                    return sources;
                }

                ivBase64Url      = playbackObj.GetProperty("iv").GetString() ?? "";
                payloadBase64Url = playbackObj.GetProperty("payload").GetString() ?? "";
                version          = playbackObj.TryGetProperty("version", out var vProp) ? vProp.GetString() ?? "" : "";

                var rawKeyParts = playbackObj.GetProperty("key_parts");
                foreach (var part in rawKeyParts.EnumerateArray())
                {
                    keyPartsList.Add(part.GetString() ?? "");
                }
            }

            var selectedParts = GetSelectedKeyParts(version, keyPartsList);

            Span<byte> keyBytes  = stackalloc byte[32];
            var        keyOffset = 0;
            foreach (var part in selectedParts)
            {
                var bytesWritten = Base64Url.DecodeFromChars(part, keyBytes[keyOffset..]);
                keyOffset += bytesWritten;
            }

            var finalKey = keyBytes[..keyOffset];

            Span<byte> ivBytes  = stackalloc byte[16];
            var        ivLength = Base64Url.DecodeFromChars(ivBase64Url, ivBytes);
            var        finalIv  = ivBytes[..ivLength];

            var maxEncryptedLength = Base64Url.GetMaxDecodedLength(payloadBase64Url.Length);
            var encryptedRented    = ArrayPool<byte>.Shared.Rent(maxEncryptedLength);

            try
            {
                var encryptedLength = Base64Url.DecodeFromChars(payloadBase64Url, encryptedRented);
                var encryptedSpan   = encryptedRented.AsSpan(0, encryptedLength);

                _logger.LogDebug("decrypting playback payload");
                var decryptedString = DecryptAesGcm(encryptedSpan, finalKey, finalIv);

                if (!string.IsNullOrEmpty(decryptedString))
                {
                    _logger.LogDebug("decryption successful");
                    using var decryptedDoc = JsonDocument.Parse(decryptedString);
                    if (decryptedDoc.RootElement.TryGetProperty("sources", out var sourcesArr))
                    {
                        foreach (var src in sourcesArr.EnumerateArray())
                        {
                            if (src.TryGetProperty("url", out var fileProp))
                            {
                                var m3U8Url = fileProp.GetString();
                                if (!string.IsNullOrEmpty(m3U8Url))
                                {
                                    _logger.LogDebug("resolving M3U8 Playlist: {M3U8Url}", m3U8Url);
                                    var extractedM3U8 =
                                        await _m3U8Extractor.ExtractAsync(m3U8Url,
                                                                          new Dictionary<string, string>
                                                                          {
                                                                              { "Referer", $"https://{attestDomain}/" }
                                                                          });

                                    foreach (var exSrc in extractedM3U8)
                                    {
                                        sources.Add(exSrc);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(encryptedRented);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to extract video sources for URL: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"/(?:e|embed|video|download|ns5u)/([a-zA-Z0-9_-]+)", RegexOptions.Compiled)]
    private static partial Regex VideoIdRegex();

    private static List<string> GetSelectedKeyParts(string version, List<string> keyParts)
    {
        if (string.IsNullOrEmpty(version))
        {
            return keyParts;
        }

        version = version.Trim();

        if (!int.TryParse(version, out var n) || n < 1 || n > 20)
        {
            return keyParts;
        }

        var idx2  = 31 - n;
        var count = keyParts.Count;

        if (n > count || idx2 > count)
        {
            return keyParts;
        }

        var selected = new List<string>(2);

        if (n <= count)
        {
            var part1 = keyParts[n - 1];
            if (!string.IsNullOrEmpty(part1))
            {
                selected.Add(part1);
            }
        }

        if (idx2 <= count)
        {
            var part2 = keyParts[idx2 - 1];
            if (!string.IsNullOrEmpty(part2))
            {
                selected.Add(part2);
            }
        }

        return selected.Count > 0 ? selected : keyParts;
    }

    private static AttestContext GenerateAttestPayload(string challengeId, string nonce)
    {
        var viewerId = Guid.NewGuid().ToString("n");
        var deviceId = Guid.NewGuid().ToString("n");

        var (ecdsa, xBase64Url, yBase64Url) = _sEcdsaContainer.Value;

        var nonceBytes         = Encoding.UTF8.GetBytes(nonce);
        var signatureBytes     = ecdsa.SignData(nonceBytes, HashAlgorithmName.SHA256);
        var signatureBase64Url = Base64Url.EncodeToString(signatureBytes);

        var payloadObj = new AttestPayload(
            viewerId,
            deviceId,
            challengeId,
            nonce,
            signatureBase64Url,
            new PublicKeyDto(
                "ES256",
                "P-256",
                true,
                ["verify"],
                "EC",
                xBase64Url,
                yBase64Url
            ),
            new ClientDto(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                1,
                1920,
                1080,
                24,
                ["en-US", "en"],
                "Europe/Istanbul",
                8,
                0,
                "Google Inc. (NVIDIA)",
                "ANGLE (NVIDIA, NVIDIA GeForce GTX 980 Direct3D11 vs_5_0 ps_5_0)",
                "f3UUiHOulTOLvZYFkfJ0bVvzKSDVNXmwjiEj83uSA_A",
                "_oGTjFqFiMCfUhMTzdEID7gIliFGMmPeNMqniFYvQ7M",
                "fine,hover",
                new ClientExtraDto(
                    "",
                    "5.0 (Windows)"
                )
            ),
            new StorageDto(
                viewerId,
                viewerId,
                $"{viewerId}:{deviceId}",
                $"{viewerId}:{deviceId}"
            ),
            new AttributesDto(
                "low"
            )
        );

        return new AttestContext
        {
            ViewerId = viewerId,
            DeviceId = deviceId,
            Payload  = JsonSerializer.Serialize(payloadObj, ByseJsonContext.Default.AttestPayload)
        };
    }

    private static string SolvePow(string nonce, int difficulty)
    {
        if (difficulty <= 0)
        {
            return "0";
        }

        var  prefix = nonce + ":";
        uint s      = 0;

        while (true)
        {
            var candidate = prefix + s;
            var bytes     = Encoding.UTF8.GetBytes(candidate);
            var hash      = CustomHash(bytes);
            if (CountLeadingZeros(hash) >= difficulty)
            {
                return s.ToString();
            }

            s++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int offset)
    {
        return (value << offset) | (value >> (32 - offset));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(uint[] t)
    {
        t[0] += t[1];
        t[3] =  RotateLeft(t[3] ^ t[0], 16);
        t[2] += t[3];
        t[1] =  RotateLeft(t[1] ^ t[2], 12);
        t[0] += t[1];
        t[3] =  RotateLeft(t[3] ^ t[0], 8);
        t[2] += t[3];
        t[1] =  RotateLeft(t[1] ^ t[2], 7);
    }

    private static uint[] CustomHash(byte[] tBytes)
    {
        uint[] e = [1779033703, 3144134277, 1013904242, 2773480762];
        foreach (var t in tBytes)
        {
            e[0] += t;
            e[0] =  RotateLeft(e[0], 7);
            QuarterRound(e);
        }

        for (var i = 0; i < 8; i++)
        {
            QuarterRound(e);
        }

        const int  be = 512;
        const int  lt = be - 1;
        const int  dr = 2;
        const uint lr = 2654435761;
        const uint hr = 2246822519;

        var r = new uint[be];
        for (var i = 0; i < be; i++)
        {
            QuarterRound(e);
            r[i] = e[0] ^ e[2];
        }

        for (var i = 0; i < dr; i++)
        {
            for (var s = 0; s < be; s++)
            {
                var a = r[s] & lt;
                var c = r[s] + r[a];
                c    =  RotateLeft(c, 13);
                c    ^= r[(s + 1) & lt] * lr;
                r[s] =  c;
                e[0] ^= c;
                QuarterRound(e);
            }
        }

        var       n = new uint[8];
        const int o = be / 8;
        for (var i = 0; i < 8; i++)
        {
            QuarterRound(e);
            var s = e[0];
            var a = i * o;
            for (var c = 0; c < o; c++)
            {
                var d = r[a + c];
                s += d;
                s =  RotateLeft(s, 5);
                s ^= d * hr;
            }

            n[i] = s ^ e[2];
        }

        return n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clz32(uint x)
    {
        if (x == 0)
        {
            return 32;
        }

        var n = 0;
        if (x <= 0x0000FFFF)
        {
            n +=  16;
            x <<= 16;
        }

        if (x <= 0x00FFFFFF)
        {
            n +=  8;
            x <<= 8;
        }

        if (x <= 0x0FFFFFFF)
        {
            n +=  4;
            x <<= 4;
        }

        if (x <= 0x3FFFFFFF)
        {
            n +=  2;
            x <<= 2;
        }

        if (x <= 0x7FFFFFFF) { n += 1; }

        return n;
    }

    private static int CountLeadingZeros(uint[] t)
    {
        var e = 0;
        foreach (var n in t)
        {
            if (n == 0)
            {
                e += 32;

                continue;
            }

            e += Clz32(n);

            break;
        }

        return e;
    }

    private static string ExtractVideoId(string url)
    {
        var match = VideoIdRegex().Match(url);

        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string GetDomain(string url)
    {
        try
        {
            var uri = new Uri(url);

            return uri.Host;
        }
        catch
        {
            return "filemoon.sx";
        }
    }

    private static string DecryptAesGcm(ReadOnlySpan<byte> encryptedBytes,
        ReadOnlySpan<byte>                                 keyBytes,
        ReadOnlySpan<byte>                                 ivBytes)
    {
        const int tagLength = 16;
        if (encryptedBytes.Length < tagLength)
        {
            throw new ArgumentException("Encrypted data is too short");
        }

        var ciphertextLength = encryptedBytes.Length - tagLength;
        var ciphertext       = encryptedBytes[..ciphertextLength];
        var tag              = encryptedBytes.Slice(ciphertextLength, tagLength);

        byte[]? rented = null;
        var decryptedBytes = ciphertextLength <= 2048
                                 ? stackalloc byte[ciphertextLength]
                                 : (rented = ArrayPool<byte>.Shared.Rent(ciphertextLength)).AsSpan(0, ciphertextLength);

        try
        {
            using var aesGcm = new AesGcm(keyBytes, tagLength);
            aesGcm.Decrypt(ivBytes, ciphertext, tag, decryptedBytes);

            return Encoding.UTF8.GetString(decryptedBytes);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private class AttestContext
    {
        public string ViewerId { get; init; } = string.Empty;
        public string DeviceId { get; init; } = string.Empty;
        public string Payload  { get; init; } = string.Empty;
    }

    internal record AttestPayload(
        [property: JsonPropertyName("viewer_id")]
        string ViewerId,
        [property: JsonPropertyName("device_id")]
        string DeviceId,
        [property: JsonPropertyName("challenge_id")]
        string ChallengeId,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("signature")]
        string Signature,
        [property: JsonPropertyName("public_key")]
        PublicKeyDto PublicKey,
        [property: JsonPropertyName("client")] ClientDto Client,
        [property: JsonPropertyName("storage")]
        StorageDto Storage,
        [property: JsonPropertyName("attributes")]
        AttributesDto Attributes
    );

    internal record PublicKeyDto(
        [property: JsonPropertyName("alg")] string Alg,
        [property: JsonPropertyName("crv")] string Crv,
        [property: JsonPropertyName("ext")] bool   Ext,
        [property: JsonPropertyName("key_ops")]
        string[] KeyOps,
        [property: JsonPropertyName("kty")] string Kty,
        [property: JsonPropertyName("x")]   string X,
        [property: JsonPropertyName("y")]   string Y
    );

    internal record ClientDto(
        [property: JsonPropertyName("user_agent")]
        string UserAgent,
        [property: JsonPropertyName("pixel_ratio")]
        int PixelRatio,
        [property: JsonPropertyName("screen_width")]
        int ScreenWidth,
        [property: JsonPropertyName("screen_height")]
        int ScreenHeight,
        [property: JsonPropertyName("color_depth")]
        int ColorDepth,
        [property: JsonPropertyName("languages")]
        string[] Languages,
        [property: JsonPropertyName("timezone")]
        string Timezone,
        [property: JsonPropertyName("hardware_concurrency")]
        int HardwareConcurrency,
        [property: JsonPropertyName("touch_points")]
        int TouchPoints,
        [property: JsonPropertyName("webgl_vendor")]
        string WebglVendor,
        [property: JsonPropertyName("webgl_renderer")]
        string WebglRenderer,
        [property: JsonPropertyName("canvas_hash")]
        string CanvasHash,
        [property: JsonPropertyName("audio_hash")]
        string AudioHash,
        [property: JsonPropertyName("pointer_type")]
        string PointerType,
        [property: JsonPropertyName("extra")] ClientExtraDto Extra
    );

    internal record ClientExtraDto(
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("appVersion")]
        string AppVersion
    );

    internal record StorageDto(
        [property: JsonPropertyName("cookie")] string Cookie,
        [property: JsonPropertyName("local_storage")]
        string LocalStorage,
        [property: JsonPropertyName("indexed_db")]
        string IndexedDb,
        [property: JsonPropertyName("cache_storage")]
        string CacheStorage
    );

    internal record AttributesDto(
        [property: JsonPropertyName("entropy")]
        string Entropy
    );

    internal record PlaybackRequestPayload(
        [property: JsonPropertyName("fingerprint")]
        FingerprintPayload Fingerprint
    );

    internal record FingerprintPayload(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("viewer_id")]
        string ViewerId,
        [property: JsonPropertyName("device_id")]
        string DeviceId,
        [property: JsonPropertyName("confidence")]
        double Confidence
    );

    internal record CaptchaChallengeRequest(
        [property: JsonPropertyName("fingerprint")]
        FingerprintPayload Fingerprint
    );

    internal record CaptchaVerifyRequest(
        [property: JsonPropertyName("pow_token")]
        string PowToken,
        [property: JsonPropertyName("solution")]
        string Solution,
        [property: JsonPropertyName("fingerprint")]
        FingerprintPayload Fingerprint
    );
}
