using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Migurdex.Core.Services.Turnstile;

public sealed class ChromiumCdpSolver : IDisposable
{
    public enum BrowserMode
    {
        Auto,
        Visible
    }

    private readonly string      _executablePath;
    private readonly string      _dataRoot;
    private readonly bool        _incognito;
    private readonly ILogger     _logger;
    private readonly BrowserMode _mode;
    private readonly bool        _autoClick;

    public ChromiumCdpSolver(string executablePath,
        string                      dataRoot,
        bool                        incognito = false,
        ILogger?                    logger    = null,
        BrowserMode                 mode      = BrowserMode.Auto,
        bool                        autoClick = true)
    {
        _executablePath = executablePath;
        _dataRoot       = dataRoot;
        _incognito      = incognito;
        _logger         = logger ?? NullLogger.Instance;
        _mode           = mode;
        _autoClick      = autoClick;
    }

    public async Task<TurnstileSolveResult> SolveAsync(string pageUrl,
        TimeSpan                                              timeout,
        CancellationToken                                     cancellationToken = default)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
        {
            return Fail(uri?.Host ?? string.Empty, "Yalnız HTTP/HTTPS sayfalar çözülebilir.");
        }

        if (!File.Exists(_executablePath))
        {
            return Fail(uri.Host, $"Tarayıcı bulunamadı: {_executablePath}");
        }

        var deadline = DateTime.UtcNow.Add(timeout);

        if (_mode == BrowserMode.Visible)
        {
            return await LaunchAndSolveAsync(uri, headless: false, useXvfb: NeedsXvfb(), deadline, cancellationToken)
                       .ConfigureAwait(false);
        }

        var headless = await LaunchAndSolveAsync(uri, headless: true, useXvfb: false, deadline, cancellationToken)
                           .ConfigureAwait(false);

        if (headless.Success || cancellationToken.IsCancellationRequested)
        {
            return headless;
        }

        if (OperatingSystem.IsLinux() && XvfbAvailable() && DateTime.UtcNow < deadline)
        {
            _logger.LogInformation("headless çözüm yetersiz, Xvfb altında headful deneniyor: {Host}", uri.Host);
            return await LaunchAndSolveAsync(uri, headless: false, useXvfb: true, deadline, cancellationToken)
                       .ConfigureAwait(false);
        }

