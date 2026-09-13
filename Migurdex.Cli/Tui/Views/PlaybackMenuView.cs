using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Globalization;

namespace Migurdex.Cli.Tui.Views;

public class PlaybackMenuView : BaseView
{
    private readonly IMpvPlayerService _playerService;
    private readonly IServiceProvider  _serviceProvider;

    private List<Episode>      _allEpisodes = [];
    private string?            _animeId;
    private string?            _animeTitle;
    private List<VideoSource>  _availableSources = [];
    private Episode?           _episode;
    private WatchHistoryEntry? _historyEntry;
    private string?            _lastSelectedSearchable;
    private string?            _provider;
    private VideoSource?       _selectedSource;
    private SyncOutcome?       _syncOutcome;

    public PlaybackMenuView(IMpvPlayerService playerService, IServiceProvider serviceProvider)
    {
        _playerService   = playerService;
        _serviceProvider = serviceProvider;
    }

    public void SetTarget(string   provider,
        string                     animeId,
        string                     animeTitle,
        Episode                    episode,
        List<Episode>              allEpisodes,
        VideoSource                selectedSource,
        WatchHistoryEntry          historyEntry,
        IReadOnlyList<VideoSource> availableSources,
        SyncOutcome?               syncOutcome = null)
    {
        _provider       = provider;
        _animeId        = animeId;
        _animeTitle     = animeTitle;
        _episode        = episode;
        _allEpisodes    = allEpisodes;
        _selectedSource = selectedSource;
        _historyEntry   = historyEntry;
        _syncOutcome    = syncOutcome;

        _availableSources       = [.. availableSources];
        _lastSelectedSearchable = null;
    }

    public override string GetRpcState()
    {
        var title          = _animeTitle?.Trim();
        var episodeInfoStr = "";

        if (_episode != null)
        {
            var season = Math.Max(1, _episode.Season ?? 1);
            var ep = _episode.Number % 1 == 0
                         ? ((int) _episode.Number).ToString()
                         : _episode.Number.ToString("0.#", CultureInfo.InvariantCulture);

            episodeInfoStr = $" S{season} E{ep} - {_episode.Title}";
        }

        return string.IsNullOrEmpty(title) ? "İzleme Sonrası" : $"{title}{episodeInfoStr}";
    }

