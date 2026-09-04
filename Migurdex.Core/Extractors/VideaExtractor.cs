using Microsoft.Extensions.Logging;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Migurdex.Core.Extractors;

public partial class VideaExtractor : IExtractor
{
    private readonly HttpClient              _httpClient;
    private readonly ILogger<VideaExtractor> _logger;

    public VideaExtractor(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient();
        _logger     = bridge.CreateLogger<VideaExtractor>();
    }

    public string Name => "Videa";

    public bool CanExtract(string url)
    {
        return url.Contains("videa.hu", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var sources = new List<VideoSource>();

        try
        {
            var referer = headers.GetReferer();
            var vcode   = GetVcode(url);
            if (string.IsNullOrEmpty(vcode))
            {
                _logger.LogWarning("could not extract video code (vcode) from URL: {Url}", url);

                return sources;
            }

            var playerUrl = $"https://videa.hu/player?v={vcode}";
            _logger.LogDebug("fetching Videa player page: {PlayerUrl}", playerUrl);

            var request = new HttpRequestMessage(HttpMethod.Get, playerUrl);
            request.Headers.Add("User-Agent",
                                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Referer", referer ?? playerUrl);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("player page returned status code: {StatusCode}", response.StatusCode);

                return sources;
            }

            var html    = await response.Content.ReadAsStringAsync(cancellationToken);
            var xtMatch = XtRegex().Match(html);
            if (!xtMatch.Success)
            {
                _logger.LogWarning("could not find _xt parameter in player HTML for vcode: {Vcode}", vcode);

                return sources;
            }

            var xt        = xtMatch.Groups[1].Value;
            var sessionId = RandomString(8);

            var      c       = new Dictionary<string, string>();
            string[] r1      = ["e", "a", "g", "j", "d", "c", "h", "i", "b", "f"];
            var      xtChars = xt.ToCharArray();

            for (var i = 0; i < xtChars.Length; i++)
            {
                var key = r1[(i / 8) + 1];
                if (i % 8 == 0)
                {
                    c[key] = "";
                }

                c[key] += xtChars[i];
            }

            c["e"] = sessionId;

            var          d    = c["a"] + c["g"] + c["j"] + c["d"];
            var          u    = c["c"] + c["h"] + c["i"] + c["b"];
            const string oStr = "xHb0ZvME5q8CBcoQi6AngerDu3FGO9fkUlwPmLVY_RTzj2hJIS4NasXWKy1td7p";

            var mSb = new StringBuilder(d.Length);
            for (var i = 0; i < d.Length; i++)
            {
                var idx = oStr.IndexOf(d[i]);
                if (idx >= 0)
                {
                    var uIdx = i - (idx - 31);
                    if (uIdx >= 0 && uIdx < u.Length)
                    {
                        mSb.Append(u[uIdx]);
                    }
                }
            }

            var m = mSb.ToString();

            string[] r2 = ["f", "h", "c", "b", "i"];
            for (var i = 0; i < m.Length; i++)
            {
                var key = r2[(i / 8) + 1];
                if (i % 8 == 0)
                {
                    c[key] = "";
                }

                c[key] += m[i];
            }

            c["f"] = "";

            var sParam = c["e"];
            var tParam = c["h"] + c["c"];

            var xmlApiUrl =
                $"https://videa.hu/player/xml?platform=desktop&v={vcode}&lang=hu&_s={sParam}&_t={tParam}&start=0";

            _logger.LogDebug("requesting encrypted XML data from: {XmlApiUrl}", xmlApiUrl);

            var xmlRequest = new HttpRequestMessage(HttpMethod.Get, xmlApiUrl);
            xmlRequest.Headers.Add("User-Agent",
                                   "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            xmlRequest.Headers.Add("Referer", playerUrl);

            var xmlResponse = await _httpClient.SendAsync(xmlRequest, cancellationToken);
            if (!xmlResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("xML API returned status code: {StatusCode}", xmlResponse.StatusCode);

                return sources;
            }

            var xVideaXs = xmlResponse.Headers.TryGetValues("X-Videa-XS", out var values)
                               ? values.FirstOrDefault() ?? ""
                               : "";
            var encryptedBase64 = await xmlResponse.Content.ReadAsStringAsync(cancellationToken);

            c["f"] = xVideaXs;

            var rc4KeyStr      = c["b"] + c["i"] + c["e"] + c["f"];
            var rc4KeyBytes    = Encoding.UTF8.GetBytes(rc4KeyStr);
            var encryptedBytes = Convert.FromBase64String(encryptedBase64);

            var decryptedBytes = Rc4(encryptedBytes, rc4KeyBytes);
            var xmlText        = Encoding.UTF8.GetString(decryptedBytes);

            var doc = XDocument.Parse(xmlText);
            var hashValues = doc.Descendants("hash_values")
                                .Elements()
                                .ToDictionary(e => e.Name.LocalName, e => e.Value);

            foreach (var vs in doc.Descendants("video_source"))
            {
                var path     = vs.Value;
                var name     = vs.Attribute("name")?.Value;
                var exp      = vs.Attribute("exp")?.Value;
                var mimetype = vs.Attribute("mimetype")?.Value;

                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(exp))
                {
                    continue;
                }

                var hashKey = $"hash_value_{name}";
                hashValues.TryGetValue(hashKey, out var md5Hash);

                var fullUrl = $"https:{path}?md5={md5Hash}&expires={exp}";

                sources.Add(new VideoSource
                {
                    Url     = fullUrl,
                    Quality = string.IsNullOrWhiteSpace(name) ? "Auto" : name,
                    Type    = VideoType.Mp4
                });
            }

            _logger.LogInformation("successfully extracted {Count} video sources for vcode: {Vcode}",
                                   sources.Count,
                                   vcode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error extracting video sources from URL: {Url}", url);
        }

        return sources;
    }

    [GeneratedRegex(@"var\s+_xt\s*=\s*""([^""]+)""")]
    private static partial Regex XtRegex();

    [GeneratedRegex(@"[?&]v=([a-zA-Z0-9]+)")]
    private static partial Regex VQueryRegex();

    [GeneratedRegex(@"-([a-zA-Z0-9]{16})(?:[?#]|$)")]
    private static partial Regex VPathRegex();

    private static string? GetVcode(string url)
    {
        var queryMatch = VQueryRegex().Match(url);
        if (queryMatch.Success)
        {
            return queryMatch.Groups[1].Value;
        }

        var pathMatch = VPathRegex().Match(url);
        if (pathMatch.Success)
        {
            return pathMatch.Groups[1].Value;
        }

        return null;
    }

    private static string RandomString(int length)
    {
        const string chars  = "abcdefghijklmnopqrstuvwxyz0123456789";
        var          bytes  = RandomNumberGenerator.GetBytes(length);
        var          result = new char[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }

        return new string(result);
    }

    private static byte[] Rc4(byte[] data, byte[] key)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = (byte) i;
        }

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j            = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var a = 0;
        j = 0;
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            a            = (a + 1) & 0xFF;
            j            = (j + s[a]) & 0xFF;
            (s[a], s[j]) = (s[j], s[a]);
            result[i]    = (byte) (data[i] ^ s[(s[a] + s[j]) & 0xFF]);
        }

        return result;
    }
}
