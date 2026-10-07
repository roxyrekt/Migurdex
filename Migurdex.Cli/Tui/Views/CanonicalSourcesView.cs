using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Cli.Tui;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Globalization;

namespace Migurdex.Cli.Tui.Views;

public class CanonicalSourcesView : BaseView
{
    private readonly IApiClientService      _apiClient;
    private readonly IConfigurationService  _configService;
    private readonly PlaybackOrchestrator   _playback;
    private readonly IDownloadService       _downloadService;
    private readonly IServiceProvider       _serviceProvider;

    private string? _canonicalId;
    private string? _animeTitle;
    private CanonicalEpisodeEntry?          _entry;
    private List<CanonicalEpisodeSource>    _episodeSources = [];
    private List<VideoSource>?              _cachedSources;
    private string?                         _posterUrl;

    public CanonicalSourcesView(IApiClientService apiClient,
        IConfigurationService                        configService,
        PlaybackOrchestrator                         playback,
        IDownloadService                             downloadService,
        IServiceProvider                             serviceProvider)
    {
        _apiClient       = apiClient;
        _configService   = configService;
        _playback        = playback;
        _downloadService = downloadService;
        _serviceProvider = serviceProvider;
    }

    public void SetTarget(string canonicalId,
        string                                  animeTitle,
        CanonicalEpisodeEntry                   entry,
        List<CanonicalEpisodeSource>            episodeSources,
        string?                                 posterUrl = null)
    {
        if (_canonicalId != canonicalId || _entry?.Number != entry.Number)
        {
            _cachedSources = null;
        }

        _canonicalId    = canonicalId;
        _animeTitle     = animeTitle;
        _entry          = entry;
        _episodeSources = episodeSources;
        _posterUrl      = posterUrl;
    }

    public override string GetRpcState()
    {
        if (string.IsNullOrWhiteSpace(_animeTitle) || _entry is null)
        {
            return "Kaynaklar";
        }

        var ep = _entry.Number % 1 == 0
                     ? ((int) _entry.Number).ToString()
                     : _entry.Number.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{_animeTitle} E{ep} (birleştirilmiş)";
    }

    public override async Task RenderAsync(ITuiNavigator navigator)
    {
        if (string.IsNullOrEmpty(_canonicalId) || string.IsNullOrEmpty(_animeTitle) || _entry is null)
        {
            navigator.Pop();
            return;
        }

        var sources = _cachedSources;
        if (sources is null)
        {
            await CollectSourcesProgressiveAsync(navigator);
            return;
        }

        while (true)
        {
            var pick = ShowSources(navigator, sources, false);
            if (pick is null || pick == MoreSentinel)
            {
                navigator.Pop();
                return;
            }

            if (await HandleSourceAsync(navigator, pick, sources))
            {
                return;
            }
        }
    }