    public override void Render(ITuiNavigator navigator)
    {
        if (string.IsNullOrEmpty(_provider)
            || string.IsNullOrEmpty(_animeId)
            || string.IsNullOrEmpty(_animeTitle)
            || _episode is null
            || _selectedSource is null
            || _historyEntry is null)
        {
            AnsiConsole.MarkupLine("[red]Hata: Gerekli parametreler eksik.[/]");
            Console.ReadKey(true);
            navigator.Pop();
            return;
        }

        var provider       = _provider;
        var animeId        = _animeId;
        var animeTitle     = _animeTitle;
        var episode        = _episode;
        var selectedSource = _selectedSource;
        var historyEntry   = _historyEntry;

        var headerLines  = new List<string>();
        var progressLine = FormatProgressLine(historyEntry);
        if (progressLine is not null)
        {
            headerLines.Add($"[grey]{Markup.Escape(progressLine)}[/]");
        }

        var sourceLine = FormatSourceLine(selectedSource);
        if (sourceLine is not null)
        {
            headerLines.Add($"[grey]{Markup.Escape(sourceLine)}[/]");
        }

        var syncLine = FormatSyncLine(_syncOutcome);
        if (syncLine is not null)
        {
            headerLines.Add(syncLine);
        }

        var currentIdx = _allEpisodes.FindIndex(e => e.Id == episode.Id);
        var nextEpisode = currentIdx >= 0 && currentIdx < _allEpisodes.Count - 1
                              ? _allEpisodes[currentIdx + 1]
                              : null;
        var prevEpisode = currentIdx > 0 ? _allEpisodes[currentIdx - 1] : null;

        var currentSeason = episode.Season ?? 1;
        var menuChoices   = new List<FuzzyChoice>();

        if (nextEpisode != null)
        {
            var nextSeason       = nextEpisode.Season ?? 1;
            var nextSeasonPrefix = nextSeason != currentSeason ? $"{nextSeason}. Sezon " : "";
            var nextEpLabel      = $"{nextSeasonPrefix}Bölüm {nextEpisode.Number}";
            var nextTitlePart = string.IsNullOrWhiteSpace(nextEpisode.Title)
                                    ? ""
                                    : $" - {Markup.Escape(nextEpisode.Title)}";

            menuChoices.Add(new FuzzyChoice
            {
                Display = $"[bold green]Sonraki Bölüm[/] [silver]({nextEpLabel})[/]",
                DisplayActive =
                    $"[bold green]Sonraki Bölüm[/] [bold white]({nextEpLabel}{nextTitlePart})[/]",
                Searchable = "Sonraki Bölüm"
            });
        }

        if (prevEpisode != null)
        {
            var prevSeason       = prevEpisode.Season ?? 1;
            var prevSeasonPrefix = prevSeason != currentSeason ? $"{prevSeason}. Sezon " : "";
            var prevEpLabel      = $"{prevSeasonPrefix}Bölüm {prevEpisode.Number}";
            var prevTitlePart = string.IsNullOrWhiteSpace(prevEpisode.Title)
                                    ? ""
                                    : $" - {Markup.Escape(prevEpisode.Title)}";

            menuChoices.Add(new FuzzyChoice
            {
                Display = $"[silver]Önceki Bölüm[/] [grey]({prevEpLabel})[/]",
                DisplayActive =
                    $"[bold white]Önceki Bölüm[/] [bold white]({prevEpLabel}{prevTitlePart})[/]",
                Searchable = "Önceki Bölüm"
            });
        }

        menuChoices.Add(new FuzzyChoice
        {
            Display       = "[silver]Kaynağı Değiştir[/] [grey](Farklı Oynatıcı/Çözünürlük Seç)[/]",
            DisplayActive = "[bold white]Kaynağı Değiştir[/] [bold gold1](Farklı Oynatıcı/Çözünürlük Seç)[/]",
            Searchable    = "Kaynağı Değiştir"
        });

        var resumeSeconds = historyEntry.LastPositionSeconds;
        var replayLabel = resumeSeconds > 0
                              ? $"Devam Et ({FormatTimestamp(resumeSeconds)})"
                              : "Baştan İzle";
        menuChoices.Add(new FuzzyChoice
        {
            Display       = $"[silver]{Markup.Escape(replayLabel)}[/]",
            DisplayActive = $"[bold white]{Markup.Escape(replayLabel)}[/]",
            Searchable    = "Tekrar İzle"
        });

        menuChoices.Add(new FuzzyChoice
        {
            Display       = "[red]Bölüm Listesi[/]",
            DisplayActive = "[bold red]Bölüm Listesi[/]",
            Searchable    = "Bölüm Listesi"
        });

        var promptTitle = $"İzleme Sonrası: {animeTitle}";
        var choice = FuzzyPrompt.Show(promptTitle,
                                      menuChoices,
                                      initialSelection: _lastSelectedSearchable,
                                      headerLines: headerLines);

        if (choice == null)
        {
            navigator.Pop();
            return;
        }

        if (choice.Searchable == "Bölüm Listesi")
        {
            var detailsView = (AnimeDetailsView) _serviceProvider.GetService(typeof(AnimeDetailsView))!;
            detailsView.SetTarget(provider, animeId, historyEntry.PosterUrl, animeTitle);
            navigator.Push(detailsView);
            return;
        }

        _lastSelectedSearchable = choice.Searchable;

        if (choice.Searchable == "Sonraki Bölüm" && nextEpisode != null)
        {
            PushSources(navigator, provider, animeId, animeTitle, nextEpisode, historyEntry);
        }
        else if (choice.Searchable == "Önceki Bölüm" && prevEpisode != null)
        {
            PushSources(navigator, provider, animeId, animeTitle, prevEpisode, historyEntry);
        }
        else if (choice.Searchable == "Kaynağı Değiştir")
        {
            var sourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
            sourcesView.SetTarget(provider,
                                  animeId,
                                  animeTitle,
                                  episode,
                                  _allEpisodes,
                                  historyEntry.PosterUrl,
                                  _availableSources);
            navigator.Push(sourcesView);
        }
        else if (choice.Searchable == "Tekrar İzle")
        {
            AnsiConsole.MarkupLine("[green]OK:[/] Tekrar başlatılıyor...");
            SyncOutcome? syncOutcome = null;
            AnsiConsole.Status()
                       .Spinner(Spinner.Known.Dots)
                       .Start("Hazırlanıyor...",
                              ctx =>
                              {
                                  syncOutcome = _playerService
                                                .PlayAsync(selectedSource.Url,
                                                           historyEntry,
                                                           selectedSource.Headers,
                                                           selectedSource.Subtitles)
                                                .GetAwaiter()
                                                .GetResult();
                              });
            _syncOutcome = syncOutcome;
            SyncAmbiguityPrompt.HandleAfterPlayback(_serviceProvider,
                                                    syncOutcome ?? new SyncOutcome());
            SyncAmbiguityPrompt.ShowPlaybackNotification(syncOutcome ?? new SyncOutcome());
        }
    }

