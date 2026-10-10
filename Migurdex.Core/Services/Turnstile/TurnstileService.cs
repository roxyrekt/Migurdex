using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Diagnostics;

namespace Migurdex.Core.Services.Turnstile;

public static class TurnstilePaths
{
    public static string DataRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        var dir = Path.Combine(appData, "migurdex");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string BrowserCacheRoot()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "Local");
            }
        }
        else
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache");
        }

        var dir = Path.Combine(baseDir, "migurdex", "browser");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed class TurnstileService
{
    private readonly CfClearanceStore _store;
    private readonly IBlameCollector? _blame;
    private readonly ILogger          _logger;
    private readonly string           _dataRoot;
    private readonly string           _browserCacheRoot;
    private readonly bool             _incognito;
    private readonly TimeSpan         _defaultTimeout = TimeSpan.FromSeconds(45);

    public TurnstileService(CfClearanceStore store,
        string                               dataRoot,
        string                               browserCacheRoot,
        bool                                 incognito = false,
        IBlameCollector?                     blame     = null,
        ILogger?                             logger    = null)
    {
        _store            = store;
        _dataRoot         = dataRoot;
        _browserCacheRoot = browserCacheRoot;
        _incognito        = incognito;
        _blame            = blame;
        _logger           = logger ?? NullLogger.Instance;
    }

    public string DataRoot         => _dataRoot;
    public string BrowserCacheRoot => _browserCacheRoot;

    public async Task<TurnstileSolveResult> SolveAsync(string pageUrl,
        Func<string, Task<bool>>?                             confirmDownloadAsync = null,
        bool                                                  allowDownload        = true,
        TimeSpan?                                             timeout              = null,
        IProgress<double>?                                    downloadProgress     = null,
        bool                                                  visible              = false,
        bool                                                  autoClick            = true,
        bool                                                  forceDownload        = false,
        CancellationToken                                     cancellationToken    = default)
    {
        var host = TurnstileDetector.NormalizeHost(pageUrl);
        if (string.IsNullOrEmpty(host))
        {
            return new TurnstileSolveResult
            {
                Success = false,
                Error   = "Geçersiz URL."
            };
        }

        if (_store.TryGet(host, out var cached, out var cachedExp) && !string.IsNullOrEmpty(cached))
        {
            return new TurnstileSolveResult
            {
                Success      = true,
                Host         = host,
                Clearance    = cached,
                ExpiresAtUtc = cachedExp,
                Source       = TurnstileSolverSource.Cache
            };
        }

        var limit = timeout ?? _defaultTimeout;

        string? browserFailure = null;

        if (!forceDownload)
        {
            var browser = BrowserDetector.Detect().FirstOrDefault(b => b.Kind == BrowserKind.Chromium);
            _logger.LogInformation("tarayıcı taraması: {Count} uyumlu bulundu", browser is null ? 0 : 1);
            if (browser is not null)
            {
                var result = await SolveWithBrowserAsync(browser.ExecutablePath,
                                                         browser.Name,
                                                         pageUrl,
                                                         host,
                                                         TurnstileSolverSource.UserBrowser,
                                                         limit,
                                                         visible,
                                                         autoClick,
                                                         cancellationToken)
                                 .ConfigureAwait(false);
                if (result.Success)
                {
                    return result;
                }

                if (result.NoChallenge)
                {
                    _logger.LogInformation("challenge yok, indirme atlanıyor: {Host}", host);
                    return result;
                }

                browserFailure = result.Error;
                _logger.LogWarning("kullanıcı tarayıcısıyla çözüm başarısız ({Browser}): {Error}",
                                   browser.Name,
                                   result.Error);
            }
        }
        else
        {
            _logger.LogInformation("sistem tarayıcıları atlandı (--force-download)");
        }

        if (allowDownload)
        {
            var downloaded = ChromiumDownloader.FindCachedExecutable(_browserCacheRoot);
            if (downloaded is not null)
            {
                return await SolveWithBrowserAsync(downloaded,
                                                   $"Chromium {ChromiumDownloader.PinnedVersion}",
                                                   pageUrl,
                                                   host,
                                                   TurnstileSolverSource.DownloadedBrowser,
                                                   limit,
                                                   visible,
                                                   autoClick,
                                                   cancellationToken)
                           .ConfigureAwait(false);
            }

            if (ChromiumDownloader.GetPlatformSlug() is not null && confirmDownloadAsync is not null)
            {
                var prompt = browserFailure is null
                                 ? (forceDownload
                                        ? $"Paketli çözücü Chromium indiriliyor (~200MB, tek seferlik, {_browserCacheRoot})"
                                        : $"Hiçbir uyumlu tarayıcı bulunamadı. Çözücü Chromium indirilsin mi? (~200MB, tek seferlik, {_browserCacheRoot})")
                                 : $"Sistem tarayıcısıyla çözülemedi ({browserFailure}). Paketlenmiş Chromium indirilip onunla denensin mi? (~200MB, tek seferlik, {_browserCacheRoot})";

                var ok = forceDownload || await confirmDownloadAsync(prompt).ConfigureAwait(false);
                if (ok)
                {
                    try
                    {
                        _logger.LogInformation("chromium indiriliyor: {Version} ({Cache})",
                                               ChromiumDownloader.PinnedVersion,
                                               _browserCacheRoot);
                        var exe = await ChromiumDownloader.DownloadAsync(_browserCacheRoot,
                                                                         downloadProgress,
                                                                         cancellationToken)
                                                          .ConfigureAwait(false);
                        return await SolveWithBrowserAsync(exe,
                                                           $"Chromium {ChromiumDownloader.PinnedVersion}",
                                                           pageUrl,
                                                           host,
                                                           TurnstileSolverSource.DownloadedBrowser,
                                                           limit,
                                                           visible,
                                                           autoClick,
                                                           cancellationToken)
                                   .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "chromium indirilemedi");
                        return ManualResult(host, $"İndirme başarısız: {ex.Message}");
                    }
                }
            }
        }

        return ManualResult(host,
                            browserFailure is not null
                                ? $"Otomatik çözüm başarısız ({browserFailure}). Tarayıcıda çözüp cf_clearance değerini yapıştırın."
                                : "Uyumlu tarayıcı bulunamadı. Tarayıcıda çözüp cf_clearance değerini yapıştırın.");
    }

    public TurnstileSolveResult StoreManual(string host, string clearance, double? expiresInHours = null)
    {
        var normalized = TurnstileDetector.NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrWhiteSpace(clearance))
        {
            return new TurnstileSolveResult
            {
                Success = false,
                Host    = normalized,
                Error   = "Host ve değer gerekli."
            };
        }

        var expires = DateTime.UtcNow.AddHours(expiresInHours is > 0 ? expiresInHours.Value : 6);
        _store.Set(normalized, clearance.Trim(), expires, "manual");
        Record("turnstile.solve:manual", 0, false);

        return new TurnstileSolveResult
        {
            Success      = true,
            Host         = normalized,
            Clearance    = clearance.Trim(),
            ExpiresAtUtc = expires,
            Source       = TurnstileSolverSource.Manual
        };
    }

    private async Task<TurnstileSolveResult> SolveWithBrowserAsync(string executable,
        string                                                            browserName,
        string                                                            pageUrl,
        string                                                            host,
        TurnstileSolverSource                                             source,
        TimeSpan                                                          timeout,
        bool                                                              visible,
        bool                                                              autoClick,
        CancellationToken                                                 ct)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("çözüm deneniyor: {Browser} ({Exe}) -> {Url}", browserName, executable, pageUrl);
        using var solver = new ChromiumCdpSolver(executable,
                                                 _dataRoot,
                                                 _incognito,
                                                 _logger,
                                                 visible
                                                     ? ChromiumCdpSolver.BrowserMode.Visible
                                                     : ChromiumCdpSolver.BrowserMode.Auto,
                                                 autoClick);
        var result = await solver.SolveAsync(pageUrl, timeout, ct).ConfigureAwait(false);
        sw.Stop();

        result.Source      = source;
        result.BrowserName = browserName;
        Record(source == TurnstileSolverSource.DownloadedBrowser
                   ? "turnstile.solve:downloaded"
                   : "turnstile.solve:user-browser",
               sw.ElapsedMilliseconds,
               !result.Success);

        if (result is { Success: true, Clearance.Length: > 0, ExpiresAtUtc: not null })
        {
            var tag = source.ToString().ToLowerInvariant();
            _store.Set(result.Host, result.Clearance, result.ExpiresAtUtc, tag);
            if (!result.Host.Equals(host, StringComparison.Ordinal))
            {
                _store.Set(host, result.Clearance, result.ExpiresAtUtc, tag);
            }
        }

        return result;
    }

    private static TurnstileSolveResult ManualResult(string host, string error)
    {
        return new TurnstileSolveResult
        {
            Success              = false,
            Host                 = host,
            ManualActionRequired = true,
            Error                = error
        };
    }

    private void Record(string operation, long elapsedMs, bool failed)
    {
        try
        {
            _blame?.RecordOperation(operation, elapsedMs, failed);
        }
        catch
        {
            // ignored
        }
    }
}
