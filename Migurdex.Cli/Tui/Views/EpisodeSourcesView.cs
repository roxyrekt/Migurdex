using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Globalization;

namespace Migurdex.Cli.Tui.Views;

public class EpisodeSourcesView : BaseView
{
    private readonly IApiClientService     _apiClient;
    private readonly IConfigurationService _configService;
    private readonly IHistoryService       _historyService;
    private readonly PlaybackOrchestrator  _playback;
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
        PlaybackOrchestrator  playback,
        IServiceProvider      serviceProvider)
    {
        _apiClient       = apiClient;
        _playerService   = playerService;
        _historyService  = historyService;
        _configService   = configService;
        _playback        = playback;
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
        var sorted = SourceSelector.SortVideoSources(rawList, config);

        var selectList = new List<FuzzyChoice>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var src     = sorted[i];
            var group   = Theme.Ellipsize(src.Group ?? "Bilinmeyen", 22);
            var hoster  = Theme.Ellipsize(src.Hoster ?? "Bilinmeyen", 18);
            var quality = src.Quality ?? "Auto";
            var format  = src.Type.ToString();

            var idx = i + 1;

            selectList.Add(new FuzzyChoice
            {
                Display =
                    $"[grey]#{idx}[/] {Markup.Escape(group)} [grey]•[/] {Markup.Escape(hoster)} [grey]•[/] [white]{Markup.Escape(quality)}[/] [grey]• {format}[/]",
                DisplayActive =
                    $"[bold white]{Markup.Escape(group)} • {Markup.Escape(hoster)} • {Markup.Escape(quality)}[/] [grey]• {format}[/]",
                Searchable      = $"#{idx} - {group} | {hoster} | {quality} | {format}",
                AssociatedValue = src
            });
        }

        return selectList;
    }

    public override string GetRpcState()
    {
        if (string.IsNullOrWhiteSpace(_animeTitle) || _episode is null)
        {
            return "Kaynaklar";
        }

        var season = Math.Max(1, _episode.Season ?? 1);
        var ep = _episode.Number % 1 == 0
                     ? ((int) _episode.Number).ToString()
                     : _episode.Number.ToString("0.#", CultureInfo.InvariantCulture);

        return $"{_animeTitle} S{season} E{ep}";
    }

    public override async Task RenderAsync(ITuiNavigator navigator)
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
            Theme.WriteHeader($"{animeTitle}", $"Bölüm {episode.Number} • otomatik seçim");
            AnsiConsole.MarkupLine("[cyan]Kaynaklar taranıyor...[/] [grey](Esc: vazgeç)[/]");

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

                                                  if (SourceSelector.IsExactMatch(src, config)
                                                      && SourceSelector.IsAutoEligible(src, config))
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

            var       startTime         = DateTime.UtcNow;
            DateTime? firstReceivedTime = null;

            AnsiConsole.Status()
                       .Spinner(Spinner.Known.Dots)
                       .Start("Kaynaklar taranıyor...",
                              ctx =>
                              {
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
                                          var elapsedSinceFirst =
                                              (DateTime.UtcNow - firstReceivedTime.Value).TotalSeconds;
                                          if (elapsedSinceFirst >= config.AutoSelectTimeoutSeconds)
                                          {
                                              cts.Cancel();
                                              break;
                                          }
                                      }

                                      ctx.Status($"[grey]{currentCount} kaynak[/]");
                                      Thread.Sleep(80);
                                  }
                              });

            try { await streamTask; }
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
            AnsiConsole.MarkupLine($"[green]✓[/] {_cachedSources.Count} kaynak bulundu.{errorSuffix}");

            if (!userCancelled && _cachedSources.Count > 0)
            {
                var eligible   = _cachedSources.Where(s => SourceSelector.IsAutoEligible(s, config)).ToList();
                var bestSource = SourceSelector.SortVideoSources(eligible, config).FirstOrDefault();

                if (bestSource != null)
                {
                    var exactTag = SourceSelector.IsExactMatch(bestSource, config)
                                   && SourceSelector.IsAutoEligible(bestSource, config)
                                       ? " (tam eşleşme)"
                                       : string.Empty;
                    AnsiConsole.MarkupLine(
                        $"[green]✓{exactTag}:[/] {Markup.Escape(bestSource.Hoster ?? "Bilinmeyen")} [grey]({Markup.Escape(bestSource.Quality ?? "Auto")} • {bestSource.Type})[/]");

                    var historyEntry = _playback.BuildEntry(provider, animeId, animeTitle, episode, _posterUrl);

                    var autoSyncOutcome = await _playback.PlayAndNotifyAsync(bestSource, historyEntry, false);

                    PushPlaybackMenu(navigator,
                                     bestSource,
                                     historyEntry,
                                     provider,
                                     animeId,
                                     animeTitle,
                                     episode,
                                     autoSyncOutcome);
                    return;
                }
            }

            _isFallbackMode = true;
            Toast.Show("[yellow][[!]] Otomatik seçim yapılamadı, manuel liste açılıyor...[/]");
        }

        AnsiConsole.Clear();

        var cancelChoice = TuiHelpers.Back();

        var manualStats = new StreamScanStats();
        var manualStream = _cachedSources.Count > 0 || _isFallbackMode
                               ? ToAsyncEnumerable(_cachedSources)
                               : stream;

        if (manualStream == stream)
        {
            manualStats = scanStats;
        }

        var isMovie = string.Equals(episode.Title?.Trim(), "Film", StringComparison.OrdinalIgnoreCase)
                      || _allEpisodes.Count == 1;
        var epLabel = isMovie ? "Film" : $"Bölüm {episode.Number}";

        var promptResult = FuzzyPrompt.ShowDynamic(
            "Kaynaklar",
            manualStream,
            sources =>
            {
                var choices = FormatSources(sources, config);
                choices.Insert(0, TuiHelpers.Rescan());
                return choices;
            },
            cancelChoice,
            stats: manualStats,
            initialSelection: _lastSelectedSearchable,
            headerLines:
            [$"[grey]{Markup.Escape(TuiHelpers.EllipsizedTitle(animeTitle))} › {Markup.Escape(epLabel)}[/]"]);

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

        var selectedHistoryEntry = _playback.BuildEntry(provider, animeId, animeTitle, episode, _posterUrl);

        AnsiConsole.MarkupLine("[green]✓[/] Oynatıcı başlatıldı.");
        var syncOutcome = await _playback.PlayAndNotifyAsync(selectedSource, selectedHistoryEntry, false);

        PushPlaybackMenu(navigator,
                         selectedSource,
                         selectedHistoryEntry,
                         provider,
                         animeId,
                         animeTitle,
                         episode,
                         syncOutcome);
    }

    private void PushPlaybackMenu(ITuiNavigator navigator,
        VideoSource                             selectedSource,
        WatchHistoryEntry                       historyEntry,
        string                                  provider,
        string                                  animeId,
        string                                  animeTitle,
        Episode                                 episode,
        SyncOutcome?                            syncOutcome = null)
    {
        var playbackMenu = (PlaybackMenuView) _serviceProvider.GetService(typeof(PlaybackMenuView))!;
        playbackMenu.SetTarget(provider,
                               animeId,
                               animeTitle,
                               episode,
                               _allEpisodes,
                               selectedSource,
                               historyEntry,
                               _cachedSources,
                               syncOutcome);
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
