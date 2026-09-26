using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Cli.Tui;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Globalization;

namespace Migurdex.Cli.Tui.Views;

public class EpisodeSourcesView : BaseView
{
    private readonly IApiClientService     _apiClient;
    private readonly IConfigurationService _configService;
    private readonly IDownloadService      _downloadService;
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
        : this(apiClient,
               playerService,
               historyService,
               configService,
               serviceProvider.GetService(typeof(IDownloadService)) as IDownloadService ?? new DownloadService(),
               playback,
               serviceProvider)
    {
    }

    public EpisodeSourcesView(
        IApiClientService     apiClient,
        IMpvPlayerService     playerService,
        IHistoryService       historyService,
        IConfigurationService configService,
        IDownloadService      downloadService,
        PlaybackOrchestrator  playback,
        IServiceProvider      serviceProvider)
    {
        _apiClient       = apiClient;
        _playerService   = playerService;
        _historyService  = historyService;
        _configService   = configService;
        _downloadService = downloadService;
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
            var cts             = CancellationTokenSource.CreateLinkedTokenSource(
                TuiApplicationCancellation.Token);
            var userCancelled = false;

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

            if (TuiApplicationCancellation.Token.IsCancellationRequested)
            {
                return;
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

                    await HandleSelectedSourceAsync(navigator,
                                                   bestSource,
                                                   provider,
                                                   animeId,
                                                   animeTitle,
                                                   episode);
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

        await HandleSelectedSourceAsync(navigator,
                                        selectedSource,
                                        provider,
                                        animeId,
                                        animeTitle,
                                        episode);
    }

    private async Task HandleSelectedSourceAsync(ITuiNavigator navigator,
        VideoSource                                                       selectedSource,
        string                                                            provider,
        string                                                            animeId,
        string                                                            animeTitle,
        Episode                                                           episode)
    {
        var sourceDescription = string.Join(" • ",
                                            new[]
                                            {
                                                selectedSource.Group ?? "Bilinmeyen",
                                                selectedSource.Hoster ?? "Bilinmeyen",
                                                selectedSource.Quality ?? "Auto",
                                                selectedSource.Type.ToString()
                                            });
        var actions = new List<FuzzyChoice>
        {
            Theme.PlayChoice("Oynat")
        };
        if (DownloadSourceResolver.IsDirectDownloadable(selectedSource))
        {
            actions.Add(Theme.ActionChoice("İndir", Theme.Primary));
        }

        actions.Add(TuiHelpers.Back());
        var action = FuzzyPrompt.Show("Kaynak seçildi",
                                      actions,
                                      headerLines:
                                      [
                                          $"[grey]{Markup.Escape(TuiHelpers.EllipsizedTitle(animeTitle))} › "
                                          + $"[grey]{Markup.Escape(TuiHelpers.FormatEpisodeRef(episode))}[/]",
                                          $"[grey]{Markup.Escape(sourceDescription)}[/]"
                                      ]);

        if (action == null || action.Searchable == "Geri")
        {
            return;
        }

        if (action.Searchable == "İndir")
        {
            await DownloadSourceAsync(navigator,
                                      selectedSource,
                                      animeTitle,
                                      episode);
            return;
        }

        var historyEntry = _playback.BuildEntry(provider, animeId, animeTitle, episode, _posterUrl);
        AnsiConsole.MarkupLine("[green]✓[/] Oynatıcı başlatıldı.");
        var syncOutcome = await _playback.PlayAndNotifyAsync(selectedSource, historyEntry, false);
        PushPlaybackMenu(navigator,
                         selectedSource,
                         historyEntry,
                         provider,
                         animeId,
                         animeTitle,
                         episode,
                         syncOutcome);
    }

    private async Task DownloadSourceAsync(ITuiNavigator navigator,
        VideoSource                                                       selectedSource,
        string                                                            animeTitle,
        Episode                                                           episode)
    {
        if (!DownloadSourceResolver.IsDirectDownloadable(selectedSource))
        {
            await ShowDownloadResultAsync(navigator,
                                           DownloadResult.Failed(
                                               "Bu kaynak doğrudan MP4/HLS indirilebilir değil."));
            return;
        }

        var config            = _configService.Config;
        var progressTracker   = new DownloadProgressTracker();
        var userCancelled     = false;
        DownloadResult?       result           = null;
        string?               unexpectedError = null;
        Task<DownloadResult>? downloadTask     = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TuiApplicationCancellation.Token);

        try
        {
            downloadTask = _downloadService.DownloadAsync(
                                                    new DownloadRequest
                                                    {
                                                        Source            = selectedSource,
                                                        OutputDirectory   = config.DownloadDirectory,
                                                        AnimeTitle        = animeTitle,
                                                        EpisodeTitle      = episode.Title,
                                                        Season            = episode.Season ?? 1,
                                                        EpisodeNumber     = episode.Number,
                                                        Overwrite         = config.DownloadOverwrite,
                                                        Resume            = config.DownloadResume,
                                                        DownloadSubtitles = config.DownloadSubtitles,
                                                        Progress          = progressTracker
                                                    },
                                                    cancellation.Token);
            AnsiConsole.Status()
                       .Spinner(Spinner.Known.Dots)
                       .Start("İndiriliyor...",
                              context =>
                              {
                                  while (!downloadTask.IsCompleted)
                                  {
                                      if (Console.KeyAvailable
                                          && Console.ReadKey(true).Key == ConsoleKey.Escape)
                                      {
                                          userCancelled = true;
                                          cancellation.Cancel();
                                          break;
                                      }

                                      var progress = progressTracker.Current;
                                      context.Status(FormatDownloadProgress(progress));
                                      Thread.Sleep(80);
                                  }
                              });

            result = await downloadTask;
        }
        catch (OperationCanceledException) when (userCancelled || cancellation.IsCancellationRequested)
        {
            await AbortDownloadAsync(downloadTask, cancellation);
            result = new DownloadResult
            {
                Success     = false,
                Error       = "İndirme iptal edildi.",
                IsCancelled = true
            };
        }
        catch (Exception)
        {
            await AbortDownloadAsync(downloadTask, cancellation);
            unexpectedError = "İndirme sırasında beklenmeyen bir hata oluştu.";
        }

        if (TuiApplicationCancellation.Token.IsCancellationRequested)
        {
            return;
        }

        await ShowDownloadResultAsync(navigator, result, unexpectedError);
    }

