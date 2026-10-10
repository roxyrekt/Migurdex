using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Plugins.TrAnimeIzle;

internal static partial class TrAnimeProbeSolver
{
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    [GeneratedRegex("id\\s*=\\s*\"rtt-cfg\"[^>]*>(.*?)</script>",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CfgRegex();

    private sealed record ProbeCfg(string Pt, string WsUrl, string MintUrl, string? Stun, int GatherMs, int TimeoutMs);

    public static async Task<bool> SolveAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Uri                                                                    gateUri,
        string                                                                 gateHtml,
        ILogger                                                                logger,
        CancellationToken                                                      cancellationToken)
    {
        var cfg = ParseCfg(gateHtml, logger);
        if (cfg is null)
        {
            var idx = gateHtml.IndexOf("rtt-cfg", StringComparison.OrdinalIgnoreCase);
            var tag = idx >= 0
                          ? gateHtml.Substring(idx, Math.Min(350, gateHtml.Length - idx))
                          : "(yok)";
            logger.LogDebug("probe cfg null (html={Length}b, tag={Tag})", gateHtml.Length, tag);
            return false;
        }

        if (JwtExpired(cfg.Pt))
        {
            logger.LogDebug("probe pt expired (ptLen={Len}, exp={Exp})", cfg.Pt.Length, JwtExpiryDebug(cfg.Pt));
            return false;
        }

        var (receipt, minRtt) = await MeasureRttAsync(cfg, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(receipt))
        {
            logger.LogDebug("probe RTT receipt alınamadı");
            return false;
        }

        var srflx  = await GetSrflxAddressAsync(cfg.Stun, cfg.GatherMs, cancellationToken).ConfigureAwait(false);
        var webrtc = srflx is null ? "none" : "ok";

        string? redirect;
        try
        {
            redirect = await MintAsync(send, gateUri, cfg, receipt, webrtc, srflx, minRtt, cancellationToken)
                           .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "probe mint başarısız");
            return false;
        }

        if (string.IsNullOrEmpty(redirect))
        {
            logger.LogDebug("probe mint redirect vermedi");
            return false;
        }

        using var final = await send(new HttpRequestMessage(HttpMethod.Get, redirect), cancellationToken)
                              .ConfigureAwait(false);
        var finalHost = final.RequestMessage?.RequestUri?.Host ?? string.Empty;
        var ok        = final.IsSuccessStatusCode && !IsProbeHost(finalHost);
        logger.LogInformation("rtt probe {Result} ({Host})", ok ? "geçti" : "tamamlanamadı", finalHost);
        return ok;
    }

    public static bool IsProbeGate(Uri uri, string targetHost)
    {
        if (uri.Host.Equals(targetHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Host.Contains("probe", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.Equals("/g", StringComparison.Ordinal);
    }

    private static bool IsProbeHost(string host)
    {
        return host.Contains("probe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractTitle(string html)
    {
        var match = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : "?";
    }

    private static ProbeCfg? ParseCfg(string html, ILogger? logger = null)
    {
        var match = CfgRegex().Match(html);
        if (!match.Success)
        {
            logger?.LogDebug("probe cfg regex tutmadı");
            return null;
        }

        var captured = match.Groups[1].Value;
        logger?.LogDebug("probe cfg match idx={Idx} len={Len} head={Head}",
                         match.Index,
                         captured.Length,
                         captured.Length > 80 ? captured[..80] : captured);

        try
        {
            using var doc  = JsonDocument.Parse(match.Groups[1].Value);
            var       root = doc.RootElement;
            var       pt   = root.TryGetProperty("pt", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(pt))
            {
                return null;
            }

            return new ProbeCfg(
                pt,
                root.TryGetProperty("wsUrl", out var ws) ? ws.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("mintUrl", out var mu) ? mu.GetString() ?? "/mint" : "/mint",
                root.TryGetProperty("stun", out var st) ? st.GetString() : null,
                root.TryGetProperty("gatherMs", out var gm) && gm.TryGetInt32(out var gmv) ? gmv : 1500,
                root.TryGetProperty("timeoutMs", out var tm) && tm.TryGetInt32(out var tmv) ? tmv : 7000);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "probe cfg JSON parse patladı (jsonLen={Len})", match.Groups[1].Value.Length);
            return null;
        }
    }

    private static string JwtExpiryDebug(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');

            var raw = parts.Length >= 3 ? parts[1] : parts[0];
            if (parts.Length < 2)
            {
                return $"parts={parts.Length}";
            }

            var payload = raw.Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var unix))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("O");
            }

            return "exp-yok";
        }
        catch (Exception ex)
        {
            return $"hata:{ex.GetType().Name}";
        }
    }

