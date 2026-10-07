using Microsoft.Extensions.DependencyInjection;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Globalization;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public static class NonInteractiveCommand
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true
    };

    public static bool IsCommand(string arg)
    {
        return arg.Equals("search", StringComparison.OrdinalIgnoreCase)
               || arg.Equals("play", StringComparison.OrdinalIgnoreCase)
               || arg.Equals("continue", StringComparison.OrdinalIgnoreCase)
               || arg.Equals("download", StringComparison.OrdinalIgnoreCase)
               || arg.Equals("blame", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintLine(string plain, string markup)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(plain);
        }
        else
        {
            AnsiConsole.MarkupLine(markup);
        }
    }

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var sub = args[0].ToLowerInvariant();
        return sub switch
        {
            "search"   => await SearchAsync(args[1..], services),
            "play"     => await PlayAsync(args[1..], services),
            "continue" => await ContinueAsync(args[1..], services),
            "download" => await DownloadCommand.RunAsync(args[1..], services),
            "blame"    => await BlameAsync(args[1..], services),
            _          => UsageError($"Bilinmeyen komut: {args[0]}")
        };
    }

    private static async Task<int> SearchAsync(string[] args, IServiceProvider services)
    {
        if (!TryParseCommon(args, out var opts, out var error, false, true))
        {
            return UsageError(error!);
        }

        if (opts.ShowHelp || string.IsNullOrWhiteSpace(opts.Query))
        {
            PrintHelp();
            return opts.ShowHelp ? 0 : 2;
        }

        var api = services.GetRequiredService<IApiClientService>();
        if (!await EnsureApiOnlineAsync(api))
        {
            return 1;
        }

        var provider = await ResolveProviderAsync(api, opts.Provider);
        if (provider is null && !string.IsNullOrWhiteSpace(opts.Provider))
        {
            return 1;
        }

        var result = await api.SearchAnimeAsync(opts.Query, provider);
        if (!result.IsSuccess)
        {
            await Console.Error.WriteLineAsync($"Hata: {result.Error}");
            return 1;
        }

        if (result.Data.Count == 0)
        {
            await Console.Error.WriteLineAsync("Sonuç bulunamadı.");
            return 1;
        }

        if (opts.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result.Data, _jsonOpts));
            return 0;
        }

        foreach (var item in result.Data)
        {
            PrintLine($"{item.ProviderName} | {item.Title} | {item.Id}",
                      $"[cyan]{Markup.Escape(item.ProviderName)}[/] | {Markup.Escape(item.Title)} [grey]({Markup.Escape(item.Id)})[/]");
        }

        return 0;
    }

    private static async Task<int> PlayAsync(string[] args, IServiceProvider services)
    {
        if (!TryParseCommon(args, out var opts, out var error, true, false))
        {
            return UsageError(error!);
        }

        if (opts.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(opts.Query))
        {
            return UsageError("Arama sorgusu gerekli.");
        }

        var api     = services.GetRequiredService<IApiClientService>();
        var history = services.GetRequiredService<IHistoryService>();
        if (!await EnsureApiOnlineAsync(api))
        {
            return 1;
        }

        var provider = await ResolveProviderAsync(api, opts.Provider);
        if (provider is null && !string.IsNullOrWhiteSpace(opts.Provider))
        {
            return 1;
        }

        var search = await api.SearchAnimeAsync(opts.Query, provider);
        if (!search.IsSuccess)
        {
            await Console.Error.WriteLineAsync($"Hata: {search.Error}");
            return 1;
        }

        var picked = PickResult(search.Data, opts.Query);
        if (picked is null)
        {
            await Console.Error.WriteLineAsync("Sonuç bulunamadı.");
            return 1;
        }

        var detailsResult = await api.GetAnimeDetailsAsync(picked.ProviderName, picked.Id);
        if (!detailsResult.IsSuccess || detailsResult.Data is null)
        {
            await Console.Error.WriteLineAsync($"Hata: {detailsResult.Error ?? "Anime detayları alınamadı."}");
            return 1;
        }

        var details = detailsResult.Data;
        var episode = PickEpisode(details, history, picked.ProviderName, opts);
        if (episode is null)
        {
            var available = details.Episodes.Count > 0
                                ? $" (mevcut: {details.Episodes.Min(e => e.Number)}-{details.Episodes.Max(e => e.Number)})"
                                : string.Empty;
            await Console.Error.WriteLineAsync($"Hata: bölüm bulunamadı{available}.");
            return 1;
        }

        return await ResolveAndPlayAsync(services,
                                         api,
                                         history,
                                         picked.ProviderName,
                                         picked.Id,
                                         details,
                                         episode,
                                         opts);
    }

    private static async Task<int> ContinueAsync(string[] args, IServiceProvider services)
    {
        var debug    = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));
        var showHelp = args.Any(a => a is "--help" or "-h");
        var unknown = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)
                                               && !a.Equals("--debug", StringComparison.OrdinalIgnoreCase)
                                               && !a.Equals("--help", StringComparison.OrdinalIgnoreCase));
        if (unknown is not null)
        {
            return UsageError($"Bilinmeyen bayrak: {unknown}");
        }

        if (showHelp)
        {
            PrintHelp();
            return 0;
        }

        var api     = services.GetRequiredService<IApiClientService>();
        var history = services.GetRequiredService<IHistoryService>();
        if (!await EnsureApiOnlineAsync(api))
        {
            return 1;
        }

        var last = history.GetWatchHistory().FirstOrDefault();
        if (last is null)
        {
            await Console.Error.WriteLineAsync("Hata: izleme geçmişi boş.");
            return 1;
        }

        var detailsResult = await api.GetAnimeDetailsAsync(last.ProviderName, last.AnimeId);
        if (!detailsResult.IsSuccess || detailsResult.Data is null)
        {
            await Console.Error.WriteLineAsync($"Hata: {detailsResult.Error ?? "Anime detayları alınamadı."}");
            return 1;
        }

        var details = detailsResult.Data;
        var episode =
            details.Episodes.FirstOrDefault(e => e.Id.Equals(last.EpisodeId, StringComparison.OrdinalIgnoreCase))
            ?? details.Episodes.FirstOrDefault(e => e.Number.Equals(last.EpisodeNumber)
                                                    && (e.Season ?? 1) == last.Season);
        if (episode is null)
        {
            await Console.Error.WriteLineAsync("Hata: geçmişteki bölüm sağlayıcıda bulunamadı.");
            return 1;
        }

        PrintLine($"{last.AnimeTitle} S{last.Season}E{FormatNumber(last.EpisodeNumber)} devam ediliyor...",
                  $"{Markup.Escape(last.AnimeTitle)} [grey]S{last.Season}E{FormatNumber(last.EpisodeNumber)}[/] [cyan]devam ediliyor...[/]");
        var opts = new Options
        {
            Debug = debug
        };
        return await ResolveAndPlayAsync(services,
                                         api,
                                         history,
                                         last.ProviderName,
                                         last.AnimeId,
                                         details,
                                         episode,
                                         opts,
                                         last);
    }

    private static async Task<int> ResolveAndPlayAsync(IServiceProvider services,
        IApiClientService                                               api,
        IHistoryService                                                 history,
        string                                                          provider,
        string                                                          animeId,
        AnimeDetails                                                    details,
        Episode                                                         episode,
        Options                                                         opts,
        WatchHistoryEntry?                                              resumeFrom = null)
    {
        var groupsResult = await api.GetEpisodeGroupsAsync(provider, episode.Id);
        var groups       = groupsResult.IsSuccess ? groupsResult.Data : [];
        if (!string.IsNullOrWhiteSpace(opts.Group)
            && !groups.Any(g => g.Equals(opts.Group, StringComparison.OrdinalIgnoreCase)))
        {
            await Console.Error.WriteLineAsync($"Hata: '{opts.Group}' grubu bulunamadı ({string.Join(", ", groups)}).");
            return 1;
        }

        var sourcesResult = await api.GetVideoSourcesAsync(provider, episode.Id, opts.Group);
        if (!sourcesResult.IsSuccess)
        {
            await Console.Error.WriteLineAsync($"Hata: {sourcesResult.Error}");
            return 1;
        }

        var playable = sourcesResult.Data
                                    .Where(s => s.Type != VideoType.Embed && !string.IsNullOrWhiteSpace(s.Url))
                                    .ToList();
        if (playable.Count == 0)
        {
            await Console.Error.WriteLineAsync("Hata: oynatılabilir kaynak bulunamadı.");
            return 1;
        }

        var config = services.GetRequiredService<IConfigurationService>().Config;
        var best   = SourceSelector.PickBest(playable, config);
        if (best is null)
        {
            await Console.Error.WriteLineAsync("Hata: kaynak seçilemedi.");
            return 1;
        }

        if (opts.Debug)
        {
            Console.WriteLine($"hoster: {best.Hoster ?? "bilinmiyor"}");
            Console.WriteLine($"quality: {best.Quality}");
            Console.WriteLine($"type: {best.Type}");
            Console.WriteLine($"url: {best.Url}");
            if (best.Headers is { Count: > 0 })
            {
                Console.WriteLine($"headers: {string.Join(",", best.Headers.Select(kv => $"{kv.Key}: {kv.Value}"))}");
            }

            return 0;
        }

        var entry = new WatchHistoryEntry
        {
            AnimeId       = resumeFrom?.AnimeId ?? animeId,
            AnimeTitle    = details.Title,
            ProviderName  = provider,
            EpisodeId     = episode.Id,
            EpisodeTitle  = episode.Title,
            PosterUrl     = details.PosterUrl ?? string.Empty,
            Season        = episode.Season ?? 1,
            EpisodeNumber = episode.Number
        };

        var existing = resumeFrom
                       ?? history.GetWatchHistory()
                                 .FirstOrDefault(h => h.AnimeId.Equals(animeId, StringComparison.OrdinalIgnoreCase)
                                                      && h.EpisodeId.Equals(
                                                          episode.Id,
                                                          StringComparison.OrdinalIgnoreCase)
                                                      && h.ProviderName.Equals(
                                                          provider,
                                                          StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            entry.LastPositionSeconds  = existing.LastPositionSeconds;
            entry.TotalDurationSeconds = existing.TotalDurationSeconds;
        }

        PrintLine(
            $"{details.Title} S{entry.Season}E{FormatNumber(entry.EpisodeNumber)} oynatılıyor [{best.Hoster ?? "bilinmiyor"} / {best.Quality}]...",
            $"{Markup.Escape(details.Title)} [grey]S{entry.Season}E{FormatNumber(entry.EpisodeNumber)}[/] [cyan]oynatılıyor[/] [grey][[{Markup.Escape(best.Hoster ?? "bilinmiyor")} / {Markup.Escape(best.Quality)}]][/]...");

        var player  = services.GetRequiredService<IMpvPlayerService>();
        var outcome = await player.PlayAsync(best.Url, entry, best.Headers, best.Subtitles);

        PrintLine($"Bitti: {DescribeOutcome(outcome)}",
                  $"[green]Bitti:[/] {Markup.Escape(DescribeOutcome(outcome))}");
        return 0;
    }

    private static async Task<string?> ResolveProviderAsync(IApiClientService api, string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var providersResult = await api.GetProvidersAsync();
        if (!providersResult.IsSuccess)
        {
            return input;
        }

        if (!MediaSelection.TryResolveProvider(providersResult.Data,
                                                  input,
                                                  out var provider,
                                                  out var error))
        {
            Console.Error.WriteLine($"Hata: {error}");
            return null;
        }

        if (!provider!.Equals(input, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Sağlayıcı: {provider}");
        }

        return provider;
    }

    private static SearchResult? PickResult(IReadOnlyList<SearchResult> results, string query)
    {
        return MediaSelection.PickSearchResult(results, query);
    }

    private static Episode? PickEpisode(AnimeDetails details,
        IHistoryService                              history,
        string                                       provider,
        Options                                      opts)
    {
        var candidates = details.Episodes.AsEnumerable();
        if (opts.Season.HasValue)
        {
            candidates = candidates.Where(e => (e.Season ?? 1) == opts.Season.Value);
        }

        var list = candidates.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        double wanted;
        if (opts.Episode.HasValue)
        {
            wanted = opts.Episode.Value;
        }
        else
        {
            var lastNum = history.GetWatchHistory()
                                 .Where(h => h.ProviderName.Equals(provider, StringComparison.OrdinalIgnoreCase)
                                             && details.Episodes.Any(e => e.Id.Equals(h.EpisodeId,
                                                                         StringComparison.OrdinalIgnoreCase)))
                                 .Select(h => (double?) h.EpisodeNumber)
                                 .Max();
            wanted = lastNum ?? list.Min(e => e.Number);
        }

        return list.FirstOrDefault(e => e.Number.Equals(wanted))
               ?? (opts.Episode.HasValue || opts.Season.HasValue ? null : list.OrderBy(e => e.Number).First());
    }

    private static async Task<int> BlameAsync(string[] args, IServiceProvider services)
    {
        var json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        var showHelp = args.Any(a => a is "--help" or "-h");
        var unknown = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)
                                               && !a.Equals("--json", StringComparison.OrdinalIgnoreCase)
                                               && !a.Equals("--help", StringComparison.OrdinalIgnoreCase)
                                               && !a.Equals("-h", StringComparison.Ordinal));
        if (unknown is not null)
        {
            return UsageError($"Bilinmeyen bayrak: {unknown}");
        }

        if (showHelp)
        {
            Console.WriteLine("Kullanım: migurdex blame [--json]");
            Console.WriteLine("API daemon'un gördüğü süreleri yazar: endpoint'ler ve sağlayıcılar.");
            return 0;
        }

        var api = services.GetRequiredService<IApiClientService>();
        if (!await EnsureApiOnlineAsync(api))
        {
            return 1;
        }

        var result = await api.GetBlameReportAsync();
        if (!result.IsSuccess || result.Data is null)
        {
            await Console.Error.WriteLineAsync($"Hata: {result.Error ?? "İstatistik alınamadı."}");
            return 1;
        }

        var report = result.Data;
        if (report.Operations.Count == 0 && report.Providers.Count == 0)
        {
            Console.WriteLine("Henüz ölçüm yok (daemon yeni başladı, önce arama/bölüm çağırın).");
            return 0;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, _jsonOpts));
            return 0;
        }

        if (!Console.IsOutputRedirected)
        {
            var ops = new Table().Border(TableBorder.Rounded).Title("[cyan]İşlemler[/]");
            ops.AddColumns("İşlem", "Çağrı", "Ort", "Max", "Hata", "4xx");
            foreach (var o in report.Operations)
            {
                ops.AddRow(Markup.Escape(o.Operation),
                           o.Calls.ToString(),
                           $"{o.AvgMs:0}ms",
                           $"{o.MaxMs}ms",
                           o.Errors.ToString(),
                           o.ClientErrors.ToString());
            }

            AnsiConsole.Write(ops);

            if (report.Providers.Count > 0)
            {
                var prov = new Table().Border(TableBorder.Rounded).Title("[cyan]Sağlayıcılar[/]");
                prov.AddColumns("Sağlayıcı", "İş", "Çağrı", "Ort", "Max", "Timeout", "Uyumsuz", "Başarılı");
                foreach (var p in report.Providers)
                {
                    prov.AddRow(Markup.Escape(p.Provider),
                                Markup.Escape(p.Operation),
                                p.Calls.ToString(),
                                $"{p.AvgMs:0}ms",
                                $"{p.MaxMs}ms",
                                p.Timeouts.ToString(),
                                p.Mismatches.ToString(),
                                p.Matched.ToString());
                }

                AnsiConsole.Write(prov);
            }

            return 0;
        }

        foreach (var o in report.Operations)
        {
            Console.WriteLine($"{o.Operation} | calls={o.Calls} avg={o.AvgMs:0}ms max={o.MaxMs}ms errors={o.Errors} 4xx={o.ClientErrors}");
        }

        foreach (var p in report.Providers)
        {
            Console.WriteLine($"{p.Provider} [{p.Operation}] | calls={p.Calls} avg={p.AvgMs:0}ms max={p.MaxMs}ms "
                              + $"timeout={p.Timeouts} mismatch={p.Mismatches} ok={p.Matched}");
        }

        return 0;
    }

    private static async Task<bool> EnsureApiOnlineAsync(IApiClientService api)
    {
        if (await api.IsApiOnlineAsync())
        {
            return true;
        }

        Console.WriteLine("API başlatılıyor...");
        if (await api.TryStartApiDaemonAsync())
        {
            return true;
        }

        await Console.Error.WriteLineAsync("Hata: API bağlantısı kurulamadı. Ayarlardaki API adresini kontrol edin.");
        return false;
    }

    private static string DescribeOutcome(SyncOutcome outcome)
    {
        return outcome.Kind switch
        {
            SyncOutcomeKind.Pushed => outcome.SyncedTo.Count > 0
                                          ? $"takipçilere işlendi ({string.Join(", ", outcome.SyncedTo)})"
                                          : "işlendi",
            SyncOutcomeKind.Queued         => "çevrimdışı kuyruğa alındı",
            SyncOutcomeKind.Ambiguous      => "eşleşme belirsiz, kuyruğa alındı",
            SyncOutcomeKind.SkippedNoToken => "giriş yok, kuyruğa alındı",
            _                              => "tamamlandı"
        };
    }

    private static string FormatNumber(double n)
    {
        return n % 1 == 0 ? ((int) n).ToString() : n.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static bool TryParseCommon(string[] args,
        out Options                             opts,
        out string?                             error,
        bool                                    allowEpisode,
        bool                                    allowJson)
    {
        opts  = new Options();
        error = null;

        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--help" or "-h")
            {
                opts.ShowHelp = true;
            }
            else if (a is "-e" or "--episode")
            {
                if (!allowEpisode)
                {
                    error = $"{a} bu komutta desteklenmiyor.";
                    return false;
                }

                if (++i >= args.Length
                    || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var ep))
                {
                    error = $"{a} için sayı gerekli.";
                    return false;
                }

                opts.Episode = ep;
            }
            else if (a is "-s" or "--season")
            {
                if (!allowEpisode)
                {
                    error = $"{a} bu komutta desteklenmiyor.";
                    return false;
                }

                if (++i >= args.Length || !int.TryParse(args[i], out var season))
                {
                    error = $"{a} için sayı gerekli.";
                    return false;
                }

                opts.Season = season;
            }
            else if (a is "-p" or "--provider")
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    error = $"{a} için değer gerekli.";
                    return false;
                }

                opts.Provider = args[i];
            }
            else if (a is "-g" or "--group")
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    error = $"{a} için değer gerekli.";
                    return false;
                }

                opts.Group = args[i];
            }
            else if (a.Equals("--debug", StringComparison.OrdinalIgnoreCase))
            {
                opts.Debug = true;
            }
            else if (a.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowJson)
                {
                    error = $"{a} bu komutta desteklenmiyor.";
                    return false;
                }

                opts.Json = true;
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
            opts.Query = string.Join(" ", positionals);
        }

        return true;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"Hata: {message}");
        Console.Error.WriteLine("Kullanım: migurdex <search|play|continue|download|blame> --help");
        return 2;
    }

    internal static void PrintHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("Kullanım:");
        WriteCommandLines(writer);
        WriteFlagLegend(writer);
    }

    internal static void WriteCommandLines(TextWriter writer)
    {
        writer.WriteLine("  migurdex search <sorgu> [-p|--provider <ad>] [--json]");
        writer.WriteLine(
            "  migurdex play <sorgu> [-e|--episode <n>] [-s|--season <n>] [-p|--provider <ad>] [-g|--group <ad>] [--debug]");
        writer.WriteLine("  migurdex continue [--debug]");
        writer.WriteLine(
            "  migurdex download <sorgu> [-e <n>] [-s <n>] [-p <ad>] [-g <ad>] [-o <dizin>] [--format auto|mp4|hls] [--subs|--no-subs] [--force] [--no-resume] [--debug] [--json]");
        writer.WriteLine("  migurdex update [--check] [--channel stable|prerelease] [-y] [--no-restart]");
        writer.WriteLine("  migurdex auth <login|logout|status>");
        writer.WriteLine("  migurdex blame [--json]");
        writer.WriteLine("  migurdex --version");
    }

    internal static void WriteFlagLegend(TextWriter writer)
    {
        writer.WriteLine(
            "Bayraklar: -e bölüm, -s sezon, -p sağlayıcı, -g fansub grubu. Play varsayılanı kaldığın yer; download varsayılanı ilk bölüm.");
        writer.WriteLine(
            "          --debug oynatmadan/indirmeden çözülen kaynağı yazdırır; --json yalnız makine okunur sonuç verir.");
    }

    private sealed class Options
    {
        public string? Query    { get; set; }
        public string? Provider { get; set; }
        public string? Group    { get; set; }
        public double? Episode  { get; set; }
        public int?    Season   { get; set; }
        public bool    Debug    { get; set; }
        public bool    Json     { get; set; }
        public bool    ShowHelp { get; set; }
    }
}
