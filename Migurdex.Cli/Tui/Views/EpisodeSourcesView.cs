using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Core.Database;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Globalization;

namespace Migurdex.Cli.Tui.Views;

public enum SourceViewMode
{
    Play,
    Download
}

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
    private          SourceViewMode        _mode = SourceViewMode.Play;
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
        IReadOnlyList<VideoSource>? adoptedSources = null,
        SourceViewMode              mode           = SourceViewMode.Play)
    {
        _provider       = provider;
        _animeId        = animeId;
        _animeTitle     = animeTitle;
        _posterUrl      = posterUrl;
        _episode        = episode;
        _allEpisodes    = allEpisodes;
        _isFallbackMode = false;
        _mode           = mode;

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

    internal static List<FuzzyChoice> FormatSources(List<VideoSource> rawList, CliConfig config)
    {
        var sorted = SourceSelector.SortVideoSources(rawList, config);

        var rows = sorted.Select(src => new SourceRow(src)).ToList();

        var groupW   = rows.Count > 0 ? rows.Max(r => r.Group.Length) : 0;
        var hosterW  = rows.Count > 0 ? rows.Max(r => r.Hoster.Length) : 0;
        var qualityW = rows.Count > 0 ? rows.Max(r => r.Quality.Length) : 0;
        var bitrateW = rows.Count > 0 ? rows.Max(r => r.Bitrate.Length) : 0;
        var codecW   = rows.Count > 0 ? rows.Max(r => r.Codec.Length) : 0;
        var formatW  = rows.Count > 0 ? rows.Max(r => r.Format.Length) : 0;

        var selectList = new List<FuzzyChoice>();
        var idxWidth   = rows.Count.ToString().Length;
        for (var i = 0; i < rows.Count; i++)
        {
            var row          = rows[i];
            var qualityColor = QualityColor(row.Src.Quality);
            var formatColor  = FormatColor(row.Src.Type);
            var bitratePart = row.Bitrate.Length == 0
                                  ? " " + new string(' ', bitrateW)
                                  : $" [{qualityColor}]{Markup.Escape(row.Bitrate.PadRight(bitrateW))}[/]";
            var codecPart = row.Codec.Length == 0
                                ? " " + new string(' ', codecW)
                                : $" [grey]{Markup.Escape(row.Codec.PadRight(codecW))}[/]";

            var idx       = (i + 1).ToString().PadLeft(idxWidth);
            var idxMarkup = $"[grey]#{idx}[/]";

            selectList.Add(new FuzzyChoice
            {
                Display =
                    $"{idxMarkup} [silver]{Markup.Escape(row.Group.PadRight(groupW))}[/] [grey]•[/] [cyan]{Markup.Escape(row.Hoster.PadRight(hosterW))}[/] [grey]•[/] [{qualityColor}]{Markup.Escape(row.Quality.PadRight(qualityW))}[/]{bitratePart}{codecPart} [grey]• [{formatColor}]{row.Format.PadRight(formatW)}[/][/]",
                DisplayActive =
                    $"[bold silver]{Markup.Escape(row.Group.PadRight(groupW))}[/] [grey]•[/] [bold cyan]{Markup.Escape(row.Hoster.PadRight(hosterW))}[/] [grey]•[/] [bold {qualityColor}]{Markup.Escape(row.Quality.PadRight(qualityW))}[/]{bitratePart}{codecPart} [grey]• [bold {formatColor}]{row.Format.PadRight(formatW)}[/][/]",
                Searchable      = $"#{i + 1} - {row.Group} | {row.Hoster} | {row.Src.DisplayLabel} | {row.Format}",
                AssociatedValue = row.Src
            });
        }

        return selectList;
    }

    internal static string QualityColor(string? quality)
    {
        if (string.IsNullOrEmpty(quality))
        {
            return "red";
        }

        var digits = new string(quality.TakeWhile(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var height))
        {
            if (height >= 1080)
            {
                return "green";
            }

            if (height >= 720)
            {
                return "yellow";
            }

            return "grey";
        }

        return quality.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "grey" : "silver";
    }

    internal static string FormatColor(VideoType type)
    {
        return type switch
        {
            VideoType.M3U8 => "blue",
            VideoType.Mp4  => "green",
            _              => "grey"
        };
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
            if (!TuiConsole.WaitForKey())
            {
                return;
            }

            navigator.Pop();
            return;
        }

        var provider   = _provider;
        var animeId    = _animeId;
        var animeTitle = _animeTitle;
        var episode    = _episode;

        var config = _configService.Config;

        if (_mode == SourceViewMode.Download
            && config.AutoDownloadBestSource
            && !_isFallbackMode
            && await TryAutoDownloadAsync(navigator, provider, animeTitle, episode, config))
        {
            return;
        }

        if (_mode == SourceViewMode.Download && _isFallbackMode)
        {
            Toast.Show("[yellow][[!]] Otomatik seçim yapılamadı, manuel liste açılıyor...[/]");
        }

        var scanStats = new StreamScanStats();
        var stream = _cachedSources.Count > 0
                         ? ToAsyncEnumerable(_cachedSources)
                         : _apiClient.GetVideoSourcesStreamAsync(provider, episode.Id, stats: scanStats)
                                     .Where(src => src.Type != VideoType.Embed);

        if (_mode == SourceViewMode.Play
            && config.AutoSelectBestSource
            && _cachedSources.Count == 0
            && !_isFallbackMode)
        {
            AnsiConsole.Clear();
            Theme.WriteHeader($"{animeTitle}", $"Bölüm {episode.Number} • otomatik seçim");
            AnsiConsole.MarkupLine("[cyan]Kaynaklar taranıyor...[/] [grey](Esc: vazgeç)[/]");

            var resolvedSources = new List<VideoSource>();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(
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
                        $"[green]✓{exactTag}:[/] {Markup.Escape(bestSource.Hoster ?? "Bilinmeyen")} [grey]({Markup.Escape(bestSource.DisplayLabel)} • {bestSource.Type})[/]");

                    await PlaySelectedSourceAsync(navigator,
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
                var pool = _mode == SourceViewMode.Download
                               ? sources.Where(DownloadSourceResolver.IsDirectDownloadable).ToList()
                               : sources;
                var choices = FormatSources(pool, config);
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

        if (_mode == SourceViewMode.Download)
        {
            var jobId = await DownloadSourceAsync(selectedSource, animeTitle, episode);
            if (jobId is not null)
            {
                Toast.Show($"[green]✓ Kuyruğa alındı:[/] {episode.Number:0.##}. Bölüm - İndirilenler'den takip et.");
                navigator.Pop();
            }

            return;
        }

        await PlaySelectedSourceAsync(navigator,
                                      selectedSource,
                                      provider,
                                      animeId,
                                      animeTitle,
                                      episode);
    }

    private async Task<bool> TryAutoDownloadAsync(ITuiNavigator navigator,
        string                                                  provider,
        string                                                  animeTitle,
        Episode                                                 episode,
        CliConfig                                               config)
    {
        var pool = _cachedSources.Count > 0
                       ? DownloadSourceResolver.SelectCandidates(_cachedSources,
                                                                 DownloadSourceFormat.Auto,
                                                                 config)
                       : (await DownloadCandidateResolver.ResolveAsync(
                                                             _apiClient,
                                                             provider,
                                                             episode.Id,
                                                             null,
                                                             DownloadSourceFormat.Auto,
                                                             config,
                                                             config.DownloadAutoSelectTimeoutSeconds,
                                                             TuiApplicationCancellation.Token)
                                                         .ConfigureAwait(false)).Candidates;

        if (pool.Count == 0)
        {
            _isFallbackMode = true;
            return false;
        }

        var jobId = await DownloadSourceAsync(pool[0], animeTitle, episode);
        if (jobId is null)
        {
            _isFallbackMode = true;
            return false;
        }

        Toast.Show($"[green]✓ Kuyruğa alındı:[/] {episode.Number:0.##}. Bölüm - İndirilenler'den takip et.");
        navigator.Pop();
        return true;
    }

    private async Task PlaySelectedSourceAsync(ITuiNavigator navigator,
        VideoSource                                          selectedSource,
        string                                               provider,
        string                                               animeId,
        string                                               animeTitle,
        Episode                                              episode)
    {
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

    private Task<Guid?> DownloadSourceAsync(VideoSource selectedSource,
        string                                          animeTitle,
        Episode                                         episode)
    {
        if (!DownloadSourceResolver.IsDirectDownloadable(selectedSource))
        {
            ShowDownloadError("Bu kaynak doğrudan MP4/HLS indirilebilir değil.");
            return Task.FromResult<Guid?>(null);
        }

        var config = _configService.Config;
        if (_serviceProvider.GetService(typeof(BackgroundDownloadTracker)) is not BackgroundDownloadTracker tracker)
        {
            ShowDownloadError("Arka plan kuyruğu kayıtlı değil.");
            return Task.FromResult<Guid?>(null);
        }

        var maxSegments = Math.Clamp(config.ConnectionsPerFile, 1, 8);
        var label       = $"Bölüm {episode.Number:0.##}";
        var provider    = _provider ?? string.Empty;
        var groups = string.IsNullOrWhiteSpace(selectedSource.Group)
                         ? new List<string>()
                         : new List<string>
                         {
                             selectedSource.Group
                         };
        var hosters = string.IsNullOrWhiteSpace(selectedSource.Hoster)
                          ? new List<string>()
                          : new List<string>
                          {
                              selectedSource.Hoster
                          };
        var filter  = new DownloadFilterSpec(groups, hosters, [], []);
        var animeId = _animeId ?? string.Empty;
        var jobId = tracker.Enqueue(animeTitle,
                                    label,
                                    control => DownloadJobRunner.RunEpisodeAsync(
                                        _apiClient,
                                        _downloadService,
                                        tracker,
                                        config,
                                        provider,
                                        animeTitle,
                                        episode,
                                        episode.Season ?? 1,
                                        filter,
                                        config.DownloadDirectory,
                                        config.DownloadSubtitles,
                                        config.DownloadOverwrite,
                                        config.DownloadResume,
                                        maxSegments,
                                        control),
                                    id => new StoredDownloadJob(
                                        id,
                                        provider,
                                        animeId,
                                        animeTitle,
                                        episode.Id,
                                        episode.Title,
                                        episode.Season ?? 1,
                                        episode.Number,
                                        groups,
                                        hosters,
                                        [],
                                        [],
                                        config.DownloadDirectory,
                                        config.DownloadSubtitles,
                                        "queued",
                                        null,
                                        null,
                                        DateTimeOffset.UtcNow,
                                        DateTimeOffset.UtcNow));

        return Task.FromResult<Guid?>(jobId);
    }

    private static void ShowDownloadError(string error)
    {
        FuzzyPrompt.Show("İndirme başarısız",
                         [TuiHelpers.Back()],
                         headerLines: [$"[red]{Markup.Escape(error)}[/]"],
                         footerHelp: "Enter devam • Esc geri",
                         searchable: false);
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

    private sealed class SourceRow
    {
        public SourceRow(VideoSource src)
        {
            Src     = src;
            Group   = Theme.Ellipsize(src.Group ?? "Bilinmeyen", 22);
            Hoster  = Theme.Ellipsize(src.Hoster ?? "Bilinmeyen", 18);
            Quality = string.IsNullOrEmpty(src.Quality) ? "Çözülemedi" : src.Quality;
            Bitrate = VideoSource.FormatBitrate(src.Bitrate);
            Codec   = src.VideoCodec ?? string.Empty;
            Format  = src.Type.ToString();
        }

        public VideoSource Src     { get; }
        public string      Group   { get; }
        public string      Hoster  { get; }
        public string      Quality { get; }
        public string      Bitrate { get; }
        public string      Codec   { get; }
        public string      Format  { get; }
    }
}