    private static bool JwtExpired(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            var raw   = parts.Length >= 3 ? parts[1] : parts[0];
            if (parts.Length < 2)
            {
                return true;
            }

            var payload = raw.Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var unix))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime <= DateTime.UtcNow.AddSeconds(10);
            }
        }
        catch
        {
            // ignored
        }

        return true;
    }

    private static async Task<(string? Receipt, int MinRtt)> MeasureRttAsync(ProbeCfg cfg,
        CancellationToken                                                             ct)
    {
        if (string.IsNullOrEmpty(cfg.WsUrl))
        {
            return (null, -1);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(cfg.TimeoutMs > 0 ? cfg.TimeoutMs : 7000));

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", ChromeUa);
        try
        {
            var wsUri = new Uri(cfg.WsUrl + "?pt=" + Uri.EscapeDataString(cfg.Pt));
            ws.Options.SetRequestHeader("Origin", $"{wsUri.Scheme}://{wsUri.Host}");
            await ws.ConnectAsync(wsUri, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            return (null, -1);
        }

        var buffer  = new byte[65536];
        var sb      = new StringBuilder();
        var sw      = new Stopwatch();
        var hasLast = false;

        try
        {
            while (ws.State == WebSocketState.Open && !timeout.Token.IsCancellationRequested)
            {
                sb.Clear();
                ValueWebSocketReceiveResult chunk;
                do
                {
                    chunk = await ws.ReceiveAsync(new Memory<byte>(buffer), timeout.Token).ConfigureAwait(false);
                    if (chunk.MessageType == WebSocketMessageType.Close)
                    {
                        return (null, -1);
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, chunk.Count));
                }
                while (!chunk.EndOfMessage);

                JsonDocument msg;
                try { msg = JsonDocument.Parse(sb.ToString()); }
                catch { continue; }

                using (msg)
                {
                    if (!msg.RootElement.TryGetProperty("t", out var t))
                    {
                        continue;
                    }

                    if (t.GetString() == "ping")
                    {
                        var c = hasLast ? (int) sw.ElapsedMilliseconds : -1;
                        sw.Restart();
                        hasLast = true;
                        var pong = JsonSerializer.Serialize(new
                        {
                            t = "pong",
                            i = msg.RootElement.TryGetProperty("i", out var pi) ? pi.GetInt32() : 0,
                            n = msg.RootElement.TryGetProperty("n", out var pn) ? pn.GetString() : null,
                            c
                        });
                        var bytes = Encoding.UTF8.GetBytes(pong);
                        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token)
                                .ConfigureAwait(false);
                    }
                    else if (t.GetString() == "done")
                    {
                        var receipt = msg.RootElement.TryGetProperty("receipt", out var r) ? r.GetString() : null;
                        var min = msg.RootElement.TryGetProperty("min", out var m) && m.TryGetInt32(out var mv)
                                      ? mv
                                      : -1;
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default); }
                        catch
                        {
                            // ignored
                        }

                        return (receipt, min);
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        return (null, -1);
    }

    private static async Task<string?> GetSrflxAddressAsync(string? stunUri,
        int                                                         gatherMs,
        CancellationToken                                           ct)
    {
        if (string.IsNullOrWhiteSpace(stunUri))
        {
            return null;
        }

        var part = stunUri.Trim();
        if (part.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
        {
            part = part["stun:".Length..];
        }

        var host  = part;
        var port  = 3478;
        var colon = part.LastIndexOf(':');
        if (colon > 0 && int.TryParse(part[(colon + 1)..], out var p))
        {
            host = part[..colon];
            port = p;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(gatherMs > 0 ? gatherMs : 1500));

        foreach (var address in addresses.OrderByDescending(a => a.AddressFamily == AddressFamily.InterNetworkV6))
        {
            try
            {
                using var udp  = new UdpClient(address.AddressFamily);
                var       txId = new byte[12];
                Random.Shared.NextBytes(txId);

                var request = new byte[20];
                request[0] = 0x00;
                request[1] = 0x01;
                request[2] = 0x00;
                request[3] = 0x00;
                request[4] = 0x21;
                request[5] = 0x12;
                request[6] = 0xA4;
                request[7] = 0x42;
                txId.CopyTo(request, 8);

                await udp.SendAsync(request, new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
                var received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var srflx    = ParseSrflx(received.Buffer, txId);
                if (srflx is not null)
                {
                    return srflx;
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    private static string? ParseSrflx(byte[] response, byte[] txId)
    {
        try
        {
            if (response.Length < 20 || response[0] != 0x01 || response[1] != 0x01)
            {
                return null;
            }

            if (response[4] != 0x21 || response[5] != 0x12 || response[6] != 0xA4 || response[7] != 0x42)
            {
                return null;
            }

            for (var i = 0; i < 12; i++)
            {
                if (response[8 + i] != txId[i])
                {
                    return null;
                }
            }

            var offset = 20;
            while (offset + 4 <= response.Length)
            {
                var type = (response[offset] << 8) | response[offset + 1];
                var len  = (response[offset + 2] << 8) | response[offset + 3];
                offset += 4;
                if (offset + len > response.Length)
                {
                    break;
                }

                if (type == 0x0020 && len >= 8)
                {
                    var family = response[offset + 1];
                    if (family == 0x01 && len >= 8)
                    {
                        return new IPAddress(new[]
                        {
                            (byte) (response[offset + 4] ^ 0x21),
                            (byte) (response[offset + 5] ^ 0x12),
                            (byte) (response[offset + 6] ^ 0xA4),
                            (byte) (response[offset + 7] ^ 0x42)
                        }).ToString();
                    }

                    if (family == 0x02 && len >= 20)
                    {
                        var key = new byte[16] { 0x21, 0x12, 0xA4, 0x42, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                        txId.CopyTo(key, 4);
                        var ip = new byte[16];
                        for (var i = 0; i < 16; i++)
                        {
                            ip[i] = (byte) (response[offset + 4 + i] ^ key[i]);
                        }

                        return new IPAddress(ip).ToString();
                    }
                }

                offset += (len + 3) & ~3;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static async Task<string?> MintAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Uri                                                                    gateUri,
        ProbeCfg                                                               cfg,
        string                                                                 receipt,
        string                                                                 webrtc,
        string?                                                                srflx,
        int                                                                    minRtt,
        CancellationToken                                                      ct)
    {
        var tz = "Europe/Istanbul";
        try
        {
            var local = TimeZoneInfo.Local.Id;
            if (!string.IsNullOrWhiteSpace(local))
            {
                tz = local;
            }
        }
        catch
        {
            // ignored
        }

        var body = new Dictionary<string, object?>
        {
            ["pt"]          = cfg.Pt,
            ["receipt"]     = receipt,
            ["turnstile"]   = null,
            ["webrtc"]      = webrtc,
            ["tz"]          = tz,
            ["lang"]        = CultureInfo.CurrentCulture.Name is { Length: > 0 } lang ? lang : "en-US",
            ["clientRttMs"] = minRtt
        };
        if (srflx is not null)
        {
            body["srflx"] = srflx;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(gateUri, cfg.MintUrl));
        req.Content          = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Referrer = gateUri;

        using var resp = await send(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("risk skoru geçemedi");
        }

        resp.EnsureSuccessStatusCode();
        var       json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc  = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("redirect", out var r) ? r.GetString() : null;
    }
}
