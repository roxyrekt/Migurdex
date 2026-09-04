using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Models;
using Spectre.Console;

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
        IReadOnlyList<VideoSource> availableSources)
    {
        _provider       = provider;
        _animeId        = animeId;
        _animeTitle     = animeTitle;
        _episode        = episode;
        _allEpisodes    = allEpisodes;
        _selectedSource = selectedSource;
        _historyEntry   = historyEntry;

        _availableSources       = [.. availableSources];
        _lastSelectedSearchable = null;
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

        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[grey]~~[/] [yellow]İzleme Sonrası: {animeTitle}[/] [grey]~~[/]");

        var progressLine = FormatProgressLine(historyEntry);
        if (progressLine is not null)
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(progressLine)}[/]");
        }

        var sourceLine = FormatSourceLine(selectedSource);
        if (sourceLine is not null)
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(sourceLine)}[/]");
        }

        AnsiConsole.WriteLine();

        var currentIdx = _allEpisodes.FindIndex(e => e.Id == episode.Id);
        var nextEpisode = currentIdx >= 0 && currentIdx < _allEpisodes.Count - 1
                              ? _allEpisodes[currentIdx + 1]
                              : null;
        var prevEpisode = currentIdx > 0 ? _allEpisodes[currentIdx - 1] : null;

        var menuChoices = new List<FuzzyChoice>();

        if (nextEpisode != null)
        {
            menuChoices.Add(new FuzzyChoice
            {
                Display = $"[bold green]Sonraki Bölüm[/] [silver](Bölüm {nextEpisode.Number})[/]",
                DisplayActive =
                    $"[bold green]Sonraki Bölüm[/] [bold white](Bölüm {nextEpisode.Number} - {Markup.Escape(nextEpisode.Title)})[/]",
                Searchable = "Sonraki Bölüm"
            });
        }

        if (prevEpisode != null)
        {
            menuChoices.Add(new FuzzyChoice
            {
                Display = $"[silver]Önceki Bölüm[/] [grey](Bölüm {prevEpisode.Number})[/]",
                DisplayActive =
                    $"[bold white]Önceki Bölüm[/] [bold white](Bölüm {prevEpisode.Number} - {Markup.Escape(prevEpisode.Title)})[/]",
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

        var choice = FuzzyPrompt.Show("Seçim:", menuChoices, initialSelection: _lastSelectedSearchable);

        if (choice == null)
        {
            navigator.Pop();
            return;
        }

        if (choice.Searchable == "Bölüm Listesi")
        {
            var detailsView = (AnimeDetailsView) _serviceProvider.GetService(typeof(AnimeDetailsView))!;
            detailsView.SetTarget(provider, animeId, historyEntry.PosterUrl);
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
            AnsiConsole.Status()
                       .Spinner(Spinner.Known.Dots)
                       .Start("Hazırlanıyor...",
                              ctx =>
                              {
                                  _playerService
                                      .PlayAsync(selectedSource.Url,
                                                 historyEntry,
                                                 selectedSource.Headers,
                                                 selectedSource.Subtitles)
                                      .GetAwaiter()
                                      .GetResult();
                              });
        }
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