    private static async Task AbortDownloadAsync(
        Task<DownloadResult>? downloadTask,
        CancellationTokenSource cancellation)
    {
        if (downloadTask is null)
        {
            return;
        }

        try
        {
            if (!downloadTask.IsCompleted)
            {
                cancellation.Cancel();
            }

            await downloadTask.ConfigureAwait(false);
        }
        catch
        {
            // Task bu noktada zaten iptal/başarısız olabilir; asıl sonucu maskeleme.
        }
    }

    private static Task ShowDownloadResultAsync(ITuiNavigator navigator,
        DownloadResult?                                                   result,
        string?                                                           unexpectedError = null)
    {
        var title = result is { IsCancelled: true }
                        ? "İndirme iptal edildi"
                        : result?.Success == true
                            ? "İndirme tamamlandı"
                            : "İndirme başarısız";
        var headers = new List<string>();
        var hasMedia = result?.Success == true
                       || !string.IsNullOrWhiteSpace(result?.MediaPath);
        if (hasMedia)
        {
            headers.Add(result?.IsCancelled == true
                            ? "[yellow]Video tamamlandı; altyazılar iptal edildi.[/]"
                            : "[green]Video indirildi.[/]");
            if (!string.IsNullOrWhiteSpace(result?.MediaPath))
            {
                headers.Add($"[grey]Video:[/] [white]{Markup.Escape(result.MediaPath)}[/]");
            }

            foreach (var subtitlePath in result?.SubtitlePaths ?? [])
            {
                headers.Add($"[grey]Altyazı:[/] [white]{Markup.Escape(subtitlePath)}[/]");
            }
        }
        else
        {
            var error = unexpectedError
                        ?? result?.Error
                        ?? "Video indirilemedi.";
            headers.Add($"[red]{Markup.Escape(error)}[/]");
        }

        foreach (var warning in result?.Warnings ?? [])
        {
            headers.Add($"[yellow]Uyarı:[/] [grey]{Markup.Escape(warning)}[/]");
        }

        FuzzyPrompt.Show(title,
                          [TuiHelpers.Back()],
                          headerLines: headers,
                          footerHelp: "↑↓ gez • Enter seç • Esc geri");
        if (result?.Success == true && result.IsCancelled != true)
        {
            navigator.Pop();
        }

        return Task.CompletedTask;
    }

    private static string FormatDownloadProgress(DownloadProgress? progress)
    {
        if (progress is null)
        {
            return "İndiriliyor...";
        }

        var stage = progress.Stage switch
        {
            DownloadStage.Preparing  => "Hazırlanıyor",
            DownloadStage.Requesting => "Bağlanıyor",
            DownloadStage.Downloading => "İndiriliyor",
            DownloadStage.Finalizing  => "Tamamlanıyor",
            DownloadStage.Subtitle    => "Altyazı indiriliyor",
            DownloadStage.Completed   => "Tamamlandı",
            DownloadStage.Failed      => "Başarısız",
            DownloadStage.Cancelled   => "İptal edildi",
            _                         => "İşleniyor"
        };
        var bytes = FormatBytes(progress.BytesDownloaded);
        if (progress.TotalBytes is > 0)
        {
            bytes += $" / {FormatBytes(progress.TotalBytes.Value)}";
        }

        return $"{stage} • {bytes} • Esc: iptal";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value   = Math.Max(0, bytes);
        var    unit    = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
                   ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[unit]}"
                   : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private sealed class DownloadProgressTracker : IProgress<DownloadProgress>
    {
        private readonly Lock _sync = new();
        private          DownloadProgress? _current;

        public DownloadProgress? Current
        {
            get
            {
                lock (_sync)
                {
                    return _current;
                }
            }
        }

        public void Report(DownloadProgress value)
        {
            lock (_sync)
            {
                _current = value;
            }
        }
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