    private static string? FormatSyncLine(SyncOutcome? outcome)
    {
        if (outcome is null || outcome.Kind is SyncOutcomeKind.None or SyncOutcomeKind.SkippedNoToken)
        {
            return null;
        }

        if (outcome.Kind == SyncOutcomeKind.Pushed && outcome.SyncedTo.Count > 0)
        {
            var epText = outcome.Progress.HasValue ? $" (Bölüm {outcome.Progress.Value})" : "";
            var list   = string.Join(" • ", outcome.SyncedTo.Select(s => $"[cyan]{s}[/]{epText}"));
            return $"[grey]Senkronizasyon:[/] [green]✓[/] {list}";
        }

        if (outcome.Kind == SyncOutcomeKind.Queued)
        {
            return "[grey]Senkronizasyon:[/] [yellow]Kuyrukta (bağlantı kurulunca iletilecek)[/]";
        }

        return null;
    }

    private static string FormatTimestamp(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return ts.TotalHours >= 1
                   ? $"{(int) ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                   : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    private static string? FormatProgressLine(WatchHistoryEntry historyEntry)
    {
        if (historyEntry is { LastPositionSeconds: <= 0, ProgressPercentage: <= 0 })
        {
            return null;
        }

        if (historyEntry.TotalDurationSeconds > 0)
        {
            return
                $"{FormatTimestamp(historyEntry.LastPositionSeconds)} / {FormatTimestamp(historyEntry.TotalDurationSeconds)}"
                + $" • %{historyEntry.ProgressPercentage:F0}";
        }

        return $"Kaldığın yer: {FormatTimestamp(historyEntry.LastPositionSeconds)}";
    }

    private static string? FormatSourceLine(VideoSource selectedSource)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(selectedSource.Hoster))
        {
            parts.Add(selectedSource.Hoster);
        }

        if (!string.IsNullOrWhiteSpace(selectedSource.Quality))
        {
            parts.Add(selectedSource.Quality);
        }

        parts.Add(selectedSource.Type.ToString());

        return parts.Count > 0 ? string.Join(" • ", parts) : null;
    }

    private void PushSources(ITuiNavigator navigator,
        string                             provider,
        string                             animeId,
        string                             animeTitle,
        Episode                            episode,
        WatchHistoryEntry                  historyEntry)
    {
        navigator.Pop();

        var sourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
        sourcesView.SetTarget(provider, animeId, animeTitle, episode, _allEpisodes, historyEntry.PosterUrl);
        navigator.Replace(sourcesView);
    }
}
