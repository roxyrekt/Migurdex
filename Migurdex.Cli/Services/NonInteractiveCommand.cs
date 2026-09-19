using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Configuration;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
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
               || arg.Equals("continue", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var sub = args[0].ToLowerInvariant();
        return sub switch
        {
            "search"   => await SearchAsync(args[1..], services),
            "play"     => await PlayAsync(args[1..], services),
            "continue" => await ContinueAsync(args[1..], services),
            _          => UsageError($"Bilinmeyen komut: {args[0]}")
        };
    }

    private static async Task<int> SearchAsync(string[] args, IServiceProvider services)
    {
        if (!TryParseCommon(args, out var opts, out var error, allowEpisode: false, allowJson: true))
        {
            return UsageError(error!);
        }

        if (opts.ShowHelp || string.IsNullOrWhiteSpace(opts.Query))
        {
            Help();
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
            Console.WriteLine($"{item.ProviderName} | {item.Title} | {item.Id}");
        }

        return 0;
    }

    private static async Task<int> PlayAsync(string[] args, IServiceProvider services)
    {
        if (!TryParseCommon(args, out var opts, out var error, allowEpisode: true, allowJson: false))
        {
            return UsageError(error!);
        }

        if (opts.ShowHelp)
        {
            Help();
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
            Help();
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

        Console.WriteLine($"{last.AnimeTitle} S{last.Season}E{FormatNumber(last.EpisodeNumber)} devam ediliyor...");
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

        Console.WriteLine(
            $"{details.Title} S{entry.Season}E{FormatNumber(entry.EpisodeNumber)} oynatılıyor [{best.Hoster ?? "bilinmiyor"} / {best.Quality}]...");

        var player  = services.GetRequiredService<IMpvPlayerService>();
        var outcome = await player.PlayAsync(best.Url, entry, best.Headers, best.Subtitles);

        Console.WriteLine($"Bitti: {DescribeOutcome(outcome)}");
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

        var names = providersResult.Data.Select(p => p.Name).ToList();
        var exact = names.FirstOrDefault(n => n.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var matches = names.Where(n => n.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            Console.WriteLine($"Sağlayıcı: {matches[0]}");
            return matches[0];
        }

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"Hata: '{input}' sağlayıcısı bulunamadı ({string.Join(", ", names)}).");
        }
        else
        {
            Console.Error.WriteLine($"Hata: '{input}' belirsiz ({string.Join(", ", matches)}).");
        }

        return null;
    }

    private static SearchResult? PickResult(IReadOnlyList<SearchResult> results, string query)
    {
        if (results.Count == 0)
        {
            return null;
        }

        return results.FirstOrDefault(r => r.Title.Equals(query, StringComparison.OrdinalIgnoreCase))
               ?? results[0];
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
        Console.Error.WriteLine("Kullanım: migurdex <search|play|continue> --help");
        return 2;
    }

    private static void Help()
    {
        Console.WriteLine("Kullanım:");
        Console.WriteLine("  migurdex search <sorgu> [-p|--provider <ad>] [--json]");
        Console.WriteLine(
            "  migurdex play <sorgu> [-e|--episode <n>] [-s|--season <n>] [-p|--provider <ad>] [-g|--group <ad>] [--debug]");
        Console.WriteLine("  migurdex continue [--debug]");
        Console.WriteLine("  migurdex update [--check] [--channel stable|prerelease] [-y] [--no-restart]");
        Console.WriteLine("  migurdex auth <login|logout|status>");
        Console.WriteLine("  migurdex --version");
        Console.WriteLine(
            "Bayraklar: -e bölüm (varsayılan: kaldığın yer ya da 1), -s sezon, -p sağlayıcı, -g fansub grubu, --debug mpv açmadan URL yazdırır.");
    }
}
