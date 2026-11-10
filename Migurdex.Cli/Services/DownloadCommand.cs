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
    public string?              Query           { get; set; }
    public string?              Provider        { get; set; }
    public string?              Group           { get; set; }
    public string?              OutputDirectory { get; set; }
    public double?              Episode         { get; set; }
    public EpisodeSelector?     EpisodeSelector { get; set; }
    public int?                 Season          { get; set; }
    public DownloadSourceFormat Format          { get; set; } = DownloadSourceFormat.Auto;
    public bool?                Subtitles       { get; set; }
    public bool                 Force           { get; set; }
    public bool                 NoResume        { get; set; }
    public bool                 Debug           { get; set; }
    public bool                 Json            { get; set; }
    public bool                 ShowHelp        { get; set; }
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
                    || !MediaSelection.TryParseEpisodeSelector(value, out var selector, out error)
                    || selector is null)
                {
                    error ??= $"{argument} için geçerli bir bölüm numarası veya aralığı gerekli.";
                    return false;
                }

                options.EpisodeSelector = selector;
                options.Episode         = selector.SingleEpisode;
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

        var details = detailsResult.Data;
        if (options.EpisodeSelector is { IsBatch: true })
        {
            var batchEpisodes = MediaSelection.PickEpisodes(details, options.Season, options.EpisodeSelector);
            if (batchEpisodes.Count == 0)
            {
                return await FailAsync(options, "Belirtilen aralıkta bölüm bulunamadı.");
            }

            return await ExecuteBatchAsync(options,
                                           services,
                                           api,
                                           config,
                                           picked,
                                           details,
                                           batchEpisodes,
                                           cancellationToken);
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

        var candidates = resolved.Candidates;
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
                                                      Source            = source,
                                                      OutputDirectory   = outputDirectory,
                                                      AnimeTitle        = details.Title,
                                                      EpisodeTitle      = episode.Title,
                                                      Season            = episode.Season ?? 1,
                                                      EpisodeNumber     = episode.Number,
                                                      Overwrite         = overwrite,
                                                      Resume            = resume,
                                                      DownloadSubtitles = downloadSubtitles,
                                                      Progress          = progress
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

    private static async Task<int> ExecuteBatchAsync(
        DownloadCommandOptions options,
        IServiceProvider       services,
        IApiClientService      api,
        CliConfig              config,
        SearchResult           picked,
        AnimeDetails           details,
        IReadOnlyList<Episode> episodes,
        CancellationToken      cancellationToken)
    {
        var downloadService = services.GetRequiredService<IDownloadService>();
        var outputDirectory = string.IsNullOrWhiteSpace(options.OutputDirectory)
                                  ? config.DownloadDirectory
                                  : options.OutputDirectory;
        var downloadSubtitles = options.Subtitles ?? config.DownloadSubtitles;
        var overwrite         = options.Force || config.DownloadOverwrite;
        var resume            = config.DownloadResume && !options.NoResume;

        var items        = new List<object>(episodes.Count);
        var successCount = 0;
        var failCount    = 0;

        for (var epIdx = 0; epIdx < episodes.Count; epIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episode = episodes[epIdx];
            var epLabel = $"S{episode.Season ?? 1}E{episode.Number.ToString("0.##", CultureInfo.InvariantCulture)}";

            await Console
                  .Error.WriteLineAsync($"[{epIdx + 1}/{episodes.Count}] {details.Title} {epLabel} hazırlanıyor...")
                  .ConfigureAwait(false);

            string? group = null;
            if (!string.IsNullOrWhiteSpace(options.Group))
            {
                var groupsResult = await api.GetEpisodeGroupsAsync(picked.ProviderName, episode.Id, cancellationToken);
                if (!groupsResult.IsSuccess
                    || !MediaSelection.TryResolveGroup(groupsResult.Data, options.Group, out group))
                {
                    failCount++;
                    var groupErr = groupsResult.Error ?? $"'{options.Group}' grubu bulunamadı.";
                    items.Add(new
                    {
                        episode = EpisodeSummary(episode),
                        success = false,
                        error   = groupErr
                    });
                    if (!options.Json)
                    {
                        await Console.Error.WriteLineAsync($"Hata ({epLabel}): {groupErr}").ConfigureAwait(false);
                    }

                    continue;
                }
            }

            var resolved = await DownloadCandidateResolver.ResolveAsync(
                               api,
                               picked.ProviderName,
                               episode.Id,
                               group,
                               options.Format,
                               config,
                               config.DownloadAutoSelectTimeoutSeconds,
                               cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (resolved.Candidates.Count == 0)
            {
                failCount++;
                var resolveErr = resolved.Error ?? "Uygun doğrudan MP4/HLS kaynağı bulunamadı.";
                items.Add(new
                {
                    episode = EpisodeSummary(episode),
                    success = false,
                    error   = resolveErr
                });
                if (!options.Json)
                {
                    await Console.Error.WriteLineAsync($"Hata ({epLabel}): {resolveErr}").ConfigureAwait(false);
                }

                continue;
            }

            if (options.Debug)
            {
                WriteDebugSummary(picked, details, episode, resolved.Candidates);
                successCount++;
                items.Add(new
                {
                    episode        = DebugEpisodeSummary(episode),
                    success        = true,
                    dryRun         = true,
                    source         = SourceSummary(resolved.Candidates[0]),
                    candidateCount = resolved.Candidates.Count
                });
                continue;
            }

            DownloadResult? epResult    = null;
            VideoSource?    usedSource  = null;
            string?         lastEpError = null;

            for (var index = 0; index < resolved.Candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source   = resolved.Candidates[index];
                var progress = new ConsoleDownloadProgress();
                var attempt = await downloadService.DownloadAsync(
                                                       new DownloadRequest
                                                       {
                                                           Source            = source,
                                                           OutputDirectory   = outputDirectory,
                                                           AnimeTitle        = details.Title,
                                                           EpisodeTitle      = episode.Title,
                                                           Season            = episode.Season ?? 1,
                                                           EpisodeNumber     = episode.Number,
                                                           Overwrite         = overwrite,
                                                           Resume            = resume,
                                                           DownloadSubtitles = downloadSubtitles,
                                                           Progress          = progress
                                                       },
                                                       cancellationToken)
                                                   .ConfigureAwait(false);

                if (attempt.IsCancelled && string.IsNullOrWhiteSpace(attempt.MediaPath))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (attempt.Success)
                {
                    epResult   = attempt;
                    usedSource = source;
                    break;
                }

                lastEpError = SanitizeFailure(attempt.Error, source);
            }

            if (epResult is { Success: true } && usedSource is not null)
            {
                successCount++;
                items.Add(new
                {
                    episode       = EpisodeSummary(episode),
                    success       = true,
                    source        = SourceSummary(usedSource),
                    mediaPath     = epResult.MediaPath,
                    subtitlePaths = epResult.SubtitlePaths,
                    warnings      = epResult.Warnings
                });
                if (!options.Json)
                {
                    Console.WriteLine($"İndirildi ({epLabel}): {epResult.MediaPath}");
                }
            }
            else
            {
                failCount++;
                var err = lastEpError ?? "Video indirilemedi.";
                items.Add(new
                {
                    episode = EpisodeSummary(episode),
                    success = false,
                    error   = err
                });
                if (!options.Json)
                {
                    await Console.Error.WriteLineAsync($"Hata ({epLabel}): {err}").ConfigureAwait(false);
                }
            }
        }

        if (options.Json)
        {
            WriteJson(new
            {
                success    = failCount == 0 && successCount > 0,
                batch      = true,
                provider   = picked.ProviderName,
                animeId    = picked.Id,
                animeTitle = details.Title,
                totalCount = episodes.Count,
                successCount,
                failedCount = failCount,
                items
            });
        }

        return failCount == 0 && successCount > 0 ? 0 : 1;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Kullanım:");
        Console.WriteLine(
            "  migurdex download <sorgu> [-e <n|aralık>] [-s <n>] [-p <sağlayıcı>] [-g <grup>] [-o <dizin>]");
        Console.WriteLine(
            "                     [--format auto|mp4|hls] [--subs|--no-subs] [--force] [--no-resume] [--debug] [--json]");
        Console.WriteLine();
        Console.WriteLine("  -e, --episode <seçim>   Bölüm veya aralık (örn. 12, 1-12, 1,3,5-8, all).");
        Console.WriteLine("  -s, --season <n>        Sezon filtresi.");
        Console.WriteLine("  -p, --provider <ad>     Sağlayıcıyı doğrular ve aramayı sınırlar.");
        Console.WriteLine("  -g, --group <ad>        Fansub grubunu doğrular.");
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
        private readonly DownloadSpeedometer _speed = new();
        private          string?             _lastLine;
        private          DateTimeOffset      _lastPrint = DateTimeOffset.MinValue;

        public void Report(DownloadProgress value)
        {
            _speed.Sample(value.BytesDownloaded, DateTimeOffset.UtcNow);
            var    stage = DownloadStageLabel.For(value.Stage, value.Track, value.IsAudioTrack);
            var    size  = FormatBytes(value.BytesDownloaded);
            string line;
            if (!string.IsNullOrWhiteSpace(value.Track))
            {
                line = $"{stage}: {value.Track}";
            }
            else
            {
                line = $"{stage}:";
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