        return headless;
    }

    private static bool NeedsXvfb()
    {
        return OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
    }

    private async Task<TurnstileSolveResult> LaunchAndSolveAsync(Uri uri,
        bool                                                         headless,
        bool                                                         useXvfb,
        DateTime                                                     deadline,
        CancellationToken                                            cancellationToken)
    {
        var profileDir = _incognito
                             ? Path.Combine(Path.GetTempPath(), $"migurdex-solver-{Guid.NewGuid():N}")
                             : Path.Combine(_dataRoot, "solver-profile");
        try
        {
            Directory.CreateDirectory(profileDir);
        }
        catch (Exception ex)
        {
            return Fail(uri.Host, $"Profil dizini açılamadı: {ex.Message}");
        }

        Process? process = null;
        try
        {
            if (useXvfb && !XvfbAvailable())
            {
                return Fail(uri.Host, "Görüntüsüz ortamda headful için xvfb-run gerekli (örn. sudo apt install xvfb).");
            }

            // Paylaşılan profilde önceki çökmüş çalışmadan kalma bayat port
            // dosyası yeni tarayıcıyı yanlış porta bağlar; önden sil.
            try { File.Delete(Path.Combine(profileDir, "DevToolsActivePort")); }
            catch
            {
                // ignored
            }

            process = StartBrowser(profileDir, headless, useXvfb);
            if (process is null)
            {
                return Fail(uri.Host, "Tarayıcı süreci başlatılamadı.");
            }

            _logger.LogDebug("tarayıcı başlatıldı (pid={Pid}, headless={Headless}, profil={Profile})",
                             process.Id,
                             headless,
                             profileDir);

            var port = await WaitForDevToolsPortAsync(profileDir, cancellationToken).ConfigureAwait(false);
            if (port <= 0)
            {
                return Fail(uri.Host, "Tarayıcı CDP portu açılamadı.");
            }

            _logger.LogDebug("CDP portu: {Port}", port);

            var endpoint = await GetDebuggerUrlAsync(port, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(endpoint))
            {
                return Fail(uri.Host, "CDP endpoint alınamadı.");
            }

            await using var cdp = new CdpConnection();
            await cdp.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

            var target = await cdp.SendAsync("Target.createTarget",
                                             new
                                             {
                                                 url = uri.AbsoluteUri
                                             },
                                             cancellationToken: cancellationToken)
                                  .ConfigureAwait(false);
            var targetId = target.TryGetProperty("targetId", out var tid) ? tid.GetString() : null;
            if (string.IsNullOrEmpty(targetId))
            {
                return Fail(uri.Host, "CDP hedefi açılamadı.");
            }

            try
            {
                var attached = await cdp.SendAsync("Target.attachToTarget",
                                                   new
                                                   {
                                                       targetId,
                                                       flatten = true
                                                   },
                                                   cancellationToken: cancellationToken)
                                        .ConfigureAwait(false);
                var sessionId = attached.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
                if (string.IsNullOrEmpty(sessionId))
                {
                    return Fail(uri.Host, "CDP oturumu açılamadı.");
                }

                await cdp.SendAsync("Network.enable",
                                    new
                                    {
                                    },
                                    sessionId,
                                    cancellationToken)
                         .ConfigureAwait(false);

                _logger.LogInformation("sayfaya gidildi: {Url}", uri.AbsoluteUri);

                var polls         = 0;
                var started       = DateTime.UtcNow;
                var lastSearchLog = DateTime.MinValue;
                var lastClick     = DateTime.MinValue;
                var clickAttempts = 0;
                var widgetSeen    = false;
                var cleanReads    = 0;
                var targetHosts   = new[] { uri.Host.ToLowerInvariant() };

                JsonElement lastCookies = default;
                var         sawCookies  = false;
                while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                {
                    polls++;
                    var elapsed = (DateTime.UtcNow - started).TotalSeconds;

                    var cookies = await cdp.SendAsync("Network.getAllCookies",
                                                      new
                                                      {
                                                      },
                                                      sessionId,
                                                      cancellationToken)
                                           .ConfigureAwait(false);
                    lastCookies = cookies;
                    sawCookies  = true;
                    var found = FindClearance(cookies, targetHosts);
                    if (found is not null)
                    {
                        _logger.LogInformation("clearance alındı, sayfa yönlendirmesi bekleniyor...");
                        for (var i = 0; i < 15; i++)
                        {
                            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                            var check = await ReadPageStateAsync(cdp, sessionId, cancellationToken)
                                            .ConfigureAwait(false);
                            if (!check.HasChallenge
                                && !check.Title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                                && !check.Title.Contains("Bağlantı kontrol", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogInformation("sayfa açıldı: {Title} ({Url})", check.Title, check.Url);
                                break;
                            }
                        }

                        await CaptureScreenshotAsync(cdp, sessionId, cancellationToken).ConfigureAwait(false);
                        return new TurnstileSolveResult
                        {
                            Success      = true,
                            Host         = found.Value.Host,
                            Clearance    = found.Value.Value,
                            ExpiresAtUtc = found.Value.ExpiresAtUtc,
                            Source       = TurnstileSolverSource.UserBrowser
                        };
                    }

                    var searchActive = elapsed > 2.0;

                    if (searchActive && _autoClick)
                    {
                        var peek = await PeekWidgetAsync(cdp, sessionId, cancellationToken).ConfigureAwait(false);
                        if (peek is not null)
                        {
                            if (!widgetSeen)
                            {
                                widgetSeen = true;
                                _logger.LogInformation("kutucuk görüldü ({X:0},{Y:0}) [{Elapsed:0.00}sn]",
                                                       peek.Value.X,
                                                       peek.Value.Y,
                                                       elapsed);
                            }

                            if (!peek.Value.HasToken && (DateTime.UtcNow - lastClick).TotalSeconds >= 4)
                            {
                                lastClick = DateTime.UtcNow;
                                clickAttempts++;
                                await ClickAtAsync(cdp,
                                                   sessionId,
                                                   peek.Value.X,
                                                   peek.Value.Y,
                                                   cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                        else if (widgetSeen)
                        {
                            widgetSeen = false;
                            _logger.LogDebug("kutucuk gözden kayboldu");
                        }
                    }

                    if (searchActive && polls % 6 == 0)
                    {
                        foreach (var ev in cdp.DrainEvents(5))
                        {
                            _logger.LogDebug("sayfa konsolu: {Event}",
                                             ev.Length > 300 ? ev[..300] : ev);
                        }

                        var fresh = await ReadPageStateAsync(cdp, sessionId, cancellationToken)
                                        .ConfigureAwait(false);

                        _logger.LogDebug("durum: {Title} | challenge={Has} | {Url}",
                                         fresh.Title,
                                         fresh.HasChallenge,
                                         fresh.Url);

                        targetHosts = ResolveTargetHosts(uri.Host, fresh.Url);
                        if (!fresh.HasChallenge && IsBackOnTarget(targetHosts, uri.Host))
                        {
                            var hit = FindClearance(lastCookies, targetHosts);
                            if (hit is not null)
                            {
                                return new TurnstileSolveResult
                                {
                                    Success      = true,
                                    Host         = hit.Value.Host,
                                    Clearance    = hit.Value.Value,
                                    ExpiresAtUtc = hit.Value.ExpiresAtUtc,
                                    Source       = TurnstileSolverSource.UserBrowser
                                };
                            }

                            if (++cleanReads >= 2 && elapsed > 12)
                            {
                                LogCookieSummary(lastCookies);
                                return new TurnstileSolveResult
                                {
                                    Success     = false,
                                    Host        = uri.Host.ToLowerInvariant(),
                                    NoChallenge = true,
                                    Error       = "Sayfada challenge tespit edilmedi, kayıt gerekmedi."
                                };
                            }
                        }
                        else
                        {
                            cleanReads = 0;
                        }
                    }

                    if ((DateTime.UtcNow - lastSearchLog).TotalSeconds >= 2)
                    {
                        lastSearchLog = DateTime.UtcNow;
                        _logger.LogInformation(searchActive
                                                   ? "kutucuk aranıyor... ({Elapsed:0}sn, deneme {Attempt})"
                                                   : "clearance bekleniyor... ({Elapsed:0}sn)",
                                               elapsed,
                                               clickAttempts);
                    }

                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }

                if (sawCookies)
                {
                    LogCookieSummary(lastCookies);
                }

                if (!cancellationToken.IsCancellationRequested && _logger.IsEnabled(LogLevel.Information))
                {
                    await DumpBrowserStateAsync(cdp, sessionId, CancellationToken.None).ConfigureAwait(false);
                }

                return Fail(uri.Host,
                            cancellationToken.IsCancellationRequested
                                ? "Çözüm iptal edildi."
                                : "Süre doldu, clearance alınamadı (site Managed doğrulama istiyor olabilir).");
            }
            finally
            {
                try
                {
                    await cdp.SendAsync("Target.closeTarget",
                                        new
                                        {
                                            targetId
                                        },
                                        cancellationToken: default)
                             .ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(uri.Host, "Çözüm iptal edildi.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "turnstile çözümü başarısız: {Host}", uri.Host);
            return Fail(uri.Host, $"Çözüm hatası: {ex.Message}");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignored
                }

                process.Dispose();
            }

            if (_incognito)
            {
                try { Directory.Delete(profileDir, true); }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private Process? StartBrowser(string profileDir, bool headless, bool useXvfb)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            if (useXvfb)
            {
                psi.FileName = "xvfb-run";
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(_executablePath);
            }
            else
            {
                psi.FileName = _executablePath;
            }

            if (headless)
            {
                psi.ArgumentList.Add("--headless=new");
            }

            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            psi.ArgumentList.Add("--password-store=basic");
            psi.ArgumentList.Add("--use-mock-keychain");
            psi.ArgumentList.Add("--disable-blink-features=AutomationControlled");
            psi.ArgumentList.Add("--disable-features=Translate,OptimizationHints,MediaRouter");
            psi.ArgumentList.Add("--window-size=1366,768");
            var lang = System.Globalization.CultureInfo.CurrentCulture.Name;
            if (!string.IsNullOrWhiteSpace(lang))
            {
                psi.ArgumentList.Add($"--lang={lang}");
            }

            psi.ArgumentList.Add($"--user-data-dir={profileDir}");
            psi.ArgumentList.Add("--remote-debugging-port=0");
            psi.ArgumentList.Add("about:blank");

            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tarayıcı başlatılamadı: {Exe}", _executablePath);
            return null;
        }
    }

    private static async Task<int> WaitForDevToolsPortAsync(string profileDir, CancellationToken ct)
    {
        var file = Path.Combine(profileDir, "DevToolsActivePort");
        var sw   = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && !ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(file))
                {
                    var first = (await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false))
                                .FirstOrDefault()
                                ?.Trim();
                    if (int.TryParse(first, out var port) && port > 0)
                    {
                        return port;
                    }
                }
            }
            catch
            {
                // ignored
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return -1;
    }

    private static async Task<string?> GetDebuggerUrlAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler())
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct)
                                 .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var url)
                       ? url.GetString()
                       : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool HasChallenge, string Title, string Url)> ReadPageStateAsync(CdpConnection cdp,
        string                                                                                         sessionId,
        CancellationToken                                                                              ct)
    {
        try
        {
            var result = await cdp.SendAsync("Runtime.evaluate",
                                             new
                                             {
                                                 expression =
                                                     "(()=>{const h=document.documentElement?document.documentElement.innerHTML:'';return {has:h.includes('challenges.cloudflare.com/turnstile')||h.includes('cf-challenge')||(h.includes('turnstile')&&h.includes('cf_clearance')),title:document.title||'',url:location.href||''};})()",
                                                 returnByValue = true
                                             },
                                             sessionId,
                                             ct)
                                  .ConfigureAwait(false);

            if (result.TryGetProperty("result", out var inner)
                && inner.TryGetProperty("value", out var value))
            {
                var has   = value.TryGetProperty("has", out var h) && h.ValueKind == JsonValueKind.True;
                var title = value.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                var url   = value.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                return (has, title, url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "sayfa durumu okunamadı");
        }

        return (true, string.Empty, string.Empty);
    }

    private async Task<(double X, double Y, bool HasToken)?> PeekWidgetAsync(CdpConnection cdp,
        string                                                                             sessionId,
        CancellationToken                                                                  ct)
    {
        try
        {
            var token = await cdp.SendAsync("Runtime.evaluate",
                                            new
                                            {
                                                expression =
                                                    "(()=>{const i=document.querySelector('input[name=\"cf-turnstile-response\"]');return i?(i.value||'').length:0;})()",
                                                returnByValue = true
                                            },
                                            sessionId,
                                            ct)
                                 .ConfigureAwait(false);
            if (token.TryGetProperty("result", out var tInner)
                && tInner.TryGetProperty("value", out var tVal)
                && tVal.ValueKind == JsonValueKind.Number
                && tVal.GetInt32() > 0)
            {
                return (0, 0, true);
            }

            var box = await FindWidgetBySelectorAsync(cdp, sessionId, ct).ConfigureAwait(false);
            if (box is null)
            {
                return null;
            }

            return (box.Value.X, box.Value.Y, false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "widget yoklama başarısız");
            return null;
        }
    }

    private async Task ClickAtAsync(CdpConnection cdp,
        string                                    sessionId,
        double                                    x,
        double                                    y,
        CancellationToken                         ct)
    {
        try
        {
            _logger.LogInformation("kutucuğa tıklanıyor ({X:0},{Y:0})", x, y);
            await cdp.SendAsync("Input.dispatchMouseEvent",
                                new
                                {
                                    type = "mouseMoved",
                                    x,
                                    y,
                                    button     = "none",
                                    clickCount = 0
                                },
                                sessionId,
                                ct)
                     .ConfigureAwait(false);

            await Task.Delay(300, ct).ConfigureAwait(false);
            foreach (var type in new[] { "mousePressed", "mouseReleased" })
            {
                await cdp.SendAsync("Input.dispatchMouseEvent",
                                    new
                                    {
                                        type,
                                        x,
                                        y,
                                        button     = "left",
                                        clickCount = 1
                                    },
                                    sessionId,
                                    ct)
                         .ConfigureAwait(false);

                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "otomatik tıklama başarısız");
        }
    }

    private async Task<(double X, double Y)?> FindWidgetBySelectorAsync(CdpConnection cdp,
        string                                                                        sessionId,
        CancellationToken                                                             ct)
    {
        try
        {
            var result = await cdp.SendAsync("Runtime.evaluate",
                                             new
                                             {
                                                 expression =
                                                     "(()=>{const inp=document.querySelector('input[name=\"cf-turnstile-response\"]');const slot=inp&&inp.parentElement;if(slot){const r=slot.getBoundingClientRect();if(r.width>10&&r.height>10){const x=r.x+Math.min(28,r.width*0.12),y=r.y+r.height/2;if(x>=0&&y>=0&&x<=innerWidth&&y<=innerHeight)return {x,y,via:'slot'};}}const el=document.querySelector('iframe[src*=\"challenges.cloudflare.com\"]')||document.querySelector('fencedframe')||document.querySelector('.cf-turnstile')||document.querySelector('.turnstile-container iframe')||document.querySelector('[id^=\"turnstile-\"] iframe');if(!el)return null;const r=el.getBoundingClientRect();if(r.width<1||r.height<1)return null;const x=r.x+Math.min(28,r.width*0.12),y=r.y+r.height/2;if(x<0||y<0||x>innerWidth||y>innerHeight)return null;return {x,y,via:'el'};})()",
                                                 returnByValue = true
                                             },
                                             sessionId,
                                             ct)
                                  .ConfigureAwait(false);

            if (!result.TryGetProperty("result", out var inner)
                || !inner.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("x", out var xProp)
                || !value.TryGetProperty("y", out var yProp))
            {
                return null;
            }

            _logger.LogDebug("seçici isabeti ({Via})",
                             value.TryGetProperty("via", out var via) ? via.GetString() : "?");
            return (xProp.GetDouble(), yProp.GetDouble());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "seçiciyle widget bulunamadı");
            return null;
        }
    }

    private async Task CaptureScreenshotAsync(CdpConnection cdp, string sessionId, CancellationToken ct)
    {
        try
        {
            var shot = await cdp.SendAsync("Page.captureScreenshot",
                                           new
                                           {
                                               format = "png"
                                           },
                                           sessionId,
                                           ct)
                                .ConfigureAwait(false);

            if (shot.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } b64)
            {
                var path = Path.Combine(_dataRoot, "solver-last.png");
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(b64), ct).ConfigureAwait(false);
                _logger.LogInformation("ekran görüntüsü: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ekran görüntüsü alınamadı");
        }
    }

    private async Task DumpBrowserStateAsync(CdpConnection cdp, string sessionId, CancellationToken ct)
    {
        try
        {
            try
            {
                var targets = await cdp.SendAsync("Target.getTargets",
                                                  new
                                                  {
                                                  },
                                                  null,
                                                  ct)
                                       .ConfigureAwait(false);

                if (targets.TryGetProperty("targetInfos", out var infos))
                {
                    var list = new List<string>();
                    foreach (var t in infos.EnumerateArray())
                    {
                        var type = t.TryGetProperty("type", out var ty) ? ty.GetString() : "?";
                        var url  = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        var tid  = t.TryGetProperty("targetId", out var id) ? id.GetString() ?? "" : "";
                        list.Add($"{type}:{(tid.Length > 8 ? tid[..8] : tid)}:{url}");
                    }

                    _logger.LogInformation("hedefler ({Count}): {Targets}",
                                           list.Count,
                                           string.Join(" | ", list.Take(10)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "hedef listesi alınamadı");
            }

            try
            {
                var tree = await cdp.SendAsync("Page.getFrameTree",
                                               new
                                               {
                                               },
                                               sessionId,
                                               ct)
                                    .ConfigureAwait(false);

                if (tree.TryGetProperty("frameTree", out var root))
                {
                    var frames = new List<string>();
                    CollectFrames(root, frames, 0);
                    _logger.LogInformation("çerçeveler ({Count}): {Frames}",
                                           frames.Count,
                                           string.Join(" | ", frames.Take(15)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "çerçeve ağacı alınamadı");
            }

            try
            {
                var cookies = await cdp.SendAsync("Network.getAllCookies",
                                                  new
                                                  {
                                                  },
                                                  sessionId,
                                                  ct)
                                       .ConfigureAwait(false);

                if (cookies.TryGetProperty("cookies", out var list))
                {
                    var names = list.EnumerateArray()
                                    .Select(c => (c.TryGetProperty("name", out var n) ? n.GetString() : "?")
                                                 + "@"
                                                 + (c.TryGetProperty("domain", out var d) ? d.GetString() : "?"));
                    _logger.LogInformation("cookie'ler: {Cookies}", string.Join(", ", names.Take(40)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "cookie listesi alınamadı");
            }

            try
            {
                var flat = await cdp.SendAsync("DOM.getFlattenedDocument",
                                               new
                                               {
                                                   depth  = -1,
                                                   pierce = true
                                               },
                                               sessionId,
                                               ct)
                                    .ConfigureAwait(false);

                if (flat.TryGetProperty("nodes", out var nodes))
                {
                    var arr = nodes.EnumerateArray().ToArray();
                    var hist = arr.GroupBy(n => n.TryGetProperty("nodeName", out var nn)
                                                    ? nn.GetString() ?? "?"
                                                    : "?")
                                  .OrderByDescending(g => g.Count())
                                  .Take(15)
                                  .Select(g => $"{g.Key}x{g.Count()}");
                    var urls = arr.Where(n =>
                                  {
                                      var nm = n.TryGetProperty("nodeName", out var q) ? q.GetString() : null;
                                      return nm is "IFRAME" or "iframe" or "FENCEDFRAME" or "fencedframe";
                                  })
                                  .Select(n => (n.TryGetProperty("documentURL", out var du) ? du.GetString() : "?")
                                               + "#"
                                               + (n.TryGetProperty("backendNodeId", out var b)
                                                      ? b.GetInt32().ToString()
                                                      : "?"));
                    _logger.LogInformation("düğümler ({Total}): {Hist}", arr.Length, string.Join(", ", hist));
                    _logger.LogInformation("çerçeve düğümleri: {Frames}",
                                           string.Join(" | ", urls.Take(10).Select(u => u.Length > 90 ? u[..90] : u)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DOM histogramı alınamadı");
            }

            foreach (var ev in cdp.DrainEvents(10))
            {
                _logger.LogInformation("sayfa olayı: {Event}", ev.Length > 250 ? ev[..250] : ev);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "durum dökümü başarısız");
        }

        static void CollectFrames(JsonElement node, List<string> into, int depth)
        {
            if (depth > 6 || into.Count >= 30)
            {
                return;
            }

            if (node.TryGetProperty("frame", out var frame))
            {
                var url = frame.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                into.Add($"{new string(' ', depth * 2)}{url}");
            }

            if (node.TryGetProperty("childFrames", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    CollectFrames(child, into, depth + 1);
                }
            }
        }
    }

    private void LogCookieSummary(JsonElement result)
    {
        try
        {
            if (!result.TryGetProperty("cookies", out var cookies))
            {
                _logger.LogInformation("cookie yok");
                return;
            }

            var names = new List<string>();
            var count = 0;
            foreach (var cookie in cookies.EnumerateArray())
            {
                count++;
                var name   = cookie.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                var domain = cookie.TryGetProperty("domain", out var d) ? d.GetString() ?? "?" : "?";
                names.Add($"{name}@{domain}");
            }

            _logger.LogInformation("toplam {Count} cookie: {Cookies}", count, string.Join(", ", names));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "cookie özeti çıkarılamadı");
        }
    }

    private static string[] ResolveTargetHosts(string originalHost, string? currentUrl)
    {
        return new[] { originalHost.ToLowerInvariant(), TurnstileDetector.NormalizeHost(currentUrl ?? string.Empty) }
               .Where(h => !string.IsNullOrEmpty(h))
               .Distinct(StringComparer.Ordinal)
               .ToArray();
    }

    private static bool IsBackOnTarget(string[] currentHosts, string originalHost)
    {
        var wanted = originalHost.Trim().ToLowerInvariant();
        return currentHosts.Any(h => h.Equals(wanted, StringComparison.Ordinal)
                                     || h.EndsWith($".{wanted}", StringComparison.Ordinal)
                                     || wanted.EndsWith($".{h}", StringComparison.Ordinal));
    }

    private static (string Host, string Value, DateTime ExpiresAtUtc)? FindClearance(JsonElement result,
        string[]                                                                                 hosts)
    {
        try
        {
            if (!result.TryGetProperty("cookies", out var cookies))
            {
                return null;
            }

            foreach (var cookie in cookies.EnumerateArray())
            {
                if (!cookie.TryGetProperty("name", out var name)
                    || !string.Equals(name.GetString(), "cf_clearance", StringComparison.Ordinal))
                {
                    continue;
                }

                var domain = cookie.TryGetProperty("domain", out var d)
                                 ? (d.GetString() ?? string.Empty).Trim().ToLowerInvariant().TrimStart('.')
                                 : string.Empty;
                string? matched = null;
                foreach (var wantedRaw in hosts)
                {
                    var wanted = wantedRaw.Trim().ToLowerInvariant();
                    if (wanted.Length == 0)
                    {
                        continue;
                    }

                    if (domain.Length == 0
                        || wanted.Equals(domain, StringComparison.Ordinal)
                        || wanted.EndsWith($".{domain}", StringComparison.Ordinal)
                        || domain.EndsWith($".{wanted}", StringComparison.Ordinal))
                    {
                        matched = wanted;
                        break;
                    }
                }

                if (matched is null)
                {
                    continue;
                }

                var value = cookie.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                DateTime expires;
                if (cookie.TryGetProperty("expires", out var expVal) && expVal.TryGetDouble(out var unix) && unix > 0)
                {
                    expires = DateTimeOffset.FromUnixTimeSeconds((long) unix).UtcDateTime;
                }
                else
                {
                    expires = DateTime.UtcNow.Add(CfClearanceStore.DefaultTtl);
                }

                if (expires <= DateTime.UtcNow)
                {
                    continue;
                }

                return (matched, value, expires);
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static bool XvfbAvailable()
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return pathDirs.Any(dir =>
        {
            try { return File.Exists(Path.Combine(dir, "xvfb-run")); }
            catch { return false; }
        });
    }

    private static TurnstileSolveResult Fail(string host, string error)
    {
        return new TurnstileSolveResult
        {
            Success              = false,
            Host                 = host.Trim().ToLowerInvariant(),
            ManualActionRequired = true,
            Error                = error
        };
    }

    public void Dispose() { }

    private sealed class CdpConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket                                              _socket  = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly ConcurrentQueue<string>                                      _events  = new();
        private readonly CancellationTokenSource                                      _loopCts = new();
        private          int                                                          _id;

        public IReadOnlyList<string> DrainEvents(int max = 20)
        {
            var list = new List<string>();
            while (list.Count < max && _events.TryDequeue(out var e))
            {
                list.Add(e);
            }

            return list;
        }

        public async Task ConnectAsync(string wsUrl, CancellationToken ct)
        {
            await _socket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
            _ = Task.Run(() => ReceiveLoopAsync(_loopCts.Token));
        }

        public async Task<JsonElement> SendAsync(string method,
            object?                                     parameters,
            string?                                     sessionId         = null,
            CancellationToken                           cancellationToken = default)
        {
            var id  = Interlocked.Increment(ref _id);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            byte[] payload;
            using (var ms = new MemoryStream())
            {
                await using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", id);
                    writer.WriteString("method", method);
                    writer.WritePropertyName("params");
                    JsonSerializer.Serialize(writer,
                                             parameters
                                             ?? new
                                             {
                                             });
                    if (sessionId is not null)
                    {
                        writer.WriteString("sessionId", sessionId);
                    }

                    writer.WriteEndObject();
                    await writer.FlushAsync(cancellationToken);
                }

                payload = ms.ToArray();
            }

            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken)
                         .ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[65536];
            var ms     = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    ValueWebSocketReceiveResult chunk;
                    do
                    {
                        chunk = await _socket.ReceiveAsync(new Memory<byte>(buffer), ct).ConfigureAwait(false);
                        if (chunk.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        ms.Write(buffer, 0, chunk.Count);
                    }
                    while (!chunk.EndOfMessage);

                    JsonDocument msg;
                    try { msg = JsonDocument.Parse(ms.ToArray()); }
                    catch { continue; }

                    using (msg)
                    {
                        if (msg.RootElement.TryGetProperty("method", out var methodProp))
                        {
                            var method = methodProp.GetString();
                            if (method is "Log.entryAdded" or "Runtime.exceptionThrown")
                            {
                                _events.Enqueue(msg.RootElement.GetRawText());
                                while (_events.Count > 50)
                                {
                                    _events.TryDequeue(out _);
                                }

                                continue;
                            }
                        }

                        if (!msg.RootElement.TryGetProperty("id", out var idProp)
                            || !_pending.TryGetValue(idProp.GetInt32(), out var tcs))
                        {
                            continue;
                        }

                        if (msg.RootElement.TryGetProperty("error", out var err))
                        {
                            tcs.TrySetException(new InvalidOperationException($"CDP hatası: {err}"));
                        }
                        else if (msg.RootElement.TryGetProperty("result", out var result))
                        {
                            tcs.TrySetResult(result.Clone());
                        }
                        else
                        {
                            tcs.TrySetResult(JsonDocument.Parse("{}").RootElement);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch
            {
                foreach (var kvp in _pending)
                {
                    kvp.Value.TrySetCanceled(ct);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _loopCts.CancelAsync(); }
            catch
            {
                // ignored
            }

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token)
                                 .ConfigureAwait(false);
                }
            }
            catch
            {
                // ignored
            }

            _socket.Dispose();
            _loopCts.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
