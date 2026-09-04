using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class EpisodeSourcesView : BaseView
{
    private readonly IApiClientService     _apiClient;
    private readonly IConfigurationService _configService;
    private readonly IHistoryService       _historyService;
    private readonly IMpvPlayerService     _playerService;
    private readonly IServiceProvider      _serviceProvider;
    private          List<Episode>         _allEpisodes = [];
    private          string?               _animeId;
    private          string?               _animeTitle;
    private          string?               _cachedEpisodeId;
    private          List<VideoSource>     _cachedSources = [];
    private          Episode?              _episode;
    private          bool                  _isFallbackMode;
    private          string?               _lastSelectedSearchable;
    private          string?               _posterUrl;
    private          string?               _provider;

    public EpisodeSourcesView(
        IApiClientService     apiClient,
        IMpvPlayerService     playerService,
        IHistoryService       historyService,
        IConfigurationService configService,
        IServiceProvider      serviceProvider)
    {
        _apiClient       = apiClient;
        _playerService   = playerService;
        _historyService  = historyService;
        _configService   = configService;
        _serviceProvider = serviceProvider;
    }

    public void SetTarget(string    provider,
        string                      animeId,
        string                      animeTitle,
        Episode                     episode,
        List<Episode>               allEpisodes,
        string?                     posterUrl      = null,
        IReadOnlyList<VideoSource>? adoptedSources = null)
    {
        _provider       = provider;
        _animeId        = animeId;
        _animeTitle     = animeTitle;
        _posterUrl      = posterUrl;
        _episode        = episode;
        _allEpisodes    = allEpisodes;
        _isFallbackMode = false;

        _lastSelectedSearchable = null;

        if (adoptedSources is not null)
        {
            _cachedSources   = [.. adoptedSources];
            _cachedEpisodeId = episode.Id;
        }
        else if (_cachedEpisodeId != episode.Id)
        {
            _cachedSources.Clear();
            _cachedEpisodeId = episode.Id;
        }
    }

    private static List<FuzzyChoice> FormatSources(List<VideoSource> rawList, CliConfig config)
    {
        var sorted = SortVideoSources(rawList, config);

        var maxIdxWidth = sorted.Count > 0 ? sorted.Count.ToString().Length : 1;
        var maxGroup    = sorted.Count > 0 ? sorted.Max(s => (s.Group ?? "Bilinmeyen Fansub").Length) : 0;
        var maxHoster   = sorted.Count > 0 ? sorted.Max(s => (s.Hoster ?? "Bilinmeyen Oynatıcı").Length) : 0;
        var maxQuality  = sorted.Count > 0 ? sorted.Max(s => (s.Quality ?? "Auto").Length) : 0;

        var selectList = new List<FuzzyChoice>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var src         = sorted[i];
            var groupText   = (src.Group ?? "Bilinmeyen Fansub").PadRight(maxGroup);
            var hosterText  = (src.Hoster ?? "Bilinmeyen Oynatıcı").PadRight(maxHoster);
            var qualityText = (src.Quality ?? "Auto").PadRight(maxQuality);
            var formatText  = src.Type.ToString();

            var idx     = i + 1;
            var idxText = $"#{idx}".PadRight(maxIdxWidth + 1);

            selectList.Add(new FuzzyChoice
            {
                Display =
                    $"[grey]{idxText}[/] [silver]{Markup.Escape(groupText)}  |  {Markup.Escape(hosterText)}  |  {Markup.Escape(qualityText)}  |  {formatText}[/]",
                DisplayActive =
                    $"[bold pink1]{idxText}[/] [bold white]{Markup.Escape(groupText)}[/]  [grey]|[/]  [bold mediumpurple1]{Markup.Escape(hosterText)}[/]  [grey]|[/]  [bold gold1]{Markup.Escape(qualityText)}[/]  [grey]|[/]  [bold cornflowerblue]{formatText}[/]",
                Searchable      = $"{idxText} - {groupText} | {hosterText} | {qualityText} | {formatText}",
                AssociatedValue = src
            });
        }

        return selectList;
    }

    private static List<VideoSource> SortVideoSources(List<VideoSource> rawList, CliConfig config)
    {
        if (rawList.Count == 0)
        {
            return rawList;
        }

        IOrderedEnumerable<VideoSource>? ordered = null;

        foreach (var criterion in config.SourceSortPriority)
        {
            Func<VideoSource, object> keySelector = criterion switch
            {
                "Quality" => s => GetRankFromOrderList(s.Quality, config.PreferredQualityOrder),
                "Format"  => s => GetRankFromOrderList(s.Type.ToString(), config.PreferredFormatOrder),
                "Hoster"  => s => GetRankFromOrderList(s.Hoster, config.PreferredHosterOrder),
                "Group"   => s => s.Group ?? "",
                _         => s => 0
            };

            var descending = criterion != "Group";

            if (ordered == null)
            {
                ordered = descending ? rawList.OrderByDescending(keySelector) : rawList.OrderBy(keySelector);
            }
            else
            {
                ordered = descending ? ordered.ThenByDescending(keySelector) : ordered.ThenBy(keySelector);
            }
        }

        return [.. ordered ?? rawList.OrderBy(s => 0)];
    }

    private static int GetRankFromOrderList(string? value, List<string> orderList)
    {
        if (string.IsNullOrEmpty(value))
        {
            return -1;
        }

        var valLower = value.ToLowerInvariant();

        for (var i = 0; i < orderList.Count; i++)
        {
            if (valLower.Contains(orderList[i].ToLowerInvariant()))
            {
                return orderList.Count - i;
            }
        }

        return 0;
    }

    private static bool IsExactMatch(VideoSource s, CliConfig config)
    {
        var bestQuality = config.PreferredQualityOrder.FirstOrDefault() ?? "1080p";
        var bestFormat  = config.PreferredFormatOrder.FirstOrDefault() ?? "M3U8";
        var bestHoster  = config.PreferredHosterOrder.FirstOrDefault() ?? "GoogleDrive";

        var isBestQuality = s.Quality.Contains(bestQuality, StringComparison.OrdinalIgnoreCase);
        var isBestFormat  = s.Type.ToString().Contains(bestFormat, StringComparison.OrdinalIgnoreCase);
        var isBestHoster  = s.Hoster != null && s.Hoster.Contains(bestHoster, StringComparison.OrdinalIgnoreCase);

        return isBestQuality && isBestFormat && isBestHoster;
    }

    private static bool IsListed(string? value, List<string> list)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return list.Any(e => value.Contains(e, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAutoEligible(VideoSource s, CliConfig config)
    {
        if (IsListed(s.Hoster, config.AutoNeverHosters)
            || IsListed(s.Quality, config.AutoNeverQualities)
            || IsListed(s.Type.ToString(), config.AutoNeverTypes))
        {
            return false;
        }

        if (config.AutoOnlyHosters.Count > 0 && !IsListed(s.Hoster, config.AutoOnlyHosters))
        {
            return false;
        }

        if (config.AutoOnlyQualities.Count > 0 && !IsListed(s.Quality, config.AutoOnlyQualities))
        {
            return false;
        }

        if (config.AutoOnlyTypes.Count > 0 && !IsListed(s.Type.ToString(), config.AutoOnlyTypes))
        {
            return false;
        }

        return true;
    }

    public override void Render(ITuiNavigator navigator)
    {
        if (string.IsNullOrEmpty(_provider)
            || string.IsNullOrEmpty(_animeId)
            || string.IsNullOrEmpty(_animeTitle)
            || _episode == null)
        {
            AnsiConsole.MarkupLine("[red]Hata: Gerekli parametreler eksik.[/]");
            Console.ReadKey(true);
            navigator.Pop();
            return;
        }

        var provider   = _provider;
        var animeId    = _animeId;
        var animeTitle = _animeTitle;
        var episode    = _episode;

        var config = _configService.Config;

        var scanStats = new StreamScanStats();
        var stream = _cachedSources.Count > 0
                         ? ToAsyncEnumerable(_cachedSources)
                         : _apiClient.GetVideoSourcesStreamAsync(provider, episode.Id, stats: scanStats)
                                     .Where(src => src.Type != VideoType.Embed);

        if (config.AutoSelectBestSource && _cachedSources.Count == 0 && !_isFallbackMode)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine(
                $"[grey]~~[/] [yellow]Otomatik: {animeTitle} - Bölüm {episode.Number}[/] [grey]~~[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Kaynaklar taranıyor...[/]");
            AnsiConsole.MarkupLine("[grey]İptal için [bold red]Esc[/] tuşuna basın.[/]");
            AnsiConsole.WriteLine();

            var resolvedSources = new List<VideoSource>();
            var cts             = new CancellationTokenSource();
            var userCancelled   = false;

            var streamTask = Task.Run(async () =>
                                      {
                                          try
                                          {
                                              await foreach (var src in stream.WithCancellation(cts.Token))
                                              {
                                                  lock (resolvedSources)
                                                  {
                                                      resolvedSources.Add(src);
                                                  }

                                                  if (IsExactMatch(src, config) && IsAutoEligible(src, config))
                                                  {
                                                      await cts.CancelAsync();
                                                      break;
                                                  }
                                              }
                                          }
                                          catch
                                          {
                                              // ignored
                                          }
                                      },
                                      cts.Token);

            var       spinnerFrames     = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
            var       spinnerIdx        = 0;
            var       startTime         = DateTime.UtcNow;
            DateTime? firstReceivedTime = null;

            while (!streamTask.IsCompleted && !cts.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        userCancelled = true;
                        cts.Cancel();
                        break;
                    }
                }

                int currentCount;
                lock (resolvedSources)
                {
                    currentCount = resolvedSources.Count;
                }

                if (currentCount > 0 && firstReceivedTime == null)
                {
                    firstReceivedTime = DateTime.UtcNow;
                }

                var elapsedSinceStart = (DateTime.UtcNow - startTime).TotalSeconds;
                if (elapsedSinceStart >= 240.0)
                {
                    cts.Cancel();
                    break;
                }

                if (firstReceivedTime != null)
                {
                    var elapsedSinceFirst = (DateTime.UtcNow - firstReceivedTime.Value).TotalSeconds;
                    if (elapsedSinceFirst >= config.AutoSelectTimeoutSeconds)
                    {
                        cts.Cancel();
                        break;
                    }
                }

                AnsiConsole.Markup(
                    $"\r [bold yellow]{spinnerFrames[spinnerIdx]}[/] {currentCount} kaynak bulundu...      ");
                spinnerIdx = (spinnerIdx + 1) % spinnerFrames.Length;
                Thread.Sleep(80);
            }

            try { streamTask.GetAwaiter().GetResult(); }
            catch
            {
                // ignored
            }

            lock (resolvedSources)
            {
                _cachedSources = [.. resolvedSources];
            }

            var scanErrors  = scanStats.Errors;
            var errorSuffix = scanErrors > 0 ? $" [red]• {scanErrors} hata[/]" : string.Empty;
            AnsiConsole.Markup($"\r [green]✓[/] {_cachedSources.Count} kaynak bulundu.{errorSuffix}          \n");

            if (!userCancelled && _cachedSources.Count > 0)
            {
                var eligible   = _cachedSources.Where(s => IsAutoEligible(s, config)).ToList();
                var bestSource = SortVideoSources(eligible, config).FirstOrDefault();

                if (bestSource != null)
                {
                    var exactTag = IsExactMatch(bestSource, config) && IsAutoEligible(bestSource, config)
                                       ? " (tam eşleşme)"
                                       : string.Empty;
                    AnsiConsole.MarkupLine(
                        $"[green]OK{exactTag}:[/] [bold white]{bestSource.Hoster ?? "Bilinmeyen"}[/] ({bestSource.Quality ?? "Auto"}) - {bestSource.Type}");
                    Thread.Sleep(100);

                    var historyEntry = new WatchHistoryEntry
                    {
                        AnimeId       = animeId,
                        AnimeTitle    = animeTitle,
                        ProviderName  = provider,
                        EpisodeId     = episode.Id,
                        EpisodeTitle  = episode.Title ?? $"Bölüm {episode.Number}",
                        PosterUrl     = _posterUrl ?? string.Empty,
                        Season        = episode.Season ?? 1,
                        EpisodeNumber = episode.Number
                    };

                    var existingHistory = _historyService.GetWatchHistory()
                                                         .FirstOrDefault(h => h.AnimeId == animeId
                                                                              && h.EpisodeId == episode.Id
                                                                              && h.ProviderName == provider);

                    if (existingHistory != null)
                    {
                        historyEntry.LastPositionSeconds  = existingHistory.LastPositionSeconds;
                        historyEntry.TotalDurationSeconds = existingHistory.TotalDurationSeconds;
                    }

                    _playerService.PlayAsync(bestSource.Url,
                                             historyEntry,
                                             bestSource.Headers,
                                             bestSource.Subtitles,
                                             CancellationToken.None)
                                  .GetAwaiter()
                                  .GetResult();

                    PushPlaybackMenu(navigator, bestSource, historyEntry, provider, animeId, animeTitle, episode);
                    return;
                }
            }

            _isFallbackMode = true;
            Toast.Show("[yellow][[!]] Otomatik seçim yapılamadı, manuel liste açılıyor...[/]");
        }

        AnsiConsole.Clear();

        var cancelChoice = new FuzzyChoice
        {
            Display       = "[red]Geri[/]",
            DisplayActive = "[bold red]Geri[/]",
            Searchable    = "Geri"
        };

        var manualStats = new StreamScanStats();
        var manualStream = _cachedSources.Count > 0 || _isFallbackMode
                               ? ToAsyncEnumerable(_cachedSources)
                               : stream;

        if (manualStream == stream)
        {
            manualStats = scanStats;
        }

        var promptResult = FuzzyPrompt.ShowDynamic(
            $"Kaynaklar: {animeTitle} - Bölüm {episode.Number}",
            manualStream,
            sources =>
            {
                var choices = FormatSources(sources, config);
                choices.Insert(0,
                               new FuzzyChoice
                               {
                                   Display       = "[yellow]Yeniden Tara[/]",
                                   DisplayActive = "[bold yellow]Yeniden Tara[/]",
                                   Searchable    = "Yeniden Tara"
                               });
                return choices;
            },
            cancelChoice,
            stats: manualStats,
            initialSelection: _lastSelectedSearchable);

        var selection = promptResult?.Selection;

        if (promptResult != null && _cachedSources.Count == 0)
        {
            _cachedSources = promptResult.AccumulatedItems;
        }

        if (selection == null || selection.Searchable == "Geri")
        {
            navigator.Pop();
            return;
        }

        if (selection.Searchable == "Yeniden Tara")
        {
            _cachedSources.Clear();
            _isFallbackMode = false;
            navigator.Replace(this);
            return;
        }

        _lastSelectedSearchable = selection.Searchable;

        if (selection.AssociatedValue is not VideoSource selectedSource)
        {
            navigator.Pop();
            return;
        }

        var selectedHistoryEntry = new WatchHistoryEntry
        {
            AnimeId       = animeId,
            AnimeTitle    = animeTitle,
            ProviderName  = provider,
            EpisodeId     = episode.Id,
            EpisodeTitle  = episode.Title ?? $"Bölüm {episode.Number}",
            PosterUrl     = _posterUrl ?? string.Empty,
            Season        = episode.Season ?? 1,
            EpisodeNumber = episode.Number
        };

        var existingManualHistory = _historyService.GetWatchHistory()
                                                   .FirstOrDefault(h => h.AnimeId == animeId
                                                                        && h.EpisodeId == episode.Id
                                                                        && h.ProviderName == provider);

        if (existingManualHistory != null)
        {
            selectedHistoryEntry.LastPositionSeconds  = existingManualHistory.LastPositionSeconds;
            selectedHistoryEntry.TotalDurationSeconds = existingManualHistory.TotalDurationSeconds;
        }

        AnsiConsole.Status()
                   .Spinner(Spinner.Known.Dots)
                   .Start("Hazırlanıyor...",
                          ctx =>
                          {
                              Thread.Sleep(100);
                          });

        AnsiConsole.MarkupLine("[green]OK:[/] Oynatıcı başlatıldı.");
        _playerService
            .PlayAsync(selectedSource.Url, selectedHistoryEntry, selectedSource.Headers, selectedSource.Subtitles)
            .GetAwaiter()
            .GetResult();

        PushPlaybackMenu(navigator, selectedSource, selectedHistoryEntry, provider, animeId, animeTitle, episode);
    }

    private void PushPlaybackMenu(ITuiNavigator navigator,
        VideoSource                             selectedSource,
        WatchHistoryEntry                       historyEntry,
        string                                  provider,
        string                                  animeId,
        string                                  animeTitle,
        Episode                                 episode)
    {
        var playbackMenu = (PlaybackMenuView) _serviceProvider.GetService(typeof(PlaybackMenuView))!;
        playbackMenu.SetTarget(provider,
                               animeId,
                               animeTitle,
                               episode,
                               _allEpisodes,
                               selectedSource,
                               historyEntry,
                               _cachedSources);
        navigator.Push(playbackMenu);
    }

    private static async IAsyncEnumerable<VideoSource> ToAsyncEnumerable(List<VideoSource> list)
    {
        foreach (var item in list)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
