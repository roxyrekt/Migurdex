using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Configuration;
using Migurdex.Core.Services.Turnstile;
using Spectre.Console;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public sealed class SolveCommandOptions
{
    public string? PageUrl       { get; set; }
    public string? Manual        { get; set; }
    public bool    Yes           { get; set; }
    public bool    NoDownload    { get; set; }
    public bool    ForceDownload { get; set; }
    public double? TimeoutSecs   { get; set; }
    public bool    Json          { get; set; }
    public bool    Verbose       { get; set; }
    public bool    Visible       { get; set; }
    public bool    NoAutoClick   { get; set; }
    public bool    ShowHelp      { get; set; }
}

public static class SolveCommand
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true
    };

    public static bool TryParse(string[] args, out SolveCommandOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = new SolveCommandOptions();
        error   = null;

        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--help" or "-h")
            {
                options.ShowHelp = true;
            }
            else if (a.Equals("--manual", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    error = "--manual için cf_clearance değeri gerekli.";
                    return false;
                }

                options.Manual = args[i];
            }
            else if (a.Equals("--yes", StringComparison.OrdinalIgnoreCase) || a.Equals("-y", StringComparison.Ordinal))
            {
                options.Yes = true;
            }
            else if (a.Equals("--no-download", StringComparison.OrdinalIgnoreCase))
            {
                options.NoDownload = true;
            }
            else if (a.Equals("--force-download", StringComparison.OrdinalIgnoreCase))
            {
                options.ForceDownload = true;
            }
            else if (a.Equals("--timeout", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length
                    || !double.TryParse(args[i],
                                        System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        out var secs)
                    || double.IsNaN(secs)
                    || secs is <= 0 or > 300)
                {
                    error = "--timeout için 1-300 arası saniye gerekli.";
                    return false;
                }

                options.TimeoutSecs = secs;
            }
            else if (a.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                options.Json = true;
            }
            else if (a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)
                     || a.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                options.Verbose = true;
            }
            else if (a.Equals("--visible", StringComparison.OrdinalIgnoreCase))
            {
                options.Visible = true;
            }
            else if (a.Equals("--no-autoclick", StringComparison.OrdinalIgnoreCase))
            {
                options.NoAutoClick = true;
            }
            else if (a.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Bilinmeyen bayrak: {a}";
                return false;
            }
            else
            {
                positionals.Add(a);
            }
        }

        if (positionals.Count > 0)
        {
            options.PageUrl = string.Join(" ", positionals);
        }

        return true;
    }

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        if (!TryParse(args, out var opts, out var error))
        {
            await Console.Error.WriteLineAsync($"Hata: {error}");
            return 2;
        }

        if (opts.ShowHelp || string.IsNullOrWhiteSpace(opts.PageUrl))
        {
            PrintHelp();
            return opts.ShowHelp ? 0 : 2;
        }

        if (!Uri.TryCreate(opts.PageUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            await Console.Error.WriteLineAsync("Hata: geçerli bir http(s) URL gerekli.");
            return 2;
        }

        var solver       = services.GetRequiredService<TurnstileService>();
        var turnstileCfg = services.GetRequiredService<IConfigurationService>().Config.Turnstile;
        if (!turnstileCfg.Enabled)
        {
            await Console.Error.WriteLineAsync("Hata: Turnstile çözücü ayarlarda kapalı.");
            return 2;
        }

        if (opts.Verbose)
        {
            var stderr = services.GetService<StderrLoggerProvider>();
            if (stderr is not null)
            {
                stderr.Verbose = true;
            }

            PrintDiagnostics();
        }

        if (!string.IsNullOrWhiteSpace(opts.Manual))
        {
            var stored = solver.StoreManual(uri.AbsoluteUri, opts.Manual);
            return Report(stored, opts.Json, 0);
        }

        var timeoutSecs = opts.TimeoutSecs
                          ?? (turnstileCfg.TimeoutSeconds is > 0 and <= 300 ? turnstileCfg.TimeoutSeconds : 45);
        var timeout = TimeSpan.FromSeconds(timeoutSecs);
        _lastPercent = -1;

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = null;
        cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var result = await solver.SolveAsync(uri.AbsoluteUri,
                                                 confirm => ConfirmDownloadAsync(confirm, opts, cts.Token),
                                                 allowDownload: !opts.NoDownload
                                                                && (turnstileCfg.AllowDownload || opts.ForceDownload),
                                                 timeout: timeout,
                                                 downloadProgress: new Progress<double>(p => ReportProgress(p, opts.Json)),
                                                 visible: opts.Visible,
                                                 autoClick: !opts.NoAutoClick,
                                                 forceDownload: opts.ForceDownload,
                                                 cancellationToken: cts.Token)
                                    .ConfigureAwait(false);

            if (result.Success)
            {
                return Report(result, opts.Json, 0);
            }

            await Console.Error.WriteLineAsync($"Çözülemedi: {result.Error}");
            if (result.ManualActionRequired)
            {
                await Console.Error.WriteLineAsync(
                    $"İpucu: tarayıcıda {result.Host} adresindeki doğrulamayı geçip `migurdex solve {result.Host} --manual <cf_clearance>` ile yapıştırın.");
            }

            return Report(result, opts.Json, 1);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static Task<bool> ConfirmDownloadAsync(string message, SolveCommandOptions opts, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        if (opts.Yes)
        {
            Console.Error.WriteLine($"{message} [--yes ile onaylandı]");
            return Task.FromResult(true);
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"{message} (girdi yönlendirilmiş, indirme atlandı; --yes ile onayla)");
            return Task.FromResult(false);
        }

        try
        {
            return Task.FromResult(AnsiConsole.Confirm($"{Markup.Escape(message)}"));
        }
        catch
        {
            Console.Error.Write($"{message} [e/H]: ");
            var answer = Console.ReadLine();
            return Task.FromResult(answer?.Trim().Equals("e", StringComparison.OrdinalIgnoreCase) == true
                                   || answer?.Trim().Equals("evet", StringComparison.OrdinalIgnoreCase) == true
                                   || answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    private static int _lastPercent = -1;

    private static void ReportProgress(double fraction, bool json)
    {
        if (json)
        {
            return;
        }

        var pct = Math.Clamp((int) (fraction * 100), 0, 100);
        if (pct == _lastPercent)
        {
            return;
        }

        _lastPercent = pct;
        Console.Error.Write($"\rİndiriliyor: %{pct} ");
        if (pct >= 100)
        {
            Console.Error.WriteLine();
        }
    }

    private static int Report(Migurdex.Shared.Models.TurnstileSolveResult result, bool json, int code)
    {
        if (result.NoChallenge)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                                                           {
                                                               success     = false,
                                                               noChallenge = true,
                                                               host        = result.Host,
                                                               error       = result.Error
                                                           },
                                                           _jsonOpts));
                return 0;
            }

            Console.WriteLine($"{result.Host}: challenge yok, kayıt gerekmedi.");
            return 0;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
                                                       {
                                                           success   = result.Success,
                                                           host      = result.Host,
                                                           source    = result.Source.ToString(),
                                                           browser   = result.BrowserName,
                                                           expiresAt = result.ExpiresAtUtc,
                                                           clearance = result.Clearance,
                                                           error     = result.Error
                                                       },
                                                       _jsonOpts));
            return code;
        }

        if (result.Success)
        {
            var via = result.Source switch
            {
                Migurdex.Shared.Models.TurnstileSolverSource.Cache => "önbellek",
                Migurdex.Shared.Models.TurnstileSolverSource.UserBrowser =>
                    $"sistem tarayıcısı ({result.BrowserName ?? "?"})",
                Migurdex.Shared.Models.TurnstileSolverSource.DownloadedBrowser =>
                    $"indirilen Chromium ({result.BrowserName ?? "?"})",
                _ => "manuel",
            };
            Console.WriteLine($"{result.Host}: cf_clearance kaydedildi ({via}, geçerlilik: {result.ExpiresAtUtc:u}).");
        }

        return code;
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            "Kullanım: migurdex solve <sayfa-url> [--manual <cf_clearance>] [--yes] [--no-download] [--force-download] [--timeout <sn>] [--json] [--verbose] [--visible] [--no-autoclick]");
        Console.WriteLine("  Sistem tarayıcısıyla Cloudflare Turnstile çözer, cf_clearance'i saklar.");
        Console.WriteLine("  Tarayıcı yoksa onaylı şekilde Chromium indirir (~200MB, tek seferlik).");
        Console.WriteLine("  --manual <değer>  tarayıcı açmadan cf_clearance değerini doğrudan saklar.");
        Console.WriteLine("  --timeout <sn>    çözüm bekleme süresi (1-300, öntanımlı 45). İndirme süresini kapsamaz.");
        Console.WriteLine("  --force-download  sistem tarayıcılarını atlar, paketli Chromium'u indirip kullanır.");
        Console.WriteLine("  --no-download     Chromium indirmeyi yasaklar (uygun tarayıcı yoksa manuel moda düşer).");
        Console.WriteLine("  --no-autoclick    kutucuğa otomatik tıklamaz, yalnızca clearance yoklar.");
        Console.WriteLine("  --json            sonucu stdout'a JSON yazar.");
        Console.WriteLine("  --verbose         tarayıcı taraması ve çözüm adımlarını stderr'e yazar.");
        Console.WriteLine("  --visible         tarayıcıyı görünür açar (kutucuk çıkarsa elle tıklayabilirsin).");
    }

    private static void PrintDiagnostics()
    {
        var browsers = BrowserDetector.Detect();
        Console.Error.WriteLine($"[diag] platform: {ChromiumDownloader.GetPlatformSlug() ?? "desteklenmiyor"}");
        Console.Error.WriteLine($"[diag] pinli chromium: {ChromiumDownloader.PinnedVersion}");
        Console.Error.WriteLine($"[diag] tarayıcı önbelleği: {TurnstilePaths.BrowserCacheRoot()}");
        Console.Error.WriteLine($"[diag] profil kökü: {TurnstilePaths.DataRoot()}");
        if (browsers.Count == 0)
        {
            Console.Error.WriteLine("[diag] uyumlu tarayıcı bulunamadı. Bakılanlar:");
            foreach (var loc in BrowserDetector.DescribeSearch().Take(25))
            {
                Console.Error.WriteLine($"[diag]   - {loc}");
            }
        }
        else
        {
            foreach (var b in browsers)
            {
                Console.Error.WriteLine($"[diag] bulundu: {b.Name} ({b.Kind}) -> {b.ExecutablePath}");
            }
        }
    }
}