    private async Task<List<VideoSource>?> CollectSourcesProgressiveAsync(ITuiNavigator navigator)
    {
        var collected = new List<VideoSource>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TuiApplicationCancellation.Token);

        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var src in _apiClient
                                               .GetCanonicalSourcesStreamAsync(_canonicalId!, _entry!.Number)
                                               .WithCancellation(cts.Token))
                {
                    if (src.Type == VideoType.Embed)
                    {
                        continue;
                    }

                    lock (collected)
                    {
                        collected.Add(src);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }, cts.Token);

        try
        {
            while (!TuiApplicationCancellation.Token.IsCancellationRequested)
            {
                var settled = await WaitForBatchAsync(pump, collected, TimeSpan.FromSeconds(12), 5);

                List<VideoSource> snapshot;
                lock (collected)
                {
                    snapshot = collected.ToList();
                }

                if (snapshot.Count == 0)
                {
                    if (settled)
                    {
                        break;
                    }

                    continue;
                }

                var pick = ShowSources(navigator, snapshot, !settled);
                if (pick is null)
                {
                    await cts.CancelAsync();
                    try
                    {
                        await pump;
                    }
                    catch
                    {
                    }

                    navigator.Pop();
                    return null;
                }

                if (pick == MoreSentinel)
                {
                    continue;
                }

                _cachedSources = snapshot;
                await cts.CancelAsync();
                try
                {
                    await pump;
                }
                catch
                {
                }

                if (await HandleSourceAsync(navigator, pick, snapshot))
                {
                    return snapshot;
                }
            }
        }
        finally
        {
            await cts.CancelAsync();
        }

        Toast.Show("[yellow]Bu bölümde oynatılabilir kaynak yok.[/]");
        navigator.Pop();
        return null;
    }

    private static readonly VideoSource MoreSentinel = new();

    private VideoSource? ShowSources(ITuiNavigator navigator, List<VideoSource> snapshot, bool canLoadMore)
    {
        var config  = _configService.Config;
        var choices = EpisodeSourcesView.FormatSources(snapshot, config);
        if (canLoadMore)
        {
            choices.Add(new FuzzyChoice
            {
                Display       = $"[cyan]⟳ Daha fazla yükle[/] [grey]({snapshot.Count} bulundu, tarama sürüyor)[/]",
                DisplayActive = "[bold white]⟳ Daha fazla yükle[/]",
                Searchable    = "Daha Fazla Yükle"
            });
        }

        choices.Add(TuiHelpers.Back());

        var epNum = _entry!.Number % 1 == 0
                        ? ((int) _entry.Number).ToString()
                        : _entry.Number.ToString("0.#", CultureInfo.InvariantCulture);
        var choice = FuzzyPrompt.Show($"{_animeTitle} › E{epNum}",
                                      choices,
                                      headerLines:
                                      [$"[grey]{snapshot.Count} kaynak • {string.Join(", ", snapshot.Select(s => s.ProviderName).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))}[/]"]);
        if (choice is null || choice.Searchable == "Geri")
        {
            return null;
        }

        if (choice.Searchable == "Daha Fazla Yükle")
        {
            return MoreSentinel;
        }

        return choice.AssociatedValue as VideoSource;
    }

    private async Task<bool> HandleSourceAsync(ITuiNavigator navigator,
        VideoSource                                                            selectedSource,
        List<VideoSource>                                                    availableSources)
    {
        var actions = new List<FuzzyChoice>
        {
            Theme.PlayChoice("Oynat"),
            Theme.ActionChoice("İndir", Theme.Primary),
            TuiHelpers.Back()
        };
        var action = FuzzyPrompt.Show(_animeTitle!, actions, searchable: false);
        if (action is null || action.Searchable == "Geri")
        {
            return false;
        }

        if (action.Searchable == "İndir")
        {
            await DownloadSourceAsync(selectedSource);
            return false;
        }

        await PlaySelectedSourceAsync(navigator, selectedSource, availableSources);
        return true;
    }

    private static async Task<bool> WaitForBatchAsync(Task pump,
        List<VideoSource>                                          collected,
        TimeSpan                                                   budget,
        int                                                        minCount)
    {
        var deadline = DateTime.UtcNow + budget;
        while (!pump.IsCompleted)
        {
            lock (collected)
            {
                if (collected.Count >= minCount)
                {
                    return false;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            try
            {
                await Task.WhenAny(pump, Task.Delay(300));
            }
            catch
            {
                return true;
            }
        }

        return true;
    }

    private Task PlaySelectedSourceAsync(ITuiNavigator navigator,
        VideoSource                                        selectedSource,
        List<VideoSource>                                availableSources)
    {
        var mapping = _episodeSources.FirstOrDefault(s =>
                                                         s.ProviderName.Equals(selectedSource.ProviderName,
                                                                               StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
        {
            Toast.Show("[red]Kaynak sağlayıcı eşleşmedi.[/]");
            return Task.CompletedTask;
        }

        var episode = new Episode
        {
            Id     = mapping.ProviderEpisodeId,
            Title  = _entry!.Title ?? $"Bölüm {_entry.Number}",
            Number = _entry.Number,
            Season = _entry.Season
        };

        var historyEntry = _playback.BuildEntry(mapping.ProviderName,
                                                mapping.ProviderAnimeId,
                                                _animeTitle!,
                                                episode,
                                                _posterUrl);
        AnsiConsole.MarkupLine("[green]✓[/] Oynatıcı başlatıldı.");
        return PlayAndPushAsync(navigator,
                                selectedSource,
                                historyEntry,
                                mapping.ProviderName,
                                mapping.ProviderAnimeId,
                                episode,
                                availableSources);
    }

    private async Task PlayAndPushAsync(ITuiNavigator navigator,
        VideoSource                                        selectedSource,
        WatchHistoryEntry                                  historyEntry,
        string                                             provider,
        string                                             animeId,
        Episode                                            episode,
        List<VideoSource>                                availableSources)
    {
        var syncOutcome = await _playback.PlayAndNotifyAsync(selectedSource, historyEntry, false);
        var playbackMenu = (PlaybackMenuView) _serviceProvider.GetService(typeof(PlaybackMenuView))!;
        playbackMenu.SetTarget(provider,
                               animeId,
                               _animeTitle!,
                               episode,
                               [episode],
                               selectedSource,
                               historyEntry,
                               availableSources,
                               syncOutcome);
        navigator.Push(playbackMenu);
    }

    private async Task DownloadSourceAsync(VideoSource selectedSource)
    {
        if (!DownloadSourceResolver.IsDirectDownloadable(selectedSource))
        {
            Toast.Show("[red]Bu kaynak doğrudan MP4/HLS indirilebilir değil.[/]");
            return;
        }

        var config          = _configService.Config;
        var progressTracker = new SimpleProgressTracker();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TuiApplicationCancellation.Token);

        try
        {
            using var modal = TuiApplicationCancellation.BeginModal(cancellation);
            var downloadTask = _downloadService.DownloadAsync(
                new DownloadRequest
                {
                    Source            = selectedSource,
                    OutputDirectory   = config.DownloadDirectory,
                    AnimeTitle        = _animeTitle!,
                    EpisodeTitle      = _entry!.Title ?? $"Bölüm {_entry.Number}",
                    Season            = _entry.Season,
                    EpisodeNumber     = _entry.Number,
                    Overwrite         = config.DownloadOverwrite,
                    Resume            = config.DownloadResume,
                    DownloadSubtitles = config.DownloadSubtitles,
                    Progress          = progressTracker
                },
                cancellation.Token);

            await AnsiConsole.Progress()
                             .AutoClear(false)
                             .HideCompleted(false)
                             .Columns(
                                 new PercentageColumn(),
                                 new ProgressBarColumn(),
                                 new TaskDescriptionColumn(),
                                 new SpinnerColumn())
                             .StartAsync(async progressContext =>
                             {
                                 var task = progressContext.AddTask("Hazırlanıyor", maxValue: 100);
                                 while (!downloadTask.IsCompleted)
                                 {
                                     if (Console.KeyAvailable
                                         && Console.ReadKey(true).Key == ConsoleKey.Escape)
                                     {
                                         cancellation.Cancel();
                                         break;
                                     }

                                     UpdateTask(task, progressTracker);
                                     await Task.Delay(100);
                                 }

                                 UpdateTask(task, progressTracker);
                             });

            var result = await downloadTask;
            if (result.Success)
            {
                Toast.Show($"[green]İndirildi:[/] {Markup.Escape(result.MediaPath ?? string.Empty)}");
            }
            else
            {
                Toast.Show($"[red]İndirilemedi:[/] {Markup.Escape(result.Error ?? "bilinmeyen hata")}");
            }
        }
        catch (OperationCanceledException)
        {
            Toast.Show("[yellow]İndirme iptal edildi.[/]");
        }
    }

    private static void UpdateTask(ProgressTask task, SimpleProgressTracker tracker)
    {
        var progress = tracker.Current;
        if (progress?.Percent is double percent)
        {
            task.IsIndeterminate = false;
            task.Value           = Math.Clamp(percent, 0, 100);
            task.Description     = $"{progress.Stage} • Esc: iptal";
            return;
        }

        task.IsIndeterminate = true;
        task.Description     = $"{progress?.Stage.ToString() ?? "Hazırlanıyor"} • Esc: iptal";
    }

    private sealed class SimpleProgressTracker : IProgress<DownloadProgress>
    {
        public DownloadProgress? Current { get; private set; }

        public void Report(DownloadProgress value)
        {
            Current = value;
        }
    }
}
