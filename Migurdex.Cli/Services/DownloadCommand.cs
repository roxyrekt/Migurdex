using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Migurdex.Cli.Services;

public sealed class DownloadCommandOptions
{
    public string?              Query             { get; set; }
    public string?              Provider          { get; set; }
    public string?              Group             { get; set; }
    public string?              Hoster            { get; set; }
    public string?              Quality           { get; set; }
    public string?              OutputDirectory   { get; set; }
    public double?              Episode           { get; set; }
    public List<double>?        Episodes          { get; set; }
    public bool                 SelectAllEpisodes { get; set; }
    public int?                 Season            { get; set; }
    public DownloadSourceFormat Format            { get; set; } = DownloadSourceFormat.Auto;
    public bool?                Subtitles         { get; set; }
    public bool                 Force             { get; set; }
    public bool                 NoResume          { get; set; }
    public bool                 Debug             { get; set; }
    public bool                 Json              { get; set; }
    public bool                 ShowHelp          { get; set; }
}

public static class DownloadCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static bool TryParse(string[] args, out DownloadCommandOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = new DownloadCommandOptions();
        error   = null;

        var positionals = new List<string>();
        var subsSeen    = false;
        var noSubsSeen  = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (Is(argument, "--help", "-h"))
            {
                options.ShowHelp = true;
            }
            else if (Is(argument, "-e", "--episode"))
            {
                if (!TryReadValue(args, ref index, argument, out var value, out error)
                    || !EpisodeRangeParser.TryParse(value, out var episodes, out var selectAll))
                {
                    error ??= $"{argument} için geçerli bir bölüm numarası gerekli (örn. 12, 1-12, 1,3,5, all).";
                    return false;
                }

                if (selectAll)
                {
                    options.SelectAllEpisodes = true;
                    options.Episodes          = null;
                    options.Episode           = null;
                }
                else
                {
                    options.Episodes = episodes;
                    options.Episode  = episodes[0];
                }
            }
            else if (Is(argument, "-s", "--season"))
            {
                if (!TryReadValue(args, ref index, argument, out var value, out error)
                    || !int.TryParse(value,
                                     NumberStyles.Integer,
                                     CultureInfo.InvariantCulture,
                                     out var season)
                    || season < 1)
                {
                    error ??= $"{argument} için geçerli bir sezon numarası gerekli.";
                    return false;
                }

                options.Season = season;
            }
            else if (Is(argument, "-p", "--provider"))
            {
                if (!TryReadValue(args, ref index, argument, out var value, out error))
                {
                    return false;
                }

                options.Provider = value;
            }
            else if (Is(argument, "-g", "--group"))
            {
                if (!TryReadValue(args, ref index, argument, out var value, out error))
                {
                    return false;
                }

                options.Group = value;
            }
            else if (Is(argument, "--hoster"))
            {
                if (!TryReadValue(args, ref index, argument, out var value, out error))
                {
                    return false;
                }

                options.Hoster = value;
            }
            else if (Is(argument, "-q", "--quality"))
            {
                if (!TryReadValue(args, ref index, argument, out var value, out error))
                {
                    return false;
                }

                options.Quality = value;
            }
            else if (Is(argument, "--all"))
            {
                options.SelectAllEpisodes = true;
                options.Episodes          = null;
                options.Episode           = null;
            }
            else if (Is(argument, "-o", "--output", "--output-directory"))
            {
                if (!TryReadValue(args, ref index, argument, out var value, out error))
                {
                    return false;
                }

                options.OutputDirectory = value;
            }
            else if (Is(argument, "--format"))
            {
                if (!TryReadValue(args, ref index, argument, out var value, out error)
                    || !TryParseFormat(value, out var format))
                {
                    error ??= "--format için auto, mp4 veya hls gerekli.";
                    return false;
                }

                options.Format = format;
            }
            else if (argument.StartsWith("--format=", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseFormat(argument["--format=".Length..], out var format))
                {
                    error = "--format için auto, mp4 veya hls gerekli.";
                    return false;
                }

                options.Format = format;
            }
            else if (Is(argument, "--subs"))
            {
                options.Subtitles = true;
                subsSeen          = true;
            }
            else if (Is(argument, "--no-subs"))
            {
                options.Subtitles = false;
                noSubsSeen        = true;
            }
            else if (Is(argument, "--force"))
            {
                options.Force = true;
            }
            else if (Is(argument, "--no-resume"))
            {
                options.NoResume = true;
            }
            else if (Is(argument, "--debug"))
            {
                options.Debug = true;
            }
            else if (Is(argument, "--json"))
            {
                options.Json = true;
            }
            else if (argument.StartsWith('-'))
            {
                error = $"Bilinmeyen bayrak: {argument}";
                return false;
            }
            else
            {
                positionals.Add(argument);
            }
        }

        if (subsSeen && noSubsSeen)
        {
            error = "--subs ve --no-subs birlikte kullanılamaz.";
            return false;
        }

        if (positionals.Count > 0)
        {
            options.Query = string.Join(" ", positionals);
        }

        if (!options.ShowHelp && string.IsNullOrWhiteSpace(options.Query))
        {
            error = "Arama sorgusu gerekli.";
            return false;
        }

        return true;
    }

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!TryParse(args, out var options, out var error))
        {
            return UsageError(error!);
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        using var cancellation    = new CancellationTokenSource();
        var       cancelRequested = 0;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            if (Interlocked.Exchange(ref cancelRequested, 1) == 0)
            {
                eventArgs.Cancel = true;
                Console.Error.WriteLine("İndirme iptal ediliyor...");
                cancellation.Cancel();
            }
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var result = await ExecuteAsync(options, services, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return await FailAsync(options, "İndirme iptal edildi.");
        }
        catch (Exception)
        {
            return await FailAsync(options, "İndirme tamamlanamadı.");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> ExecuteAsync(DownloadCommandOptions options,
        IServiceProvider                                               services,
        CancellationToken                                              cancellationToken)
    {
        var config = services.GetRequiredService<IConfigurationService>().Config;
        var api    = services.GetRequiredService<IApiClientService>();
        if (!await EnsureApiOnlineAsync(api, cancellationToken))
        {
            return await FailAsync(options, "API bağlantısı kurulamadı. Ayarlardaki API adresini kontrol edin.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string? provider = null;
        if (!string.IsNullOrWhiteSpace(options.Provider))
        {
            var providersResult = await api.GetProvidersAsync(cancellationToken);
            if (!providersResult.IsSuccess)
            {
                return await FailAsync(options, providersResult.Error ?? "Sağlayıcı listesi alınamadı.");
            }

            if (!MediaSelection.TryResolveProvider(providersResult.Data,
                                                   options.Provider,
                                                   out provider,
                                                   out var providerError))
            {
                return await FailAsync(options, providerError!);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        var search = await api.SearchAnimeAsync(options.Query!, provider, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!search.IsSuccess)
        {
            return await FailAsync(options, search.Error ?? "Arama yapılamadı.");
        }

        var picked = MediaSelection.PickSearchResult(search.Data, options.Query!);
        if (picked is null)
        {
            return await FailAsync(options, "Sonuç bulunamadı.");
        }

        var detailsResult = await api.GetAnimeDetailsAsync(picked.ProviderName,
                                                           picked.Id,
                                                           cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!detailsResult.IsSuccess || detailsResult.Data is null)
        {
            return await FailAsync(options,
                                   detailsResult.Error ?? "Anime detayları alınamadı.");
        }

        var details           = detailsResult.Data;
        var effectiveEpisodes = options.Episodes;
        if (options.SelectAllEpisodes)
        {
            effectiveEpisodes = details.Episodes
                                       .Where(ep => !options.Season.HasValue
                                                    || (ep.Season ?? 1) == options.Season.Value)
                                       .OrderBy(ep => ep.Season ?? 1)
                                       .ThenBy(ep => ep.Number)
                                       .Select(ep => ep.Number)
                                       .Distinct()
                                       .ToList();
            if (effectiveEpisodes.Count == 0)
            {
                return await FailAsync(options, "Bölüm bulunamadı.");
            }

            options.Episodes = effectiveEpisodes;
            options.Episode  = effectiveEpisodes[0];
        }

        if (effectiveEpisodes is { Count: > 1 })
        {
            return await ExecuteBatchAsync(options, services, api, config, picked, details, cancellationToken);
        }

        var episode = MediaSelection.PickEpisode(details, options.Season, options.Episode);
        if (episode is null)
        {
            return await FailAsync(options, "Bölüm bulunamadı.");
        }

        string? group = null;
        if (!string.IsNullOrWhiteSpace(options.Group))
        {
            var groupsResult = await api.GetEpisodeGroupsAsync(picked.ProviderName,
                                                               episode.Id,
                                                               cancellationToken);
            if (!groupsResult.IsSuccess)
            {
                return await FailAsync(options, groupsResult.Error ?? "Fansub grupları alınamadı.");
            }

            if (!MediaSelection.TryResolveGroup(groupsResult.Data, options.Group, out group))
            {
                return await FailAsync(options,
                                       $"'{options.Group}' grubu bulunamadı ({string.Join(", ", groupsResult.Data)}).");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        var resolved = await DownloadCandidateResolver.ResolveAsync(api,
                                                                    picked.ProviderName,
                                                                    episode.Id,
                                                                    group,
                                                                    options.Format,
                                                                    config,
                                                                    config.DownloadAutoSelectTimeoutSeconds,
                                                                    cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (resolved.TimedOut)
        {
            return await FailAsync(options,
                                   "Kaynak taraması zaman aşımına uğradı. config dosyasındaki DownloadAutoSelectTimeoutSeconds değeri yükseltilebilir.");
        }

        if (resolved.Error is not null)
        {
            return await FailAsync(options, resolved.Error);
        }

        var directCount = resolved.Sources.Count(DownloadSourceResolver.IsDirectDownloadable);
        if (directCount == 0)
        {
            return await FailAsync(options, "API'den indirilebilir doğrudan MP4/HLS kaynağı bulunamadı.");
        }

        var candidates = ApplyCliFilters(resolved.Candidates, options);
        if (candidates.Count == 0)
        {
            return await FailAsync(options,
                                   $"'{FormatLabel(options.Format)}' biçiminde uygun kaynak bulunamadı.");
        }

        if (options.Debug)
        {
            WriteDebugSummary(picked, details, episode, candidates);
            if (options.Json)
            {
                WriteJson(new
                {
                    success        = true,
                    dryRun         = true,
                    provider       = picked.ProviderName,
                    animeTitle     = details.Title,
                    episode        = DebugEpisodeSummary(episode),
                    source         = SourceSummary(candidates[0]),
                    candidateCount = candidates.Count
                });
            }
            else
            {
                Console.WriteLine("Kaynak çözüldü; --debug nedeniyle indirme başlatılmadı.");
            }

            return 0;
        }

        var downloadService = services.GetRequiredService<IDownloadService>();
        var outputDirectory = string.IsNullOrWhiteSpace(options.OutputDirectory)
                                  ? config.DownloadDirectory
                                  : options.OutputDirectory;
        var     downloadSubtitles = options.Subtitles ?? config.DownloadSubtitles;
        var     overwrite         = options.Force || config.DownloadOverwrite;
        var     resume            = config.DownloadResume && !options.NoResume;
        var     connections       = Math.Clamp(config.ConnectionsPerFile, 1, 8);
        string? lastError         = null;

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = candidates[index];
            await Console.Error.WriteLineAsync(
                             $"Kaynak {index + 1}/{candidates.Count} deneniyor: "
                             + DescribeSource(source))
                         .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var progress = new ConsoleDownloadProgress();
            var result = await downloadService.DownloadAsync(
                                                  new DownloadRequest
                                                  {
                                                      Source             = source,
                                                      OutputDirectory    = outputDirectory,
                                                      AnimeTitle         = details.Title,
                                                      EpisodeTitle       = episode.Title,
                                                      Season             = episode.Season ?? 1,
                                                      EpisodeNumber      = episode.Number,
                                                      Overwrite          = overwrite,
                                                      Resume             = resume,
                                                      DownloadSubtitles  = downloadSubtitles,
                                                      ConnectionsPerFile = connections,
                                                      Progress           = progress
                                                  },
                                                  cancellationToken)
                                              .ConfigureAwait(false);
            if (!result.IsCancelled || string.IsNullOrWhiteSpace(result.MediaPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (result.IsCancelled && !string.IsNullOrWhiteSpace(result.MediaPath))
            {
                if (options.Json)
                {
                    WriteJson(new
                    {
                        success       = true,
                        cancelled     = true,
                        mediaPath     = result.MediaPath,
                        subtitlePaths = result.SubtitlePaths,
                        warnings      = result.Warnings
                    });
                }
                else
                {
                    Console.WriteLine($"Video tamamlandı: {result.MediaPath}");
                    foreach (var warning in result.Warnings)
                    {
                        Console.Error.WriteLine($"Uyarı: {warning}");
                    }
                }

                return 3;
            }

            if (result.IsCancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (result.Success)
            {
                if (options.Json)
                {
                    WriteJson(new
                    {
                        success       = true,
                        dryRun        = false,
                        provider      = picked.ProviderName,
                        animeId       = picked.Id,
                        animeTitle    = details.Title,
                        episode       = EpisodeSummary(episode),
                        source        = SourceSummary(source),
                        mediaPath     = result.MediaPath,
                        subtitlePaths = result.SubtitlePaths,
                        warnings      = result.Warnings
                    });
                }
                else
                {
                    Console.WriteLine($"İndirildi: {result.MediaPath}");
                    foreach (var subtitlePath in result.SubtitlePaths)
                    {
                        Console.WriteLine($"Altyazı: {subtitlePath}");
                    }
                }

                foreach (var warning in result.Warnings)
                {
                    await Console.Error.WriteLineAsync($"Uyarı: {warning}").ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return 0;
            }

            lastError = SanitizeFailure(result.Error, source);
            await Console.Error.WriteLineAsync(
                             $"Kaynak denenemedi: {lastError}"
                             + (index + 1 < candidates.Count ? "; sıradaki kaynak denenecek." : "."))
                         .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await FailAsync(options, lastError ?? "Video indirilemedi.");
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Kullanım:");
        Console.WriteLine(
            "  migurdex download <sorgu> [-e <n>] [-s <n>] [-p <sağlayıcı>] [-g <grup>] [--hoster <ad>] [-q <kalite>] [-o <dizin>]");
        Console.WriteLine(
            "                     [--format auto|mp4|hls] [--subs|--no-subs] [--force] [--no-resume] [--debug] [--json] [--all]");
        Console.WriteLine();
        Console.WriteLine(
            "  -e, --episode <n>       Bölüm: 12, 1-12, 1..5, 1,3,5 veya all. Verilmezse deterministik ilk bölüm.");
        Console.WriteLine("  -s, --season <n>        Sezon filtresi.");
        Console.WriteLine("  -p, --provider <ad>     Sağlayıcıyı doğrular ve aramayı sınırlar.");
        Console.WriteLine("  -g, --group <ad>        Fansub grubunu doğrular.");
        Console.WriteLine("      --hoster <ad>       Hoster filtresi (örn. Tau Video).");
        Console.WriteLine("  -q, --quality <ad>      Kalite filtresi (örn. 1080p).");
        Console.WriteLine("      --all               Tüm bölümleri indir (sezon filtresiyle birlikte kullanılabilir).");
        Console.WriteLine("  -o, --output <dizin>    Çıktı kök dizini.");
        Console.WriteLine("      --format <tür>      auto (varsayılan), mp4 veya hls.");
        Console.WriteLine("      --subs/--no-subs    Altyazı indirmeyi aç/kapat (config varsayılanı: açık).");
        Console.WriteLine("      --force             Var olan hedefi değiştir (config varsayılanı: kapalı).");
        Console.WriteLine("      --no-resume         Kısmi MP4 dosyasından devam etme.");
        Console.WriteLine("      --debug             İndirmeden güvenli kaynak özetini gösterir.");
        Console.WriteLine("      --json              Yalnız makine okunur sonucu stdout'a yazar.");
        Console.WriteLine("  Çıkış kodları: 0 başarı, 1 çalışma hatası, 2 kullanım hatası, 3 altyazı iptali.");
    }

    private static async Task<bool> EnsureApiOnlineAsync(IApiClientService api, CancellationToken cancellationToken)
    {
        if (await api.IsApiOnlineAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        await Console.Error.WriteLineAsync("API başlatılıyor...").ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var started = await api.TryStartApiDaemonAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return started;
    }

    private static async Task<int> FailAsync(DownloadCommandOptions options, string error)
    {
        if (options.Json)
        {
            WriteJson(new
            {
                success = false,
                error
            });
        }
        else
        {
            await Console.Error.WriteLineAsync($"Hata: {error}").ConfigureAwait(false);
        }

        return 1;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"Hata: {message}");
        Console.Error.WriteLine(
            "Kullanım: migurdex download \"<sorgu>\" [-e <n>] [seçenekler] --help");
        return 2;
    }

    private static List<VideoSource> ApplyCliFilters(IReadOnlyList<VideoSource> candidates,
        DownloadCommandOptions                                                  options)
    {
        var filtered = candidates.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(options.Hoster))
        {
            filtered = filtered.Where(source => source.Hoster is not null
                                                && source.Hoster.Contains(
                                                    options.Hoster,
                                                    StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(options.Quality))
        {
            filtered = filtered.Where(source => source.Quality is not null
                                                && source.Quality.Contains(
                                                    options.Quality,
                                                    StringComparison.OrdinalIgnoreCase));
        }

        return filtered.ToList();
    }

    private static async Task<int> ExecuteBatchAsync(DownloadCommandOptions options,
        IServiceProvider                                                    services,
        IApiClientService                                                   api,
        CliConfig                                                           config,
        SearchResult                                                        picked,
        AnimeDetails                                                        details,
        CancellationToken                                                   ct)
    {
        var downloadService = services.GetRequiredService<IDownloadService>();
        var outputDirectory = string.IsNullOrWhiteSpace(options.OutputDirectory)
                                  ? config.DownloadDirectory
                                  : options.OutputDirectory;
        var downloadSubtitles = options.Subtitles ?? config.DownloadSubtitles;
        var overwrite         = options.Force || config.DownloadOverwrite;
        var resume            = config.DownloadResume && !options.NoResume;
        var connections       = Math.Clamp(config.ConnectionsPerFile, 1, 8);
        var maxEpisodes       = Math.Clamp(config.MaxConcurrentEpisodes, 1, 5);
        var maxSockets        = Math.Clamp(config.MaxSocketsPerHoster, 1, 32);
        var perHosterCap      = Math.Max(1, maxSockets / Math.Max(1, connections));
        var results           = new List<object>();
        var errors            = new List<object>();
        var failed            = 0;
        var gate              = new Lock();
        var episodeGate       = new SemaphoreSlim(maxEpisodes, maxEpisodes);
        var hosterGates       = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        SemaphoreSlim HosterGate(string hoster)
        {
            lock (gate)
            {
                if (!hosterGates.TryGetValue(hoster, out var semaphore))
                {
                    semaphore           = new SemaphoreSlim(perHosterCap, perHosterCap);
                    hosterGates[hoster] = semaphore;
                }

                return semaphore;
            }
        }

        void AddResult(object value)
        {
            lock (gate)
            {
                results.Add(value);
            }
        }

        void AddError(double episodeNumber, string message)
        {
            lock (gate)
            {
                failed++;
                errors.Add(new
                {
                    episode = episodeNumber,
                    error   = message
                });
            }
        }

        async Task DownloadOneAsync(double epNum)
        {
            ct.ThrowIfCancellationRequested();
            var episode = MediaSelection.PickEpisode(details, options.Season, epNum);
            if (episode is null)
            {
                AddError(epNum, "Bölüm bulunamadı.");
                lock (gate)
                {
                    Console.Error.WriteLine($"Bölüm {epNum} bulunamadı.");
                }

                return;
            }

            string? group = null;
            if (!string.IsNullOrWhiteSpace(options.Group))
            {
                var gr = await api.GetEpisodeGroupsAsync(picked.ProviderName, episode.Id, ct).ConfigureAwait(false);
                if (!gr.IsSuccess || !MediaSelection.TryResolveGroup(gr.Data, options.Group, out group))
                {
                    var msg = gr.IsSuccess
                                  ? $"Grup bulunamadı: {options.Group}."
                                  : gr.Error ?? "Fansub grupları alınamadı.";
                    AddError(epNum, msg);
                    lock (gate)
                    {
                        Console.Error.WriteLine($"Bölüm {epNum}: {msg}");
                    }

                    return;
                }
            }

            var epTag      = $"[B{episode.Number.ToString("0.##", CultureInfo.InvariantCulture)}]";
            var maxRetries = Math.Clamp(config.DownloadMaxRetries, 1, 10);
            var baseDelay  = TimeSpan.FromSeconds(Math.Clamp(config.DownloadRetryBaseDelaySeconds, 0, 60));
            try
            {
                for (var attempt = 1;; attempt++)
                {
                    try
                    {
                        var resolved = await DownloadCandidateResolver.ResolveAsync(api,
                                                                          picked.ProviderName,
                                                                          episode.Id,
                                                                          group,
                                                                          options.Format,
                                                                          config,
                                                                          config.DownloadAutoSelectTimeoutSeconds,
                                                                          ct)
                                                                      .ConfigureAwait(false);
                        if (resolved.TimedOut)
                        {
                            throw new DownloadException(
                                $"Kaynak taraması zaman aşımına uğradı ({config.DownloadAutoSelectTimeoutSeconds:F1} sn).");
                        }

                        var candidates = resolved.Error is null ? ApplyCliFilters(resolved.Candidates, options) : [];
                        if (resolved.Error is not null)
                        {
                            throw new DownloadException(resolved.Error);
                        }

                        if (candidates.Count == 0)
                        {
                            throw new DownloadException("Uygun kaynak bulunamadı.");
                        }

                        var hosterKey = candidates[0].Hoster ?? "unknown";
                        await episodeGate.WaitAsync(ct).ConfigureAwait(false);
                        await HosterGate(hosterKey).WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            string?      lastError    = null;
                            VideoSource? failedSource = null;
                            for (var index = 0; index < candidates.Count; index++)
                            {
                                ct.ThrowIfCancellationRequested();
                                var source = candidates[index];
                                if (candidates.Count > 1)
                                {
                                    lock (gate)
                                    {
                                        Console.Error.WriteLine(
                                            $"{epTag} Kaynak {index + 1}/{candidates.Count} deneniyor: {DescribeSource(source)}");
                                    }
                                }

                                var progress = new ConsoleDownloadProgress(epTag);
                                var r = await downloadService.DownloadAsync(new DownloadRequest
                                                                            {
                                                                                Source          = source,
                                                                                OutputDirectory = outputDirectory,
                                                                                AnimeTitle      = details.Title,
                                                                                EpisodeTitle    = episode.Title,
                                                                                Season =
                                                                                    episode.Season ?? 1,
                                                                                EpisodeNumber      = episode.Number,
                                                                                Overwrite          = overwrite,
                                                                                Resume             = resume,
                                                                                DownloadSubtitles  = downloadSubtitles,
                                                                                ConnectionsPerFile = connections,
                                                                                Progress           = progress
                                                                            },
                                                                            ct)
                                                             .ConfigureAwait(false);
                                if (r.Success)
                                {
                                    AddResult(new
                                    {
                                        episode       = episode.Number,
                                        mediaPath     = r.MediaPath,
                                        subtitlePaths = r.SubtitlePaths,
                                        warnings      = r.Warnings
                                    });
                                    lock (gate)
                                    {
                                        if (options.Json)
                                        {
                                            Console.Error.WriteLine($"İndirildi ({episode.Number}): {r.MediaPath}");
                                        }
                                        else
                                        {
                                            Console.Out.WriteLine($"İndirildi ({episode.Number}): {r.MediaPath}");
                                        }
                                    }

                                    return;
                                }

                                lastError    = r.Error;
                                failedSource = source;
                                if (index + 1 < candidates.Count)
                                {
                                    lock (gate)
                                    {
                                        Console.Error.WriteLine(
                                            $"{epTag} Kaynak denenemedi: {SanitizeFailure(lastError, failedSource)}; sıradaki deneniyor.");
                                    }
                                }
                            }

                            throw new DownloadException(failedSource is not null
                                                            ? SanitizeFailure(lastError, failedSource)
                                                            : lastError ?? "Video indirilemedi.");
                        }
                        finally
                        {
                            HosterGate(hosterKey).Release();
                            episodeGate.Release();
                        }
                    }
                    catch (DownloadException ex) when (attempt < maxRetries
                                                       && DownloadRetryPolicy.IsTransient(ex.Message))
                    {
                        lock (gate)
                        {
                            Console.Error.WriteLine(
                                $"{epTag} Tekrar deneniyor ({attempt + 1}/{maxRetries}): {ex.Message}");
                        }

                        await Task.Delay(DownloadRetryPolicy.BackoffDelay(attempt, baseDelay), ct)
                                  .ConfigureAwait(false);
                    }
                }
            }
            catch (DownloadException ex)
            {
                AddError(epNum, ex.Message);
                lock (gate)
                {
                    Console.Error.WriteLine($"Bölüm {epNum}: {ex.Message}");
                }
            }
        }

        if (options.Debug)
        {
            if (options.Json)
            {
                WriteJson(new
                {
                    success    = true,
                    dryRun     = true,
                    provider   = picked.ProviderName,
                    animeTitle = details.Title,
                    episodes   = options.Episodes
                });
            }
            else
            {
                foreach (var epNum in options.Episodes!)
                {
                    Console.WriteLine($"Bölüm {epNum} (dry-run, kaynak aranmadı)");
                }

                Console.WriteLine("--debug nedeniyle indirme başlatılmadı (batch modda kaynak çözülmez).");
            }

            return 0;
        }

        var episodeNumbers = options.Episodes!.ToList();
        var tasks          = episodeNumbers.Select(DownloadOneAsync).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        if (options.Json)
        {
            WriteJson(new
            {
                success   = failed == 0,
                completed = results.Count,
                failed,
                results,
                errors
            });
        }

        return failed == 0 ? 0 : 1;
    }

    private static bool TryReadValue(string[] args,
        ref int                               index,
        string                                option,
        out string                            value,
        out string?                           error)
    {
        value = string.Empty;
        error = null;
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            error = $"{option} için değer gerekli.";
            return false;
        }

        index++;
        value = args[index].Trim();
        return true;
    }

    private static bool TryParseFormat(string value, out DownloadSourceFormat format)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            format = DownloadSourceFormat.Auto;
            return true;
        }

        if (value.Equals("mp4", StringComparison.OrdinalIgnoreCase))
        {
            format = DownloadSourceFormat.Mp4;
            return true;
        }

        if (value.Equals("hls", StringComparison.OrdinalIgnoreCase)
            || value.Equals("m3u8", StringComparison.OrdinalIgnoreCase))
        {
            format = DownloadSourceFormat.Hls;
            return true;
        }

        format = DownloadSourceFormat.Auto;
        return false;
    }

    private static bool Is(string value, params string[] names)
    {
        return names.Any(name => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatLabel(DownloadSourceFormat format)
    {
        return format switch
        {
            DownloadSourceFormat.Mp4 => "mp4",
            DownloadSourceFormat.Hls => "hls",
            _                        => "auto"
        };
    }

    private static string DescribeSource(VideoSource source)
    {
        var parts = new List<string>
        {
            source.Hoster ?? "bilinmiyor",
            source.DisplayLabel,
            source.Type.ToString()
        };
        if (!string.IsNullOrWhiteSpace(source.Group))
        {
            parts.Add(source.Group);
        }

        return string.Join(" / ", parts);
    }

    private static object SourceSummary(VideoSource source)
    {
        return new
        {
            hoster     = source.Hoster,
            quality    = source.Quality,
            bitrate    = source.Bitrate,
            width      = source.Width,
            height     = source.Height,
            videoCodec = source.VideoCodec,
            audioCodec = source.AudioCodec,
            sizeBytes  = source.SizeBytes,
            duration   = source.DurationSeconds,
            format     = source.Type,
            group      = source.Group
        };
    }

    private static object EpisodeSummary(Episode episode)
    {
        return new
        {
            id     = episode.Id,
            title  = episode.Title,
            season = episode.Season ?? 1,
            number = episode.Number
        };
    }

    private static object DebugEpisodeSummary(Episode episode)
    {
        return new
        {
            title  = episode.Title,
            season = episode.Season ?? 1,
            number = episode.Number
        };
    }

    private static void WriteDebugSummary(SearchResult search,
        AnimeDetails                                   details,
        Episode                                        episode,
        IReadOnlyList<VideoSource>                     candidates)
    {
        var source = candidates[0];
        Console.Error.WriteLine($"sağlayıcı: {search.ProviderName}");
        Console.Error.WriteLine($"anime: {details.Title}");
        Console.Error.WriteLine(
            $"bölüm: S{episode.Season ?? 1}E{episode.Number.ToString("0.##", CultureInfo.InvariantCulture)}");
        Console.Error.WriteLine($"grup: {source.Group ?? "bilinmiyor"}");
        Console.Error.WriteLine($"hoster: {source.Hoster ?? "bilinmiyor"}");
        Console.Error.WriteLine($"kalite: {source.DisplayLabel}");
        Console.Error.WriteLine($"tür: {source.Type}");
        Console.Error.WriteLine($"aday sayısı: {candidates.Count}");
    }

    private static string SanitizeFailure(string? error, VideoSource source)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Video indirilemedi.";
        }

        var sanitized = error;
        if (!string.IsNullOrWhiteSpace(source.Url))
        {
            sanitized = sanitized.Replace(source.Url, "[gizli]", StringComparison.Ordinal);
        }

        if (source.Headers is not null)
        {
            foreach (var value in source.Headers.Values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                sanitized = sanitized.Replace(value, "[gizli]", StringComparison.Ordinal);
            }
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "Video indirilemedi." : sanitized;
    }

    private static void WriteJson(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    private sealed class ConsoleDownloadProgress : IProgress<DownloadProgress>
    {
        private readonly string?             _prefix;
        private readonly DownloadSpeedometer _speed = new();
        private          string?             _lastLine;
        private          DateTimeOffset      _lastPrint = DateTimeOffset.MinValue;

        public ConsoleDownloadProgress(string? prefix = null)
        {
            _prefix = prefix;
        }

        public void Report(DownloadProgress value)
        {
            _speed.Sample(value.BytesDownloaded, DateTimeOffset.UtcNow);
            var    stage = DownloadStageLabel.For(value.Stage, value.Track, value.IsAudioTrack);
            var    size  = FormatBytes(value.BytesDownloaded);
            var    tag   = string.IsNullOrWhiteSpace(_prefix) ? string.Empty : $"{_prefix} ";
            string line;
            if (!string.IsNullOrWhiteSpace(value.Track))
            {
                line = $"{tag}{stage}: {value.Track}";
            }
            else
            {
                line = $"{tag}{stage}:";
            }

            if (value is { Stage: DownloadStage.Downloading, FragmentsTotal: > 0, FragmentsDone: not null })
            {
                line += value.Percent is not null
                            ? $" %{value.Percent.Value.ToString("0.#", CultureInfo.InvariantCulture)} (frag {value.FragmentsDone.Value}/{value.FragmentsTotal.Value})"
                            : $" frag {value.FragmentsDone.Value}/{value.FragmentsTotal.Value}";
                var speed = value.SpeedBytesPerSecond ?? _speed.BytesPerSecond;
                if (speed > 0)
                {
                    line += $" • {FormatBytes((long) speed)}/s";
                }

                if (value.Eta is not null)
                {
                    line += $" • {DownloadSpeedometer.FormatEta(value.Eta.Value)}";
                }
            }
            else if (value is { Stage: DownloadStage.Downloading, TotalBytes: > 0 })
            {
                var pct = Math.Min(100, (double) value.BytesDownloaded * 100 / value.TotalBytes.Value);
                line +=
                    $" %{pct.ToString("0.#", CultureInfo.InvariantCulture)} ({size} / {FormatBytes(value.TotalBytes.Value)})";
                var speed = value.SpeedBytesPerSecond ?? _speed.BytesPerSecond;
                if (speed > 0)
                {
                    line += $" • {FormatBytes((long) speed)}/s";
                    var eta = _speed.EstimateRemaining(value.BytesDownloaded,
                                                       value.TotalBytes,
                                                       value.SpeedBytesPerSecond);
                    if (eta is not null)
                    {
                        line += $" • {DownloadSpeedometer.FormatEta(eta.Value)}";
                    }
                }
            }
            else
            {
                if (value.TotalBytes is > 0)
                {
                    size += $" / {FormatBytes(value.TotalBytes.Value)}";
                }

                line += $" {size}";
            }

            var now = DateTimeOffset.UtcNow;
            if (string.Equals(line, _lastLine, StringComparison.Ordinal)
                || (value.Stage == DownloadStage.Downloading
                    && now - _lastPrint < TimeSpan.FromSeconds(1)))
            {
                return;
            }

            _lastLine  = line;
            _lastPrint = now;
            Console.Error.WriteLine(line);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            double   value = Math.Max(0, bytes);
            var      unit  = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0
                       ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[unit]}"
                       : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
        }
    }
}
